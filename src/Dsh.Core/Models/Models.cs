using System.Globalization;
using System.Text.Json.Serialization;

namespace Dsh.Core;

// MARK: - Wire shapes (OpenAI-compatible chat completion, our internal currency)

public enum MessageRole { System, User, Assistant, Tool }

/// <summary>A chat message in provider-neutral form.</summary>
public sealed record LlmMessage
{
    public MessageRole Role { get; init; }
    public string? Content { get; init; }
    /// <summary>Assistant tool calls, emitted after the message completes.</summary>
    public IReadOnlyList<ToolCall>? ToolCalls { get; init; }
    /// <summary>For <see cref="MessageRole.Tool"/>: the call this result answers.</summary>
    public string? ToolCallId { get; init; }
    /// <summary>For <see cref="MessageRole.Tool"/>: the tool that produced it.</summary>
    public string? Name { get; init; }
    /// <summary>User-attached files/images sent alongside the text.</summary>
    public IReadOnlyList<MessageAttachment>? Attachments { get; init; }
    /// <summary>Set on the synthetic user message that carries images a tool produced (names of the
    /// tools). Such messages are pruned to the newest few so a long debugging session doesn't fill
    /// the window with old screenshots.</summary>
    public string? ImageSource { get; init; }
    /// <summary>For <see cref="MessageRole.Assistant"/>: opaque provider data that must be sent back
    /// verbatim with this message (copied from <see cref="LlmStreamEvent.Done.ProviderState"/>) — e.g.
    /// Claude's signed reasoning on Bedrock, which Claude insists on seeing again while it works through
    /// the tool calls of that turn. Only the client that wrote it reads it; others ignore it. Lost when a
    /// transcript is rebuilt from text (compaction, replay), which every reader must tolerate.</summary>
    public string? ProviderState { get; init; }

    public LlmMessage(MessageRole role, string? content = null)
    {
        Role = role;
        Content = content;
    }

    public static LlmMessage User(string text, IReadOnlyList<MessageAttachment>? attachments = null) =>
        new(MessageRole.User, text) { Attachments = attachments is { Count: > 0 } ? attachments : null };

    public static LlmMessage SystemText(string text) => new(MessageRole.System, text);

    public static LlmMessage Assistant(string text, IReadOnlyList<ToolCall>? calls = null) =>
        new(MessageRole.Assistant, text) { ToolCalls = calls is { Count: > 0 } ? calls : null };

    public static LlmMessage ToolOutput(string id, string name, string output) =>
        new(MessageRole.Tool, output) { ToolCallId = id, Name = name };
}

public enum AttachmentKind { Image, File }

/// <summary>A file or image the user attaches to a message.</summary>
public sealed record MessageAttachment(AttachmentKind Kind, string Name, byte[] Data)
{
    /// <summary>Best-effort MIME type from the filename extension.</summary>
    public string Mime => System.IO.Path.GetExtension(Name).TrimStart('.').ToLowerInvariant() switch
    {
        "png" => "image/png",
        "jpg" or "jpeg" => "image/jpeg",
        "gif" => "image/gif",
        "webp" => "image/webp",
        "bmp" => "image/bmp",
        "svg" => "image/svg+xml",
        "pdf" => "application/pdf",
        _ => "application/octet-stream",
    };
}

/// <summary>One tool call requested by the model. <see cref="Arguments"/> is raw JSON as sent.</summary>
public sealed record ToolCall(string Id, string Name, string Arguments);

// MARK: - Tool specifications (what we tell the model)

/// <summary>A tool offered to the model. <see cref="Parameters"/> is a JSON Schema object, as text.</summary>
public sealed record ToolSpec(string Name, string Description, string Parameters);

// MARK: - Provider profiles

