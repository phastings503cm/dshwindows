using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace Dsh.Core;

// MARK: - OpenAI-compatible streaming client
//
// Talks to any server that speaks the OpenAI chat-completions API (OpenAI, OpenRouter, Ollama /v1,
// LM Studio, vLLM/SGLang on a DGX Spark, llama.cpp server, ...). The engine stays provider-agnostic
// through ILlmClient; this is the only real-network implementation.
//
// Two tool-call shapes are understood:
//   1. native tool_calls deltas (OpenAI, vLLM, OpenRouter, LM Studio), and
//   2. the Qwen/DeepSeek XML convention emitted inside plain text, for backends that lack
//      function calling (see XmlToolCalls).

public sealed class OpenAiClient : IProviderClient
{
    /// <summary>A stream that sends nothing for this long is treated as dead (and the call retried,
    /// see RequestRetry). Generous: a local server prefilling a few hundred thousand tokens sends
    /// nothing until the first token, and a timeout here only means a retry that starts the prefill
    /// over.</summary>
    public static TimeSpan IdleTimeout { get; set; } = TimeSpan.FromSeconds(600);

    // One pooled handler for the whole app. No automatic decompression: a gzip'd SSE stream is
    // buffered until the compressor flushes, which would stall live output.
    private static readonly SocketsHttpHandler SharedHandler = new()
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectTimeout = TimeSpan.FromSeconds(15),
        AutomaticDecompression = DecompressionMethods.None,
    };

    /// <summary>Handlers for servers with a pinned self-signed certificate, one per fingerprint (clients
    /// are made per request and per retry; the connection pools must not be).</summary>
    private static readonly ConcurrentDictionary<string, SocketsHttpHandler> PinnedHandlers = new(StringComparer.OrdinalIgnoreCase);

    public ProviderProfile Profile { get; }
    private readonly HttpClient _http;

    public OpenAiClient(ProviderProfile profile, HttpMessageHandler? handler = null)
    {
        Profile = profile;
        _http = new HttpClient(handler ?? HandlerFor(profile.PinnedCertificate), disposeHandler: false)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    /// <summary>The shared handler, or one that also accepts exactly the pinned certificate when normal
    /// trust fails (a self-signed server the user chose to trust — never "accept anything").</summary>
    private static SocketsHttpHandler HandlerFor(string? pinned)
    {
        if (string.IsNullOrWhiteSpace(pinned)) return SharedHandler;
        return PinnedHandlers.GetOrAdd(pinned.Trim(), fingerprint => new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(15),
            AutomaticDecompression = DecompressionMethods.None,
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
                    errors == SslPolicyErrors.None
                    || (certificate is not null && string.Equals(SparkSwapperClient.Fingerprint(certificate.GetRawCertData()),
                                                                 fingerprint, StringComparison.OrdinalIgnoreCase)),
            },
        });
    }

    // MARK: ILlmClient

    public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(LlmRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var channel = Channel.CreateUnbounded<LlmStreamEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
        });
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var producer = Task.Run(async () =>
        {
            try
            {
                var result = await RunTurnAsync(request,
                    delta => channel.Writer.TryWrite(new LlmStreamEvent.Text(delta)),
                    delta => channel.Writer.TryWrite(new LlmStreamEvent.Reasoning(delta)),
                    isRetry: false, cts.Token).ConfigureAwait(false);
                channel.Writer.TryWrite(new LlmStreamEvent.Done(result.Calls, result.Finish, result.Usage));
                channel.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                channel.Writer.TryComplete(ex);
            }
        }, CancellationToken.None);

        try
        {
            await foreach (var item in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return item;
            }
        }
        finally
        {
            // The consumer stopped early (or was cancelled): stop the request too.
            cts.Cancel();
            try { await producer.ConfigureAwait(false); } catch { /* already surfaced */ }
        }
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(Profile.Endpoint("models"), UriKind.Absolute, out var url))
            throw LlmException.Unsupported($"bad base URL: {Profile.BaseUrl}");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyHeaders(req);
        string body;
        HttpStatusCode status;
        try
        {
            using var response = await _http.SendAsync(req, timeout.Token).ConfigureAwait(false);
            status = response.StatusCode;
            body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw LlmException.Connection("no answer within 10 s");
        }
        catch (HttpRequestException ex)
        {
            throw LlmException.Connection(ex.InnerException?.Message ?? ex.Message, ex);
        }
        if ((int)status is < 200 or > 299) throw LlmException.Http((int)status, body);
        try
        {
            if (JsonNode.Parse(body) is JsonObject obj && obj["data"] is JsonArray data)
            {
                return data.OfType<JsonObject>()
                    .Select(e => JsonArgs.String(e, "id"))
                    .OfType<string>()
                    .ToList();
            }
        }
        catch (JsonException) { }
        throw LlmException.Sse("unexpected /models payload");
    }

    /// <summary>Probe the server for the active model's metadata (context window, the id it really
    /// serves, ...). ContextWindow is null when no live source answered — deliberately: the caller
    /// applies the static fallback tables fresh each time, so a transient miss never locks a guessed
    /// window in as "learned".
    ///
    /// Sources, in order: GET /v1/models (vLLM and SGLang report max_model_len; if the configured id
    /// isn't listed but the server serves exactly one model, that one is used and reported as Id),
    /// then SGLang's /get_model_info and /get_server_info at /v1/... and at the server root.</summary>
    public async Task<ModelInfo> ModelInfoAsync(string? model = null, CancellationToken cancellationToken = default)
    {
        var id = string.IsNullOrEmpty(model) ? Profile.Model : model;
        int? limit = null;
        int? maxOut = null;
        var resolved = id;
        var served = new List<string>();

        if (Uri.TryCreate(Profile.Endpoint("models"), UriKind.Absolute, out var url))
        {
            var obj = await GetJsonAsync(url, TimeSpan.FromSeconds(8), cancellationToken).ConfigureAwait(false);
            if (obj?["data"] is JsonArray arr)
            {
                var entries = arr.OfType<JsonObject>().ToList();
                served = entries.Select(e => JsonArgs.String(e, "id")).OfType<string>().ToList();
                var hit = entries.FirstOrDefault(e => JsonArgs.String(e, "id") == id)
                          ?? entries.FirstOrDefault(e => MatchesModel(JsonArgs.String(e, "id") ?? "", id));
                if (hit is null && entries.Count == 1) hit = entries[0];
                if (hit is not null)
                {
                    resolved = JsonArgs.String(hit, "id") ?? id;
                    limit = ContextWindow(hit);
                    maxOut = JsonNumbers.TryGetInt(hit["max_tokens"], out var m) ? m : null;
                }
            }
        }
        limit ??= await SglangContextLengthAsync(cancellationToken).ConfigureAwait(false);
        return new ModelInfo(resolved, limit, maxOut, served);
    }

    /// <summary>SGLang-specific metadata routes. They live at the server root, but the base URL
    /// usually ends in /v1 (and behind a proxy the root may belong to something else entirely,
    /// which answers HTML — ignored), so try both.</summary>
    private async Task<int?> SglangContextLengthAsync(CancellationToken cancellationToken)
    {
        var root = StrippingV1(Profile.BaseUrl);
        string[] candidates =
        [
            Profile.Endpoint("get_model_info"), root + "/get_model_info",
            Profile.Endpoint("get_server_info"), root + "/get_server_info",
        ];
        foreach (var candidate in candidates)
        {
            if (!Uri.TryCreate(candidate, UriKind.Absolute, out var url)) continue;
            var obj = await GetJsonAsync(url, TimeSpan.FromSeconds(6), cancellationToken).ConfigureAwait(false);
            if (obj is null) continue;
            if (JsonNumbers.Positive(obj, "context_len") is { } a) return a;
            if (JsonNumbers.Positive(obj, "context_length") is { } b) return b;
        }
        return null;
    }

    private async Task<JsonObject?> GetJsonAsync(Uri url, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout);
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            ApplyHeaders(req);
            using var response = await _http.SendAsync(req, cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            var text = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            return JsonNode.Parse(text) as JsonObject;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>A base URL with a trailing "/v1" removed, so root-level SGLang routes can be hit even
    /// when the provider was configured with the OpenAI path.</summary>
    public static string StrippingV1(string baseUrl)
    {
        var s = baseUrl.TrimEnd('/');
        if (s.EndsWith("/v1", StringComparison.Ordinal)) s = s[..^3];
        return s;
    }

    /// <summary>Match the active model name against a /models entry id, tolerating the common
    /// shapes: exact, "vendor/model" vs "model", and Ollama tags.</summary>
    public static bool MatchesModel(string entryId, string requested)
    {
        if (entryId == requested) return true;
        var baseName = requested.Split('/')[^1];
        var entryBase = entryId.Split('/')[^1];
        return entryId == baseName || entryBase == baseName;
    }

    private static readonly Regex[] OverflowPatterns =
    [
        // SGLang: "The input (270000 tokens) is longer than the model's context length (262144 tokens)."
        new(@"context length \((\d+) tokens\)", RegexOptions.IgnoreCase),
        new(@"maximum context length of (\d+)", RegexOptions.IgnoreCase),
        new(@"context length of (\d+)", RegexOptions.IgnoreCase),
        new(@"maximum context length is (\d+)", RegexOptions.IgnoreCase),
        new(@"context length is (\d+)", RegexOptions.IgnoreCase),
        new(@"exceed the maximum context length of (\d+)", RegexOptions.IgnoreCase),
        new(@"max context length of (\d+)", RegexOptions.IgnoreCase),
        new(@"maximum context tokens: (\d+)", RegexOptions.IgnoreCase),
    ];

    /// <summary>The real context limit from an overflow error body, e.g. SGLang's "This model's
    /// maximum context length is 262144 tokens; however, you requested 270000 tokens". Null when the
    /// body carries no limit.</summary>
    public static int? OverflowLimit(string body)
    {
        foreach (var regex in OverflowPatterns)
        {
            var m = regex.Match(body);
            if (m.Success && int.TryParse(m.Groups[1].Value, out var n) && n > 0) return n;
        }
        return null;
    }

    /// <summary>A context-window figure from the assorted field names servers use.</summary>
    public static int? ContextWindow(JsonObject entry) =>
        JsonNumbers.Positive(entry, "context")                 // Ollama
        ?? JsonNumbers.Positive(entry, "context_length")       // some vLLM
        ?? JsonNumbers.Positive(entry, "max_context_length")   // LM Studio
        ?? JsonNumbers.Positive(entry, "context_window")       // OpenRouter
        ?? JsonNumbers.Positive(entry, "context_len")          // SGLang (echoed on /models)
        ?? JsonNumbers.Positive(entry, "max_context_len")      // SGLang
        ?? JsonNumbers.Positive(entry, "max_model_len");       // vLLM / SGLang / llama.cpp
    // Not max_total_tokens: that is SGLang's KV-pool size, not a window.

    // MARK: One streaming turn

    internal sealed record TurnResult(string Text, IReadOnlyList<ToolCall> Calls, string? Finish, LlmUsage? Usage);

    internal sealed class PendingCall
    {
        public string Id = "";
        public string Name = "";
        public readonly StringBuilder Args = new();
    }

    private async Task<TurnResult> RunTurnAsync(LlmRequest request, Action<string> onText, Action<string> onReasoning,
                                                bool isRetry, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(Profile.Endpoint("chat/completions"), UriKind.Absolute, out var url))
            throw LlmException.Unsupported($"bad base URL: {Profile.BaseUrl}");

        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(MakeBody(request), TextUtil.Utf8NoBom, "application/json"),
        };
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        ApplyHeaders(req);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw LlmException.Connection(ex.InnerException?.Message ?? ex.Message, ex);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Not our Stop: the connect timed out or the system dropped the request — worth a retry,
            // not a reason to halt a queue.
            throw LlmException.Connection("the request timed out", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var body = await ReadErrorBodyAsync(response, cancellationToken).ConfigureAwait(false);
                var code = (int)response.StatusCode;
                // A chat template that rejects our effort word ("Unexpected reasoning effort high.
                // Supported types are xhigh (default), medium, and low.") — learn the nearest
                // accepted word and retry once.
                if (!isRetry && code == 400 && EffortWord(request) is { } requested
                    && EffortCorrection(requested, body) is { } fix)
                {
                    ReasoningEffortCache.Shared.Learn(EffortRoute(request), requested, fix);
                    return await RunTurnAsync(request, onText, onReasoning, isRetry: true, cancellationToken)
                        .ConfigureAwait(false);
                }
                // SGLang / vLLM report a context overflow as "This model's maximum context length
                // is N tokens; however, you requested M ...".
                if (OverflowLimit(body) is { } limit) throw LlmException.Overflow(limit, body);
                // A proxy in front of the model (nginx's client_max_body_size) refusing the body:
                // the conversation must shrink, like an overflow.
                if (code == 413) throw LlmException.Overflow(0, "request too large for the server (HTTP 413)");
                throw LlmException.Http(code, body);
            }

            var state = new StreamState();

            Stream stream;
            try
            {
                stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw LlmException.Connection(ex.Message, ex);
            }

            // StreamReader assembles whole lines from raw bytes, so UTF-8 sequences split across
            // network chunks never break.
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
                    throw LlmException.Sse($"no data from the server for {(int)IdleTimeout.TotalSeconds} s");
                }
                catch (IOException ex)
                {
                    throw LlmException.Sse(ex.Message);
                }
                catch (HttpRequestException ex)
                {
                    throw LlmException.Sse(ex.Message);
                }
                if (line is null) break;
                ParseLine(line, state, onText, onReasoning);
            }

            var status = (int)response.StatusCode;
            // Not SSE at all: some servers ignore stream: true and answer with one JSON completion
            // (or a JSON error).
            if (!state.SawData && state.OtherBody.Length > 0)
            {
                ParsePlainBody(state.OtherBody.ToString(), state, status, onText);
                if (state.Finish is null)
                {
                    // Something answered, but not a model (a proxy's HTML page, a wrong base URL):
                    // retrying can't fix that.
                    var snippet = TextUtil.Prefix(state.OtherBody.ToString().Replace("\r", "").Replace('\n', ' '), 200);
                    throw LlmException.Http(status,
                        $"unrecognised reply (is the base URL an OpenAI-compatible /v1?): {snippet}");
                }
            }
            if (state.Finish == "error") throw LlmException.Http(500, "the server ended the reply with an error");
            // Every OpenAI-compatible server ends a reply with a finish_reason and/or data: [DONE].
            // Neither means the connection closed mid-reply (server restarted, proxy dropped it) — a
            // failure worth retrying, not a short answer.
            if (!state.SawDone && state.Finish is null)
            {
                throw LlmException.Sse(state.SawData
                    ? "the reply was cut off before it finished"
                    : "the server closed the connection without replying");
            }

            var calls = state.Pending.Select(entry => new ToolCall(
                    entry.Value.Id.Length == 0 ? $"call-{entry.Key}" : entry.Value.Id,
                    entry.Value.Name,
                    entry.Value.Args.Length == 0 ? "{}" : entry.Value.Args.ToString()))
                .ToList();

            var full = state.Text.ToString();
            // XML tool-call fallback: some backends (Qwen on Ollama without function calling, older
            // vLLM) emit tool blocks in the text instead.
            if (calls.Count == 0 && XmlToolCalls.ContainsBlock(full))
            {
                var parsed = XmlToolCalls.Parse(full);
                for (var i = 0; i < parsed.Count; i++)
                    calls.Add(new ToolCall($"xml-{i}", parsed[i].Name, parsed[i].ArgumentsJson));
            }
            return new TurnResult(full, calls, state.Finish, state.Usage);
        }
    }

    /// <summary>The first 8 KB of an error reply (enough for any message; a proxy's HTML page can be huge).</summary>
    internal static async Task<string> ReadErrorBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var buffer = new byte[8_000];
            var total = 0;
            while (total < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                total += read;
            }
            return Encoding.UTF8.GetString(buffer, 0, total);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return "";
        }
    }

    /// <summary>Everything one streamed reply accumulates.</summary>
    internal sealed class StreamState
    {
        public readonly StringBuilder Text = new();
        public LlmUsage? Usage;
        public string? Finish;
        public readonly SortedDictionary<int, PendingCall> Pending = new();
        /// <summary>Saw data: [DONE].</summary>
        public bool SawDone;
        /// <summary>Saw at least one data: line.</summary>
        public bool SawData;
        /// <summary>Non-SSE lines (capped), for servers that answer in plain JSON.</summary>
        public readonly StringBuilder OtherBody = new();
    }

    internal static void ParseLine(string rawLine, StreamState state, Action<string> onText, Action<string> onReasoning)
    {
        var line = rawLine.TrimEnd('\r');
        // SSE: only "data:" lines matter (ignore event:, id:, keepalives) — but keep anything else
        // until the first data: line, in case the server answered with plain JSON.
        if (!line.StartsWith("data:", StringComparison.Ordinal))
        {
            if (!state.SawData && line.Length > 0 && !line.StartsWith(':') && state.OtherBody.Length < 2_000_000)
                state.OtherBody.Append(line).Append('\n');
            return;
        }
        state.SawData = true;
        var payload = line[5..];
        if (payload.StartsWith(' ')) payload = payload[1..];
        if (payload.Trim() == "[DONE]")
        {
            state.SawDone = true;
            return;
        }

        JsonObject? obj;
        try
        {
            obj = JsonNode.Parse(payload) as JsonObject;
        }
        catch (JsonException)
        {
            return; // undecodable line (keepalive pings etc.)
        }
        if (obj is null) return;

        // An error reported mid-stream (vLLM: {"object":"error",...}; others: {"error":{...}}) —
        // surface it instead of ending with an empty reply.
        if ((obj["error"] is not null || obj["choices"] is null) && StreamError(obj) is var (code, message))
        {
            if (OverflowLimit(message) is { } limit) throw LlmException.Overflow(limit, message);
            throw LlmException.Http(code, message);
        }

        if (obj["usage"] is JsonObject u
            && JsonNumbers.TryGetInt(u["prompt_tokens"], out var p)
            && JsonNumbers.TryGetInt(u["completion_tokens"], out var c)
            && (p > 0 || c > 0))
        {
            state.Usage = new LlmUsage(p, c, CachedTokens(u));
        }
        if (obj["choices"] is not JsonArray { Count: > 0 } choices || choices[0] is not JsonObject first) return;

        if (first["delta"] is JsonObject delta)
        {
            var reasoning = JsonArgs.String(delta, "reasoning_content") ?? JsonArgs.String(delta, "reasoning");
            if (!string.IsNullOrEmpty(reasoning)) onReasoning(reasoning);
            var content = JsonArgs.String(delta, "content");
            if (!string.IsNullOrEmpty(content))
            {
                state.Text.Append(content);
                onText(content);
            }
            if (delta["tool_calls"] is JsonArray toolCalls)
            {
                foreach (var tc in toolCalls.OfType<JsonObject>())
                {
                    var index = JsonNumbers.TryGetInt(tc["index"], out var i) ? i : 0;
                    if (!state.Pending.TryGetValue(index, out var entry)) state.Pending[index] = entry = new PendingCall();
                    if (JsonArgs.String(tc, "id") is { Length: > 0 } id) entry.Id = id;
                    if (tc["function"] is JsonObject fn)
                    {
                        if (JsonArgs.String(fn, "name") is { } name) entry.Name = name;
                        if (JsonArgs.String(fn, "arguments") is { } args) entry.Args.Append(args);
                    }
                }
            }
        }
        if (JsonArgs.String(first, "finish_reason") is { } fr) state.Finish = fr;
    }

    /// <summary>A plain (non-streamed) JSON reply: a completion, or an error.</summary>
    internal static void ParsePlainBody(string body, StreamState state, int status, Action<string> onText)
    {
        JsonObject? obj;
        try
        {
            obj = JsonNode.Parse(body) as JsonObject;
        }
        catch (JsonException)
        {
            return;
        }
        if (obj is null) return;
        if (obj["choices"] is null && StreamError(obj) is var (code, message))
        {
            if (OverflowLimit(message) is { } limit) throw LlmException.Overflow(limit, message);
            throw LlmException.Http(code == 500 ? status : code, message);
        }
        if (obj["choices"] is not JsonArray { Count: > 0 } choices || choices[0] is not JsonObject first
            || first["message"] is not JsonObject msg) return;
        if (JsonArgs.String(msg, "content") is { Length: > 0 } content)
        {
            state.Text.Append(content);
            onText(content);
        }
        if (msg["tool_calls"] is JsonArray calls)
        {
            var i = 0;
            foreach (var tc in calls.OfType<JsonObject>())
            {
                var entry = new PendingCall { Id = JsonArgs.String(tc, "id") ?? "" };
                if (tc["function"] is JsonObject fn)
                {
                    entry.Name = JsonArgs.String(fn, "name") ?? "";
                    entry.Args.Append(JsonArgs.String(fn, "arguments") ?? "");
                }
                state.Pending[i++] = entry;
            }
        }
        state.Finish = JsonArgs.String(first, "finish_reason") ?? "stop";
        if (obj["usage"] is JsonObject u
            && JsonNumbers.TryGetInt(u["prompt_tokens"], out var p)
            && JsonNumbers.TryGetInt(u["completion_tokens"], out var c))
        {
            state.Usage = new LlmUsage(p, c, CachedTokens(u));
        }
    }

    /// <summary>The prompt tokens the server says it served from its cache: OpenAI, vLLM and SGLang put
    /// them in <c>usage.prompt_tokens_details.cached_tokens</c>; a few servers use <c>cached_tokens</c> or
    /// <c>prompt_cache_hit_tokens</c> (DeepSeek) directly.</summary>
    internal static int? CachedTokens(JsonObject usage)
    {
        if (usage["prompt_tokens_details"] is JsonObject details && JsonNumbers.TryGetInt(details["cached_tokens"], out var nested))
            return nested;
        if (JsonNumbers.TryGetInt(usage["cached_tokens"], out var flat)) return flat;
        if (JsonNumbers.TryGetInt(usage["prompt_cache_hit_tokens"], out var hit)) return hit;
        return null;
    }

    /// <summary>The status code and message of an error object, if <paramref name="obj"/> is one.</summary>
    internal static (int Code, string Message)? StreamError(JsonObject obj)
    {
        static int? Code(JsonNode? node) =>
            JsonNumbers.TryGetInt(node, out var n) ? n
            : node is JsonValue v && v.TryGetValue<string>(out var s) && int.TryParse(s, out var parsed) ? parsed
            : null;

        if (obj["error"] is JsonObject err)
        {
            var message = JsonArgs.String(err, "message") ?? err.ToJsonString();
            return (Code(err["code"]) ?? Code(err["status"]) ?? 500, message);
        }
        if (JsonArgs.String(obj, "error") is { } text)
            return (Code(obj["code"]) ?? Code(obj["status"]) ?? 500, text);
        if (JsonArgs.String(obj, "object") == "error")
            return (Code(obj["code"]) ?? 500, JsonArgs.String(obj, "message") ?? "server error");
        return null;
    }

    // MARK: Request body

    private const string NoUserTurnNote = "(Earlier work in this conversation was summarised above. Carry on with the task.)";

    /// <summary>A tool call's arguments as the wire wants them: a JSON object. Empty or broken text (a call the model
    /// cut off or mangled) becomes <c>{}</c> — servers that hand the arguments to a chat template as a parsed object
    /// (vLLM) fail the whole request on anything else, and would keep failing for the rest of the conversation.</summary>
    internal static string SafeArguments(string arguments) =>
        string.IsNullOrWhiteSpace(arguments) || !Engine.ValidArguments(arguments) ? "{}" : arguments;

    /// <summary>The chat-completions request body, as JSON text.</summary>
    public string MakeBody(LlmRequest request)
    {
        var messages = new JsonArray();
        // Some OpenAI-compatible servers (SGLang on the DGX Spark, notably) reject any request with
        // more than one system message, or with one anywhere but index 0 — e.g. after compaction
        // inserts an "[Earlier conversation, compacted]" note into the transcript as a system
        // message. Collect every system-role message and merge them into one leading message.
        var systemParts = new List<string>();
        if (!string.IsNullOrEmpty(request.SystemPrompt)) systemParts.Add(request.SystemPrompt);
        foreach (var m in request.Messages)
        {
            switch (m.Role)
            {
                case MessageRole.System:
                    if (!string.IsNullOrEmpty(m.Content)) systemParts.Add(m.Content);
                    break;
                case MessageRole.User:
                    messages.Add(new JsonObject { ["role"] = "user", ["content"] = UserContent(m) });
                    break;
                case MessageRole.Assistant:
                    var msg = new JsonObject { ["role"] = "assistant", ["content"] = m.Content ?? "" };
                    if (m.ToolCalls is { Count: > 0 } calls)
                    {
                        var arr = new JsonArray();
                        foreach (var call in calls)
                        {
                            arr.Add(new JsonObject
                            {
                                ["id"] = call.Id,
                                ["type"] = "function",
                                ["function"] = new JsonObject { ["name"] = call.Name, ["arguments"] = SafeArguments(call.Arguments) },
                            });
                        }
                        msg["tool_calls"] = arr;
                    }
                    messages.Add(msg);
                    break;
                case MessageRole.Tool:
                    messages.Add(new JsonObject
                    {
                        ["role"] = "tool",
                        ["tool_call_id"] = m.ToolCallId ?? "",
                        ["content"] = m.Content ?? "",
                    });
                    break;
            }
        }
        // Chat templates that look for the user's question (Qwen's, among others) refuse a request that has none — and
        // that is what a long run looks like after compaction cuts inside it: the summary, then assistant and tool turns.
        if (messages.Count > 0 && !messages.Any(m => m?["role"]?.GetValue<string>() == "user"))
            messages.Insert(0, new JsonObject { ["role"] = "user", ["content"] = NoUserTurnNote });
        if (systemParts.Count > 0)
            messages.Insert(0, new JsonObject { ["role"] = "system", ["content"] = string.Join("\n\n", systemParts) });

        var body = new JsonObject
        {
            ["model"] = string.IsNullOrEmpty(request.Model) ? Profile.Model : request.Model,
            ["messages"] = messages,
            ["stream"] = true,
        };
        // include_usage is an OpenAI/OpenRouter/vLLM extension; Ollama and some older servers 400 on
        // unknown fields, so only send it for OpenAI-family endpoints.
        if (Profile.Kind is ProviderKind.OpenAI or ProviderKind.OpenRouter or ProviderKind.OpenAICompat)
            body["stream_options"] = new JsonObject { ["include_usage"] = true };
        if ((request.Temperature ?? Profile.Temperature) is { } t) body["temperature"] = t;
        if ((request.MaxTokens ?? Profile.MaxOutputTokens) is { } mt) body["max_tokens"] = mt;
        ApplyThinking(request, body);
        if (request.Tools.Count > 0)
        {
            var tools = new JsonArray();
            foreach (var spec in request.Tools)
            {
                // SGLang (Pydantic) rejects anything but a real schema object here — even for
                // parameterless tools — so a spec that doesn't parse to an object gets an empty one.
                JsonNode parameters;
                try
                {
                    parameters = JsonNode.Parse(spec.Parameters) as JsonObject ?? EmptySchema();
                }
                catch (JsonException)
                {
                    parameters = EmptySchema();
                }
                tools.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = spec.Name,
                        ["description"] = spec.Description,
                        ["parameters"] = parameters,
                    },
                });
            }
            body["tools"] = tools;
        }
        return body.ToJsonString();
    }

    private static JsonObject EmptySchema() => new() { ["type"] = "object", ["properties"] = new JsonObject() };

    // MARK: Thinking

    /// <summary>The level in force for a request: the request's own, else the route's.</summary>
    public ThinkingLevel? ThinkingLevelFor(LlmRequest request) => request.Thinking ?? Profile.Thinking;

    private string EffortRoute(LlmRequest request) =>
        $"{Profile.BaseUrl}|{(string.IsNullOrEmpty(request.Model) ? Profile.Model : request.Model)}";

    /// <summary>The effort word that will go on the wire (after any learned remap), or null when none
    /// is sent.</summary>
    public string? EffortWord(LlmRequest request)
    {
        if (ThinkingLevelFor(request) is not { } level || level.WireEffort() is not { } word) return null;
        if (ReasoningEffortCache.Shared.Accepted(EffortRoute(request), word) is { } learned)
            return learned.Length == 0 ? null : learned;
        return word;
    }

    /// <summary>Put the thinking controls on the request body.
    ///
    /// Self-hosted servers get chat_template_kwargs — that is how Qwen3.x, GLM and DeepSeek templates
    /// switch thinking on/off and read the effort — plus the top-level reasoning_effort (SGLang
    /// forwards it to the template too; vLLM's gpt-oss path reads it there). Hosted APIs only
    /// understand the top-level field, and only when thinking is on.</summary>
    public void ApplyThinking(LlmRequest request, JsonObject body)
    {
        if (ThinkingLevelFor(request) is not { } level) return;
        var effort = EffortWord(request);
        if (Profile.IsSelfHosted && Profile.Kind is ProviderKind.OpenAICompat or ProviderKind.OpenAI or ProviderKind.OpenRouter)
        {
            var kwargs = new JsonObject { ["enable_thinking"] = level != ThinkingLevel.Off };
            if (effort is not null)
            {
                kwargs["reasoning_effort"] = effort;
                body["reasoning_effort"] = effort;
            }
            body["chat_template_kwargs"] = kwargs;
        }
        else if (Profile.Kind is ProviderKind.Ollama or ProviderKind.LmStudio)
        {
            if (level == ThinkingLevel.Off) body["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = false };
            if (effort is not null) body["reasoning_effort"] = effort;
        }
        else if (effort is not null)
        {
            body["reasoning_effort"] = effort;
        }
    }

    /// <summary>Effort vocabulary, weakest → strongest, for picking the nearest word a template accepts.</summary>
    public static readonly IReadOnlyDictionary<string, int> EffortRank = new Dictionary<string, int>
    {
        ["none"] = 0, ["minimal"] = 1, ["low"] = 2, ["medium"] = 3, ["high"] = 4, ["xhigh"] = 5, ["max"] = 6,
    };

    /// <summary>If <paramref name="errorBody"/> is a template rejecting <paramref name="requested"/>,
    /// the nearest effort it accepts ("" = send none). Null when the error is about something else.</summary>
    public static string? EffortCorrection(string requested, string errorBody)
    {
        var lower = errorBody.ToLowerInvariant();
        var wanted = requested.ToLowerInvariant();
        if (!(lower.Contains("reasoning effort") || lower.Contains("reasoning_effort"))) return null;
        if (!(lower.Contains(wanted) || lower.Contains("supported") || lower.Contains("invalid"))) return null;
        // Pull the accepted words from the tail ("Supported types are xhigh (default), medium, and low").
        var tail = lower;
        foreach (var marker in new[] { "supported types are", "supported values are", "supported:", "must be one of", "expected one of", "supported" })
        {
            var at = lower.IndexOf(marker, StringComparison.Ordinal);
            if (at >= 0)
            {
                tail = lower[(at + marker.Length)..];
                break;
            }
        }
        var accepted = Regex.Split(tail, @"[^\p{L}\p{N}]+")
            .Where(w => EffortRank.ContainsKey(w) && w != wanted)
            .ToHashSet();
        if (accepted.Count == 0 || !EffortRank.TryGetValue(wanted, out var want)) return "";
        return accepted
            .OrderBy(a => Math.Abs(EffortRank[a] - want))
            .ThenByDescending(a => EffortRank[a])      // tie → the stronger one
            .First();
    }

    /// <summary>The OpenAI "content" value for a user message: plain text without attachments,
    /// otherwise a multi-part array mixing text with image / file entries (inline base64 data URIs).</summary>
    private static JsonNode UserContent(LlmMessage m)
    {
        if (m.Attachments is not { Count: > 0 } attachments) return JsonValue.Create(m.Content ?? "")!;
        var parts = new JsonArray();
        if (!string.IsNullOrEmpty(m.Content)) parts.Add(new JsonObject { ["type"] = "text", ["text"] = m.Content });
        foreach (var a in attachments)
        {
            var b64 = Convert.ToBase64String(a.Data);
            if (a.Kind == AttachmentKind.Image)
            {
                parts.Add(new JsonObject
                {
                    ["type"] = "image_url",
                    ["image_url"] = new JsonObject { ["url"] = $"data:{a.Mime};base64,{b64}" },
                });
            }
            else
            {
                parts.Add(new JsonObject
                {
                    ["type"] = "file",
                    ["file"] = new JsonObject { ["filename"] = a.Name, ["file_data"] = $"data:{a.Mime};base64,{b64}" },
                });
            }
        }
        return parts;
    }

    /// <summary>Authorization + user-supplied custom headers on every request.</summary>
    private void ApplyHeaders(HttpRequestMessage req)
    {
        if (!string.IsNullOrEmpty(Profile.ApiKey))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Profile.ApiKey);
        req.Headers.UserAgent.ParseAdd($"DSH-Windows/{AppInfo.Version}");
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
}

