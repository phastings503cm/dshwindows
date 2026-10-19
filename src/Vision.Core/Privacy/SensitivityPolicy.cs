namespace Vision.Core;

// MARK: - Sensitive chats: which models may see them
//
// A chat (or every new chat of a project, by default) can be marked Sensitive. A Sensitive chat talks only to routes that
// may see its data as it is — or, for every other route, only through the sensitive-data shield, which masks what it
// detects before a request leaves this PC and puts the real values back in the answer:
//
//   * private routes — the model runs on hardware you control (a Spark, a Strix Halo box, Ollama, LM Studio: the route's
//     Private switch, by default decided by its address — see RoutePrivacy), your organisation's own Guardian, or Amazon
//     Bedrock in your own AWS account. (A route whose Private switch is off still goes through the shield as it does in
//     every chat — Bedrock does by default.)
//   * masked routes — every other online service, but only while the shield is switched on;
//   * refused — engines that read the project's files themselves and send them to their service (the Claude Code CLI):
//     the shield never sees what they send, so no masking can stand between them and the data. And every route that isn't
//     private while the shield is switched off.
//
// Pure functions, with the words and machine codes of Vision for macOS's SensitivityPolicy.swift.

/// <summary>How a Sensitive chat may use a route (the same shape as the Claude Code lane's copy of this file).</summary>
public abstract record RouteAdmission
{
    /// <summary>The route may see the chat as it is; <paramref name="Why"/> names what makes it private.</summary>
    public sealed record Private(string Why) : RouteAdmission;
    /// <summary>Allowed only behind the sensitive-data shield.</summary>
    public sealed record Masked : RouteAdmission;
    /// <summary>Not allowed in a Sensitive chat; <paramref name="Reason"/> says why in a plain sentence.</summary>
    public sealed record Refused(string Reason) : RouteAdmission;

    public bool IsAllowed => this is not Refused;
    public bool IsPrivate => this is Private;
    public bool IsMasked => this is Masked;

    /// <summary>The reason a route is refused (null when it is allowed).</summary>
    public string? Refusal => this is Refused refused ? refused.Reason : null;

    /// <summary>Stable machine word for logs and tests: "private", "masked", "refused".</summary>
    public string Code => this switch
    {
        Private => "private",
        Masked => "masked",
        _ => "refused",
    };
}

/// <summary>A turn refused because the chat is Sensitive and its model may not see it.</summary>
public sealed class SensitivityRefusalException(string message) : Exception(message);

