using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Dsh.Core.Tests;

/// <summary>A tiny HTTP/1.1 server on 127.0.0.1 (optionally HTTPS with a self-signed certificate),
/// one request per connection — enough to stand in for vLLM, the Spark Swapper, or nginx.</summary>
public sealed class TestHttpServer : IAsyncDisposable
{
    public sealed record Request(string Method, string Path, IReadOnlyDictionary<string, string> Headers, string Body);

    public sealed record Response(int Status, string Body, string ContentType = "application/json",
                                  IReadOnlyDictionary<string, string>? Headers = null);

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly Func<Request, Response> _handler;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private readonly Lock _lock = new();
    private readonly List<Request> _requests = [];

    public X509Certificate2? Certificate { get; }
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public Uri BaseUrl => new($"{(Certificate is null ? "http" : "https")}://127.0.0.1:{Port}/");

    public IReadOnlyList<Request> Requests
    {
        get
        {
            lock (_lock) return [.. _requests];
        }
    }

    public TestHttpServer(Func<Request, Response> handler, bool tls = false)
    {
        _handler = handler;
        if (tls) Certificate = SelfSigned("Spark Swapper (test)");
        _listener.Start();
        _loop = Task.Run(AcceptLoop);
    }

    /// <summary>A self-signed certificate for localhost and 127.0.0.1, with its private key.</summary>
    public static X509Certificate2 SelfSigned(string commonName)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN={commonName}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        // Round-trip through PFX so the key is usable by SslStream on every platform.
        return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), null);
    }

    private async Task AcceptLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception)
            {
                return;
            }
            _ = Task.Run(() => Serve(client));
        }
    }

    private async Task Serve(TcpClient client)
    {
        using (client)
        {
            try
            {
                Stream stream = client.GetStream();
                if (Certificate is not null)
                {
                    var ssl = new SslStream(stream);
                    await ssl.AuthenticateAsServerAsync(Certificate, false, false);
                    stream = ssl;
                }
                var request = await ReadRequest(stream);
                if (request is null) return;
                lock (_lock) _requests.Add(request);
                var response = _handler(request);
                var body = Encoding.UTF8.GetBytes(response.Body);
                var head = new StringBuilder()
                    .Append($"HTTP/1.1 {response.Status} {(response.Status == 200 ? "OK" : "Status")}\r\n")
                    .Append($"Content-Type: {response.ContentType}\r\n")
                    .Append($"Content-Length: {body.Length}\r\n")
                    .Append("Connection: close\r\n");
                foreach (var (key, value) in response.Headers ?? new Dictionary<string, string>()) head.Append($"{key}: {value}\r\n");
                head.Append("\r\n");
                await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()));
                await stream.WriteAsync(body);
                await stream.FlushAsync();
            }
            catch (Exception)
            {
                // Probes hang up early; that's fine.
            }
        }
    }

    private static async Task<Request?> ReadRequest(Stream stream)
    {
        var buffer = new List<byte>();
        var one = new byte[1];
        while (true)
        {
            if (await stream.ReadAsync(one) == 0) return null;
            buffer.Add(one[0]);
            if (buffer.Count >= 4 && buffer[^4] == '\r' && buffer[^3] == '\n' && buffer[^2] == '\r' && buffer[^1] == '\n') break;
            if (buffer.Count > 64 * 1024) return null;
        }
        var lines = Encoding.ASCII.GetString([.. buffer]).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var parts = lines[0].Split(' ');
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon > 0) headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }
        var length = headers.TryGetValue("Content-Length", out var text) && int.TryParse(text, out var n) ? n : 0;
        var body = new byte[length];
        var read = 0;
        while (read < length)
        {
            var got = await stream.ReadAsync(body.AsMemory(read));
            if (got == 0) break;
            read += got;
        }
        return new Request(parts[0], parts.Length > 1 ? parts[1] : "/", headers, Encoding.UTF8.GetString(body, 0, read));
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        try { await _loop; } catch (Exception) { }
        Certificate?.Dispose();
        _stop.Dispose();
    }
}

/// <summary>Something that talks first, like an SSH server.</summary>
public sealed class BannerServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public BannerServer(string banner)
    {
        _listener.Start();
        _loop = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes(banner + "\r\n"));
                    await Task.Delay(50);
                }
                catch (Exception)
                {
                    if (_stop.IsCancellationRequested) return;
                }
            }
        });
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        try { await _loop; } catch (Exception) { }
        _stop.Dispose();
    }
}

/// <summary>A port nothing listens on (bound, then released).</summary>
public static class FreePort
{
    public static int Get()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
