using System.Text.RegularExpressions;

namespace Vision.Core;

// MARK: - What each model takes on the wire
//
// Hosted APIs disagree about the optional parts of a chat request, and they refuse the whole request (HTTP 400) when one
// is wrong: Moonshot's Kimi K2.6/K2.7/K3 fix the temperature ("invalid temperature: only 1 is allowed for this model"),
// K2.7 Code can't stop thinking, Z.ai's GLM-5.3 can't either and takes only low/high/max, Z.ai's text models take
// plain-string content only and `tool_choice: "auto"` only, OpenAI's reasoning models want `max_completion_tokens` and
// no temperature. This table says, per provider and model, which sampling and thinking parameters to send, so the request
// is right the first time. What the table doesn't know, the server's own 400 teaches (see RequestAdaptations).
// Researched from the providers' docs on 2026-10-18/19:
//   - Moonshot: platform.kimi.ai/docs/guide/use-thinking-models, /docs/models (K3: reasoning_effort low/high/max, sampling
//     fixed; K2.7 Code: always thinks, no `thinking`, temperature fixed; K2.6: thinking enabled|disabled, temperature fixed;
//     kimi-k2.5, moonshot-v1, the kimi-k2 previews, kimi-k2-thinking and kimi-latest are discontinued)
//   - Kimi Code: kimi.com/code/docs (models k3, k3-256k, kimi-for-coding, kimi-for-coding-highspeed; error reference)
//   - Z.ai: docs.z.ai/api-reference/llm/chat-completion (GLM-5.3: thinking only enabled, reasoning_effort low/high/max;
//     GLM-5.2: none/minimal skip thinking, low/medium → high, xhigh → max; reasoning_effort is GLM-5.2 and later only;
//     temperature [0, 1]; max_tokens ≤ 131072; tool_choice "auto" only; text models take string content), /api-code
//   - DeepSeek (2026-10-19): api-docs.deepseek.com, /quick_start/pricing, /guides/thinking_mode (deepseek-v4-pro and
//     deepseek-flash, 1M context, at most 384K output; thinking on by default, switched with thinking.type enabled|disabled,
//     reasoning_effort low/high/max (medium → high, xhigh → high); temperature has no effect while thinking; with tools,
//     every earlier assistant turn's reasoning_content must come back or the API answers 400; Flash reads images, V4 Pro
//     doesn't; deepseek-v4-flash is the retired name of deepseek-flash)
// Vision for macOS carries the same table (same ids, same rules): ModelRequestRules.swift.

/// <summary>The services whose rules Vision knows, recognised by address.</summary>
public enum KnownProvider
{
    /// <summary>Moonshot's open platform (api.moonshot.ai / api.moonshot.cn): kimi-k3, kimi-k2.7-code, kimi-k2.6, …</summary>
    Moonshot,
    /// <summary>The Kimi Code membership API (api.kimi.com/coding, api.kimi.ai/coding): k3, kimi-for-coding, …</summary>
    KimiCode,
    /// <summary>Z.ai / BigModel (api.z.ai, open.bigmodel.cn): GLM models, general and Coding Plan endpoints.</summary>
    Zai,
    OpenAI,
    /// <summary>DeepSeek's API (api.deepseek.com): deepseek-v4-pro, deepseek-flash.</summary>
    DeepSeek,
    /// <summary>Anthropic's API (api.anthropic.com), reached through the Messages API client.</summary>
    Anthropic,
    Other,
}

public static class KnownProviders
{
    /// <summary>How the provider is named in a notice ("Moonshot", "Z.ai").</summary>
    public static string Label(this KnownProvider provider) => provider switch
    {
        KnownProvider.Moonshot => "Moonshot",
        KnownProvider.KimiCode => "Kimi Code",
        KnownProvider.Zai => "Z.ai",
        KnownProvider.OpenAI => "OpenAI",
        KnownProvider.DeepSeek => "DeepSeek",
        KnownProvider.Anthropic => "Anthropic",
        _ => "The model server",
    };