[JsonConverter(typeof(JsonStringEnumConverter<ProviderKind>))]
public enum ProviderKind
{
    /// <summary>127.0.0.1:11434/v1</summary>
    [JsonStringEnumMemberName("ollama")] Ollama,
    /// <summary>127.0.0.1:1234/v1</summary>
    [JsonStringEnumMemberName("lmStudio")] LmStudio,
    /// <summary>Any OpenAI-compatible server (vLLM, SGLang, a DGX Spark, llama.cpp, ...).</summary>
    [JsonStringEnumMemberName("openAICompat")] OpenAICompat,
    [JsonStringEnumMemberName("openAI")] OpenAI,
    [JsonStringEnumMemberName("openRouter")] OpenRouter,
    /// <summary>Amazon Bedrock through its Converse API, signed with AWS credentials from the AWS CLI
    /// (<see cref="ProviderProfile.AwsProfile"/>) or a Bedrock API key.</summary>
    [JsonStringEnumMemberName("bedrock")] Bedrock,
}

/// <summary>One configured model route: where the server is, which model, and how to talk to it.
/// The API key is never serialized — it lives in Windows Credential Manager.</summary>
public sealed record ProviderProfile
{
    public ProviderKind Kind { get; set; }
    public string Name { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    [JsonIgnore] public string? ApiKey { get; set; }
    public string Model { get; set; } = "";
    public double? Temperature { get; set; }
    public int? MaxOutputTokens { get; set; }
    /// <summary>Explicit context-window override in tokens. When set, it wins over the server probe
    /// and the well-known fallback tables.</summary>
    public int? ContextWindow { get; set; }
    /// <summary>Extra HTTP headers sent with every request (auth tokens, org IDs, ...).</summary>
    public Dictionary<string, string>? CustomHeaders { get; set; }
    /// <summary>Default thinking level for this route, stored as a <see cref="ThinkingLevel"/> raw
    /// value ("off", "low", "medium", "high", "max"); null = the server's own default.</summary>
    public string? ReasoningEffort { get; set; }
    /// <summary>Whether the model can take images. null = assume yes.</summary>
    public bool? Vision { get; set; }
    /// <summary>SHA-256 fingerprint ("AB:CD:…", as openssl prints it) of a self-signed certificate this
    /// server is trusted to present — e.g. the DGX Spark's nginx TLS front. Checked only when normal
    /// Windows trust fails; null = normal trust only.</summary>
    public string? PinnedCertificate { get; set; }
    /// <summary>Bedrock: the AWS CLI profile whose sign-in DSH uses (e.g. "dsh-bedrock"). Null with an
    /// <see cref="ApiKey"/> = authenticate with that Bedrock API key instead.</summary>
    public string? AwsProfile { get; set; }
    /// <summary>Bedrock: the AWS Region to call (e.g. "us-east-1").</summary>
    public string? AwsRegion { get; set; }

    public ProviderProfile() { }

    public ProviderProfile(ProviderKind kind, string name, string baseUrl, string model)
    {
        Kind = kind;
        Name = name;
        BaseUrl = baseUrl;
        Model = model;
    }

    public static IReadOnlyList<ProviderProfile> Presets =>
    [
        new(ProviderKind.Ollama, "Ollama (local)", "http://127.0.0.1:11434/v1", "qwen3:8b"),
        new(ProviderKind.LmStudio, "LM Studio (local)", "http://127.0.0.1:1234/v1", "local-model"),
        new(ProviderKind.OpenAICompat, "DGX Spark / vLLM (LAN)", "http://DGX-SPARK-ADDRESS:8002/v1", "my-ai"),
        new(ProviderKind.OpenAI, "OpenAI", "https://api.openai.com/v1", "gpt-4o"),
        new(ProviderKind.OpenRouter, "OpenRouter", "https://openrouter.ai/api/v1", "qwen/qwen3-32b"),
    ];

    public string Endpoint(string path) => BaseUrl.EndsWith('/') ? BaseUrl + path : BaseUrl + "/" + path;

    /// <summary>The route's default thinking level (null = leave it to the server).</summary>
    [JsonIgnore]
    public ThinkingLevel? Thinking
    {
        get => ThinkingLevels.FromRaw(ReasoningEffort);
        set => ReasoningEffort = value?.RawValue();
    }

    /// <summary>Stable identity for a configured route: survives reordering, and two models on the
    /// same server stay distinct.</summary>
    [JsonIgnore] public string RouteId => $"{Name}|{Model}";

    [JsonIgnore] public string DisplayName => $"{Name} · {Model}";

    /// <summary>True for a server you run yourself (vLLM, SGLang, llama.cpp, Ollama, LM Studio ...) as
    /// opposed to a hosted API. Decided by host, not kind: a Spark behind nginx is often configured
    /// as kind "OpenAI" because it speaks the same protocol, and it still wants chat_template_kwargs.</summary>
    [JsonIgnore]
    public bool IsSelfHosted
    {
        get
        {
            if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host)) return true;
            var host = uri.Host.ToLowerInvariant();
            string[] hosted =
            [
                "openai.com", "openrouter.ai", "anthropic.com", "groq.com", "together.xyz", "together.ai",
                "fireworks.ai", "mistral.ai", "deepseek.com", "x.ai", "googleapis.com", "azure.com",
                "cerebras.ai", "perplexity.ai", "amazonaws.com", "api.aws",
            ];
            return !hosted.Any(h => host == h || host.EndsWith("." + h, StringComparison.Ordinal));
        }
    }

    /// <summary>A local server needs no key and should not be nagged for one.</summary>
    [JsonIgnore]
    public bool NeedsApiKey => Kind switch
    {
        ProviderKind.Ollama or ProviderKind.LmStudio => false,
        ProviderKind.OpenAICompat => !IsLoopbackOrLan,
        ProviderKind.Bedrock => AwsProfile is null,
        _ => true,
    };

    [JsonIgnore]
    public bool IsLoopbackOrLan
    {
        get
        {
            if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri)) return false;
            var host = uri.Host;
            if (host == "localhost" || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)) return true;
            if (host.StartsWith("127.") || host.StartsWith("10.") || host.StartsWith("192.168.")) return true;
            if (host.StartsWith("172."))
            {
                var parts = host.Split('.');
                return parts.Length > 1 && int.TryParse(parts[1], out var second) && second is >= 16 and <= 31;
            }
            return false;
        }
    }

    /// <summary>A deep copy (headers included) so an editor can change it freely.</summary>
    public ProviderProfile DeepCopy() => this with
    {
        CustomHeaders = CustomHeaders is null ? null : new Dictionary<string, string>(CustomHeaders),
    };
}

