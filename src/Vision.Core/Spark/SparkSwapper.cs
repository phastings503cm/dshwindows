using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Vision.Core;

// MARK: - Spark Swapper client
//
// Talks to the Spark Swapper web service (https://<spark>:8999) that stops one model server on the
// DGX Spark (or, from Swapper 2.0, an AMD Strix Halo box) and starts another. Same API the web page
// uses: POST /api/login (cookie session), GET /api/status, POST /api/swap — and, for the setup guide,
// GET /api/session, POST /api/setup (the first admin), GET /api/provisioning + POST /api/install +
// GET /api/provisioning/job (installs with a live log), GET /api/credentials (the model API key) and
// GET /cert.crt.
//
// Swapper 2.0 adds app tokens: a connection code (see SwapperConnectionCode) carries one, and with it
// every request goes out with "Authorization: Bearer …" instead of a login. GET /api/connect says how
// to reach the box's model API, and POST /api/tokens/create makes a token for a signed-in user.
//
// Every state change is a JSON POST carrying an X-Swapper header and an Origin matching the Host,
// which is what the service checks before it believes a request isn't cross-site.
//
// The service uses a self-signed certificate. If the system doesn't trust it, the client pins the
// leaf certificate's SHA-256 fingerprint the user approved once (trust on first use) and refuses
// anything else.

