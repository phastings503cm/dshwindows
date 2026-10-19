using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Vision.Core;

// MARK: - Anthropic API client (Messages API, API key)
//
// Streams a turn through POST {base}/v1/messages with x-api-key and anthropic-version, reading the server-sent events
// (message_start, content_block_start/delta/stop, message_delta, message_stop, ping, error); lists models from GET
// /v1/models and sizes the context gauge from GET /v1/models/{id} (max_input_tokens). Request shaping and the reply's
// accumulation live in AnthropicMessages. Errors are put into plain words (AnthropicErrors) and classified for RequestRetry:
// rate limits and overload are waited out; a refused key, credit or model surfaces at once.
//
// This is the API-key route. A Claude Pro/Max subscription is used through the official Claude Code app, never here.

public sealed class AnthropicClient : IProviderClient
{
    /// <summary>The public API (a route's base URL; given without the /v1, it is added).</summary>
    public const string DefaultBaseUrl = "https://api.anthropic.com/v1";

    /// <summary>A stream that sends nothing for this long is treated as dead (and retried). Pings come every few seconds, so a
    /// silent minute means the connection is gone even while Claude thinks with its text hidden.</summary>
    public static TimeSpan IdleTimeout { get; set; } = TimeSpan.FromSeconds(600);

    // No automatic decompression: a compressed event stream would be buffered until the compressor flushes, stalling output.
    private static readonly SocketsHttpHandler SharedHandler = new()
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectTimeout = TimeSpan.FromSeconds(15),
        AutomaticDecompression = DecompressionMethods.None,
    };

    public ProviderProfile Profile { get; }
    private readonly HttpClient _http;
    private readonly Action<string>? _onNotice;

    public AnthropicClient(ProviderProfile profile, HttpMessageHandler? handler = null, Action<string>? onNotice = null)
    {
        Profile = profile;
        _onNotice = onNotice;
        _http = new HttpClient(handler ?? SharedHandler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>{base}/v1/{path}: the base URL may be given with or without its /v1.</summary>
    public string Endpoint(string path)
    {
        var root = string.IsNullOrWhiteSpace(Profile.BaseUrl) ? DefaultBaseUrl : Profile.BaseUrl.Trim().TrimEnd('/');
        return root.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? $"{root}/{path}" : $"{root}/v1/{path}";
    }

    // MARK: ILlmClient

    public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(LlmRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var model = AnthropicMessages.ModelId(request, Profile);
        if (string.IsNullOrWhiteSpace(model)) throw LlmException.NoModel();
        var response = await OpenStreamAsync(request, model, cancellationToken).ConfigureAwait(false);
        using (response)
        {
            Stream stream;
            try
            {
                stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw LlmException.Connection(ex.Message, ex);
            }
            var reply = new AnthropicReply();
            var sawEvent = false;
            var other = new StringBuilder();
            // StreamReader assembles whole lines from raw bytes, so UTF-8 sequences split across network chunks never break.
            using var reader = new StreamReader(stream, TextUtil.Utf8NoBom, detectEncodingFromByteOrderMarks: false);
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            while (true)
            {
                idle.CancelAfter(IdleTimeout);
                string? line;
                try
                {
                    line = await reader.ReadLineAsync(idle.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw LlmException.Sse($"no data from Anthropic for {(int)IdleTimeout.TotalSeconds} s");
                }
                catch (Exception ex) when (ex is IOException or HttpRequestException)
                {
                    throw LlmException.Sse(ex.Message);
                }
                if (line is null) break;
                if (line.StartsWith("data:", StringComparison.Ordinal)) sawEvent = true;
                else if (!line.StartsWith("event:", StringComparison.Ordinal) && !line.StartsWith(':') && line.Length > 0 && other.Length < 4000)
                    other.Append(line);
                if (reply.Handle(line) is { } delta) yield return delta;
            }
            if (!sawEvent && other.Length > 0)
            {
                // Something answered, but not the Messages API (a proxy's page, a wrong base URL).
                throw LlmException.Http((int)response.StatusCode,
                    $"unrecognised reply (is the address Anthropic's API, ending in /v1?): {TextUtil.Prefix(other.ToString(), 200)}");
            }
            foreach (var (from, to) in reply.Handovers)
                _onNotice?.Invoke($"{from} declined this request — {to} carried on (Anthropic's server-side fallback).");
            yield return reply.Finish();
        }
    }

    /// <summary>Keys (per address and model) that turned the server-side-fallback beta down: not asked again this run.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> NoFallbacks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>For tests: forget which keys turned the fallback beta down.</summary>
    internal static void ResetLearned() => NoFallbacks.Clear();

    /// <summary>Said once when a key isn't enabled for Anthropic's server-side fallback.</summary>
    public const string NoFallbackNotice =
        "Anthropic doesn't offer server-side fallback to this key — Vision carries on without it (a declined request then stops instead of moving to another Claude model).";

    /// <summary>Send the turn; the open event-stream response. When the key isn't enabled for the server-side-fallback beta, the
    /// request goes again without it (said once). When Claude refuses the thinking replayed from earlier turns (the history
    /// changed under its signature), the request goes again once without it.</summary>
    private async Task<HttpResponseMessage> OpenStreamAsync(LlmRequest request, string model, CancellationToken cancellationToken)
    {
        var fallbackKey = $"{Endpoint("messages")}|{model}";
        var replay = true;
        var fallbacks = !NoFallbacks.ContainsKey(fallbackKey);
        while (true)
        {
            var body = AnthropicMessages.Request(request, Profile, replay, fallbacks);
            var sendsFallbacks = body["fallbacks"] is not null;
            try
            {
                return await SendAsync(HttpMethod.Post, new Uri(Endpoint("messages")), body.ToJsonString(), model, cancellationToken,
                    beta: sendsFallbacks ? AnthropicMessages.FallbackBeta : null).ConfigureAwait(false);
            }
            catch (LlmException ex) when (sendsFallbacks && ex.StatusCode == 400 && RejectsFallbackBeta(ex.Body))
            {
                NoFallbacks[fallbackKey] = true;
                fallbacks = false;
                _onNotice?.Invoke(NoFallbackNotice);
            }
            catch (LlmException ex) when (replay && ex.StatusCode == 400 && RejectsThinking(ex.Body)
                                          && request.Messages.Any(m => m.ProviderState?.StartsWith(AnthropicMessages.StatePrefix, StringComparison.Ordinal) == true))
            {
                replay = false;
            }
        }
    }

    /// <summary>"Unexpected value(s) `server-side-fallback-2026-07-01` for the `anthropic-beta` header.", or a refusal of the
    /// fallbacks field.</summary>
    internal static bool RejectsFallbackBeta(string body)
    {
        // Not just "anthropic-beta": preserved thinking's 400 names that header too.
        var lower = body.ToLowerInvariant();
        return lower.Contains("server-side-fallback", StringComparison.Ordinal) || lower.Contains("fallbacks", StringComparison.Ordinal);
    }

    /// <summary>"messages.5.content.0: Invalid `signature` in `thinking` block. The block is bound to a different conversation…"</summary>
    internal static bool RejectsThinking(string body)
    {
        var lower = body.ToLowerInvariant();
        return lower.Contains("signature", StringComparison.Ordinal) || (lower.Contains("thinking", StringComparison.Ordinal) && lower.Contains("block", StringComparison.Ordinal));
    }

    /// <summary>One request; the response when it succeeded (status 2xx), else the error in plain words.</summary>
    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, Uri url, string? body, string model, CancellationToken cancellationToken,
                                                      bool stream = true, string? beta = null)
    {
        using var req = new HttpRequestMessage(method, url);
        if (body is not null) req.Content = new StringContent(body, TextUtil.Utf8NoBom, "application/json");
        if (stream) req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        ApplyHeaders(req);
        if (beta is not null && !req.Headers.Contains("anthropic-beta")) req.Headers.TryAddWithoutValidation("anthropic-beta", beta);
        else if (beta is not null) req.Headers.TryAddWithoutValidation("anthropic-beta", string.Join(",", req.Headers.GetValues("anthropic-beta").Append(beta)));
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
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
        using (response)
        {
            var text = await OpenAiClient.ReadErrorBodyAsync(response, cancellationToken).ConfigureAwait(false);
            throw Error((int)response.StatusCode, text, model);
        }
    }

    /// <summary>The exception for an error reply: an overflow (the window is known from the message), Anthropic's error in
    /// plain words, or the raw reply.</summary>
    internal static LlmException Error(int status, string body, string model)
    {
        if (status == 413 || AnthropicErrors.Parse(body).Type == "request_too_large")
            return LlmException.Overflow(0, "request too large for Anthropic's API (HTTP 413)");
        if (status == 400 && AnthropicErrors.OverflowLimit(body) is { } limit) return LlmException.Overflow(limit, AnthropicErrors.Parse(body).Message);
        if (AnthropicErrors.Explain(status, body, model) is { } explained)
            return LlmException.Rejected(status, explained.Message, body, explained.Permanent);
        return LlmException.Http(status, body);
    }

    private void ApplyHeaders(HttpRequestMessage req)
    {
        if (!string.IsNullOrEmpty(Profile.ApiKey)) req.Headers.TryAddWithoutValidation("x-api-key", Profile.ApiKey);
        req.Headers.TryAddWithoutValidation("anthropic-version", AnthropicMessages.Version);
        req.Headers.UserAgent.ParseAdd(OpenAiClient.UserAgent);
        if (Profile.CustomHeaders is null) return;
        foreach (var (key, value) in Profile.CustomHeaders)
        {
            if (string.IsNullOrWhiteSpace(key)) continue;
            req.Headers.Remove(key);
            if (!req.Headers.TryAddWithoutValidation(key, value) && req.Content is not null)
            {
                req.Content.Headers.Remove(key);
                req.Content.Headers.TryAddWithoutValidation(key, value);
            }
        }
    }

    // MARK: Models

    /// <summary>Every model the key can use (GET /v1/models, a page at a time).</summary>
    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        var ids = new List<string>();
        string? after = null;
        for (var page = 0; page < 10; page++)
        {
            var url = Endpoint("models") + "?limit=1000" + (after is null ? "" : "&after_id=" + Uri.EscapeDataString(after));
            var obj = await GetJsonAsync(new Uri(url), cancellationToken).ConfigureAwait(false);
            if (obj["data"] is not JsonArray data) throw LlmException.Sse("unexpected /v1/models payload");
            ids.AddRange(data.OfType<JsonObject>().Select(e => JsonArgs.String(e, "id")).OfType<string>());
            if (!JsonArgs.Bool(obj, "has_more", false) || JsonArgs.String(obj, "last_id") is not { Length: > 0 } last) break;
            after = last;
        }
        return ids;
    }

    /// <summary>The model's window and output limit from GET /v1/models/{id} (max_input_tokens, max_tokens), else the model
    /// table's. The route always "serves" its configured model: the API serves every model at once.</summary>
    public async Task<ModelInfo> ModelInfoAsync(string? model = null, CancellationToken cancellationToken = default)
    {
        var id = string.IsNullOrEmpty(model) ? Profile.Model : model;
        var traits = BedrockModels.Traits(id);
        int? window = null, maxOut = null;
        try
        {
            var obj = await GetJsonAsync(new Uri(Endpoint("models/" + Uri.EscapeDataString(id))), cancellationToken).ConfigureAwait(false);
            window = JsonNumbers.Positive(obj, "max_input_tokens");
            maxOut = JsonNumbers.Positive(obj, "max_tokens");
        }
        catch (LlmException) when (!cancellationToken.IsCancellationRequested)
        {
            // The table's figures below; the first message says whether the key and model work.
        }
        return new ModelInfo(id, window ?? traits.ContextWindow, maxOut ?? traits.MaxTokensCeiling, [id]);
    }

    private async Task<JsonObject> GetJsonAsync(Uri url, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        HttpResponseMessage response;
        try
        {
            response = await SendAsync(HttpMethod.Get, url, null, Profile.Model, timeout.Token, stream: false).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw LlmException.Connection("no answer within 15 s");
        }
        using (response)
        {
            string text;
            try
            {
                text = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                throw LlmException.Connection(ex.Message, ex);
            }
            try
            {
                if (JsonNode.Parse(text) is JsonObject obj) return obj;
            }
            catch (JsonException) { }
            throw LlmException.Sse($"unexpected reply from {url.AbsolutePath}");
        }
    }
}