// MARK: - Fallback context windows
//
// Servers that don't expose their limits (most vLLM/llama.cpp builds, older Ollama) fall back to
// these tables. Keyed on the last path component of the model name, so
// "meta-llama/Llama-3.3-70B-Instruct" and "Llama-3.3-70B-Instruct" both match, and an Ollama
// "qwen3:8b" tag resolves through its base name.

public static class FallbackContextWindow
{
    /// <summary>Default when nothing else matches: 32k keeps the gauge honest without promising more
    /// than the model actually has.</summary>
    public const int DefaultLimit = 32_768;

    private static readonly Dictionary<string, int> Exact = new()
    {
        ["gpt-4o"] = 128_000, ["gpt-4o-mini"] = 128_000, ["gpt-4.1"] = 1_048_576,
        ["gpt-4.1-mini"] = 1_048_576, ["gpt-4.1-nano"] = 1_048_576, ["o1"] = 200_000,
        ["o3"] = 200_000, ["o4-mini"] = 200_000, ["o3-mini"] = 200_000,
        ["qwen3:8b"] = 32_768, ["qwen3:14b"] = 32_768, ["qwen3:32b"] = 32_768,
        ["qwen2.5:7b"] = 32_768, ["qwen2.5:14b"] = 32_768, ["qwen2.5:32b"] = 32_768,
        ["qwen2.5-coder:7b"] = 32_768, ["qwen2.5-coder:32b"] = 32_768,
        ["llama3.1:8b"] = 131_072, ["llama3.1:70b"] = 131_072, ["llama3.2:8b"] = 131_072,
        ["deepseek-r1:14b"] = 65_536, ["deepseek-r1:32b"] = 65_536,
        ["deepseek-r1:70b"] = 131_072, ["mistral:7b"] = 32_768, ["mixtral:8x7b"] = 32_768,
        // SGLang on a DGX Spark typically serves these (native windows; the live probe corrects them
        // when YaRN or an extended context_len is configured).
        ["qwen3-30b-a3b"] = 32_768, ["qwen3-32b"] = 32_768, ["qwen3-235b-a22b"] = 32_768,
        ["llama3.3-70b-instruct"] = 131_072, ["llama3.1-8b-instruct"] = 131_072,
        ["glm-4.5"] = 131_072, ["glm-4.5-air"] = 131_072, ["glm-4.6"] = 131_072,
    };

