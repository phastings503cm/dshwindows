using System.Globalization;
using System.Text.RegularExpressions;

namespace Dsh.Core;

// MARK: - What Bedrock's models accept
//
// Converse is one API over many model families, but it passes through what each family refuses: a
// thinking budget on a Claude that only thinks adaptively, a temperature on one whose sampling
// parameters were removed, a cache point where the family has no cache, an output limit above the
// model's ceiling. Each is a 400 that no retry can fix, so the client shapes every request from this
// table. Ids are matched with and without a cross-Region inference-profile prefix ("us.", "eu.",
// "apac.", "global.", ...) and inside inference-profile ARNs. An application inference profile's ARN
// names no model, so it gets the conservative defaults.

/// <summary>How a Claude model is asked to think.</summary>
public enum ClaudeThinking
{
    /// <summary>No extended thinking: Claude 3.5 and older, and every non-Claude model.</summary>
    None,
    /// <summary>thinking {type: "enabled", budget_tokens}: Claude 3.7 through the 4.5 generation.</summary>
    Budget,
    /// <summary>thinking {type: "adaptive"} plus outputConfig.effort: Claude 4.6 and later, where
    /// budget_tokens is deprecated (4.6) or rejected (4.7 on).</summary>
    Adaptive,
}

/// <summary>How an adaptive-thinking Claude is told not to think.</summary>
public enum ThinkingOff
{
    /// <summary>Send no thinking field: the model then doesn't think (4.6 – 4.8, and budget models).</summary>
    Omit,
    /// <summary>thinking {type: "disabled"} — Opus 5 and Sonnet 5 think unless told not to.</summary>
    Disabled,
    /// <summary>thinking {type: "between_tools"} — Sonnet 5.5 rejects "disabled".</summary>
    BetweenTools,
    /// <summary>Thinking can't be switched off (Opus 5.5, Fable): ask for the lowest effort instead.</summary>
    LowEffort,
}

/// <summary>The request-shaping facts about one Bedrock model.</summary>
public sealed record BedrockModelTraits
{
    public bool IsClaude { get; init; }
    public ClaudeThinking Thinking { get; init; }
    public ThinkingOff Off { get; init; }
    /// <summary>Ask for summarized thinking text: from Claude 4.7 on it is omitted unless requested,
    /// which would leave the live reasoning pane empty.</summary>
    public bool SummarizedThinking { get; init; }
    /// <summary>Whether temperature is accepted (Claude 4.7+ and 5.x reject sampling parameters).</summary>
    public bool Sampling { get; init; } = true;
    public int? ContextWindow { get; init; }
    public int DefaultMaxTokens { get; init; } = 4096;
    /// <summary>The most output tokens the model allows, when known; larger requests are clamped.</summary>
    public int? MaxTokensCeiling { get; init; }
    /// <summary>Cache points after the system prompt and at the end of the conversation.</summary>
    public bool CachePoints { get; init; }
    /// <summary>A cache point after the tool list (Claude only — Nova has no tool caching).</summary>
    public bool ToolCachePoint { get; init; }
    /// <summary>toolResult.status is only understood by Claude and Nova.</summary>
    public bool ToolResultStatus { get; init; }
    /// <summary>PDFs can go in as document blocks.</summary>
    public bool Documents { get; init; }
}

public static class BedrockModels
{
    /// <summary>Cross-Region inference-profile prefixes.</summary>
    private static readonly HashSet<string> Geographies = ["us", "us-gov", "eu", "apac", "jp", "au", "ca", "global"];

    /// <summary>Model providers on Bedrock (the "vendor." in "vendor.model").</summary>
    private static readonly HashSet<string> Vendors =
    [
        "anthropic", "amazon", "meta", "mistral", "deepseek", "openai", "qwen", "cohere", "ai21", "writer",
        "moonshot", "moonshotai", "minimax", "google", "nvidia", "twelvelabs",
    ];

    /// <summary>"us.anthropic.claude-…" or an ARN → "anthropic.claude-…" (lower-cased); null when the
    /// id isn't a Bedrock "vendor.model" id.</summary>
    public static string? BaseId(string modelId)
    {
        var id = modelId.Trim().ToLowerInvariant();
        var slash = id.LastIndexOf('/');
        if (slash >= 0) id = id[(slash + 1)..];
        var dot = id.IndexOf('.');
        if (dot > 0 && Geographies.Contains(id[..dot]) && id.IndexOf('.', dot + 1) > 0) id = id[(dot + 1)..];
        dot = id.IndexOf('.');
        return dot > 0 && Vendors.Contains(id[..dot]) ? id : null;
    }

    /// <summary>The context window for a Bedrock model id, or null when it isn't one we know.</summary>
    public static int? ContextWindow(string modelId) => BaseId(modelId) is null ? null : Traits(modelId).ContextWindow;