// MARK: - Thinking / reasoning effort

/// <summary>How hard a reasoning model should think before answering.
///
/// On the wire this becomes, for self-hosted OpenAI-compatible servers (vLLM / SGLang serving
/// Qwen3.x, GLM, DeepSeek, gpt-oss ...): chat_template_kwargs.enable_thinking (false for Off), and
/// reasoning_effort both top-level and inside chat_template_kwargs. For hosted APIs only the
/// top-level reasoning_effort is sent. Templates disagree on the vocabulary, so the client learns
/// the accepted set from the server's 400 and remaps automatically (see
/// <see cref="ReasoningEffortCache"/>).</summary>
public enum ThinkingLevel { Off, Low, Medium, High, Max }

public static class ThinkingLevels
{
    public static IReadOnlyList<ThinkingLevel> All { get; } =
        [ThinkingLevel.Off, ThinkingLevel.Low, ThinkingLevel.Medium, ThinkingLevel.High, ThinkingLevel.Max];

    public static string RawValue(this ThinkingLevel level) => level switch
    {
        ThinkingLevel.Off => "off",
        ThinkingLevel.Low => "low",
        ThinkingLevel.Medium => "medium",
        ThinkingLevel.High => "high",
        _ => "max",
    };

    public static ThinkingLevel? FromRaw(string? raw) => raw switch
    {
        "off" => ThinkingLevel.Off,
        "low" => ThinkingLevel.Low,
        "medium" => ThinkingLevel.Medium,
        "high" => ThinkingLevel.High,
        "max" => ThinkingLevel.Max,
        _ => null,
    };