    public static KnownProvider Of(ProviderProfile profile) => Of(profile.BaseUrl);

    public static KnownProvider Of(string? baseUrl)
    {
        if (!Uri.TryCreate(baseUrl?.Trim(), UriKind.Absolute, out var url) || string.IsNullOrEmpty(url.Host)) return KnownProvider.Other;
        var host = url.Host.ToLowerInvariant();
        bool Under(string domain) => host == domain || host.EndsWith("." + domain, StringComparison.Ordinal);
        if (Under("moonshot.ai") || Under("moonshot.cn")) return KnownProvider.Moonshot;
        if (Under("kimi.com") || Under("kimi.ai"))
            return url.AbsolutePath.Contains("/coding", StringComparison.OrdinalIgnoreCase) ? KnownProvider.KimiCode : KnownProvider.Moonshot;
        if (Under("z.ai") || Under("bigmodel.cn")) return KnownProvider.Zai;
        if (Under("openai.com")) return KnownProvider.OpenAI;
        if (Under("deepseek.com")) return KnownProvider.DeepSeek;
        if (Under("anthropic.com")) return KnownProvider.Anthropic;
        return KnownProvider.Other;
    }
}

/// <summary>How a model's thinking is switched and sized on the wire.</summary>
public enum ThinkingControlKind
{
    /// <summary>A chat template on a server you run (vLLM, SGLang, llama.cpp, Ollama, LM Studio):
    /// <c>chat_template_kwargs.enable_thinking</c> + <c>reasoning_effort</c>. Every level is offered.</summary>
    Template,
    /// <summary>A hosted API's top-level <c>reasoning_effort</c>, sent only while thinking is on. Every level is offered.</summary>
    EffortField,
    /// <summary><c>reasoning_effort</c> from a fixed vocabulary (<see cref="ThinkingControl.Words"/>): each offered level and
    /// the word it sends. A model with no Off entry always thinks.</summary>
    Effort,
    /// <summary><c>thinking: {"type": "enabled" | "disabled"}</c> — on or off, no depth (Kimi K2.6, GLM-4.6 … GLM-5.1).</summary>
    Toggle,
    /// <summary>Always thinks and takes no setting (Kimi K2.7 Code): nothing is sent.</summary>
    AlwaysOn,
    /// <summary>No thinking control at all (moonshot-v1, the retired Kimi K2 previews): nothing is sent.</summary>
    None,
}

/// <summary>A thinking control: its kind, and for <see cref="ThinkingControlKind.Effort"/> the words per offered level
/// (<see cref="InTemplate"/> also puts the word in <c>chat_template_kwargs</c> — a GLM-5 on vLLM; <see cref="WithToggle"/> also
/// sends <c>thinking: {"type": "enabled" | "disabled"}</c>, Off being "disabled" with no word — DeepSeek).</summary>
public sealed record ThinkingControl(ThinkingControlKind Kind, IReadOnlyDictionary<ThinkingLevel, string>? Words = null, bool InTemplate = false,
                                     bool WithToggle = false)
{
    public static ThinkingControl Template { get; } = new(ThinkingControlKind.Template);
    public static ThinkingControl EffortField { get; } = new(ThinkingControlKind.EffortField);
    public static ThinkingControl Toggle { get; } = new(ThinkingControlKind.Toggle);
    public static ThinkingControl AlwaysOn { get; } = new(ThinkingControlKind.AlwaysOn);
    public static ThinkingControl None { get; } = new(ThinkingControlKind.None);

    public static ThinkingControl Effort(IReadOnlyDictionary<ThinkingLevel, string> words, bool inTemplate = false, bool withToggle = false) =>
        new(ThinkingControlKind.Effort, words, inTemplate, withToggle);
}