    // "claude-opus-4-1-…", "claude-sonnet-4-20250514-…" (4.0: the date is not a minor version),
    // "claude-3-7-sonnet-…", "claude-3-haiku-…".
    private static readonly Regex ClaudeNew = new(@"claude-(?<family>opus|sonnet|haiku|fable|mythos)-(?<major>\d+)(?:-(?<minor>\d)(?!\d))?");
    private static readonly Regex ClaudeOld = new(@"claude-(?<major>\d)-(?:(?<minor>\d)-)?(?<family>opus|sonnet|haiku)");

    public static BedrockModelTraits Traits(string modelId)
    {
        var id = BaseId(modelId) ?? modelId.Trim().ToLowerInvariant();
        if (id.Contains("anthropic.claude", StringComparison.Ordinal) || id.StartsWith("claude", StringComparison.Ordinal))
            return Claude(id);
        if (id.Contains("amazon.nova", StringComparison.Ordinal))
        {
            var window = id.Contains("nova-micro", StringComparison.Ordinal) ? 128_000
                : id.Contains("nova-premier", StringComparison.Ordinal) ? 1_000_000
                : 300_000;
            return new BedrockModelTraits
            {
                ContextWindow = window, CachePoints = true, ToolResultStatus = true, Documents = true,
            };
        }
        return new BedrockModelTraits
        {
            ContextWindow = id switch
            {
                _ when id.StartsWith("meta.llama", StringComparison.Ordinal) => 128_000,
                _ when id.Contains("mistral-large-2402", StringComparison.Ordinal) => 32_000,
                _ when id.Contains("mistral-large", StringComparison.Ordinal) || id.Contains("pixtral-large", StringComparison.Ordinal) => 128_000,
                _ when id.StartsWith("mistral.", StringComparison.Ordinal) => 32_000,
                _ when id.StartsWith("deepseek.", StringComparison.Ordinal) => 128_000,
                _ when id.Contains("gpt-oss", StringComparison.Ordinal) => 128_000,
                _ when id.StartsWith("qwen.", StringComparison.Ordinal) => 128_000,
                _ => null,
            },
            // Llama on Bedrock caps a reply at 2048 tokens; reasoning models need room to think.
            DefaultMaxTokens = id switch
            {
                _ when id.StartsWith("meta.llama", StringComparison.Ordinal) => 2048,
                _ when id.StartsWith("deepseek.r1", StringComparison.Ordinal) => 16_384,
                _ when id.StartsWith("deepseek.", StringComparison.Ordinal) || id.Contains("gpt-oss", StringComparison.Ordinal)
                       || id.StartsWith("qwen.", StringComparison.Ordinal) => 8192,
                _ => 4096,
            },
        };
    }

    private static BedrockModelTraits Claude(string id)
    {
        string family;
        double version;
        var m = ClaudeNew.Match(id);
        if (!m.Success) m = ClaudeOld.Match(id);
        if (m.Success)
        {
            family = m.Groups["family"].Value;
            version = int.Parse(m.Groups["major"].Value, CultureInfo.InvariantCulture)
                      + (m.Groups["minor"].Success ? int.Parse(m.Groups["minor"].Value, CultureInfo.InvariantCulture) / 10.0 : 0);
        }
        else if (id.Contains("claude-v2", StringComparison.Ordinal) || id.Contains("claude-instant", StringComparison.Ordinal))
        {
            family = "legacy";
            version = 2;
        }
        else
        {
            // A Claude we can't place is newer than this table: assume the current generation.
            family = "unknown";
            version = 99;
        }

        // Prompt caching on Bedrock starts with Claude 3.5 Haiku and 3.7 Sonnet.
        var caches = version >= 3.7 || (family == "haiku" && version >= 3.5);
        var claude = new BedrockModelTraits
        {
            IsClaude = true, ToolResultStatus = true, Documents = true, ContextWindow = 200_000,
            CachePoints = caches, ToolCachePoint = caches,
        };
        if (version < 3.7)
            return claude with { DefaultMaxTokens = version >= 3.5 ? 8192 : 4096, MaxTokensCeiling = version >= 3.5 ? 8192 : 4096 };
        if (version < 4.6)
        {
            // 3.7 – 4.5 think with a budget. Opus 4 and 4.1 stop at 32K output, the rest at 64K.
            var ceiling = family == "opus" && version < 4.5 ? 32_000 : 64_000;
            return claude with { Thinking = ClaudeThinking.Budget, DefaultMaxTokens = 16_000, MaxTokensCeiling = ceiling };
        }
        var adaptive = claude with
        {
            Thinking = ClaudeThinking.Adaptive, ContextWindow = 1_000_000, DefaultMaxTokens = 16_000, MaxTokensCeiling = 128_000,
        };
        if (version < 4.7) return adaptive; // 4.6: sampling allowed, summaries by default, no thinking unless asked
        adaptive = adaptive with { Sampling = false, SummarizedThinking = true };
        if (version < 5) return adaptive;   // 4.7, 4.8
        if (family == "sonnet" && version >= 5.5 && version < 6) return adaptive with { Off = ThinkingOff.BetweenTools };
        if (family is "opus" or "sonnet" && version < 5.5) return adaptive with { Off = ThinkingOff.Disabled };
        return adaptive with { Off = ThinkingOff.LowEffort };
    }
}