    public static string Label(this ThinkingLevel level) => level switch
    {
        ThinkingLevel.Off => "Off",
        ThinkingLevel.Low => "Low",
        ThinkingLevel.Medium => "Medium",
        ThinkingLevel.High => "High",
        _ => "Max",
    };

    public static string Blurb(this ThinkingLevel level) => level switch
    {
        ThinkingLevel.Off => "No thinking — fastest replies",
        ThinkingLevel.Low => "Brief thinking",
        ThinkingLevel.Medium => "Balanced",
        ThinkingLevel.High => "Careful",
        _ => "Deepest (slowest)",
    };

    /// <summary>The effort word we try first; null for Off.</summary>
    public static string? WireEffort(this ThinkingLevel level) => level switch
    {
        ThinkingLevel.Off => null,
        ThinkingLevel.Low => "low",
        ThinkingLevel.Medium => "medium",
        ThinkingLevel.High => "high",
        _ => "xhigh",
    };

    /// <summary>Parse user input like "/think high", "fast", "none", "xhigh".</summary>
    public static ThinkingLevel? ParseUserInput(string raw) => raw.Trim().ToLowerInvariant() switch
    {
        "off" or "none" or "no" or "fast" or "0" or "false" or "disable" or "disabled" => ThinkingLevel.Off,
        "low" or "light" or "minimal" or "1" => ThinkingLevel.Low,
        "medium" or "med" or "mid" or "normal" or "2" => ThinkingLevel.Medium,
        "high" or "hard" or "slow" or "3" => ThinkingLevel.High,
        "max" or "xhigh" or "maximum" or "highest" or "deep" or "4" => ThinkingLevel.Max,
        _ => null,
    };
}

/// <summary>Metadata about a model, used to size the context gauge.
/// <see cref="Id"/> is the id the server actually serves for the route: usually the configured
/// model; when that isn't served but the server serves exactly one model (a box that swaps models)
/// it is that one, so the app follows it instead of failing with "model not found".</summary>
public sealed record ModelInfo(string Id, int? ContextWindow = null, int? MaxTokens = null,
                               IReadOnlyList<string>? ServedModels = null)
{
    public IReadOnlyList<string> Served => ServedModels ?? [];
}

// MARK: - Requests / responses

public sealed record LlmRequest(
    string SystemPrompt,
    IReadOnlyList<LlmMessage> Messages,
    IReadOnlyList<ToolSpec> Tools,
    string Model,
    double? Temperature = null,
    int? MaxTokens = null,
    /// <summary>Thinking level for this request; null = the provider profile's default.</summary>
    ThinkingLevel? Thinking = null);

public sealed record LlmUsage(int PromptTokens, int CompletionTokens);

/// <summary>What the engine consumes from a streaming client. The client accumulates partial
/// tool_calls internally and hands the engine complete calls at the end.</summary>
public abstract record LlmStreamEvent
{
    /// <summary>A text delta to append to the assistant message.</summary>
    public sealed record Text(string Delta) : LlmStreamEvent;
    /// <summary>A reasoning ("thinking") delta. Shown live; a provider that needs it back carries it in
    /// <see cref="Done.ProviderState"/>.</summary>
    public sealed record Reasoning(string Delta) : LlmStreamEvent;
    /// <summary>The stream finished: complete tool calls (may be empty) + metadata.</summary>
    public sealed record Done(IReadOnlyList<ToolCall> Calls, string? Finish, LlmUsage? Usage) : LlmStreamEvent
    {
        /// <summary>Opaque provider data the caller must store on the assistant message this turn
        /// becomes (<see cref="LlmMessage.ProviderState"/>) and so send back verbatim — e.g. signed
        /// reasoning. Null for providers that need nothing.</summary>
        public string? ProviderState { get; init; }
    }
}

