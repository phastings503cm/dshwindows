using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Dsh.Core;

// MARK: - Amazon Bedrock client (Converse API)
//
// Streams a turn through ConverseStream (POST /model/{modelId}/converse-stream on
// bedrock-runtime.{region}.amazonaws.com), whose reply is an AWS event stream rather than SSE, and lists
// models from the control plane (bedrock.{region}.amazonaws.com). Requests are signed with SigV4 from
// an IAwsCredentialSource (the AWS CLI sign-in in the app), or carry a Bedrock API key as a bearer
// token. Request shaping lives in BedrockConverse, per-model facts in BedrockModels.
//
// AWS errors arrive as {"message": ...} with the type in the x-amzn-ErrorType header, or mid-stream as
// exception frames; both are mapped onto LlmException so RequestRetry classifies them: throttling and
// outages are waited out, a refused model or a bad request surfaces at once.

public sealed class BedrockClient : IProviderClient
{
    /// <summary>A stream that sends nothing for this long is treated as dead (and retried). Long: with
    /// thinking hidden, Claude can think for minutes before the first visible byte.</summary>
    public static TimeSpan IdleTimeout { get; set; } = TimeSpan.FromSeconds(600);

    /// <summary>SigV4 signs both the runtime and the control plane as "bedrock".</summary>
    public const string SigningName = "bedrock";

