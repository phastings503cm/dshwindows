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
// GET /api/status, POST /api/swap — and, for the setup guide, GET /api/session, POST /api/setup (the
// first admin), GET /api/provisioning + POST /api/install + GET /api/provisioning/job (installs with
// a live log), GET /api/credentials (the model API key) and GET /cert.crt.
//
// Every state change is a JSON POST carrying an X-Swapper header and an Origin matching the Host,
// which is what the service checks before it believes a request isn't cross-site.
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

/// <summary>One line of a job's log: seconds since the epoch, and the text.</summary>
public sealed record SwapperLogLine(double T, string M);

public sealed record SwapperJob(
    string Id, string Target, string? Source, string State, string? Error, string? Note,
    double Started, double? Finished, IReadOnlyList<SwapperStep>? Steps,
    IReadOnlyList<SwapperLogLine>? Log = null, int? LogTotal = null)
{
    public bool IsRunning => State == "running";
    public bool IsDone => State == "done";
    public bool IsFailed => State == "failed";

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

/// <summary>GET /api/session: whether anyone has created the admin login yet, and who we are.</summary>
public sealed record SwapperSession(bool SetupNeeded, string? User, string? Role, bool Demo);

/// <summary>One row of the Provisioning tab: git, docker, a model recipe, OpenClaw, SSL.</summary>
public sealed record SwapperProvisionItem(string Action, bool Present, string? Detail, JsonObject Raw)
{
    /// <summary>What the Provisioning tab calls it.</summary>
    public string Title => Action switch
    {
        "git" => "git",
        "docker" => "Docker + NVIDIA GPU passthrough",
        "sglang_recipe" => "Qwen3.8 27B (SGLang) recipe",
        "flash_recipe" => "Qwen3.8 Flash Next (vLLM) recipe",
        "openclaw" => "OpenClaw",
        "ssl" => "Self-signed SSL / HTTPS model front",
        _ => Action,
    };
}

/// <summary>GET /api/credentials: the model API key and address, and the OpenClaw gateway token.</summary>
public sealed record SwapperCredentials(
    string? ApiKey, string? ApiBaseUrl, string? ApiModel, string? GatewayToken, string? GatewayPassword, string? DashboardUrl);

public enum SwapperErrorKind { NotConfigured, UntrustedCertificate, BadLogin, Server, Transport }

public sealed class SwapperException : Exception
{
    public SwapperErrorKind Kind { get; }
    public string? Fingerprint { get; }
    public int StatusCode { get; }
    /// <summary>The service's own words ("Use a password of at least 8 characters."), when it sent any.</summary>
    public string? Detail { get; }

    private SwapperException(SwapperErrorKind kind, string message, string? fingerprint = null, int statusCode = 0, string? detail = null)
        : base(message)
    {
        Kind = kind;
        Fingerprint = fingerprint;
        StatusCode = statusCode;
        Detail = detail;
    }

    public static SwapperException NotConfigured() => new(SwapperErrorKind.NotConfigured,
        "The Spark swapper isn't set up. Add its address and login in Settings › Spark.");

    public static SwapperException UntrustedCertificate(string fingerprint) => new(SwapperErrorKind.UntrustedCertificate,
        $"The Spark swapper's certificate isn't trusted yet (SHA-256 {TextUtil.Prefix(fingerprint, 23)}…). Approve it in Settings › Spark.",
        fingerprint);

    public static SwapperException BadLogin(string message) =>
        new(SwapperErrorKind.BadLogin, $"Spark swapper login failed: {message}", detail: message);

    public static SwapperException Server(int code, string message) =>
        new(SwapperErrorKind.Server, $"Spark swapper replied {code}: {message}", statusCode: code, detail: message);

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
        if (body is not null)
        {
            // The service refuses a POST whose Origin isn't its own Host ("scheme://" + Host header).
            req.Headers.TryAddWithoutValidation("Origin", OriginFor(BaseUrl));
            req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        }
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

    /// <summary>"https://192.168.1.42:8999" — what a browser on the swapper's own page sends.</summary>
    public static string OriginFor(Uri baseUrl) => $"{baseUrl.Scheme}://{baseUrl.Authority}";

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

    // MARK: - Setup (the guide)

    /// <summary>GET /api/session — needs no login.</summary>
    public async Task<SwapperSession> SessionAsync(CancellationToken cancellationToken = default)
    {
        var (code, body) = await RequestAsync("api/session", null, cancellationToken).ConfigureAwait(false);
        if (code != 200) throw SwapperException.Server(code, Message(body));
        var obj = Object(body, "session");
        return new SwapperSession(
            JsonArgs.Bool(obj, "setup_needed", false), JsonArgs.String(obj, "user"), JsonArgs.String(obj, "role"),
            JsonArgs.Bool(obj, "demo", false));
    }

    /// <summary>POST /api/setup: create the admin login with this client's username and password.
    /// Only the first visitor can; afterwards the service answers 409.</summary>
    public async Task SetupAdminAsync(CancellationToken cancellationToken = default)
    {
        var (code, body) = await RequestAsync("api/setup",
            new JsonObject { ["username"] = _username, ["password"] = _password }, cancellationToken).ConfigureAwait(false);
        if (code != 200) throw SwapperException.Server(code, Message(body));
        lock (_lock) _loggedIn = true;
    }

    /// <summary>GET /api/provisioning: what is already on the Spark, per install action.</summary>
    public async Task<IReadOnlyList<SwapperProvisionItem>> ProvisioningAsync(CancellationToken cancellationToken = default)
    {
        var obj = Object(await AuthedAsync("api/provisioning", null, cancellationToken).ConfigureAwait(false), "provisioning");
        return ParseProvisioning(obj);
    }

    internal static IReadOnlyList<SwapperProvisionItem> ParseProvisioning(JsonObject obj)
    {
        string[] order = ["git", "docker", "sglang_recipe", "flash_recipe", "openclaw", "ssl"];
        int Rank(string action) => Array.IndexOf(order, action) is var at && at >= 0 ? at : order.Length;
        return obj.Where(p => p.Value is JsonObject)
            .Select(p => (Key: p.Key, Item: (JsonObject)p.Value!))
            .Select(p => new SwapperProvisionItem(p.Key, JsonArgs.Bool(p.Item, "present", false), JsonArgs.String(p.Item, "detail"), p.Item))
            .OrderBy(i => Rank(i.Action))
            .ToList();
    }

    /// <summary>POST /api/install: start one provisioning action (409 while another runs).</summary>
    public async Task<SwapperJob?> InstallAsync(string action, CancellationToken cancellationToken = default)
    {
        var text = await AuthedAsync("api/install", new JsonObject { ["action"] = action }, cancellationToken).ConfigureAwait(false);
        return ParseJob(Object(text, "install")["job"]);
    }

    /// <summary>GET /api/provisioning/job: the install that is running or last ran, with its log.</summary>
    public async Task<SwapperJob?> ProvisioningJobAsync(CancellationToken cancellationToken = default)
    {
        var text = await AuthedAsync("api/provisioning/job", null, cancellationToken).ConfigureAwait(false);
        return ParseJob(Object(text, "provisioning job")["job"]);
    }

    /// <summary>POST /api/swap, returning the swap job the service started.</summary>
    public async Task<SwapperJob?> StartSwapAsync(string key, CancellationToken cancellationToken = default)
    {
        var text = await AuthedAsync("api/swap", new JsonObject { ["target"] = key }, cancellationToken).ConfigureAwait(false);
        return ParseJob(Object(text, "swap")["job"]);
    }

    private static SwapperJob? ParseJob(JsonNode? node)
    {
        if (node is not JsonObject job) return null;
        try
        {
            return job.Deserialize<SwapperJob>(SwapperStatus.Json);
        }
        catch (JsonException ex)
        {
            throw SwapperException.Transport($"unexpected job payload ({ex.Message})");
        }
    }

    /// <summary>GET /api/credentials: the model API key (what DSH needs to talk to the model) and
    /// the OpenClaw gateway token.</summary>
    public async Task<SwapperCredentials> CredentialsAsync(CancellationToken cancellationToken = default)
    {
        var obj = Object(await AuthedAsync("api/credentials", null, cancellationToken).ConfigureAwait(false), "credentials");
        return ParseCredentials(obj);
    }

    internal static SwapperCredentials ParseCredentials(JsonObject obj)
    {
        var api = obj["api"] as JsonObject ?? new JsonObject();
        var openclaw = obj["openclaw"] as JsonObject ?? new JsonObject();
        return new SwapperCredentials(
            JsonArgs.String(api, "key"), JsonArgs.String(api, "base_url"), JsonArgs.String(api, "model"),
            JsonArgs.String(openclaw, "token"), JsonArgs.String(openclaw, "password"), JsonArgs.String(openclaw, "dashboard_url"));
    }

    /// <summary>GET /cert.crt: the service's own certificate (PEM), for trusting it elsewhere.</summary>
    public async Task<X509Certificate2> DownloadCertificateAsync(CancellationToken cancellationToken = default)
    {
        var (code, body) = await RequestAsync("cert.crt", null, cancellationToken).ConfigureAwait(false);
        if (code != 200) throw SwapperException.Server(code, Message(body));
        try
        {
            return X509Certificate2.CreateFromPem(body);
        }
        catch (CryptographicException ex)
        {
            throw SwapperException.Transport($"the certificate download wasn't a certificate ({ex.Message})");
        }
    }

    private static JsonObject Object(string body, string what)
    {
        try
        {
            if (JsonNode.Parse(body) is JsonObject obj) return obj;
        }
        catch (JsonException)
        {
        }
        throw SwapperException.Transport($"unexpected {what} payload");
    }

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

/// <summary>Follows a job log across polls. The service returns the newest 400 lines each time (and
/// its line count stops growing at 1,500), so new lines are found by overlapping the previous
/// snapshot with the new one rather than by counting.</summary>
public sealed class SwapperLogTail
{
    private List<SwapperLogLine> _previous = [];
    private string? _jobId;

    /// <summary>The lines in <paramref name="job"/>'s log that weren't in the last snapshot.</summary>
    public IReadOnlyList<SwapperLogLine> Next(SwapperJob? job)
    {
        if (job is null) return [];
        if (job.Id != _jobId)
        {
            _jobId = job.Id;
            _previous = [];
        }
        var current = job.Log?.ToList() ?? [];
        var fresh = NewLines(_previous, current);
        _previous = current;
        return fresh;
    }

    /// <summary>The part of <paramref name="current"/> after its longest overlap with the end of
    /// <paramref name="previous"/>.</summary>
    public static IReadOnlyList<SwapperLogLine> NewLines(IReadOnlyList<SwapperLogLine> previous, IReadOnlyList<SwapperLogLine> current)
    {
        for (var offset = 0; offset < previous.Count; offset++)
        {
            var overlap = previous.Count - offset;
            if (overlap > current.Count) continue;
            var matches = true;
            for (var i = 0; i < overlap && matches; i++) matches = previous[offset + i] == current[i];
            if (matches) return current.Skip(overlap).ToList();
        }
        return current;
    }
}