    /// <summary>Prefix rules on the base name, checked after the exact table.</summary>
    private static readonly (string Prefix, int Limit)[] Prefixes =
    [
        ("llama-3.3-", 131_072), ("llama-3.1-", 131_072), ("llama-3.2-", 131_072),
        ("llama-4-", 1_048_576), ("llama4-", 1_048_576),
        // Qwen3.5 and later ship a 256K native window; the probe replaces this with the served
        // figure (e.g. 1M under YaRN).
        ("qwen3.5", 262_144), ("qwen3.6", 262_144), ("qwen3.7", 262_144), ("qwen3.8", 262_144),
        ("qwen3.9", 262_144), ("qwen3-next", 262_144), ("qwen3-coder", 262_144),
        ("qwen3", 32_768), ("qwen2.5", 32_768), ("qwen2-72b", 131_072),
        ("deepseek-r1", 65_536), ("deepseek-v3", 131_072), ("deepseek", 65_536),
        ("gpt-4o", 128_000), ("gpt-4.1", 1_048_576), ("gpt-4", 128_000),
        ("claude-3.5", 200_000), ("claude-3", 100_000), ("claude-", 200_000),
        ("mistral-large", 131_072), ("mistral-small", 131_072),
        ("mixtral", 32_768), ("llama", 131_072), ("smollm", 131_072),
        ("glm-4.5", 131_072), ("glm-4.6", 131_072), ("glm-4", 131_072),
    ];

