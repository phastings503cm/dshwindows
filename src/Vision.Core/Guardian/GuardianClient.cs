using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Vision.Core;

// MARK: - The Guardian harness client
//
// Everything is HTTPS + JSON with a bearer token:
//   sign in            POST /api/auth/login {email, password, client:"vision-harness"} (→ a 2FA challenge, or a token)
//                      POST /api/auth/login/2fa {challenge, code}
//                      browser: /app/connect/vision → POST /api/harness/connect/token (see GuardianBrowserSignIn)
//   who and what       GET  /api/harness/me · GET /api/harness/limits
//   models (proxy)     GET  /api/harness/v1/models   (the chats themselves go to /api/harness/v1/chat/completions
//                      through the ordinary OpenAI-compatible client: the token is the route's key)
//   prompt notebook    POST/GET /api/harness/prompts
//   skills             POST/GET /api/harness/skills
//   chats              POST/GET /api/harness/chats/:id · GET /api/harness/chats
//   code projects      POST/GET /api/harness/projects (chunked for big ones)
//   vault (opt-in)     GET /api/harness/vault · POST /api/harness/vault/:id/reveal
// An org is picked with x-guardian-org (the person's default otherwise). Every refusal comes back as a GuardianError
// with Guardian's own sentence (401/403/402/429 structured per the contract).

public sealed class GuardianClient
{
    /// <summary>Guardian's own deployment.</summary>
    public const string DefaultAddress = "https://guardian.503cm.com";

    /// <summary>What this harness calls itself to Guardian: the login's client tag (Guardian keeps harness sign-ins apart
    /// and lists them under Connected apps) and the prompts' source.</summary>
    public const string ClientTag = "vision-windows";
    public const string Source = "vision-windows";

    /// <summary>A manifest bigger than this goes up in chunks (Guardian's body cap for projects is 34 MB).</summary>
    public const int ProjectChunkBytes = 8_000_000;

    private readonly HttpClient _http;

    /// <summary>The deployment's base address, without a trailing slash (https://guardian.503cm.com).</summary>
    public string Address { get; }

    /// <summary>The signed-in session (the token every call carries), or null before sign-in.</summary>
    public GuardianSession? Session { get; private set; }

    /// <summary>The org calls go to (x-guardian-org); null = the account's default.</summary>
    public string? OrgId { get; set; }

    public bool IsSignedIn => Session is not null;

    /// <param name="handler">Replaces the network (tests).</param>
    public GuardianClient(string address, GuardianSession? session = null, HttpMessageHandler? handler = null, TimeSpan? timeout = null)
    {
        Address = NormalizeAddress(address) ?? throw GuardianError.BadAddress();
        Session = session;
        OrgId = session?.OrgId;
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = timeout ?? TimeSpan.FromSeconds(60);
    }

