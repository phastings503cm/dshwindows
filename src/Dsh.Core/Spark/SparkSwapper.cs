using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dsh.Core;

// MARK: - Spark Swapper client
//
// Talks to the Spark Swapper web service (https://<spark>:8999) that stops one model server on the
// DGX Spark and starts another. Same API the web page uses: POST /api/login (cookie session),
// GET /api/status, POST /api/swap.
//
// The service uses a self-signed certificate. If the system doesn't trust it, the client pins the
// leaf certificate's SHA-256 fingerprint the user approved once (trust on first use) and refuses
// anything else.

public sealed record SwapperModel(
    string Key, string Title, string? Tagline, string? Engine, int Context, int? ServedContext,
    string ServedId, bool? Vision, bool Running, bool Healthy, int? Order)
{
    public string Id => Key;
}

public sealed record SwapperStep(string Key, string Label, string State);

public sealed record SwapperJob(
    string Id, string Target, string? Source, string State, string? Error, string? Note,
    double Started, double? Finished, IReadOnlyList<SwapperStep>? Steps)
{
    public bool IsRunning => State == "running";

    public SwapperStep? CurrentStep =>
        Steps?.FirstOrDefault(s => s.State == "running") ?? Steps?.LastOrDefault(s => s.State == "failed");
}

public sealed record SwapperStatus(
    string? Active, string? Loading, bool Busy, IReadOnlyDictionary<string, SwapperModel>? Models,
    SwapperJob? Job, string? OpenclawPrimary)
{
    public IReadOnlyDictionary<string, SwapperModel> AllModels => Models ?? new Dictionary<string, SwapperModel>();

    /// <summary>Models in display order.</summary>
    public IReadOnlyList<SwapperModel> Ordered =>
        AllModels.Values.OrderBy(m => m.Order ?? 9).ThenBy(m => m.Title, StringComparer.Ordinal).ToList();

    public SwapperModel? ActiveModel => Active is not null && AllModels.TryGetValue(Active, out var m) ? m : null;

    public bool IsSwitching => Busy || (Job?.IsRunning ?? false);

    /// <summary>Match user input ("flash", "27b", "standard", a served id) to a model key.</summary>
    public string? Resolve(string query)
    {
        var q = query.Trim().ToLowerInvariant();
        if (q.Length == 0) return null;
        if (AllModels.ContainsKey(q)) return q;
        var hits = AllModels.Values.Where(m =>
            m.ServedId.ToLowerInvariant() == q || m.Title.ToLowerInvariant() == q
            || m.Key.ToLowerInvariant().Contains(q) || m.Title.ToLowerInvariant().Contains(q)
            || m.ServedId.ToLowerInvariant().Contains(q)).ToList();
        return hits.Count == 1 ? hits[0].Key : null;
    }

    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };
}

public enum SwapperErrorKind { NotConfigured, UntrustedCertificate, BadLogin, Server, Transport }

public sealed class SwapperException : Exception
{
    public SwapperErrorKind Kind { get; }
    public string? Fingerprint { get; }
    public int StatusCode { get; }

    private SwapperException(SwapperErrorKind kind, string message, string? fingerprint = null, int statusCode = 0)
        : base(message)
    {
        Kind = kind;
        Fingerprint = fingerprint;
        StatusCode = statusCode;
    }

    public static SwapperException NotConfigured() => new(SwapperErrorKind.NotConfigured,
        "The Spark swapper isn't set up. Add its address and login in Settings › Spark.");

    public static SwapperException UntrustedCertificate(string fingerprint) => new(SwapperErrorKind.UntrustedCertificate,
        $"The Spark swapper's certificate isn't trusted yet (SHA-256 {TextUtil.Prefix(fingerprint, 23)}…). Approve it in Settings › Spark.",
        fingerprint);

    public static SwapperException BadLogin(string message) => new(SwapperErrorKind.BadLogin, $"Spark swapper login failed: {message}");

    public static SwapperException Server(int code, string message) =>
        new(SwapperErrorKind.Server, $"Spark swapper replied {code}: {message}", statusCode: code);

    public static SwapperException Transport(string message) =>
        new(SwapperErrorKind.Transport, $"Couldn't reach the Spark swapper: {message}");
}

public sealed class SparkSwapperClient : IDisposable
{
    public Uri BaseUrl { get; }
    private readonly string _username;
    private readonly string _password;
    public string? PinnedFingerprint { get; }
    private readonly HttpClient _http;
    private readonly Lock _lock = new();
    private string? _seenFingerprint;
    private bool _loggedIn;

    public SparkSwapperClient(Uri baseUrl, string username, string password, string? pinnedFingerprint)
    {
        BaseUrl = baseUrl;
        _username = username;
        _password = password;
        PinnedFingerprint = pinnedFingerprint;
        var handler = new SocketsHttpHandler
        {
            CookieContainer = new CookieContainer(),
            UseCookies = true,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = Validate },
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
    }