/// <summary>A model the Swapper can serve. The fields after <paramref name="Order"/> came with Swapper 2.0
/// and are missing from an older one.</summary>
/// <param name="Installed">False: in the catalog for this box's platform but not installed yet (a swap to it is
/// refused; it can be installed). Missing (an older Swapper) means installed.</param>
/// <param name="Platform">"nvidia-gb10" or "amd-strix-halo".</param>
/// <param name="Perf">Published speeds for this model on this kind of box.</param>
/// <param name="Measured">The decode speed the Swapper measured on this box the last time it loaded the model.</param>
/// <param name="Install">How big installing it is and how long it takes (catalog models).</param>
/// <param name="Recommended">The catalog's pick for this kind of box.</param>
/// <param name="Eta">How long switching to it takes, in words ("about a minute").</param>
/// <param name="SupersededBy">The key of the model that replaces this one.</param>
public sealed record SwapperModel(
    string Key, string Title, string? Tagline, string? Engine, int Context, int? ServedContext,
    string ServedId, bool? Vision, bool Running, bool Healthy, int? Order,
    bool? Installed = null, string? Platform = null, SwapperPerf? Perf = null, SwapperMeasured? Measured = null,
    string? Family = null, SwapperInstallInfo? Install = null, bool? Recommended = null, string? Eta = null,
    string? SupersededBy = null, string? InstallDetail = null)
{
    public string Id => Key;

    public bool IsInstalled => Installed ?? true;

    public bool IsRecommended => Recommended == true;

    /// <summary>The window it is served with (a box may serve more than the model's native context).</summary>
    public int EffectiveContext => ServedContext ?? Context;

    /// <summary>"27 tok/s measured here", "~58 tok/s", "≈35–50 tok/s (estimate)" — or null when nothing is known.</summary>
    public string? Speed(bool includeEstimate = true) => SwapperSpeed.Label(this, includeEstimate);

    /// <summary>"60 GB · 15–30 minutes: …" — what installing it takes, when the catalog says.</summary>
    public string? InstallSummary
    {
        get
        {
            if (Install is not { } install) return null;
            var parts = new List<string>();
            if (install.SizeGb is > 0 and var size) parts.Add($"{size.ToString("0.#", CultureInfo.InvariantCulture)} GB");
            if (install.Time is { Length: > 0 } time) parts.Add(time);
            return parts.Count == 0 ? null : string.Join(" · ", parts);
        }
    }
}

/// <summary>What installing a catalog model takes: its size on disk and the time it needs, in words.</summary>
public sealed record SwapperInstallInfo(double? SizeGb, string? Time);

/// <summary>Published speeds in tokens per second: <see cref="Decode"/> is one request on its own,
/// <see cref="DecodeConcurrent"/> the total across <see cref="Concurrency"/> requests at once.
/// <see cref="Estimate"/>: nobody measured it; a rough guess (with <see cref="Range"/>, "30–50", when given).</summary>
public sealed record SwapperPerf(double? Decode, double? DecodeConcurrent, int? Concurrency, double? Prefill, string? Source,
                                 bool? Estimate, string? Range = null);

/// <summary>The decode speed the Swapper measured itself, and when (seconds since the epoch).</summary>
public sealed record SwapperMeasured(double? Decode, double? At);

/// <summary>How fast a model is, in words.</summary>
public static class SwapperSpeed
{
    /// <summary>Measured here first, then the published figure; an estimate only when there's room for it, and
    /// never worded as a measurement.</summary>
    public static string? Label(SwapperModel model, bool includeEstimate = true)
    {
        if (model.Measured?.Decode is > 0 and var measured)
            return $"{Math.Round(measured).ToString(CultureInfo.InvariantCulture)} tok/s measured here";
        if (model.Perf?.Decode is not (> 0 and var decode)) return null;
        if (model.Perf.Estimate != true) return $"~{Math.Round(decode).ToString(CultureInfo.InvariantCulture)} tok/s";
        if (!includeEstimate) return null;
        if (model.Perf.Range is { Length: > 0 } range) return $"≈{range.Replace("-", "–")} tok/s (estimate)";
        // A guess is a range: about ±15%, to the nearest 5.
        var low = (int)(Math.Floor(decode * 0.85 / 5) * 5);
        var high = (int)(Math.Ceiling(decode * 1.15 / 5) * 5);
        return low > 0 && low < high
            ? $"≈{low.ToString(CultureInfo.InvariantCulture)}–{high.ToString(CultureInfo.InvariantCulture)} tok/s (estimate)"
            : $"≈{Math.Round(decode).ToString(CultureInfo.InvariantCulture)} tok/s (estimate)";
    }
}

/// <summary>The kinds of box a Swapper runs on.</summary>
public static class SwapperPlatforms
{
    public const string DgxSpark = "nvidia-gb10";
    public const string StrixHalo = "amd-strix-halo";

    /// <summary>"DGX Spark", "Strix Halo" — or null when the Swapper doesn't say (an older one, or "unknown").</summary>
    public static string? Label(string? platform) => platform switch
    {
        DgxSpark => "DGX Spark",
        StrixHalo => "Strix Halo",
        _ => null,
    };
}

public sealed record SwapperStep(string Key, string Label, string State);

/// <summary>One line of a job's log: seconds since the epoch, and the text.</summary>
public sealed record SwapperLogLine(double T, string M);

public sealed record SwapperJob(
    string Id, string Target, string? Source, string State, string? Error, string? Note,
    double Started, double? Finished, IReadOnlyList<SwapperStep>? Steps,
    IReadOnlyList<SwapperLogLine>? Log = null, int? LogTotal = null)
{
    /// <summary>The model a model-install job is for: "install:model:tf27b" → "tf27b"; null for any other job.</summary>
    public static string? ModelOf(string target) =>
        target.StartsWith("install:model:", StringComparison.Ordinal) ? target["install:model:".Length..] : null;

    public bool IsRunning => State == "running";
    public bool IsDone => State == "done";
    public bool IsFailed => State == "failed";

    public SwapperStep? CurrentStep =>
        Steps?.FirstOrDefault(s => s.State == "running") ?? Steps?.LastOrDefault(s => s.State == "failed");
}

/// <summary>GET /api/status. <paramref name="Version"/>, <paramref name="Platform"/>, <paramref name="Hostname"/> and
/// <paramref name="InstallJob"/> (an install running, or one that ended in the last ten minutes) came with Swapper 2.0.</summary>
public sealed record SwapperStatus(
    string? Active, string? Loading, bool Busy, IReadOnlyDictionary<string, SwapperModel>? Models,
    SwapperJob? Job, string? OpenclawPrimary, string? Version = null, string? Platform = null, string? Hostname = null,
    SwapperJob? InstallJob = null)
{
    public IReadOnlyDictionary<string, SwapperModel> AllModels => Models ?? new Dictionary<string, SwapperModel>();

    /// <summary>Models in display order.</summary>
    public IReadOnlyList<SwapperModel> Ordered =>
        AllModels.Values.OrderBy(m => m.Order ?? 9).ThenBy(m => m.Title, StringComparer.Ordinal).ToList();

    /// <summary>The models that can be switched to (installed ones), in display order.</summary>
    public IReadOnlyList<SwapperModel> Installed => Ordered.Where(m => m.IsInstalled).ToList();

    /// <summary>The catalog's pick: a recommended installed model, else a recommended one, else the first.</summary>
    public SwapperModel? Recommended =>
        Ordered.FirstOrDefault(m => m.IsRecommended && m.IsInstalled) ?? Ordered.FirstOrDefault(m => m.IsRecommended)
        ?? Installed.FirstOrDefault() ?? Ordered.FirstOrDefault();

    /// <summary>The model a running install is for ("install:model:tf27b" → tf27b), if one is running.</summary>
    public string? Installing => InstallJob is { IsRunning: true } job ? SwapperJob.ModelOf(job.Target) : null;

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

/// <summary>GET /api/session: whether anyone has created the admin login yet, and who we are — and, from
/// Swapper 2.0, which Swapper and box this is.</summary>
public sealed record SwapperSession(bool SetupNeeded, string? User, string? Role, bool Demo,
                                    string? Version = null, string? Platform = null, string? Hostname = null);

/// <summary>GET /api/connect (Swapper 2.0): everything an app needs to use the box — its name, the model API's
/// address and key, what it serves, and the certificate fingerprint that pins both the Swapper and the model
/// API (they present the same certificate). <see cref="Model"/> and <see cref="Context"/> are null when
/// nothing is serving.</summary>
public sealed record SwapperConnectInfo(
    string Name, string? Hostname, string? Platform, string? Version, string SwapperUrl, string ApiBase, string? ApiKey,
    string? Model, int? Context, string? Fingerprint)
{
    internal static SwapperConnectInfo? Parse(JsonObject obj)
    {
        var swapper = JsonArgs.String(obj, "swapper_url");
        var api = JsonArgs.String(obj, "api_base");
        if (string.IsNullOrWhiteSpace(swapper) || string.IsNullOrWhiteSpace(api)) return null;
        var hostname = JsonArgs.String(obj, "hostname");
        var platform = JsonArgs.String(obj, "platform");
        var name = JsonArgs.String(obj, "name") is { Length: > 0 } given
            ? given
            : SwapperConnectionCode.DefaultName(platform, hostname, swapper);
        return new SwapperConnectInfo(name, hostname, platform, JsonArgs.String(obj, "version"), swapper.Trim(), api.Trim(),
            JsonArgs.String(obj, "api_key"), JsonArgs.String(obj, "model"), JsonNumbers.Positive(obj, "context"),
            CertificateProbe.NormalizeFingerprint(JsonArgs.String(obj, "fingerprint")));
    }
}

/// <summary>POST /api/tokens/create: a new app token, the connection code that carries it, and the connect info.</summary>
public sealed record SwapperTokenGrant(string Id, string Token, string? Code, SwapperConnectInfo? Connect);

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
        "vulkan" => "Vulkan GPU drivers",
        "llamacpp" => "llama.cpp",
        "gpumem" => "GPU memory limit",
        _ => Action,
    };
}

