namespace Vision.Core;

// MARK: - Sensitive chats: which models may see them
//
// A chat can be marked Sensitive (/sensitive on). A Sensitive chat talks only to routes that may see its data as it is —
// or, for every other route, only through the sensitive-data shield, which masks what it detects before a request leaves
// the PC and puts the real values back in the answer:
//
//   * private routes — the model runs on hardware you control (the route's Private switch, by default decided by its
//     address — see RoutePrivacy), your organisation's own Guardian, or Amazon Bedrock in your own AWS account;
//   * masked routes — every other online service, but only while the shield is switched on;
//   * refused — engines that read the project's files themselves and send them to their service (the Claude Code CLI):
//     the shield never sees what they send, so no masking can stand between them and the data. And every route that
//     isn't private while the shield is switched off.
//
// Pure functions, the same decisions and the same words as Vision for macOS (SensitivityPolicy.swift).

/// <summary>How a Sensitive chat may use a route.</summary>
public abstract record RouteAdmission
{
    /// <summary>The route may see the chat as it is; <paramref name="Why"/> names what makes it private.</summary>
    public sealed record Private(string Why) : RouteAdmission;
    /// <summary>Allowed only behind the sensitive-data shield.</summary>
    public sealed record Masked : RouteAdmission;
    /// <summary>Not allowed in a Sensitive chat; <paramref name="Reason"/> says why in a plain sentence.</summary>
    public sealed record Refused(string Reason) : RouteAdmission;

    public bool IsAllowed => this is not Refused;

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

public static class SensitivityPolicy
{
    /// <summary>How a route is named in the policy's sentences: its name, else its model.</summary>
    public static string Label(ProviderProfile route) => route.Name.Length == 0 ? route.Model : route.Name;

    /// <summary>Whether a route's engine reads files itself and sends them to its own service (the claude CLI).</summary>
    public static bool ReadsFilesDirectly(ProviderProfile route) => ClaudeCodeRoutes.IsClaudeCode(route);

    /// <summary>How a Sensitive chat may use <paramref name="route"/>, with the shield on or off (Settings › AI Models ›
    /// Sensitive data). Only asked for Sensitive chats: an ordinary chat may use every route.</summary>
    public static RouteAdmission Admission(ProviderProfile route, bool shieldOn) =>
        Admission(Label(route), ReadsFilesDirectly(route), GuardianRoutes.IsGuardian(route), route.Kind == ProviderKind.Bedrock,
            route.IsPrivateRoute, shieldOn);

    /// <summary>The same rule from plain facts about a route (a subagent pool's routes — see <see cref="SubagentPool"/>).</summary>
    public static RouteAdmission Admission(string name, bool readsFilesDirectly, bool guardian, bool bedrock, bool isPrivate, bool shieldOn)
    {
        if (readsFilesDirectly)
            return new RouteAdmission.Refused($"{name} reads your files itself and sends them to its own service, so the " +
                "sensitive-data shield can't mask anything on the way. Sensitive chats can't use it — choose a private model " +
                "(your own hardware, Guardian, Amazon Bedrock in your AWS account), or turn Sensitive off for this chat.");
        if (guardian) return new RouteAdmission.Private("Guardian, your organisation's own server");
        if (bedrock) return new RouteAdmission.Private("Amazon Bedrock in your own AWS account");
        if (isPrivate) return new RouteAdmission.Private("your own hardware");
        if (!shieldOn)
            return new RouteAdmission.Refused($"{name} is an online service and the sensitive-data shield is switched off, so " +
                "nothing would be masked before sending. Turn the shield on (Settings › AI Models › Sensitive data), choose a " +
                "private model, or turn Sensitive off for this chat.");
        return new RouteAdmission.Masked();
    }

    /// <summary>May see a Sensitive chat as it is — own hardware, Guardian, Bedrock in your account (never Claude Code). The one
    /// definition of "private" for subagent servers too: a sensitive task runs only on such a server.</summary>
    public static bool IsPrivate(ProviderProfile route) => Admission(route, shieldOn: false) is RouteAdmission.Private;

    /// <summary>The error a Sensitive chat shows when its model is refused (also what a turn throws); null when allowed.</summary>
    public static string? RefusalMessage(ProviderProfile route, bool shieldOn) =>
        Admission(route, shieldOn).Refusal is { } reason ? "This chat is Sensitive. " + reason : null;

    /// <summary>The line a Sensitive chat shows about its model.</summary>
    public static string HeaderLine(ProviderProfile? route, bool shieldOn)
    {
        if (route is null) return "Sensitive — only private models, or others through the shield.";
        return Admission(route, shieldOn) switch
        {
            RouteAdmission.Private p => $"Sensitive — {Label(route)} is private ({p.Why}).",
            RouteAdmission.Masked => $"Sensitive — {Label(route)} isn't private: what the shield detects is masked before sending.",
            _ => $"Sensitive — {Label(route)} can't be used in this chat. Choose a private model.",
        };
    }

    /// <summary>What the policy is, for help text and /sensitive.</summary>
    public const string Explanation =
        "A Sensitive chat uses only private models — your own hardware (Spark, Strix Halo, Ollama, LM Studio), Guardian, or " +
        "Amazon Bedrock in your AWS account — or other models only through the sensitive-data shield, which masks what it " +
        "detects before sending and puts it back in the answer. Engines that read your files themselves, like Claude Code, are refused.";

    /// <summary>The backups a Sensitive chat may fall over to: only routes it may use.</summary>
    public static IReadOnlyList<ProviderProfile> AdmittedBackups(IEnumerable<ProviderProfile> routes, bool shieldOn) =>
        routes.Where(r => Admission(r, shieldOn).IsAllowed).ToList();
}

/// <summary>A turn refused because the chat is Sensitive and its model may not see it.</summary>
public sealed class SensitivityRefusalException(string message) : Exception(message);
