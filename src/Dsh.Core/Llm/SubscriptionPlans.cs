using System.Globalization;
using System.Text.RegularExpressions;

namespace Dsh.Core;

// MARK: - Subscription and API plans
//
// The hosted plans the "Connect a subscription plan" guide knows how to set up: GLM (Z.ai), Kimi (Kimi Code and
// Moonshot), OpenAI, and Qwen (Alibaba Cloud). Each says where its API is (per region), where to get a key and
// how, what the key looks like, which models to offer, and what its terms say. Researched October 2026.

/// <summary>Where a plan is served: its API address and the page that hands out keys.</summary>
public sealed record PlanRegion(string Id, string Label, string BaseUrl, string KeyPage, string? KeyPageLabel = null);

/// <summary>A model a plan offers, with its context window when known.</summary>
public sealed record PlanModel(string Id, string? Note = null, int? Context = null);

/// <summary>One plan. <see cref="Models"/> starts with the recommended model.</summary>
public sealed record SubscriptionPlan
{
    public required string Id { get; init; }
    /// <summary>"Z.ai", "Kimi", "OpenAI", "Alibaba Cloud".</summary>
    public required string Provider { get; init; }
    /// <summary>What the guide calls it: "Z.ai — GLM Coding Plan".</summary>
    public required string Name { get; init; }
    /// <summary>One line under the name on the plan's card.</summary>
    public required string Summary { get; init; }
    public required IReadOnlyList<PlanRegion> Regions { get; init; }
    /// <summary>Numbered steps to get a key ("it's shown only once" included where it applies).</summary>
    public required IReadOnlyList<string> KeySteps { get; init; }
    /// <summary>What a key looks like.</summary>
    public required string KeyHint { get; init; }
    /// <summary>Preset models (the first is recommended); used when <c>/models</c> isn't available.</summary>
    public required IReadOnlyList<PlanModel> Models { get; init; }
    /// <summary>The plan's <c>/models</c> is expected to work (otherwise the guide checks the key with a one-token reply).</summary>
    public bool ListsModels { get; init; }
    /// <summary>Where to subscribe, for plans that need a subscription.</summary>
    public string? SubscribeUrl { get; init; }
    /// <summary>What the plan's terms say, in a sentence (shown with the key steps).</summary>
    public string? TermsNote { get; init; }
    /// <summary>Anything else worth knowing before using it.</summary>
    public string? Note { get; init; }
    /// <summary>Keys start with this (the Qwen Coding Plan's sk-sp-).</summary>
    public string? KeyPrefix { get; init; }
    /// <summary>The plan's terms allow only interactive use: the route is made interactive-only.</summary>
    public bool InteractiveOnly { get; init; }
    public ProviderKind Kind { get; init; } = ProviderKind.OpenAICompat;

    public PlanRegion DefaultRegion => Regions[0];
    public PlanModel Recommended => Models[0];
    public bool HasRegions => Regions.Count > 1;

    public PlanRegion Region(string? id) => Regions.FirstOrDefault(r => r.Id == id) ?? DefaultRegion;

    /// <summary>The route the guide saves for this plan in <paramref name="region"/> with <paramref name="model"/>.
    /// Hosted, so not private (the user may change that); a known context window is set so long chats are budgeted
    /// against the real window.</summary>
    public ProviderProfile Route(string? region, string model)
    {
        var where = Region(region);
        var name = HasRegions && where.Id != DefaultRegion.Id ? $"{Name} ({where.Label})" : Name;
        return new ProviderProfile(Kind, name, where.BaseUrl, model)
        {
            ContextWindow = Models.FirstOrDefault(m => m.Id == model)?.Context,
            InteractiveOnly = InteractiveOnly ? true : null,
        };
    }

    /// <summary>A mistake in a pasted key that can be named: the wrong kind of key for this plan. Null when it looks fine.</summary>
    public string? KeyMistake(string key)
    {
        var trimmed = key.Trim();
        if (trimmed.Length == 0) return null;
        if (trimmed.Any(char.IsWhiteSpace)) return "The key has a space or a line break in it — copy it again, just the key.";
        if (KeyPrefix is { } prefix && !trimmed.StartsWith(prefix, StringComparison.Ordinal))
        {
            if (Id == SubscriptionPlans.QwenCoding && trimmed.StartsWith("sk-", StringComparison.Ordinal))
                return "That's a pay-as-you-go Model Studio key (it starts with sk-). The Coding Plan has its own key, starting with sk-sp-, on the Coding Plan page.";
            return $"Keys for this plan start with {prefix}. Check you copied the right one.";
        }
        if (Id == SubscriptionPlans.QwenApi && trimmed.StartsWith("sk-sp-", StringComparison.Ordinal))
            return "That's a Coding Plan key (it starts with sk-sp-). Choose \"Qwen — Alibaba Cloud Coding Plan\" for it, or use a pay-as-you-go key here.";
        return null;
    }
}