/// <summary>GET /api/credentials: the model API key and address, and the OpenClaw gateway token.</summary>
public sealed record SwapperCredentials(
    string? ApiKey, string? ApiBaseUrl, string? ApiModel, string? GatewayToken, string? GatewayPassword, string? DashboardUrl);

public enum SwapperErrorKind { NotConfigured, UntrustedCertificate, BadLogin, Server, Transport, Revoked, NotInstalled }

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
        "No Spark Swapper is set up. Add one in Settings › Model servers (paste its connection code).");

    /// <summary>A swap to a model the box doesn't have yet (a 400 with <c>"not_installed": true</c>). The app names the box
    /// when it says so.</summary>
    public static SwapperException NotInstalled(string detail) =>
        new(SwapperErrorKind.NotInstalled, $"That model isn't installed yet: {detail}", statusCode: 400, detail: detail);

    /// <summary>A 401 for an app token: it was revoked, or the Swapper was reset.</summary>
    public static SwapperException Revoked() => new(SwapperErrorKind.Revoked,
        "The Swapper no longer accepts this app's connection (it was revoked or the Swapper was reset). Paste a new connection code.",
        statusCode: 401);

    public static SwapperException UntrustedCertificate(string fingerprint) => new(SwapperErrorKind.UntrustedCertificate,
        $"The Spark swapper's certificate isn't trusted yet (SHA-256 {TextUtil.Prefix(fingerprint, 23)}…). Approve it in Settings › Model servers.",
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
    /// <summary>A Swapper 2.0 app token: sent as a bearer on every request, in place of a login.</summary>
    private readonly string? _token;
    public string? PinnedFingerprint { get; }
    /// <summary>Accept exactly the pinned certificate and nothing else — not even one the system trusts. The check of
    /// a connection code uses it: the box must present the certificate the code names.</summary>
    public bool PinOnly { get; }
    private readonly HttpClient _http;
    private readonly Lock _lock = new();
    private string? _seenFingerprint;
    private bool _loggedIn;

    public SparkSwapperClient(Uri baseUrl, string username, string password, string? pinnedFingerprint)
        : this(baseUrl, username, password, null, pinnedFingerprint, pinOnly: false)
    {
    }

    private SparkSwapperClient(Uri baseUrl, string username, string password, string? token, string? pinnedFingerprint, bool pinOnly)
    {
        BaseUrl = baseUrl;
        _username = username;
        _password = password;
        _token = string.IsNullOrEmpty(token) ? null : token;
        PinnedFingerprint = pinnedFingerprint;
        PinOnly = pinOnly;
        var handler = new SocketsHttpHandler
        {
            CookieContainer = new CookieContainer(),
            UseCookies = true,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = Validate },
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
    }

    /// <summary>A client that signs every request with a Swapper 2.0 app token (from a connection code) and never
    /// logs in.</summary>
    public static SparkSwapperClient WithToken(Uri baseUrl, string token, string? pinnedFingerprint, bool pinOnly = false) =>
        new(baseUrl, "", "", token, pinnedFingerprint, pinOnly);

    public bool UsesToken => _token is not null;

    public void Dispose() => _http.Dispose();

    /// <summary>The fingerprint of the certificate the server presented when system trust failed (or, with
    /// <see cref="PinOnly"/>, whatever it presented).</summary>
    public string? SeenFingerprint
    {
        get { lock (_lock) return _seenFingerprint; }
    }

    /// <summary>Trust handling: system trust first, then the one pinned fingerprint (only the pin with <see cref="PinOnly"/>).</summary>
    private bool Validate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        if (errors == SslPolicyErrors.None && !PinOnly) return true;
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
        if (_token is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
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

    /// <summary>POST /api/login (a cookie session). A client with an app token never logs in.</summary>
    public async Task LoginAsync(CancellationToken cancellationToken = default)
    {
        if (_token is not null) return;
        var (code, body) = await RequestAsync("api/login",
            new JsonObject { ["username"] = _username, ["password"] = _password }, cancellationToken).ConfigureAwait(false);
        if (code != 200) throw SwapperException.BadLogin(Message(body));
        lock (_lock) _loggedIn = true;
    }

    /// <summary>Call <paramref name="path"/>, logging in first (or again, after a 401) — or, with an app token, just
    /// call it: a 401 then means the token is no longer accepted.</summary>
    private async Task<string> AuthedAsync(string path, JsonObject? body, CancellationToken cancellationToken)
    {
        if (_token is not null)
        {
            var (status, reply) = await RequestAsync(path, body, cancellationToken).ConfigureAwait(false);
            if (status == 401) throw SwapperException.Revoked();
            if (status is < 200 or > 299) throw Failure(status, reply);
            return reply;
        }
        bool loggedIn;
        lock (_lock) loggedIn = _loggedIn;
        if (!loggedIn) await LoginAsync(cancellationToken).ConfigureAwait(false);
        var (code, text) = await RequestAsync(path, body, cancellationToken).ConfigureAwait(false);
        if (code == 401)
        {
            await LoginAsync(cancellationToken).ConfigureAwait(false);
            (code, text) = await RequestAsync(path, body, cancellationToken).ConfigureAwait(false);
        }
        if (code is < 200 or > 299) throw Failure(code, text);
        return text;
    }

    /// <summary>The error for a refusal: a model that isn't installed yet is told apart from the rest.</summary>
    private static SwapperException Failure(int code, string body)
    {
        if (code == 400)
        {
            try
            {
                if (JsonNode.Parse(body) is JsonObject obj && JsonArgs.Bool(obj, "not_installed", false))
                    return SwapperException.NotInstalled(JsonArgs.String(obj, "error") ?? "it isn't installed");
            }
            catch (JsonException)
            {
            }
        }
        return SwapperException.Server(code, Message(body));
    }

    /// <summary>GET /api/connect (Swapper 2.0): how to reach the box's model API, and what it serves.</summary>
    public async Task<SwapperConnectInfo> ConnectInfoAsync(CancellationToken cancellationToken = default)
    {
        var obj = Object(await AuthedAsync("api/connect", null, cancellationToken).ConfigureAwait(false), "connect");
        return SwapperConnectInfo.Parse(obj) ?? throw SwapperException.Transport("unexpected connect payload (no addresses)");
    }

    /// <summary>POST /api/tokens/create (Swapper 2.0, signed-in users only): an app token for this app, labelled so the
    /// Swapper's Connect tab can list and revoke it. Null when the Swapper is older and has no tokens — the caller keeps
    /// the password instead.</summary>
    public async Task<SwapperTokenGrant?> CreateTokenAsync(string label, CancellationToken cancellationToken = default)
    {
        if (_token is not null) throw new InvalidOperationException("Tokens are made from a signed-in session, not from another token.");
        bool loggedIn;
        lock (_lock) loggedIn = _loggedIn;
        if (!loggedIn) await LoginAsync(cancellationToken).ConfigureAwait(false);
        var request = new JsonObject { ["label"] = label };
        var (code, text) = await RequestAsync("api/tokens/create", request, cancellationToken).ConfigureAwait(false);
        if (code == 401)
        {
            await LoginAsync(cancellationToken).ConfigureAwait(false);
            (code, text) = await RequestAsync("api/tokens/create", request, cancellationToken).ConfigureAwait(false);
        }
        if (code is 404 or 405) return null; // a Swapper from before 2.0
        if (code is < 200 or > 299) throw SwapperException.Server(code, Message(text));
        var obj = Object(text, "token");
        var token = JsonArgs.String(obj, "token");
        if (string.IsNullOrEmpty(token)) throw SwapperException.Transport("unexpected token payload (no token)");
        return new SwapperTokenGrant(JsonArgs.String(obj, "id") ?? "", token, JsonArgs.String(obj, "code"),
            obj["connect"] is JsonObject connect ? SwapperConnectInfo.Parse(connect) : null);
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
            JsonArgs.Bool(obj, "demo", false), JsonArgs.String(obj, "version"), JsonArgs.String(obj, "platform"),
            JsonArgs.String(obj, "hostname"));
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
        string[] order = ["git", "docker", "vulkan", "llamacpp", "gpumem", "sglang_recipe", "flash_recipe", "openclaw", "ssl"];
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

    /// <summary>POST /api/models/install (Swapper 2.0): install a catalog model the box doesn't have yet. Its progress is
    /// followed with <see cref="ProvisioningJobAsync"/> (a 409 means another install is running).</summary>
    public async Task<SwapperJob?> InstallModelAsync(string key, CancellationToken cancellationToken = default)
    {
        var text = await AuthedAsync("api/models/install", new JsonObject { ["key"] = key }, cancellationToken).ConfigureAwait(false);
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