public static class SensitivityPolicy
{
    /// <summary>Route kinds (by name) whose engine reads the project's files itself and sends them to its own service — the Claude Code
    /// CLI, the Codex CLI. Kept as names so a kind added later (by another lane) is covered without a change here.</summary>
    public static readonly IReadOnlySet<string> DirectFileEngineKinds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "claudeCode", "claude_code", "claude-code", "claudeCodeCLI", "codex", "codexCLI", "codex_cli",
    };

    /// <summary>How a route is named in the policy's sentences: its name, else its model (as the model menu names it).</summary>
    public static string Label(ProviderProfile route) => route.Name.Length > 0 ? route.Name : route.Model;

    /// <summary>The address Windows' Claude Code routes have (an OpenAI-compatible kind with a claude-code:// address — see the
    /// Claude Code lane's ClaudeCodeRoutes).</summary>
    public const string ClaudeCodeScheme = "claude-code:";

    /// <summary>Whether a route's engine reads files directly (see <see cref="DirectFileEngineKinds"/>, and Claude Code's address).</summary>
    public static bool ReadsFilesDirectly(ProviderProfile route) =>
        DirectFileEngineKinds.Contains(route.Kind.ToString()) || route.BaseUrl.StartsWith(ClaudeCodeScheme, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a route goes through Guardian (its model proxy, or a Guardian route kind if this build has one).</summary>
    public static bool IsGuardian(ProviderProfile route) =>
        route.Kind.ToString().Equals("guardian", StringComparison.OrdinalIgnoreCase)
        || route.BaseUrl.Contains(GuardianEndpoint.HarnessPath, StringComparison.OrdinalIgnoreCase);

    /// <summary>How a Sensitive chat may use <paramref name="route"/>, with the shield switched on or off (Settings › AI Models ›
    /// Sensitive data). A chat that isn't Sensitive may use every route — this is only asked for Sensitive ones.</summary>
    public static RouteAdmission Admission(ProviderProfile route, bool shieldOn)
    {
        var kind = ReadsFilesDirectly(route) ? "claudeCode"
            : IsGuardian(route) ? "guardian"
            : route.Kind == ProviderKind.Bedrock ? "bedrock"
            : route.Kind.ToString();
        return Admission(kind, Label(route), route.IsPrivateRoute, shieldOn);
    }

    /// <summary>The rule itself, on a route's kind (by name), name and Private switch — so the tests can ask about kinds this build
    /// doesn't have.</summary>
    public static RouteAdmission Admission(string kind, string name, bool isPrivate, bool shieldOn)
    {
        if (DirectFileEngineKinds.Contains(kind))
            return new RouteAdmission.Refused($"{name} reads your files itself and sends them to its own service, so the " +
                                          "sensitive-data shield can't mask anything on the way. Sensitive chats can't use it — choose a " +
                                          "private model (your own hardware, Guardian, Amazon Bedrock in your AWS account), or turn Sensitive " +
                                          "off for this chat.");
        if (kind.Equals("guardian", StringComparison.OrdinalIgnoreCase)) return new RouteAdmission.Private("Guardian, your organisation's own server");
        if (kind.Equals("bedrock", StringComparison.OrdinalIgnoreCase)) return new RouteAdmission.Private("Amazon Bedrock in your own AWS account");
        if (isPrivate) return new RouteAdmission.Private("your own hardware");
        if (!shieldOn)
            return new RouteAdmission.Refused($"{name} is an online service and the sensitive-data shield is switched off, so " +
                                          "nothing would be masked before sending. Turn the shield on (Settings › AI Models › Sensitive data), " +
                                          "choose a private model, or turn Sensitive off for this chat.");
        return new RouteAdmission.Masked();
    }

    /// <summary>The error a Sensitive chat shows when its model is refused (also what a turn throws); null when it is allowed.</summary>
    public static string? RefusalMessage(ProviderProfile route, bool shieldOn) =>
        Admission(route, shieldOn).Refusal is { } reason ? "This chat is Sensitive. " + reason : null;

    /// <summary>The line the chat header shows for a Sensitive chat on <paramref name="route"/>.</summary>
    public static string HeaderLine(ProviderProfile? route, bool shieldOn)
    {
        if (route is null) return "Sensitive — only private models, or others through the shield.";
        return Admission(route, shieldOn) switch
        {
            RouteAdmission.Private privately => $"Sensitive — {Label(route)} is private ({privately.Why}).",
            RouteAdmission.Masked => $"Sensitive — {Label(route)} isn't private: what the shield detects is masked before sending.",
            _ => $"Sensitive — {Label(route)} can't be used in this chat. Choose a private model.",
        };
    }

    /// <summary>What the header's help (and the toggle's explanation) says the policy is.</summary>
    public const string Explanation =
        "A Sensitive chat uses only private models — your own hardware (Spark, Strix Halo, Ollama, LM Studio), " +
        "Guardian, or Amazon Bedrock in your AWS account — or other models only through the sensitive-data shield, " +
        "which masks what it detects before sending and puts it back in the answer. Engines that read your files " +
        "themselves, like Claude Code, are refused.";

    /// <summary>The backups a Sensitive chat may fall over to: only routes it may use.</summary>
    public static IReadOnlyList<ProviderProfile> AdmittedBackups(IEnumerable<ProviderProfile> routes, bool shieldOn) =>
        routes.Where(r => Admission(r, shieldOn).IsAllowed).ToList();

    /// <summary>Whether a Sensitive chat's subagents must stay on private servers: when the shield is off, a server that isn't private
    /// would get the data as it is. (With the shield on, a subagent on such a server is masked like any request to it.)</summary>
    public static bool SubagentsPrivateOnly(bool shieldOn) => !shieldOn;

    /// <summary>The chip's label: "Sensitive" or "Not sensitive".</summary>
    public static string Label(bool sensitive) => sensitive ? "Sensitive" : "Not sensitive";
}