public static partial class SubscriptionPlans
{
    public const string ZaiCoding = "zai-coding";
    public const string ZaiApi = "zai-api";
    public const string KimiCode = "kimi-code";
    public const string MoonshotApi = "moonshot-api";
    public const string OpenAi = "openai";
    public const string QwenCoding = "qwen-coding";
    public const string QwenApi = "qwen-api";

    public const string International = "intl";
    public const string China = "china";

    private const string ZaiKeys = "https://z.ai/manage-apikey/apikey-list";
    private const string BigModelKeys = "https://open.bigmodel.cn/usercenter/apikeys";

    public static IReadOnlyList<SubscriptionPlan> All { get; } =
    [
        new()
        {
            Id = ZaiCoding, Provider = "Z.ai", Name = "Z.ai — GLM Coding Plan",
            Summary = "A monthly GLM plan for coding tools, with GLM-5.3.",
            Regions =
            [
                new(International, "International", "https://api.z.ai/api/coding/paas/v4", ZaiKeys),
                new(China, "China (BigModel)", "https://open.bigmodel.cn/api/coding/paas/v4", BigModelKeys),
            ],
            SubscribeUrl = "https://z.ai/subscribe",
            KeySteps =
            [
                "Sign in at z.ai (open.bigmodel.cn in China).",
                "Subscribe to the GLM Coding Plan if you haven't yet (z.ai/subscribe).",
                "Open the API keys page — the button below does it.",
                "Create a new API key.",
                "Copy it. It's shown only once, so paste it into DSH straight away.",
            ],
            KeyHint = "A long string of letters and digits with a dot in it.",
            Models = [new("glm-5.3", "GLM-5.3 · always thinks", 1_000_000)],
            TermsNote = "Z.ai offers the Coding Plan for use in coding tools.",
            Note = "Coding Plan keys work only on the coding address DSH uses. A key refused there may be for the other region, or a pay-as-you-go key.",
        },
        new()
        {
            Id = ZaiApi, Provider = "Z.ai", Name = "Z.ai — pay-as-you-go API",
            Summary = "GLM-5.3 billed per token, without a subscription.",
            Regions =
            [
                new(International, "International", "https://api.z.ai/api/paas/v4", ZaiKeys),
                new(China, "China (BigModel)", "https://open.bigmodel.cn/api/paas/v4", BigModelKeys),
            ],
            KeySteps =
            [
                "Sign in at z.ai (open.bigmodel.cn in China) and add credit to your account.",
                "Open the API keys page — the button below does it.",
                "Create a new API key (an API key, not a Coding Plan key).",
                "Copy it. It's shown only once, so paste it into DSH straight away.",
            ],
            KeyHint = "A long string of letters and digits with a dot in it.",
            Models = [new("glm-5.3", "GLM-5.3 · always thinks", 1_000_000)],
            Note = "Billed per token.",
        },
        new()
        {
            Id = KimiCode, Provider = "Kimi", Name = "Kimi Code (subscription)",
            Summary = "Kimi's coding subscription: K2.8 with up to 1M context, and K3.",
            Regions =
            [
                new(International, "International", "https://api.kimi.ai/coding/v1", "https://www.kimi.com/code/console"),
                new(China, "China", "https://api.kimi.com/coding/v1", "https://www.kimi.com/code/console"),
            ],
            SubscribeUrl = "https://www.kimi.com/code/console",
            KeySteps =
            [
                "Sign in at kimi.com and subscribe to Kimi Code if you haven't yet.",
                "Open the Kimi Code console — the button below does it.",
                "Create an API key (you can have up to 5).",
                "Copy it. Each key is shown only once, so paste it into DSH straight away.",
            ],
            KeyHint = "Starts with sk-.",
            Models =
            [
                new("kimi-for-coding", "K2.8 · up to 1M context", 1_000_000),
                new("k3"),
                new("k3-256k", "K3 · 256K context", 262_144),
                new("kimi-for-coding-highspeed", "faster replies"),
            ],
            TermsNote = "Kimi Code checks which app is calling. DSH identifies itself honestly as DSH and never pretends to be another tool — Kimi's terms call that a violation.",
            Note = "If Kimi says the plan is only for certain coding agents, use the Moonshot pay-as-you-go API instead.",
        },
        new()
        {
            Id = MoonshotApi, Provider = "Kimi", Name = "Moonshot (Kimi) — pay-as-you-go API",
            Summary = "Kimi models billed per token from prepaid credit.",
            Regions =
            [
                new(International, "International", "https://api.moonshot.ai/v1", "https://platform.moonshot.ai/console/api-keys"),
                new(China, "China", "https://api.moonshot.cn/v1", "https://platform.moonshot.cn/console/api-keys"),
            ],
            KeySteps =
            [
                "Sign in at platform.moonshot.ai (platform.moonshot.cn in China) and add credit.",
                "Open the API keys page — the button below does it.",
                "Create a new API key.",
                "Copy it. It's shown only once, so paste it into DSH straight away.",
            ],
            KeyHint = "Starts with sk-.",
            Models = [new("kimi-k2.7-code")],
            ListsModels = true,
            Note = "Prepaid credit. Keys only work in the region they were made in.",
        },
        new()
        {
            Id = OpenAi, Provider = "OpenAI", Name = "OpenAI API",
            Summary = "OpenAI's GPT models, billed per token.",
            Regions = [new(International, "International", "https://api.openai.com/v1", "https://platform.openai.com/api-keys")],
            KeySteps =
            [
                "Sign in at platform.openai.com and add a payment method or credit.",
                "Open the API keys page — the button below does it.",
                "Create a new secret key.",
                "Copy it. It's shown only once, so paste it into DSH straight away.",
            ],
            KeyHint = "Starts with sk-.",
            Models = [new("gpt-5")],
            ListsModels = true,
            Kind = ProviderKind.OpenAI,
            Note = "A ChatGPT Plus or Pro subscription doesn't include API access — the API is billed separately at platform.openai.com.",
        },
        new()
        {
            Id = QwenCoding, Provider = "Alibaba Cloud", Name = "Qwen — Alibaba Cloud Coding Plan",
            Summary = "Alibaba's coding subscription: Qwen3.7 Plus and other coding models, for use in coding tools.",
            Regions =
            [
                new(International, "International", "https://coding-intl.dashscope.aliyuncs.com/v1",
                    "https://modelstudio.console.alibabacloud.com/", "Model Studio console → Coding Plan"),
                new(China, "China", "https://coding.dashscope.aliyuncs.com/v1",
                    "https://bailian.console.aliyun.com/cn-beijing/subscription/coding-plan"),
            ],
            SubscribeUrl = "https://modelstudio.console.alibabacloud.com/",
            KeySteps =
            [
                "Sign in to Alibaba Cloud Model Studio (Bailian in China).",
                "Open the Coding Plan page and subscribe if you haven't yet.",
                "Create the Coding Plan's API key — it starts with sk-sp-.",
                "Copy it and paste it into DSH.",
            ],
            KeyHint = "Starts with sk-sp-. A key that starts with just sk- is a pay-as-you-go key, which doesn't work here.",
            KeyPrefix = "sk-sp-",
            Models =
            [
                new("qwen3.7-plus"), new("qwen3.6-plus"), new("qwen3-coder-plus"), new("qwen3-coder-next"),
                new("qwen3.5-plus"), new("kimi-k2.5"), new("glm-5"), new("MiniMax-M2.5"),
            ],
            InteractiveOnly = true,
            TermsNote = "Alibaba allows the Coding Plan only for interactive use in coding tools — not for automated scripts or unattended work. DSH won't run task lists on it.",
        },
        new()
        {
            Id = QwenApi, Provider = "Alibaba Cloud", Name = "Qwen — Model Studio pay-as-you-go",
            Summary = "Qwen models billed per token.",
            Regions =
            [
                new(International, "International", "https://dashscope-intl.aliyuncs.com/compatible-mode/v1",
                    "https://modelstudio.console.alibabacloud.com/", "Model Studio console → API Keys"),
                new(China, "China", "https://dashscope.aliyuncs.com/compatible-mode/v1", "https://bailian.console.aliyun.com/",
                    "Bailian console → API-KEY"),
            ],
            KeySteps =
            [
                "Sign in to Alibaba Cloud Model Studio (Bailian in China) and activate it.",
                "Open the API keys page (API-KEY in China) — the button below goes to the console.",
                "Create a new API key.",
                "Copy it and paste it into DSH.",
            ],
            KeyHint = "Starts with sk-.",
            Models = [new("qwen3.7-plus")],
            ListsModels = true,
            Note = "Billed per token.",
        },
    ];