    public void Dispose() => _http.Dispose();

    /// <summary>The fingerprint of the certificate the server presented when system trust failed.</summary>
    public string? SeenFingerprint
    {
        get { lock (_lock) return _seenFingerprint; }
    }

    /// <summary>Trust handling: system trust first, then the one pinned fingerprint.</summary>
    private bool Validate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        if (errors == SslPolicyErrors.None) return true;
        if (certificate is null) return false;
        var fingerprint = Fingerprint(certificate.GetRawCertData());
        lock (_lock) _seenFingerprint = fingerprint;
        return PinnedFingerprint is not null && string.Equals(fingerprint, PinnedFingerprint, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>"AB:CD:…" — SHA-256 of the DER certificate, as openssl prints it.</summary>
    public static string Fingerprint(byte[] der) =>
        string.Join(":", SHA256.HashData(der).Select(b => b.ToString("X2")));

    /// <summary>A reasonable swapper address for a model server URL: same host, port 8999.</summary>
    public static string? DefaultUrl(string modelServerBase) =>
        Uri.TryCreate(modelServerBase, UriKind.Absolute, out var uri) && uri.Host.Length > 0
            ? $"https://{uri.Host}:8999"
            : null;

    private async Task<(int Code, string Body)> RequestAsync(string path, JsonObject? body = null,
                                                              CancellationToken cancellationToken = default)
    {
        using var req = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, new Uri(BaseUrl, path));
        req.Headers.TryAddWithoutValidation("X-Swapper", "1");
        if (body is not null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        try
        {
            using var response = await _http.SendAsync(req, cancellationToken).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return ((int)response.StatusCode, text);
        }
        catch (HttpRequestException ex)
        {
            if (SeenFingerprint is { } fp && !string.Equals(fp, PinnedFingerprint, StringComparison.OrdinalIgnoreCase))
                throw SwapperException.UntrustedCertificate(fp);
            throw SwapperException.Transport(ex.InnerException?.Message ?? ex.Message);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw SwapperException.Transport("no answer within 20 s");
        }
    }

    private static string Message(string body)
    {
        try
        {
            if (JsonNode.Parse(body) is JsonObject obj && JsonArgs.String(obj, "error") is { } error) return error;
        }
        catch (JsonException) { }
        return TextUtil.Prefix(body, 200);
    }

    public async Task LoginAsync(CancellationToken cancellationToken = default)
    {
        var (code, body) = await RequestAsync("api/login",
            new JsonObject { ["username"] = _username, ["password"] = _password }, cancellationToken).ConfigureAwait(false);
        if (code != 200) throw SwapperException.BadLogin(Message(body));
        lock (_lock) _loggedIn = true;
    }

    /// <summary>Call <paramref name="path"/>, logging in first (or again, after a 401).</summary>
    private async Task<string> AuthedAsync(string path, JsonObject? body, CancellationToken cancellationToken)
    {
        bool loggedIn;
        lock (_lock) loggedIn = _loggedIn;
        if (!loggedIn) await LoginAsync(cancellationToken).ConfigureAwait(false);
        var (code, text) = await RequestAsync(path, body, cancellationToken).ConfigureAwait(false);
        if (code == 401)
        {
            await LoginAsync(cancellationToken).ConfigureAwait(false);
            (code, text) = await RequestAsync(path, body, cancellationToken).ConfigureAwait(false);
        }
        if (code is < 200 or > 299) throw SwapperException.Server(code, Message(text));
        return text;
    }

    public async Task<SwapperStatus> StatusAsync(CancellationToken cancellationToken = default)
    {
        var text = await AuthedAsync("api/status", null, cancellationToken).ConfigureAwait(false);
        try
        {
            return JsonSerializer.Deserialize<SwapperStatus>(text, SwapperStatus.Json)
                   ?? throw SwapperException.Transport("unexpected status payload (empty)");
        }
        catch (JsonException ex)
        {
            throw SwapperException.Transport($"unexpected status payload ({ex.Message})");
        }
    }

    public Task SwapAsync(string key, CancellationToken cancellationToken = default) =>
        AuthedAsync("api/swap", new JsonObject { ["target"] = key }, cancellationToken);

    /// <summary>Connect without a pin to learn the certificate fingerprint to show the user.</summary>
    public static async Task<string?> ProbeFingerprintAsync(Uri url)
    {
        using var probe = new SparkSwapperClient(url, "", "", null);
        try
        {
            await probe.RequestAsync("api/session").ConfigureAwait(false);
        }
        catch (SwapperException)
        {
            // Expected when the certificate isn't trusted.
        }
        return probe.SeenFingerprint;
    }
}
