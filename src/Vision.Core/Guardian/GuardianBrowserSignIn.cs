using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Vision.Core;

// MARK: - Signing in with the browser (Entra SSO, passwordless people)
//
// RFC 8252 (native apps) + RFC 7636 (PKCE, S256): Vision listens on a loopback port of its own, opens
//   <guardian>/app/connect/vision?redirect_uri=http://127.0.0.1:<port>/callback&state=…&code_challenge=…
//                                 &code_challenge_method=S256&device=<this PC's name>&client=vision-windows
// in the person's browser, where Guardian signs them in however their org does (password, TOTP, Entra) and asks
// which org to connect. Guardian redirects back to the loopback address with ?code=…&state=…; the code is good for
// 60 s, once, and only with this app's verifier: POST /api/harness/connect/token {code, code_verifier, redirect_uri}.
// The listener is a bare TCP socket on 127.0.0.1 (no http.sys URL reservation, no admin rights), answers one page,
// and closes.

/// <summary>A PKCE pair and the state that ties the answer to this request.</summary>
public sealed record GuardianPkce(string Verifier, string Challenge, string State)
{
    public static GuardianPkce Create()
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        return new GuardianPkce(verifier, ChallengeFor(verifier), Base64Url(RandomNumberGenerator.GetBytes(16)));
    }

    /// <summary>S256: BASE64URL(SHA256(ASCII(verifier))).</summary>
    public static string ChallengeFor(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    public static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public override string ToString() => "GuardianPkce { … }";
}

public static class GuardianBrowserSignIn
{
    /// <summary>How long Vision waits for the person to finish in the browser.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

    /// <summary>The consent page's address. <c>client</c> names this app on the page ("Sign in to Vision for Windows") and
    /// on the sign-in Guardian keeps (Profile › API keys › Connected apps), as the Mac's <c>client=vision-mac</c> does.</summary>
    public static string ConnectUrl(string address, string redirectUri, GuardianPkce pkce, string device) =>
        $"{address.TrimEnd('/')}/app/connect/vision?redirect_uri={Uri.EscapeDataString(redirectUri)}&state={Uri.EscapeDataString(pkce.State)}"
        + $"&code_challenge={Uri.EscapeDataString(pkce.Challenge)}&code_challenge_method=S256&device={Uri.EscapeDataString(device)}"
        + $"&client={GuardianClient.ClientTag}";