    public static SubscriptionPlan? Find(string? id) => All.FirstOrDefault(p => p.Id == id);

    /// <summary>The plan a base URL belongs to, if any (so the route editor can say "Kimi Code").</summary>
    public static (SubscriptionPlan Plan, PlanRegion Region)? ForBaseUrl(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)) return null;
        foreach (var plan in All)
            foreach (var region in plan.Regions)
                if (SwapperConnection.SameBase(region.BaseUrl, baseUrl)) return (plan, region);
        return null;
    }

    /// <summary>The model to recommend from OpenAI's model list: the newest GPT chat model — the plain one before its
    /// mini/nano/pro variants, an alias before a dated snapshot. Null when the list has none.</summary>
    public static string? RecommendOpenAiModel(IEnumerable<string> ids)
    {
        string[] notChat = ["audio", "realtime", "image", "transcribe", "tts", "search", "embedding", "moderation", "instruct", "codex", "oss"];
        var best = ids
            .Select(id => (Id: id, Match: GptVersion().Match(id)))
            .Where(c => c.Match.Success && !notChat.Any(word => c.Id.Contains(word, StringComparison.OrdinalIgnoreCase)))
            .Select(c =>
            {
                var major = int.Parse(c.Match.Groups["major"].Value, CultureInfo.InvariantCulture);
                var minor = c.Match.Groups["minor"].Success ? int.Parse(c.Match.Groups["minor"].Value, CultureInfo.InvariantCulture) : 0;
                var rest = c.Id[c.Match.Length..];
                var dated = DateSuffix().IsMatch(rest);
                var variant = DateSuffix().Replace(rest, "");
                // The plain model first, then -o-style suffixes, then the smaller and the bigger siblings.
                var rank = variant.Length == 0 ? 0 : variant is "o" or "-chat-latest" ? 1 : variant.Contains("pro") ? 2 : variant.Contains("mini") ? 3 : 4;
                return (c.Id, Major: major, Minor: minor, Rank: rank, Dated: dated);
            })
            .OrderByDescending(c => c.Major).ThenByDescending(c => c.Minor).ThenBy(c => c.Rank).ThenBy(c => c.Dated)
            .ThenBy(c => c.Id.Length).ThenBy(c => c.Id, StringComparer.Ordinal)
            .FirstOrDefault();
        return best.Id;
    }

    [GeneratedRegex(@"^gpt-(?<major>\d+)(?:\.(?<minor>\d+))?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GptVersion();

    [GeneratedRegex(@"-\d{4}-\d{2}-\d{2}$|-\d{4}$", RegexOptions.CultureInvariant)]
    private static partial Regex DateSuffix();

    /// <summary>GLM-5 models always think: thinking can't be switched off (the request fails), and their effort words
    /// are low, high and max.</summary>
    public static bool AlwaysThinks(string? model) =>
        model is not null && model.Split('/')[^1].StartsWith("glm-5", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a route follows GLM's thinking rules (<see cref="AlwaysThinks"/>, <see cref="GlmEffort"/>): any GLM-5
    /// model wherever it is served, and anything on Z.ai's own servers (api.z.ai, open.bigmodel.cn).</summary>
    public static bool UsesGlmRules(string? baseUrl, string? model)
    {
        if (AlwaysThinks(model)) return true;
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)) return false;
        var host = uri.Host.ToLowerInvariant();
        return host is "z.ai" or "bigmodel.cn" || host.EndsWith(".z.ai", StringComparison.Ordinal) || host.EndsWith(".bigmodel.cn", StringComparison.Ordinal);
    }

    /// <summary>The effort word a GLM-5 model takes for a thinking level: Off and Low → low, Medium and High → high,
    /// Max → max.</summary>
    public static string GlmEffort(ThinkingLevel level) => level switch
    {
        ThinkingLevel.Off or ThinkingLevel.Low => "low",
        ThinkingLevel.Medium or ThinkingLevel.High => "high",
        _ => "max",
    };

    /// <summary>A plan's refusal in plain words, for the guide's key check.</summary>
    public static string ExplainError(SubscriptionPlan plan, PlanRegion region, int status, string body)
    {
        var lower = body.ToLowerInvariant();
        switch (status)
        {
            case 401:
                return $"The key wasn't accepted — check it's a {plan.Name} key for the {region.Label} region.";
            case 403 when plan.Id == KimiCode:
                return lower.Contains("coding agent") || lower.Contains("only") || lower.Contains("not allowed") || lower.Contains("client")
                    ? "Kimi says this plan only works with certain coding agents, and DSH identifies itself honestly rather than pretending to be one of them. Use the Moonshot (Kimi) pay-as-you-go API instead: it works with any app."
                    : "Kimi refused the request (403). Your plan may not include API use from this app — the Moonshot (Kimi) pay-as-you-go API works with any app.";
            case 403:
                return $"{plan.Provider} refused the request (403): the key's plan doesn't allow this, or the account needs attention. {TextUtil.Prefix(body.Trim(), 160)}".Trim();
            case 429:
                return "The plan's usage limit is used up for now. Try again later, or check your plan's usage page.";
            case 404:
                return $"{region.BaseUrl} doesn't know that model. Pick another one.";
            default:
                return status >= 500
                    ? $"{plan.Provider}'s service had a problem ({status}). Try again in a minute."
                    : $"{plan.Provider} replied {status}: {TextUtil.Prefix(body.Trim(), 200)}";
        }
    }
}