/// <summary>What one model accepts. <see cref="For"/> looks it up.</summary>
public sealed partial record ModelRequestRules
{
    public required KnownProvider Provider { get; init; }
    public required ThinkingControl Thinking { get; init; }
    /// <summary>False: never send <c>temperature</c> (the model fixes it and refuses any other value).</summary>
    public bool SendsTemperature { get; init; } = true;
    /// <summary>Clamp a configured temperature into this range.</summary>
    public (double Min, double Max)? TemperatureRange { get; init; }
    /// <summary>False: never send <c>top_p</c>.</summary>
    public bool SendsTopP { get; init; } = true;
    /// <summary>The most output tokens the model takes; a larger <c>max_tokens</c> is lowered to it.</summary>
    public int? MaxOutputTokens { get; init; }
    /// <summary>"max_tokens", or "max_completion_tokens" (OpenAI's reasoning models).</summary>
    public string MaxTokensField { get; init; } = "max_tokens";
    /// <summary>Whether <c>tool_choice: "required"</c> (or a named tool) is accepted. Vision doesn't send it today; Guardian's
    /// proxy and future callers read this.</summary>
    public bool ForcedToolChoice { get; init; } = true;
    /// <summary>The API takes <c>content</c> as a plain string only (Z.ai's text models): attachments are folded into the
    /// text.</summary>
    public bool TextOnlyContent { get; init; }
    /// <summary>Whether the model reads images. Null = assume so (a server that refuses them switches them off at runtime).</summary>
    public bool? AcceptsImages { get; init; }
    /// <summary>The model's reasoning must go back on the assistant messages that made tool calls (<c>reasoning_content</c>) —
    /// Kimi answers 400 "thinking is enabled but reasoning_content is missing" otherwise.</summary>
    public bool EchoReasoning { get; init; }
    /// <summary>With <see cref="EchoReasoning"/>: every assistant message carries its reasoning, not only the ones that called
    /// tools — DeepSeek answers 400 "The `reasoning_content` in the thinking mode must be passed back to the API" otherwise.</summary>
    public bool EchoReasoningEveryTurn { get; init; }

    /// <summary>Thinking is switched off for <paramref name="level"/> by <c>thinking.type: "disabled"</c> (a toggle, or an effort
    /// vocabulary with a toggle beside it).</summary>
    public bool TurnsThinkingOff(ThinkingLevel? level) =>
        level is { } chosen && (Thinking.Kind == ThinkingControlKind.Toggle || Thinking.WithToggle) && NearestLevel(chosen) == ThinkingLevel.Off;

    // MARK: Thinking levels

    /// <summary>The levels a thinking picker offers for this model, weakest first (empty = no choice to make).</summary>
    public IReadOnlyList<ThinkingLevel> ThinkingLevels => Thinking.Kind switch
    {
        ThinkingControlKind.Template or ThinkingControlKind.EffortField => Vision.Core.ThinkingLevels.All,
        ThinkingControlKind.Effort => Vision.Core.ThinkingLevels.All.Where(l => Thinking.Words?.ContainsKey(l) == true).ToList(),
        ThinkingControlKind.Toggle => [ThinkingLevel.Off, ThinkingLevel.High],
        _ => [],
    };

    /// <summary>The model always thinks (there is no Off).</summary>
    public bool AlwaysThinks => Thinking.Kind switch
    {
        ThinkingControlKind.AlwaysOn => true,
        ThinkingControlKind.Effort => Thinking.Words is { Count: > 0 } words && !words.ContainsKey(ThinkingLevel.Off),
        _ => false,
    };

    /// <summary>How a level is called in the picker for this model: a toggle's "on" is "On", not "High".</summary>
    public string Label(ThinkingLevel level) =>
        Thinking.Kind == ThinkingControlKind.Toggle ? level == ThinkingLevel.Off ? "Off" : "On" : level.Label();