    /// <summary>An address a person typed, as the client uses it ("guardian.503cm.com/" → "https://guardian.503cm.com"), or
    /// null when it can't be one. Only https — or http to this computer or the local network, for a dev Guardian.</summary>
    public static string? NormalizeAddress(string? typed)
    {
        var text = (typed ?? "").Trim().TrimEnd('/');
        if (text.Length == 0) return null;
        if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host)) return null;
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) return null;
        if (uri.Scheme == Uri.UriSchemeHttp && !new ProviderProfile { BaseUrl = text }.IsLoopbackOrLan) return null;
        // A pasted proxy address ("…/api/harness/v1") still means the deployment.
        var path = uri.AbsolutePath;
        var at = path.IndexOf("/api/", StringComparison.OrdinalIgnoreCase);
        if (at >= 0) path = path[..at];
        return $"{uri.Scheme}://{uri.Authority}{path.TrimEnd('/')}";
    }

    /// <summary>The OpenAI-compatible base URL a model route uses: <c>&lt;address&gt;/api/harness/v1</c>.</summary>
    public string ProxyBaseUrl => Address + "/api/harness/v1";

    public void SignOut() => Session = null;

    // MARK: Sign-in

    /// <summary>Sign in with the Guardian login. A TOTP account throws <see cref="GuardianErrorKind.TwoFactorRequired"/>
    /// with the challenge to finish with <see cref="VerifyTwoFactorAsync"/>. The password is sent once and kept nowhere.</summary>
    public async Task<GuardianSession> SignInAsync(string email, string password, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrEmpty(password))
            throw new GuardianError(GuardianErrorKind.Credentials, "Enter the email and password you use at Guardian.");
        var body = new JsonObject { ["email"] = email.Trim(), ["password"] = password, ["client"] = ClientTag };
        var json = await SendForSignInAsync("/api/auth/login", body, cancellationToken).ConfigureAwait(false);
        if (GuardianJson.Bool(json["twoFactorRequired"]) == true && GuardianJson.Text(json["challenge"]) is { Length: > 0 } challenge)
            throw new GuardianError(GuardianErrorKind.TwoFactorRequired,
                "This Guardian account uses two-factor sign-in. Enter the 6-digit code from your authenticator app.") { Challenge = challenge };
        Session = ReadSession(json, email.Trim()) ?? throw new GuardianError(GuardianErrorKind.Credentials,
            GuardianJson.Text(json["message"]) ?? "Guardian didn't accept that sign-in. Check the email and password.");
        return Session;
    }

    /// <summary>Finish a 2FA sign-in with the 6-digit code from the authenticator app.</summary>
    public async Task<GuardianSession> VerifyTwoFactorAsync(string challenge, string code, string? email = null, CancellationToken cancellationToken = default)
    {
        var trimmed = new string((code ?? "").Where(c => !char.IsWhiteSpace(c)).ToArray());
        if (trimmed.Length != 6 || !trimmed.All(char.IsAsciiDigit))
            throw new GuardianError(GuardianErrorKind.Credentials, "The 2FA code is the 6-digit number from your authenticator app.");
        JsonObject json;
        try
        {
            json = await SendForSignInAsync("/api/auth/login/2fa", new JsonObject { ["challenge"] = challenge, ["code"] = trimmed }, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (GuardianError error) when (error.Kind == GuardianErrorKind.Credentials && error.Status == 401)
        {
            // A wrong or rotated code: retry guidance, not "sign in again".
            throw new GuardianError(GuardianErrorKind.Credentials,
                "Guardian didn't accept that code — it may have rotated. Codes refresh every 30 seconds; enter the current one.") { Status = 401 };
        }
        Session = ReadSession(json, email ?? "") ?? throw new GuardianError(GuardianErrorKind.Credentials,
            GuardianJson.Text(json["message"]) ?? "Check the code and try again — codes rotate every 30 seconds.");
        return Session;
    }

    /// <summary>The browser sign-in's last step: the one-time code (and the PKCE verifier that proves this app asked for
    /// it) for a harness token bound to the person and the org they picked.</summary>
    public async Task<GuardianSession> ExchangeConnectCodeAsync(string code, string verifier, string redirectUri, CancellationToken cancellationToken = default)
    {
        var body = new JsonObject { ["code"] = code, ["code_verifier"] = verifier, ["redirect_uri"] = redirectUri };
        var json = await SendForSignInAsync("/api/harness/connect/token", body, cancellationToken).ConfigureAwait(false);
        var session = ReadSession(json, "") ?? throw new GuardianError(GuardianErrorKind.BrowserSignIn,
            GuardianJson.Text(json["message"]) ?? "Guardian didn't hand over a sign-in for that approval. Try signing in again.");
        Session = session with { OrgId = GuardianJson.Text(json["orgId"]) };
        OrgId = Session.OrgId ?? OrgId;
        return Session;
    }

    /// <summary>A sign-in answer's token and who it is for: <c>{token, session:{id|userId, email, name}, user:{…}}</c> and
    /// the older shapes the Mac also reads.</summary>
    internal static GuardianSession? ReadSession(JsonObject json, string typedEmail)
    {
        var token = GuardianJson.Text(json["token"]) ?? GuardianJson.Text(json["accessToken"]) ?? GuardianJson.Text(json["sessionToken"]);
        if (string.IsNullOrWhiteSpace(token)) return null;
        var user = json["user"] as JsonObject;
        var session = json["session"] as JsonObject;
        string? Field(string name) => GuardianJson.Text(user?[name]) ?? GuardianJson.Text(session?[name]) ?? GuardianJson.Text(json[name]);
        var email = Field("email") ?? typedEmail;
        var id = GuardianJson.Text(user?["id"]) ?? GuardianJson.Text(session?["userId"]) ?? GuardianJson.Text(json["userId"]) ?? email;
        return new GuardianSession(id, email, Field("name"), token.Trim(), DateTimeOffset.Now);
    }

    // MARK: Who and what

    public async Task<GuardianIdentity> MeAsync(CancellationToken cancellationToken = default)
    {
        var json = await GetObjectAsync("/api/harness/me", cancellationToken).ConfigureAwait(false);
        var user = json["user"] as JsonObject;
        var orgs = (json["orgs"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Select(o => GuardianJson.Text(o["id"]) is { } id ? new GuardianOrg(id, GuardianJson.Text(o["name"]) ?? id, GuardianJson.Bool(o["active"]) == true) : null)
            .OfType<GuardianOrg>()
            .ToList();
        return new GuardianIdentity(
            GuardianJson.Text(user?["id"]) ?? Session?.UserId ?? "",
            GuardianJson.Text(user?["email"]) ?? Session?.Email ?? "",
            GuardianJson.Text(user?["name"]) ?? Session?.Name,
            orgs,
            GuardianJson.Text(json["orgId"]),
            GuardianJson.Strings(json["modules"]),
            GuardianJson.Strings(json["permissions"]),
            GuardianJson.Limits(json["limits"]));
    }

    public async Task<IReadOnlyList<GuardianLimit>> LimitsAsync(CancellationToken cancellationToken = default) =>
        GuardianJson.Limits(await GetObjectAsync("/api/harness/limits", cancellationToken).ConfigureAwait(false));

    /// <summary>The models the person's org allows them through the proxy (the aliases first, as Guardian lists them).</summary>
    public async Task<IReadOnlyList<GuardianModel>> ModelsAsync(CancellationToken cancellationToken = default)
    {
        var json = await GetObjectAsync("/api/harness/v1/models", cancellationToken).ConfigureAwait(false);
        if (json["models"] is JsonArray models)
            return models.OfType<JsonObject>()
                .Select(m => GuardianJson.Text(m["id"]) is { Length: > 0 } id
                    ? new GuardianModel(id, GuardianJson.Text(m["label"]), (int?)GuardianJson.Number(m["contextWindow"]),
                        (int?)GuardianJson.Number(m["maxOutputTokens"]), GuardianJson.Bool(m["interactiveOnly"]))
                    : null)
                .OfType<GuardianModel>()
                .ToList();
        return (json["data"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(m => GuardianJson.Text(m["id"]) is { Length: > 0 } id ? new GuardianModel(id) : null)
            .OfType<GuardianModel>()
            .ToList();
    }

    // MARK: Prompt notebook

    /// <summary>Push prompts to the notebook. A prompt sent again with the same text and time (now with its outcome)
    /// updates the row Guardian has. Returns how many Guardian accepted.</summary>
    public async Task<int> PushPromptsAsync(IReadOnlyList<GuardianPrompt> prompts, CancellationToken cancellationToken = default)
    {
        if (prompts.Count == 0) return 0;
        var list = new JsonArray();
        foreach (var p in prompts)
        {
            var row = new JsonObject { ["text"] = p.Text, ["at"] = Iso(p.At), ["source"] = p.Source ?? Source };
            if (p.Outcome is { } outcome) row["outcome"] = outcome;
            if (p.ChatId is { } chat) row["chatId"] = chat;
            if (p.ChatTitle is { } title) row["chatTitle"] = title;
            if (p.Project is { } project) row["project"] = project;
            if (p.Model is { } model) row["model"] = model;
            if (p.DurationSeconds is { } seconds) row["durationSeconds"] = Math.Round(seconds, 1);
            list.Add(row);
        }
        var json = await SendObjectAsync(HttpMethod.Post, "/api/harness/prompts", new JsonObject { ["prompts"] = list }, cancellationToken).ConfigureAwait(false);
        return (int?)GuardianJson.Number(json["accepted"]) ?? prompts.Count;
    }

    /// <summary>The notebook, newest first.</summary>
    public async Task<IReadOnlyList<GuardianPrompt>> PromptsAsync(int limit = 200, CancellationToken cancellationToken = default)
    {
        var json = await GetObjectAsync($"/api/harness/prompts?limit={Math.Clamp(limit, 1, 500)}", cancellationToken).ConfigureAwait(false);
        return (json["prompts"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(p => GuardianJson.Text(p["text"]) is { } text && GuardianJson.Date(p["at"]) is { } at
                ? new GuardianPrompt(text, at)
                {
                    Id = GuardianJson.Text(p["id"]),
                    Outcome = GuardianJson.Text(p["outcome"]),
                    Source = GuardianJson.Text(p["source"]),
                    ChatId = GuardianJson.Text(p["chatId"]),
                    ChatTitle = GuardianJson.Text(p["chatTitle"]),
                    Project = GuardianJson.Text(p["project"]),
                    Model = GuardianJson.Text(p["model"]),
                    DurationSeconds = GuardianJson.Number(p["durationSeconds"]),
                }
                : null)
            .OfType<GuardianPrompt>()
            .ToList();
    }

    // MARK: Skills

    /// <summary>Deposit (create or replace) one of the person's own skills. Someone else's skill of that name is a
    /// <see cref="GuardianErrorKind.Conflict"/> ("name_taken").</summary>
    public async Task PutSkillAsync(GuardianSkill skill, CancellationToken cancellationToken = default)
    {
        var files = new JsonObject();
        foreach (var (path, text) in skill.Files) files[path] = text;
        await SendObjectAsync(HttpMethod.Post, "/api/harness/skills",
            new JsonObject { ["name"] = skill.Name, ["description"] = skill.Description, ["files"] = files }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The skills the person may use: their own, their org's, and those shared with them.</summary>
    public async Task<IReadOnlyList<GuardianSkill>> SkillsAsync(CancellationToken cancellationToken = default)
    {
        var json = await GetObjectAsync("/api/harness/skills", cancellationToken).ConfigureAwait(false);
        return (json["skills"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(s => GuardianJson.Text(s["name"]) is { Length: > 0 } name
                ? new GuardianSkill(name, GuardianJson.Text(s["description"]) ?? "", (s["files"] as JsonObject ?? [])
                    .Where(f => GuardianJson.Text(f.Value) is not null)
                    .ToDictionary(f => f.Key, f => GuardianJson.Text(f.Value)!, StringComparer.Ordinal))
                : null)
            .OfType<GuardianSkill>()
            .ToList();
    }

    // MARK: Chats

    /// <summary>Store one chat's transcript under the account (opaque to Guardian, ≤ 25 MB).</summary>
    public async Task PutChatAsync(string id, string title, DateTimeOffset updatedAt, string payload, CancellationToken cancellationToken = default) =>
        await SendObjectAsync(HttpMethod.Post, "/api/harness/chats/" + Uri.EscapeDataString(id),
            new JsonObject { ["title"] = title, ["updatedAt"] = Iso(updatedAt), ["payload"] = payload }, cancellationToken).ConfigureAwait(false);

    /// <summary>A stored chat's transcript (the payload as it was put).</summary>
    public async Task<string> ChatAsync(string id, CancellationToken cancellationToken = default)
    {
        var json = await GetObjectAsync("/api/harness/chats/" + Uri.EscapeDataString(id), cancellationToken).ConfigureAwait(false);
        return json["payload"] switch
        {
            JsonValue value when value.TryGetValue<string>(out var text) => text,
            JsonObject or JsonArray => json["payload"]!.ToJsonString(),
            _ => throw new GuardianError(GuardianErrorKind.Server, "Guardian's copy of that chat has no transcript."),
        };
    }

    public async Task<IReadOnlyList<GuardianChatSummary>> ChatsAsync(CancellationToken cancellationToken = default)
    {
        var json = await GetObjectAsync("/api/harness/chats", cancellationToken).ConfigureAwait(false);
        return (json["chats"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(c => GuardianJson.Text(c["id"]) is { Length: > 0 } id
                ? new GuardianChatSummary(id, GuardianJson.Text(c["title"]) ?? "Chat", GuardianJson.Date(c["updatedAt"]) ?? DateTimeOffset.MinValue)
                : null)
            .OfType<GuardianChatSummary>()
            .OrderByDescending(c => c.UpdatedAt)
            .ToList();
    }

    // MARK: Code projects

    /// <summary>Upload a project's text files. A manifest over <see cref="ProjectChunkBytes"/> goes in ordered chunks
    /// (<c>{chunk, totalChunks, uploadId}</c>); Guardian keeps the previous complete one readable until the last arrives.
    /// <paramref name="progress"/> hears "chunk i of n".</summary>
    public async Task<int> PutProjectAsync(string name, IReadOnlyDictionary<string, string> files, IProgress<(int Chunk, int Total)>? progress = null,
                                           CancellationToken cancellationToken = default)
    {
        var chunks = GuardianProjects.Chunk(files, ProjectChunkBytes);
        var uploadId = chunks.Count > 1 ? Guid.NewGuid().ToString("N") : null;
        for (var i = 0; i < chunks.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var manifest = new JsonObject();
            foreach (var (path, text) in chunks[i]) manifest[path] = text;
            var body = new JsonObject { ["name"] = name, ["files"] = manifest };
            if (uploadId is not null)
            {
                body["chunk"] = i;
                body["totalChunks"] = chunks.Count;
                body["uploadId"] = uploadId;
            }
            await SendObjectAsync(HttpMethod.Post, "/api/harness/projects", body, cancellationToken).ConfigureAwait(false);
            progress?.Report((i + 1, chunks.Count));
        }
        return chunks.Count;
    }

    public async Task<IReadOnlyList<GuardianProjectSummary>> ProjectsAsync(CancellationToken cancellationToken = default)
    {
        var json = await GetObjectAsync("/api/harness/projects", cancellationToken).ConfigureAwait(false);
        return (json["projects"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(p => GuardianJson.Text(p["id"]) is { Length: > 0 } id
                ? new GuardianProjectSummary(id, GuardianJson.Text(p["name"]) ?? id, GuardianJson.Date(p["updatedAt"]),
                    (int?)(GuardianJson.Number(p["fileCount"]) ?? GuardianJson.Number(p["files"])))
                : null)
            .OfType<GuardianProjectSummary>()
            .ToList();
    }

    // MARK: Vault (opt-in per secret)

    /// <summary>The secrets the person owns and marked "Usable in Vision" — names only.</summary>
    public async Task<IReadOnlyList<GuardianVaultSecret>> VaultAsync(CancellationToken cancellationToken = default)
    {
        var json = await GetObjectAsync("/api/harness/vault", cancellationToken).ConfigureAwait(false);
        var array = json["secrets"] as JsonArray ?? json["items"] as JsonArray ?? [];
        return array.OfType<JsonObject>()
            .Select(s => GuardianJson.Text(s["id"]) is { Length: > 0 } id
                ? new GuardianVaultSecret(id, GuardianJson.Text(s["name"]) ?? id, GuardianJson.Text(s["kind"]) ?? GuardianJson.Text(s["type"]),
                    GuardianJson.Text(s["description"]))
                : null)
            .OfType<GuardianVaultSecret>()
            .ToList();
    }

    /// <summary>One secret's value. Guardian audits the hand-out before it answers; the caller keeps the value in memory
    /// only (contract Q7).</summary>
    public async Task<string> RevealAsync(string id, CancellationToken cancellationToken = default)
    {
        var json = await SendObjectAsync(HttpMethod.Post, $"/api/harness/vault/{Uri.EscapeDataString(id)}/reveal", new JsonObject(), cancellationToken)
            .ConfigureAwait(false);
        return GuardianJson.Text(json["value"]) ?? GuardianJson.Text(json["secret"])
            ?? throw new GuardianError(GuardianErrorKind.Server, "Guardian answered without the secret's value.");
    }

    // MARK: Wire helpers

    internal static string Iso(DateTimeOffset at) => at.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private HttpRequestMessage Request(HttpMethod method, string path, bool authorized = true)
    {
        var request = new HttpRequestMessage(method, Address + path);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd($"Vision/{AppInfo.Version} (Windows)");
        if (authorized && Session?.Token is { Length: > 0 } token) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (authorized && OrgId is { Length: > 0 } org) request.Headers.TryAddWithoutValidation("x-guardian-org", org);
        return request;
    }

    private Task<JsonObject> GetObjectAsync(string path, CancellationToken cancellationToken) =>
        SendObjectAsync(HttpMethod.Get, path, null, cancellationToken);

    private async Task<JsonObject> SendObjectAsync(HttpMethod method, string path, JsonObject? body, CancellationToken cancellationToken)
    {
        if (Session is null) throw new GuardianError(GuardianErrorKind.Unauthorized, "Sign in to Guardian first (Settings › Guardian).");
        using var request = Request(method, path);
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), new UTF8Encoding(false), "application/json");
        return await SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The sign-in calls: no token yet, and a 400/401/403 is "the email, password or code was wrong", never
    /// "sign in again".</summary>
    private async Task<JsonObject> SendForSignInAsync(string path, JsonObject body, CancellationToken cancellationToken)
    {
        using var request = Request(HttpMethod.Post, path, authorized: false);
        request.Content = new StringContent(body.ToJsonString(), new UTF8Encoding(false), "application/json");
        try
        {
            return await SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (GuardianError error) when (error.Status is 400 or 401 or 403 or 422)
        {
            throw new GuardianError(GuardianErrorKind.Credentials, error.ServerMessage is { Length: > 0 } said
                ? $"Guardian didn't accept that sign-in: {said}"
                : "Guardian didn't accept that sign-in. Check the email and password.")
            { Status = error.Status, Code = error.Code };
        }
    }

    private async Task<JsonObject> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException error)
        {
            throw GuardianError.Network(error.InnerException?.Message ?? error.Message, error);
        }
        catch (TaskCanceledException error) when (!cancellationToken.IsCancellationRequested)
        {
            throw GuardianError.Network("it didn't answer in time", error);
        }
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw GuardianError.FromResponse((int)response.StatusCode, text, response.Headers);
            if (text.Length == 0 || response.StatusCode == HttpStatusCode.NoContent) return [];
            try
            {
                return JsonNode.Parse(text) as JsonObject
                       ?? throw new GuardianError(GuardianErrorKind.Server, "Guardian answered with something that isn't an object.") { Status = (int)response.StatusCode };
            }
            catch (JsonException)
            {
                // A sign-in page or a proxy's error page with a 200: the address is not Guardian's API.
                throw new GuardianError(GuardianErrorKind.Server,
                    "Guardian answered with a web page instead of data — check the address (https://guardian.503cm.com).") { Status = (int)response.StatusCode };
            }
        }
    }
}
