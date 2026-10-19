namespace Vision.Core;

// MARK: - The sensitivity policy: which routes a Sensitive chat may use
//
// A chat (or every chat of a project, by default) can be marked Sensitive. A Sensitive chat talks only to private routes —
// the person's own machines (the Spark, Ollama, LM Studio, anything on their network), Amazon Bedrock in their own AWS
// account, and Guardian — or to an online model with every sensitive detail masked on the way out by the shield (which is
// forced on for that chat, whatever the global switch says). An engine that reads the project's files itself (Claude Code,
// Codex) is refused: the shield only sees what Vision sends, not what such an engine reads on its own.
// The same classes, words and refusals are used by Vision for macOS.

/// <summary>Where a route sends what it is given, as far as the sensitivity policy is concerned.</summary>
public enum RouteReach
{
    /// <summary>A machine the person controls: this PC, the home or office network, their tailnet (see <see cref="RoutePrivacy"/>).</summary>
    OwnMachine,
    /// <summary>Amazon Bedrock, signed with the person's own AWS credentials or a Bedrock API key of their account.</summary>
    OwnCloud,
    /// <summary>Guardian's model proxy (the person's organisation's own system).</summary>
    Guardian,
    /// <summary>Someone else's online service (OpenAI, OpenRouter, DeepSeek, Kimi, Z.ai, Anthropic's API…).</summary>
    Online,
    /// <summary>An engine that reads and sends the project's files by itself (Claude Code, Codex): Vision can't mask what it reads.</summary>
    DirectFileEngine,
}

/// <summary>Whether a route may serve a chat, and how.</summary>
public sealed record Admission(bool Allowed, RouteReach Reach, bool Masked, string? Refusal)
{
    public static Admission Allow(RouteReach reach, bool masked) => new(true, reach, masked, null);
    public static Admission Refuse(RouteReach reach, string why) => new(false, reach, false, why);
}

public static class RouteAdmission
{
    /// <summary>The kinds of route that are engines reading the project's files themselves. Matched by the kind's name so this
    /// list holds whichever of them a build has (the Claude Code engine is "ClaudeCode"; a Codex one would be "Codex").</summary>
    private static readonly HashSet<string> DirectFileKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "ClaudeCode", "ClaudeCodeCli", "Codex", "CodexCli",
    };

    /// <summary>Whether a route kind (by its name) is an engine that reads the project's files itself.</summary>
    public static bool IsDirectFileKindName(string kind) => DirectFileKinds.Contains(kind);

    /// <summary>Where <paramref name="route"/> sends what it is given.</summary>
    public static RouteReach Reach(ProviderProfile route)
    {
        if (IsDirectFileKindName(route.Kind.ToString())) return RouteReach.DirectFileEngine;
        if (route.Kind == ProviderKind.Bedrock) return RouteReach.OwnCloud;
        if (route.BaseUrl.Contains(GuardianEndpoint.HarnessPath, StringComparison.OrdinalIgnoreCase)) return RouteReach.Guardian;
        return route.IsPrivateRoute ? RouteReach.OwnMachine : RouteReach.Online;
    }

    /// <summary>Whether a route that isn't one of the person's own reaches counts as private for a Sensitive chat.</summary>
    public static bool IsPrivateReach(RouteReach reach) => reach is RouteReach.OwnMachine or RouteReach.OwnCloud or RouteReach.Guardian;

    /// <summary>Whether <paramref name="route"/> may serve a chat. A chat that isn't Sensitive may use any route (masked when it
    /// isn't private and the shield is on, as always). A Sensitive chat may use a private route as it is, an online one only
    /// masked by the shield — and not at all when <paramref name="allowShielded"/> is off — and never an engine that reads the
    /// files itself.</summary>
    public static Admission Admit(ProviderProfile route, bool sensitive, bool allowShielded = true)
    {
        var reach = Reach(route);
        var maskedAnyway = !route.IsPrivateRoute; // the shield works on every route that isn't private
        if (!sensitive) return Admission.Allow(reach, maskedAnyway);
        return reach switch
        {
            RouteReach.DirectFileEngine => Admission.Refuse(reach, DirectEngineRefusal(RouteName(route))),
            RouteReach.Online when !allowShielded => Admission.Refuse(reach, StrictRefusal(RouteName(route))),
            RouteReach.Online => Admission.Allow(reach, masked: true),
            _ => Admission.Allow(reach, maskedAnyway),
        };
    }

    private static string RouteName(ProviderProfile route) => route.Name.Length > 0 ? route.Name : route.Model;

    /// <summary>Why Claude Code (or another engine that reads the files itself) can't serve a Sensitive chat.</summary>
    public static string DirectEngineRefusal(string name) =>
        $"This chat is Sensitive, so it can't use {name}: {name} reads your project's files itself and sends them to its own service, " +
        "where Vision's sensitive-data shield can't mask them. Pick a private model for this chat (your own servers, Bedrock in your AWS " +
        "account, or Guardian) or an online model through the shield — or turn Sensitive off for this chat.";

    /// <summary>Why an online model can't serve a Sensitive chat when masked online use is switched off.</summary>
    public static string StrictRefusal(string name) =>
        $"This chat is Sensitive and Sensitive chats use private models only (Settings › AI Models), so it can't use {name}, an online " +
        "service. Pick a private model for this chat (your own servers, Bedrock in your AWS account, or Guardian) — or turn Sensitive off for this chat.";

    // MARK: What the chat header says

    /// <summary>The chat header's label: "Sensitive" or "Standard".</summary>
    public static string Label(bool sensitive) => sensitive ? "Sensitive" : "Standard";

    /// <summary>The header's one-line description of the policy, with what the chat's own route does under it.</summary>
    public static string Describe(bool sensitive, Admission? admission, bool allowShielded = true)
    {
        var policy = sensitive
            ? allowShielded
                ? "Sensitive: private models only — your own servers, Bedrock in your AWS account, Guardian — or online models with every sensitive detail masked. Engines that read your files themselves (Claude Code) aren't used."
                : "Sensitive: private models only — your own servers, Bedrock in your AWS account, Guardian. Online models and engines that read your files themselves (Claude Code) aren't used."
            : "Standard: any model. Sensitive details are masked before they go to a model that isn't private (when the shield is on).";
        if (admission is null) return policy;
        var now = admission switch
        {
            { Allowed: false } => "This chat's model can't be used while it is Sensitive.",
            { Reach: RouteReach.OwnMachine } => "This chat's model runs on your own machine.",
            { Reach: RouteReach.OwnCloud, Masked: true } => "This chat's model is Bedrock in your AWS account (masked by the shield).",
            { Reach: RouteReach.OwnCloud } => "This chat's model is Bedrock in your AWS account.",
            { Reach: RouteReach.Guardian, Masked: true } => "This chat's model is through Guardian (masked by the shield).",
            { Reach: RouteReach.Guardian } => "This chat's model is through Guardian.",
            { Masked: true } => "This chat's model is an online service: sensitive details are masked before anything is sent.",
            _ => "This chat's model is an online service.",
        };
        return policy + "\n" + now;
    }
}