    /// <summary>The offered level nearest to <paramref name="level"/> (an exact match, else the closest, the stronger on a
    /// tie — and never Off for a level that asks for thinking); null when the model offers none.</summary>
    public ThinkingLevel? NearestLevel(ThinkingLevel level)
    {
        var offered = ThinkingLevels.ToList();
        if (offered.Contains(level)) return level;
        if (Thinking.Kind == ThinkingControlKind.Toggle) return level == ThinkingLevel.Off ? ThinkingLevel.Off : ThinkingLevel.High;
        // Asking for some thinking never turns it off (GLM-5.2: Low → high, as Z.ai maps it itself).
        if (level != ThinkingLevel.Off && offered.Any(l => l != ThinkingLevel.Off)) offered.Remove(ThinkingLevel.Off);
        if (offered.Count == 0) return null;
        static int Rank(ThinkingLevel l) => (int)l;
        return offered
            .OrderBy(l => Math.Abs(Rank(l) - Rank(level)))
            .ThenByDescending(Rank)
            .First();
    }

    /// <summary>The <c>reasoning_effort</c> word for <paramref name="level"/> under an Effort vocabulary (null otherwise).</summary>
    public string? EffortWord(ThinkingLevel level)
    {
        if (Thinking.Kind != ThinkingControlKind.Effort || Thinking.Words is not { } words || NearestLevel(level) is not { } nearest) return null;
        // An empty word: the level is said by the toggle alone (DeepSeek's Off), with no reasoning_effort.
        return words.GetValueOrDefault(nearest) is { Length: > 0 } word ? word : null;
    }

    /// <summary>For /think and the picker: what this model makes of a level it doesn't offer ("" when it offers it).</summary>
    public string Caveat(ThinkingLevel level)
    {
        if (ThinkingLevels.Count == 0)
            return AlwaysThinks ? " This model always thinks and takes no setting, so it changes nothing here." : " This model has no thinking setting.";
        if (NearestLevel(level) is not { } nearest || nearest == level) return "";
        return $" This model offers {string.Join(", ", ThinkingLevels.Select(Label))}, so it gets {Label(nearest)}.";
    }

    // MARK: The table

    /// <summary>The rules for <paramref name="model"/> (default: the profile's own) on <paramref name="profile"/>'s server.</summary>
    public static ModelRequestRules For(ProviderProfile profile, string? model = null)
    {
        var id = Normalized(string.IsNullOrEmpty(model) ? profile.Model : model);
        // An Anthropic route talks the Messages API wherever it points (a proxy too): its own client shapes thinking.
        if (profile.Kind == ProviderKind.Anthropic) return AnthropicRules(id);
        return KnownProviders.Of(profile) switch
        {
            KnownProvider.Moonshot => MoonshotRules(id),
            KnownProvider.KimiCode => KimiCodeRules(id),
            KnownProvider.Zai => ZaiRules(id),
            KnownProvider.OpenAI => OpenAiRules(id),
            KnownProvider.DeepSeek => DeepSeekRules(id),
            _ => OtherRules(profile, id),
        };
    }

    /// <summary>"moonshotai/Kimi-K2.6" → "kimi-k2.6"; "glm-5.3:latest" → "glm-5.3".</summary>
    public static string Normalized(string? model)
    {
        var last = (model ?? "").Split('/')[^1].ToLowerInvariant();
        return last.Split(':')[0];
    }

    /// <summary>low / high / max, no Off: GLM-5.3, Kimi K3 (and GLM-5 elsewhere).</summary>
    public static IReadOnlyDictionary<ThinkingLevel, string> LowHighMax { get; } = new Dictionary<ThinkingLevel, string>
    {
        [ThinkingLevel.Low] = "low", [ThinkingLevel.High] = "high", [ThinkingLevel.Max] = "max",
    };