public enum LlmErrorKind { NoModel, Connection, Http, Overflow, Sse, Unsupported }

/// <summary>Transport errors with user-actionable wording (the wizard's smoke test shows these).</summary>
public sealed class LlmException : Exception
{
    public LlmErrorKind Kind { get; }
    public int StatusCode { get; }
    public string Body { get; } = "";
    /// <summary>For <see cref="LlmErrorKind.Overflow"/>: the window the server says it has.</summary>
    public int Limit { get; }
    /// <summary>The client knows retrying cannot help, whatever the status code would suggest (a 404 for
    /// a model that will not appear by waiting — unlike a self-hosted server swapping models).</summary>
    public bool Permanent { get; }

    private LlmException(LlmErrorKind kind, string message, int statusCode = 0, string body = "", int limit = 0,
                         Exception? inner = null, bool permanent = false) : base(message, inner)
    {
        Kind = kind;
        StatusCode = statusCode;
        Body = body;
        Limit = limit;
        Permanent = permanent;
    }

    public static LlmException NoModel() => new(LlmErrorKind.NoModel,
        "No model is configured. Run the setup wizard or pick a model in Settings.");

    public static LlmException Connection(string why, Exception? inner = null) => new(LlmErrorKind.Connection,
        $"Could not reach the model server ({why}). Is it running and is the address right?", inner: inner);

    public static LlmException Http(int code, string body) => new(LlmErrorKind.Http,
        $"The model server replied {code}: {TextUtil.Prefix(body, 300)}", code, body);

    /// <summary>An HTTP-class refusal the provider client has already put into plain words (Bedrock's
    /// AccessDenied → "enable access to the model"; an AWS sign-in that ran out). Retried by
    /// <paramref name="code"/> like <see cref="Http"/> unless <paramref name="permanent"/>;
    /// <paramref name="body"/> is the server's own wording, for the retry status line.</summary>
    public static LlmException Rejected(int code, string message, string body, bool permanent = false,
                                        Exception? inner = null) =>
        new(LlmErrorKind.Http, message, code, body, inner: inner, permanent: permanent);

    /// <summary>The server refused the request because it would exceed the model's context window,
    /// and said what the limit is. The caller should compact the transcript and retry once.</summary>
    public static LlmException Overflow(int limit, string detail) => new(LlmErrorKind.Overflow,
        limit > 0
            ? $"Conversation is too long for the {Fmt.N(limit)}-token window ({TextUtil.Prefix(detail, 120)})."
            : $"Conversation is too long for the server ({TextUtil.Prefix(detail, 120)}).",
        400, detail, limit);

    public static LlmException Sse(string why) => new(LlmErrorKind.Sse, $"The model stream ended unexpectedly ({why}).");

    public static LlmException Unsupported(string what) => new(LlmErrorKind.Unsupported, $"{what} is not supported yet.");
}

/// <summary>A streaming chat client — the seam the engine tests against mocks.</summary>
public interface ILlmClient
{
    /// <summary>Stream one model turn: <see cref="LlmStreamEvent.Text"/> deltas then exactly one
    /// <see cref="LlmStreamEvent.Done"/>.</summary>
    IAsyncEnumerable<LlmStreamEvent> StreamAsync(LlmRequest request, CancellationToken cancellationToken = default);

    /// <summary>Liveness + capability probe: the server's model list.</summary>
    Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default);
}

/// <summary>Number formatting shared by user-facing strings.</summary>
public static class Fmt
{
    /// <summary>"262,144" (grouped, current culture).</summary>
    public static string N(long value) => value.ToString("N0", CultureInfo.CurrentCulture);

    /// <summary>"1M", "512K", "32K" — compact window sizes for menus.</summary>
    public static string Short(int tokens) => tokens >= 1_000_000
        ? (tokens / 1_000_000.0).ToString("0.#", CultureInfo.InvariantCulture) + "M"
        : $"{tokens / 1024}K";
}