    // No automatic decompression: a compressed event stream would be buffered until the compressor
    // flushes, stalling live output.
    private static readonly SocketsHttpHandler SharedHandler = new()
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectTimeout = TimeSpan.FromSeconds(15),
        AutomaticDecompression = DecompressionMethods.None,
    };

    public ProviderProfile Profile { get; }
    private readonly IAwsCredentialSource? _credentials;
    private readonly HttpClient _http;
    private readonly TimeProvider _clock;
    /// <summary>How far AWS's clock runs ahead of this machine's, learned from a "signature expired"
    /// refusal (a PC clock minutes off makes every signature look stale).</summary>
    private TimeSpan _skew;

    /// <param name="credentials">Where SigV4 credentials come from; null when the profile uses a
    /// Bedrock API key (or has no sign-in yet — then every call explains that).</param>
    public BedrockClient(ProviderProfile profile, IAwsCredentialSource? credentials, HttpMessageHandler? handler = null,
                         TimeProvider? clock = null)
    {
        Profile = profile;
        _credentials = credentials;
        _clock = clock ?? TimeProvider.System;
        _http = new HttpClient(handler ?? SharedHandler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>A Bedrock API key and no AWS profile: send the key as a bearer token instead of signing.</summary>
    public bool UsesApiKey => Profile.AwsProfile is null && !string.IsNullOrEmpty(Profile.ApiKey);

    private static readonly Regex EndpointRegion =
        new(@"^bedrock(?:-runtime)?(?:-fips)?\.([a-z0-9-]+)\.amazonaws\.com$", RegexOptions.IgnoreCase);

    /// <summary>The profile's Region, else the one in a standard endpoint URL.</summary>
    public string? Region
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Profile.AwsRegion)) return Profile.AwsRegion.Trim();
            return Uri.TryCreate(Profile.BaseUrl, UriKind.Absolute, out var url) && EndpointRegion.Match(url.Host) is { Success: true } m
                ? m.Groups[1].Value.ToLowerInvariant()
                : null;
        }
    }

    private string RequireRegion() => Region ?? throw LlmException.Rejected(400,
        "Bedrock needs an AWS Region (such as us-east-1) — set one in Settings › Models.", "no AWS Region configured",
        permanent: true);

    /// <summary>The runtime endpoint: the profile's base URL when it is an absolute http(s) URL (the
    /// wizard stores https://bedrock-runtime.{region}.amazonaws.com; a VPC endpoint or proxy works
    /// too), else the Region's public endpoint. A public endpoint for some other Region is stale (the
    /// Region was changed since) and follows the Region: signing for one Region and calling another
    /// is always refused.</summary>
    public string RuntimeBase
    {
        get
        {
            if (Uri.TryCreate(Profile.BaseUrl, UriKind.Absolute, out var url) && url.Scheme is "https" or "http")
            {
                var standard = EndpointRegion.Match(url.Host);
                if (!standard.Success || string.IsNullOrWhiteSpace(Profile.AwsRegion)
                    || string.Equals(standard.Groups[1].Value, Profile.AwsRegion.Trim(), StringComparison.OrdinalIgnoreCase))
                    return Profile.BaseUrl.TrimEnd('/');
            }
            return $"https://bedrock-runtime.{RequireRegion()}.amazonaws.com";
        }
    }

    /// <summary>The control-plane endpoint that lists models: the runtime host with "bedrock-runtime"
    /// swapped for "bedrock", else the Region's public one.</summary>
    public string ControlBase
    {
        get
        {
            var runtime = new Uri(RuntimeBase);
            if (runtime.Host.StartsWith("bedrock-runtime", StringComparison.OrdinalIgnoreCase))
                return new UriBuilder(runtime) { Host = "bedrock" + runtime.Host["bedrock-runtime".Length..], Path = "" }.Uri.ToString().TrimEnd('/');
            return $"https://bedrock.{RequireRegion()}.amazonaws.com";
        }
    }

    /// <summary>The ConverseStream URL. The model id is one path segment, percent-encoded like botocore
    /// does it: an inference-profile ARN's ':' and '/' become %3A and %2F.</summary>
    public Uri ConverseStreamUrl(string modelId) => new($"{RuntimeBase}/model/{SigV4.Encode(modelId)}/converse-stream");

    // MARK: ILlmClient

    public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(LlmRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var model = BedrockConverse.ModelId(request, Profile);
        if (string.IsNullOrWhiteSpace(model)) throw LlmException.NoModel();
        var response = await OpenStreamAsync(request, model, cancellationToken).ConfigureAwait(false);
        using (response)
        {
            if (response.Content.Headers.ContentType?.MediaType is { } mediaType
                && !mediaType.Contains("eventstream", StringComparison.OrdinalIgnoreCase))
            {
                // Something answered, but not Bedrock (a proxy's page, a wrong endpoint URL).
                var text = await OpenAiClient.ReadErrorBodyAsync(response, cancellationToken).ConfigureAwait(false);
                throw LlmException.Http((int)response.StatusCode,
                    $"unrecognised reply (is the endpoint a Bedrock runtime URL?): {TextUtil.Prefix(text.Replace("\r", "").Replace('\n', ' '), 200)}");
            }
            Stream stream;
            try
            {
                stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw LlmException.Connection(ex.Message, ex);
            }

            var reply = new ConverseReply((type, message) => Map(new BedrockError(0, type, message), model));
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            await using var frames = AwsEventStream.ReadAsync(stream, idle.Token).GetAsyncEnumerator(idle.Token);
            while (true)
            {
                idle.CancelAfter(IdleTimeout);
                bool more;
                try
                {
                    more = await frames.MoveNextAsync().ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw LlmException.Sse($"no data from Bedrock for {(int)IdleTimeout.TotalSeconds} s");
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException or HttpRequestException)
                {
                    throw LlmException.Sse(ex.Message);
                }
                if (!more) break;
                if (reply.Handle(frames.Current) is { } delta) yield return delta;
            }
            yield return reply.Finish();
        }
    }

    /// <summary>Send the turn; the open event-stream response. When Claude refuses the reasoning replayed
    /// from earlier turns (a changed history invalidates its signature; another model's reasoning isn't
    /// readable), the request goes again once without it.</summary>
    private async Task<HttpResponseMessage> OpenStreamAsync(LlmRequest request, string model, CancellationToken cancellationToken)
    {
        var url = ConverseStreamUrl(model);
        var replay = true;
        while (true)
        {
            var body = Encoding.UTF8.GetBytes(BedrockConverse.Request(request, Profile, replay).ToJsonString());
            try
            {
                return await SendAsync(HttpMethod.Post, url, body, model, cancellationToken).ConfigureAwait(false);
            }
            catch (LlmException ex) when (replay && ex.StatusCode == 400 && RejectsReasoning(ex.Body)
                                          && request.Messages.Any(m => m.ProviderState is not null))
            {
                replay = false;
            }
        }
    }

    private static bool RejectsReasoning(string body)
    {
        var lower = body.ToLowerInvariant();
        return lower.Contains("signature") || (lower.Contains("thinking") && lower.Contains("block"));
    }

    /// <summary>Invokable model ids: foundation models that take on-demand traffic and stream text, then
    /// the Region's inference profiles (some models can only be called through one).</summary>
    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            var ids = new List<string>();
            var models = await GetJsonAsync("foundation-models?byOutputModality=TEXT", timeout.Token).ConfigureAwait(false);
            foreach (var summary in (models["modelSummaries"] as JsonArray ?? []).OfType<JsonObject>())
            {
                var onDemand = summary["inferenceTypesSupported"] is JsonArray types
                               && types.Any(t => t is JsonValue v && v.TryGetValue<string>(out var s) && s == "ON_DEMAND");
                var streams = summary["responseStreamingSupported"] is not JsonValue streaming
                              || !streaming.TryGetValue<bool>(out var yes) || yes;
                if (onDemand && streams && JsonArgs.String(summary, "modelId") is { Length: > 0 } id) ids.Add(id);
            }
            string? next = null;
            for (var page = 0; page < 50; page++)
            {
                var path = "inference-profiles?maxResults=1000" + (next is null ? "" : "&nextToken=" + SigV4.Encode(next));
                var profiles = await GetJsonAsync(path, timeout.Token).ConfigureAwait(false);
                foreach (var summary in (profiles["inferenceProfileSummaries"] as JsonArray ?? []).OfType<JsonObject>())
                {
                    if (JsonArgs.String(summary, "status") is { } status && status != "ACTIVE") continue;
                    if (JsonArgs.String(summary, "inferenceProfileId") is { Length: > 0 } id) ids.Add(id);
                }
                next = JsonArgs.String(profiles, "nextToken");
                if (string.IsNullOrEmpty(next)) break;
            }
            return ids.Distinct(StringComparer.Ordinal).ToList();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw LlmException.Connection("no answer within 20 s");
        }
    }

    private async Task<JsonObject> GetJsonAsync(string path, CancellationToken cancellationToken)
    {
        var url = new Uri($"{ControlBase}/{path}");
        using var response = await SendAsync(HttpMethod.Get, url, null, Profile.Model, cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (JsonNode.Parse(text) is JsonObject obj) return obj;
        }
        catch (JsonException) { }
        throw LlmException.Sse($"unexpected reply from {url.AbsolutePath}");
    }

    /// <summary>The model's context window and output ceiling from the built-in table — Bedrock has no
    /// endpoint that reports them. No network call, so the route probe before every retry stays free.</summary>
    public Task<ModelInfo> ModelInfoAsync(string? model = null, CancellationToken cancellationToken = default)
    {
        var id = string.IsNullOrEmpty(model) ? Profile.Model : model;
        return Task.FromResult(new ModelInfo(id, BedrockModels.ContextWindow(id), BedrockModels.Traits(id).MaxTokensCeiling));
    }

    // MARK: Sending

    /// <summary>Send one authorised request; the successful response (headers read, body unread). When
    /// AWS says the credentials expired, they are refreshed and the request sent once more; a clock
    /// that is off gets the same single retry with AWS's time. Anything else becomes an LlmException.</summary>
    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, Uri url, byte[]? body, string model,
                                                      CancellationToken cancellationToken)
    {
        var retried = false;
        var refresh = false;
        while (true)
        {
            using var request = new HttpRequestMessage(method, url);
            if (body is not null)
            {
                request.Content = new ByteArrayContent(body);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            }
            ApplyHeaders(request);
            await AuthorizeAsync(request, body ?? [], refresh, cancellationToken).ConfigureAwait(false);

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw LlmException.Connection(ex.InnerException?.Message ?? ex.Message, ex);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw LlmException.Connection("the request timed out", ex);
            }
            if (response.IsSuccessStatusCode) return response;

            BedrockError error;
            using (response)
            {
                var text = await OpenAiClient.ReadErrorBodyAsync(response, cancellationToken).ConfigureAwait(false);
                error = BedrockError.From(response, text);
                if (!retried && !UsesApiKey && _credentials is not null && error.ExpiredCredentials)
                {
                    retried = refresh = true;
                    continue;
                }
                if (!retried && error.ClockSkew && response.Headers.Date is { } serverNow)
                {
                    retried = true;
                    _skew = serverNow - _clock.GetUtcNow();
                    continue;
                }
            }
            throw Map(error, model);
        }
    }

    /// <summary>Bearer key, or a SigV4 signature over the exact body.</summary>
    private async Task AuthorizeAsync(HttpRequestMessage request, byte[] body, bool refresh, CancellationToken cancellationToken)
    {
        if (UsesApiKey)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Profile.ApiKey);
            return;
        }
        if (_credentials is null)
        {
            throw LlmException.Rejected(401,
                "Bedrock needs an AWS sign-in: choose an AWS profile and sign in (Settings › Models), or use a Bedrock API key.",
                "no AWS credentials", permanent: true);
        }
        var region = RequireRegion();
        AwsCredentials credentials;
        try
        {
            credentials = await _credentials.GetAsync(refresh, cancellationToken).ConfigureAwait(false);
        }
        catch (AwsSignInRequiredException ex)
        {
            throw LlmException.Rejected(401, SignInExpired(ex.Profile), ex.Message, permanent: true, inner: ex);
        }
        SigV4.Sign(request, body, credentials, region, SigningName, _clock.GetUtcNow() + _skew);
    }

    private static string SignInExpired(string profile) =>
        $"The AWS sign-in for profile \"{profile}\" has expired. Sign in again in Settings › Models, or run `aws login --profile {profile}`.";

    /// <summary>User agent + the profile's custom headers (before signing, so x-amz-* ones are signed).</summary>
    private void ApplyHeaders(HttpRequestMessage request)
    {
        request.Headers.UserAgent.ParseAdd($"DSH-Windows/{AppInfo.Version}");
        foreach (var (key, value) in Profile.CustomHeaders ?? [])
        {
            if (string.IsNullOrWhiteSpace(key)) continue;
            request.Headers.Remove(key);
            request.Headers.TryAddWithoutValidation(key, value);
        }
    }

    // MARK: Errors

    /// <summary>An AWS error: HTTP status (0 inside a stream), the error type from x-amzn-ErrorType or
    /// the exception frame, lower-cased and without its namespace or ":http://..." suffix, and the
    /// message.</summary>
    internal sealed record BedrockError(int Status, string Type, string Message)
    {
        public string Kind { get; } = Normalize(Type);

        private static string Normalize(string type)
        {
            var t = type.Split(':')[0];
            var hash = t.LastIndexOf('#');
            if (hash >= 0) t = t[(hash + 1)..];
            return t.Trim().ToLowerInvariant();
        }

        public static BedrockError From(HttpResponseMessage response, string body)
        {
            var type = response.Headers.TryGetValues("x-amzn-ErrorType", out var values) ? values.FirstOrDefault() ?? "" : "";
            var message = body.Trim();
            try
            {
                if (JsonNode.Parse(body) is JsonObject obj)
                {
                    message = JsonArgs.String(obj, "message") ?? JsonArgs.String(obj, "Message") ?? message;
                    if (type.Length == 0) type = JsonArgs.String(obj, "__type") ?? JsonArgs.String(obj, "code") ?? "";
                }
            }
            catch (JsonException) { }
            return new BedrockError((int)response.StatusCode, type, message);
        }

        private string Lower => Message.ToLowerInvariant();

        /// <summary>The temporary credentials ran out (or were revoked): fetching fresh ones can fix it.</summary>
        public bool ExpiredCredentials =>
            Status is 400 or 403
            && (Kind is "expiredtokenexception" or "expiredtoken" or "unrecognizedclientexception" or "invalidclienttokenid"
                    or "tokenrefreshrequired"
                || Lower.Contains("security token included in the request is expired")
                || Lower.Contains("security token included in the request is invalid")
                || Lower.Contains("token has expired"));

        /// <summary>The signature's timestamp is too far from AWS's clock.</summary>
        public bool ClockSkew =>
            Kind is "requesttimetooskewed" or "requestexpired"
            || (Kind == "invalidsignatureexception" && (Lower.Contains("signature expired") || Lower.Contains("signature not yet current")));

        public bool Authentication =>
            ExpiredCredentials || ClockSkew
            || Kind is "invalidsignatureexception" or "incompletesignatureexception" or "missingauthenticationtokenexception"
                or "signaturedoesnotmatch";
    }

    /// <summary>An AWS error as the LlmException the engine's retry logic expects.</summary>
    internal LlmException Map(BedrockError e, string model)
    {
        var region = Region ?? "this Region";
        var detail = e.Message.Length == 0 ? e.Type : e.Message;
        var body = e.Type.Length == 0 ? detail : $"{e.Type.Split(':')[0]}: {detail}";
        var lower = detail.ToLowerInvariant();
        if (UsesApiKey && (e.Status is 401 or 403 || e.Authentication)
            && (e.Kind != "accessdeniedexception" || lower.Contains("api key") || lower.Contains("bearer") || lower.Contains("authentication")))
        {
            return LlmException.Rejected(e.Status is 0 ? 403 : e.Status,
                $"Bedrock did not accept the API key ({detail}). Short-term keys last at most 12 hours: create a new one, or sign in with AWS in Settings › Models.",
                body, permanent: true);
        }
        if (e.Authentication)
        {
            var profile = Profile.AwsProfile ?? "default";
            return LlmException.Rejected(e.Status is 0 ? 403 : e.Status,
                $"AWS did not accept the credentials for profile \"{profile}\" ({detail}). Sign in again in Settings › Models, or run `aws login --profile {profile}`.",
                body, permanent: true);
        }
        switch (e.Kind)
        {
            case "validationexception":
                if (IsOverflow(detail)) return LlmException.Overflow(OverflowLimit(detail) ?? 0, detail);
                return LlmException.Rejected(400, $"Bedrock rejected the request: {detail}", body);
            case "accessdeniedexception":
            {
                var text = $"Bedrock refused access to {model} in {region} ({detail}). Your AWS account may need access to this model " +
                           "enabled — the Bedrock setup in Settings › Models can do that.";
                if (lower.Contains("subscri") || lower.Contains("marketplace") || lower.Contains("not authorized"))
                    text += " Right after a first-time subscription AWS can keep refusing for up to about 15 minutes; if you just enabled it, try again shortly.";
                return LlmException.Rejected(403, text, body);
            }
            case "resourcenotfoundexception":
                // Unlike a local server swapping models, waiting won't make the id appear.
                return LlmException.Rejected(404,
                    $"Bedrock has no model \"{model}\" in {region} ({detail}). Check the model id and Region; some models can only be " +
                    "called through an inference profile (an id starting with us., eu., apac. or global.).",
                    body, permanent: true);
            case "throttlingexception" or "servicequotaexceededexception" or "toomanyrequestsexception":
                return LlmException.Rejected(429, $"Bedrock is throttling requests for {model} ({detail}).", body);
            case "modelnotreadyexception":
                return LlmException.Rejected(429, $"{model} is not ready on Bedrock yet ({detail}).", body);
            case "serviceunavailableexception":
                return LlmException.Rejected(503, $"Bedrock is unavailable ({detail}).", body);
            case "modeltimeoutexception":
                return LlmException.Rejected(408, $"{model} timed out on Bedrock ({detail}).", body);
            case "internalserverexception" or "internalfailure" or "modelerrorexception" or "modelstreamerrorexception":
                return LlmException.Rejected(500, $"Bedrock failed while running {model} ({detail}).", body);
        }
        if (e.Status is 0 or 400 && IsOverflow(detail)) return LlmException.Overflow(OverflowLimit(detail) ?? 0, detail);
        // The body itself was too big (many screenshots): the conversation must shrink, like an overflow.
        if (e.Status == 413) return LlmException.Overflow(0, "request too large for Bedrock (HTTP 413)");
        return LlmException.Http(e.Status is 0 ? 500 : e.Status, body);
    }

    /// <summary>A refusal because the conversation (input plus the requested output) is too long for the
    /// model — compaction can fix that — as opposed to a request for more output than the model allows.</summary>
    internal static bool IsOverflow(string message)
    {
        var lower = message.ToLowerInvariant();
        if (lower.Contains("context window") || lower.Contains("context length") || lower.Contains("context limit")) return true;
        var aboutOutput = lower.Contains("max_tokens") || lower.Contains("maxtokens") || lower.Contains("maximum tokens")
                          || lower.Contains("output tokens");
        var tooBig = lower.Contains("too long") || lower.Contains("exceed") || lower.Contains("too many");
        return tooBig && (lower.Contains("input") || lower.Contains("prompt")) && !aboutOutput;
    }

    private static readonly Regex[] LimitPatterns =
    [
        // Claude: "prompt is too long: 215000 tokens > 200000 maximum"; "input length and `max_tokens`
        // exceed context limit: 180000 + 32000 > 200000".
        new(@">\s*(\d{4,})"),
        new(@"maximum (?:length |context length )?(?:of |is )?(\d{4,})", RegexOptions.IgnoreCase),
    ];

    /// <summary>The window an overflow message names, if any.</summary>
    internal static int? OverflowLimit(string message)
    {
        if (OpenAiClient.OverflowLimit(message) is { } known) return known;
        foreach (var regex in LimitPatterns)
        {
            if (regex.Match(message) is { Success: true } m && int.TryParse(m.Groups[1].Value, out var n) && n > 0) return n;
        }
        return null;
    }
}