    /// <summary>Moonshot's open platform. K3: effort low/high/max (default max), temperature/top_p fixed. K2.7 Code: always
    /// thinks, takes no thinking setting and no reasoning_effort, no tool_choice "required". K2.6 (and K2.5): thinking on or
    /// off, temperature fixed (1.0 thinking, 0.6 not). The retired K2 previews and moonshot-v1 take a temperature in 0…1
    /// and no thinking setting.</summary>
    private static ModelRequestRules MoonshotRules(string id)
    {
        if (id.StartsWith("kimi-k3", StringComparison.Ordinal))
            return new() { Provider = KnownProvider.Moonshot, Thinking = ThinkingControl.Effort(LowHighMax), SendsTemperature = false, SendsTopP = false, EchoReasoning = true };
        if (id.StartsWith("kimi-k2.7", StringComparison.Ordinal) || id.StartsWith("kimi-k2-thinking", StringComparison.Ordinal)
            || id.StartsWith("kimi-thinking", StringComparison.Ordinal))
            return new()
            {
                Provider = KnownProvider.Moonshot, Thinking = ThinkingControl.AlwaysOn, SendsTemperature = false, SendsTopP = false,
                ForcedToolChoice = false, EchoReasoning = true,
            };
        if (id.StartsWith("kimi-k2.6", StringComparison.Ordinal) || id.StartsWith("kimi-k2.5", StringComparison.Ordinal))
            return new()
            {
                Provider = KnownProvider.Moonshot, Thinking = ThinkingControl.Toggle, SendsTemperature = false, SendsTopP = false,
                ForcedToolChoice = false, EchoReasoning = true,
            };
        return new() { Provider = KnownProvider.Moonshot, Thinking = ThinkingControl.None, TemperatureRange = (0, 1) };
    }

    /// <summary>The Kimi Code membership API. k3, k3-256k and kimi-for-coding take reasoning_effort low/high/max, and "none"
    /// turns thinking off; the HighSpeed model always thinks. The temperature isn't documented: like the platform's Kimi
    /// models, it isn't sent.</summary>
    private static ModelRequestRules KimiCodeRules(string id)
    {
        if (id.Contains("highspeed", StringComparison.Ordinal))
            return new()
            {
                Provider = KnownProvider.KimiCode, Thinking = ThinkingControl.AlwaysOn, SendsTemperature = false, SendsTopP = false,
                ForcedToolChoice = false, EchoReasoning = true,
            };
        var words = new Dictionary<ThinkingLevel, string>(LowHighMax) { [ThinkingLevel.Off] = "none" };
        return new()
        {
            Provider = KnownProvider.KimiCode, Thinking = ThinkingControl.Effort(words), SendsTemperature = false, SendsTopP = false,
            ForcedToolChoice = false, EchoReasoning = true,
        };
    }

    /// <summary>Z.ai / BigModel. GLM-5.3 always thinks, effort low/high/max. GLM-5.2: "none" skips thinking, effort
    /// high/max. GLM-4.6 … GLM-5.1: thinking on or off. Every GLM: temperature 0…1, at most 131072 output tokens, tool_choice
    /// "auto" only; the text models take plain-string content only and no images (the -flash and "v" models are the vision
    /// ones).</summary>
    private static ModelRequestRules ZaiRules(string id)
    {
        ThinkingControl thinking;
        if (id.StartsWith("glm-5.3", StringComparison.Ordinal))
            thinking = ThinkingControl.Effort(LowHighMax);
        else if (id.StartsWith("glm-5.2", StringComparison.Ordinal))
            thinking = ThinkingControl.Effort(new Dictionary<ThinkingLevel, string>
            {
                [ThinkingLevel.Off] = "none", [ThinkingLevel.High] = "high", [ThinkingLevel.Max] = "max",
            });
        else
            thinking = ThinkingControl.Toggle;
        var vision = id.Contains("flash", StringComparison.Ordinal) || GlmVisionModel().IsMatch(id);
        return new()
        {
            Provider = KnownProvider.Zai, Thinking = thinking, TemperatureRange = (0, 1), MaxOutputTokens = 131_072,
            ForcedToolChoice = false, TextOnlyContent = !vision, AcceptsImages = vision,
        };
    }