    /// <summary>The whole browser sign-in: listen, open the page (<paramref name="openBrowser"/>), wait for the answer,
    /// and trade the code for a token on <paramref name="client"/>.</summary>
    public static async Task<GuardianSession> SignInAsync(GuardianClient client, Action<string> openBrowser, string? device = null,
                                                          TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var pkce = GuardianPkce.Create();
        using var receiver = GuardianLoopbackReceiver.Start();
        var url = ConnectUrl(client.Address, receiver.RedirectUri, pkce, device ?? DeviceName());
        try
        {
            openBrowser(url);
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            throw new GuardianError(GuardianErrorKind.BrowserSignIn, $"Vision couldn't open your browser ({error.Message}). Sign in with your email and password instead.");
        }
        var code = await receiver.WaitForCodeAsync(pkce.State, timeout ?? DefaultTimeout, cancellationToken).ConfigureAwait(false);
        return await client.ExchangeConnectCodeAsync(code, pkce.Verifier, receiver.RedirectUri, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>"PATRICK-PC" — what Guardian's Connected apps list shows for this sign-in.</summary>
    public static string DeviceName()
    {
        try
        {
            return $"Vision for Windows on {Environment.MachineName}";
        }
        catch (InvalidOperationException)
        {
            return "Vision for Windows";
        }
    }
}

/// <summary>A one-shot HTTP listener on 127.0.0.1 for the browser's redirect.</summary>
public sealed class GuardianLoopbackReceiver : IDisposable
{
    private readonly TcpListener _listener;
    private bool _disposed;

    private GuardianLoopbackReceiver(TcpListener listener)
    {
        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>Listen on a free loopback port.</summary>
    public static GuardianLoopbackReceiver Start()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return new GuardianLoopbackReceiver(listener);
    }

    public int Port { get; }

    /// <summary>Exactly the shape Guardian accepts: http://127.0.0.1:&lt;port&gt;/callback.</summary>
    public string RedirectUri => $"http://127.0.0.1:{Port}/callback";

    /// <summary>Answer requests until one to /callback carries <paramref name="state"/>; its code. A request with another
    /// state (a stale tab, something probing the port) is turned away and the wait goes on. Guardian's "deny" ends it.</summary>
    public async Task<string> WaitForCodeAsync(string state, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        while (true)
        {
            TcpClient connection;
            try
            {
                connection = await _listener.AcceptTcpClientAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new GuardianError(GuardianErrorKind.BrowserSignIn,
                    $"Nothing came back from the browser within {(int)timeout.TotalMinutes} minutes. Start the sign-in again when you're ready.");
            }
            using (connection)
            {
                var outcome = await HandleAsync(connection, state, cts.Token).ConfigureAwait(false);
                if (outcome is { Code: { } code }) return code;
                if (outcome is { Error: { } error }) throw error;
            }
        }
    }

    private sealed record Outcome(string? Code, GuardianError? Error);

    private static async Task<Outcome?> HandleAsync(TcpClient connection, string state, CancellationToken cancellationToken)
    {
        var stream = connection.GetStream();
        string? target;
        try
        {
            target = await ReadRequestTargetAsync(stream, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or SocketException or ObjectDisposedException)
        {
            return null;
        }
        if (target is null || !target.StartsWith("/callback", StringComparison.Ordinal))
        {
            await RespondAsync(stream, 404, "Not here", "Vision is waiting for Guardian's sign-in answer on this address.", cancellationToken).ConfigureAwait(false);
            return null;
        }
        var query = ParseQuery(target);
        if (!query.TryGetValue("state", out var got) || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(got), Encoding.UTF8.GetBytes(state)))
        {
            await RespondAsync(stream, 400, "Not this sign-in",
                "This answer isn't for the sign-in Vision is waiting for (it may be from an older tab). Go back to Vision and start again if you need to.",
                cancellationToken).ConfigureAwait(false);
            return null;
        }
        if (query.TryGetValue("error", out var refused))
        {
            var denied = refused == "access_denied";
            await RespondAsync(stream, 200, denied ? "Not connected" : "Sign-in didn't finish",
                denied ? "You chose not to connect Vision. You can close this tab." : "Guardian couldn't finish the sign-in. Go back to Vision to try again.",
                cancellationToken).ConfigureAwait(false);
            var described = query.GetValueOrDefault("error_description");
            return new Outcome(null, new GuardianError(GuardianErrorKind.BrowserSignIn, denied
                ? "You chose Deny in Guardian, so Vision isn't connected."
                : $"Guardian couldn't finish the sign-in ({described ?? refused})."));
        }
        if (!query.TryGetValue("code", out var code) || code.Length == 0)
        {
            await RespondAsync(stream, 400, "Sign-in didn't finish", "Guardian's answer had no code. Go back to Vision to try again.", cancellationToken).ConfigureAwait(false);
            return new Outcome(null, new GuardianError(GuardianErrorKind.BrowserSignIn, "Guardian's answer had no sign-in code. Try again."));
        }
        await RespondAsync(stream, 200, "Vision is connected", "You're signed in to Guardian in Vision. You can close this tab and go back to the app.", cancellationToken)
            .ConfigureAwait(false);
        return new Outcome(code, null);
    }

    /// <summary>The request line's target ("/callback?code=…"), reading at most 16 KB of headers.</summary>
    private static async Task<string?> ReadRequestTargetAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        var total = 0;
        using var read = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        read.CancelAfter(TimeSpan.FromSeconds(10));
        while (total < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(total), read.Token).ConfigureAwait(false);
            if (n == 0) break;
            total += n;
            if (Encoding.ASCII.GetString(buffer, 0, total).Contains("\r\n\r\n", StringComparison.Ordinal)) break;
        }
        var head = Encoding.ASCII.GetString(buffer, 0, total);
        var line = head.Split("\r\n", 2)[0];
        var parts = line.Split(' ');
        return parts.Length >= 2 && parts[0] == "GET" ? parts[1] : null;
    }

    /// <summary>A request target's query, decoded ("/callback?code=a%2Bb" → code = "a+b").</summary>
    public static Dictionary<string, string> ParseQuery(string target)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var at = target.IndexOf('?');
        if (at < 0) return result;
        foreach (var pair in target[(at + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var key = Uri.UnescapeDataString((eq < 0 ? pair : pair[..eq]).Replace('+', ' '));
            var value = eq < 0 ? "" : Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
            result.TryAdd(key, value);
        }
        return result;
    }

    private static async Task RespondAsync(NetworkStream stream, int status, string title, string message, CancellationToken cancellationToken)
    {
        var html = "<!doctype html><html><head><meta charset=\"utf-8\"><title>Vision</title>"
                   + "<style>body{font-family:'Segoe UI',system-ui,sans-serif;max-width:32rem;margin:15vh auto;padding:0 1.5rem;color:#1b1b1b}"
                   + "h1{font-size:1.4rem;font-weight:600}p{color:#555;line-height:1.5}@media(prefers-color-scheme:dark){body{background:#202020;color:#eee}p{color:#bbb}}</style>"
                   + $"</head><body><h1>{WebUtility.HtmlEncode(title)}</h1><p>{WebUtility.HtmlEncode(message)}</p></body></html>";
        var body = Encoding.UTF8.GetBytes(html);
        var reason = status switch { 200 => "OK", 400 => "Bad Request", _ => "Not Found" };
        var header = $"HTTP/1.1 {status} {reason}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\n"
                     + "Cache-Control: no-store\r\nReferrer-Policy: no-referrer\r\nConnection: close\r\n\r\n";
        try
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes(header), cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or SocketException or ObjectDisposedException)
        {
            // The browser went away: the outcome stands.
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _listener.Stop(); } catch (SocketException) { }
    }
}