    /// <summary>A context window for a model name, or null if unknown.</summary>
    public static int? Limit(string modelId)
    {
        // Bedrock ids ("us.anthropic.claude-sonnet-4-5-20250929-v1:0", inference-profile ARNs) carry
        // vendor and Region prefixes the tables below don't expect.
        if (BedrockModels.ContextWindow(modelId) is { } bedrock) return bedrock;
        var baseName = modelId.Split('/')[^1].ToLowerInvariant();
        if (Exact.TryGetValue(baseName, out var hit)) return hit;
        var noTag = baseName.Split(':')[0];
        foreach (var (prefix, limit) in Prefixes)
        {
            if (noTag.StartsWith(prefix, StringComparison.Ordinal)) return limit;
        }
        return Exact.TryGetValue(modelId.ToLowerInvariant(), out var full) ? full : null;
    }
}

// MARK: - Learned reasoning-effort vocabulary

/// <summary>Per route (base URL + model), which effort word the server's template actually accepts
/// for each word we asked for — learned from its 400s so the retry happens once, not on every
/// request.</summary>
public sealed class ReasoningEffortCache
{
    public static ReasoningEffortCache Shared { get; } = new();
    private readonly Dictionary<string, string> _map = new();
    private readonly Lock _lock = new();

    public string? Accepted(string route, string requested)
    {
        lock (_lock) return _map.GetValueOrDefault($"{route}|{requested}");
    }

    public void Learn(string route, string requested, string accepted)
    {
        lock (_lock) _map[$"{route}|{requested}"] = accepted;
    }

    public void Reset()
    {
        lock (_lock) _map.Clear();
    }
}

/// <summary>Build facts shared by the harness (user agents, the environment block).</summary>
public static class AppInfo
{
    public static string Version { get; } =
        typeof(AppInfo).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion.Split('+')[0] ?? "0.0.0";
}