    [GeneratedRegex(@"^glm-[0-9.]+v", RegexOptions.CultureInvariant)]
    private static partial Regex GlmVisionModel();

    /// <summary>OpenAI: the reasoning models (o1, o3, o4, gpt-5…) take no temperature and want max_completion_tokens.</summary>
    private static ModelRequestRules OpenAiRules(string id)
    {
        var reasoning = new[] { "o1", "o3", "o4" }.Any(p => id == p || id.StartsWith(p + "-", StringComparison.Ordinal))
                        || id.StartsWith("gpt-5", StringComparison.Ordinal);
        return reasoning
            ? new() { Provider = KnownProvider.OpenAI, Thinking = ThinkingControl.EffortField, SendsTemperature = false, SendsTopP = false, MaxTokensField = "max_completion_tokens" }
            : new() { Provider = KnownProvider.OpenAI, Thinking = ThinkingControl.EffortField };
    }

    /// <summary>DeepSeek's API: thinking on (the default) or off with <c>thinking.type</c>, and how hard with
    /// <c>reasoning_effort</c> low/high/max (DeepSeek maps medium to high itself; Vision offers what it really does). With
    /// tools — every Vision turn — each earlier assistant message must carry its <c>reasoning_content</c>. At most 384K output
    /// tokens; Flash reads images, V4 Pro doesn't.</summary>
    private static ModelRequestRules DeepSeekRules(string id)
    {
        var words = new Dictionary<ThinkingLevel, string>(LowHighMax) { [ThinkingLevel.Off] = "" };
        return new()
        {
            Provider = KnownProvider.DeepSeek, Thinking = ThinkingControl.Effort(words, withToggle: true), MaxOutputTokens = 384_000,
            AcceptsImages = id.Contains("flash", StringComparison.Ordinal) || id.Contains("vision", StringComparison.Ordinal),
            EchoReasoning = true, EchoReasoningEveryTurn = true,
        };
    }

    /// <summary>Anthropic's Messages API (<see cref="AnthropicClient"/>): every level is offered and the client says each the
    /// way the model takes it (adaptive effort, a thinking budget on older models, the lowest setting where thinking can't
    /// be switched off). Claude 4.7 and later take no temperature; the 5.5 models refuse a forced tool choice.</summary>
    private static ModelRequestRules AnthropicRules(string id)
    {
        var traits = BedrockModels.Traits(id);
        return new()
        {
            Provider = KnownProvider.Anthropic,
            Thinking = traits.Thinking == ClaudeThinking.None ? ThinkingControl.None : ThinkingControl.EffortField,
            SendsTemperature = traits.Sampling, SendsTopP = traits.Sampling, MaxOutputTokens = traits.MaxTokensCeiling,
            ForcedToolChoice = traits.Off is not (ThinkingOff.LowEffort or ThinkingOff.BetweenTools), AcceptsImages = true,
        };
    }

    /// <summary>Anything else: a server you run gets the chat-template controls, a hosted one the top-level effort. A GLM-5
    /// model anywhere always thinks and has its own words for how hard (the Qwen Coding Plan serves glm-5; a vLLM box may
    /// serve GLM-5.3).</summary>
    private static ModelRequestRules OtherRules(ProviderProfile profile, string id)
    {
        var selfHosted = profile.IsSelfHosted;
        if (id.StartsWith("glm-5", StringComparison.Ordinal))
        {
            var template = selfHosted && profile.Kind is not (ProviderKind.Ollama or ProviderKind.LmStudio);
            return new() { Provider = KnownProvider.Other, Thinking = ThinkingControl.Effort(LowHighMax, template) };
        }
        return new() { Provider = KnownProvider.Other, Thinking = selfHosted ? ThinkingControl.Template : ThinkingControl.EffortField };
    }
}
