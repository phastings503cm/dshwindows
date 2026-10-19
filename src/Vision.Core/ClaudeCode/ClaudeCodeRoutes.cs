using System.Collections.Concurrent;

namespace Vision.Core;

// MARK: - Claude Code as a chat's engine
//
// "Claude Code (your Claude subscription)": a chat on this engine hands each turn to the official `claude` CLI on this
// computer, in headless mode (`claude -p`, stream-json in and out). The CLI does all the talking to Anthropic with the
// person's own Pro/Max sign-in — Vision never reads, copies or reuses its credentials, and never calls Anthropic itself.
// Claude Code runs its own agent loop and its own tools in the chat's project folder; Vision shows what it does as rows,
// answers its permission questions with the chat's permission preset (asking the person when the preset says ask), and
// keeps the same Claude Code session across the chat's turns (--resume).
//
// A Claude Code route is stored like a Guardian one: an ordinary route (kind openAICompat, so a settings file stays
// readable by older builds) whose address is claude-code://local — or claude-code://wsl[/<distro>] for the CLI inside
// WSL — and whose model is the CLI's alias (opus, sonnet, haiku). Nothing is sent to that "address": every path that
// would talk HTTP to a route sees ClaudeCodeClient instead. Vision for macOS stores the same.

/// <summary>One model the Claude Code engine offers: the CLI's alias and how the model menu describes it.</summary>
public sealed record ClaudeCodeModel(string Alias, string Title, string Blurb);

public static class ClaudeCodeRoutes
{
    /// <summary>The name Claude Code routes get (route ids are name|model: "Claude Code|sonnet").</summary>
    public const string RouteName = "Claude Code";
    public const string Scheme = "claude-code:";
    public const string LocalAddress = "claude-code://local";
    public const string WslAddress = "claude-code://wsl";

    /// <summary>What the model menu offers, most capable first. The aliases always mean the newest model of each family.</summary>
    public static readonly IReadOnlyList<ClaudeCodeModel> Models =
    [
        new("opus", "Opus", "the most capable — long, hard work"),
        new("sonnet", "Sonnet", "fast and capable — the everyday choice"),
        new("haiku", "Haiku", "the quickest — small jobs"),
    ];

    /// <summary>The model a new Claude Code chat starts on.</summary>
    public const string DefaultModel = "sonnet";

    /// <summary>The window assumed until a turn's result says how big it really is (the CLI reports it per model).</summary>
    public const int DefaultContextWindow = 200_000;

    /// <summary>A route that runs on the claude CLI.</summary>
    public static bool IsClaudeCode(ProviderProfile? route) =>
        route is not null && route.BaseUrl.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase);

    /// <summary>The CLI runs inside WSL for this route.</summary>
    public static bool UsesWsl(ProviderProfile route) =>
        IsClaudeCode(route) && route.BaseUrl.StartsWith(WslAddress, StringComparison.OrdinalIgnoreCase);

    /// <summary>The WSL distribution a WSL route names (null = the default distribution).</summary>
    public static string? WslDistro(ProviderProfile route)
    {
        if (!UsesWsl(route)) return null;
        var rest = route.BaseUrl[WslAddress.Length..].Trim('/');
        return rest.Length == 0 ? null : Uri.UnescapeDataString(rest);
    }

    /// <summary>The address for the CLI on this computer, or inside WSL (<paramref name="distro"/> null = the default one).</summary>
    public static string Address(bool wsl, string? distro = null) =>
        !wsl ? LocalAddress : string.IsNullOrWhiteSpace(distro) ? WslAddress : WslAddress + "/" + Uri.EscapeDataString(distro.Trim());

    /// <summary>A route for one alias. It is never private: Claude Code sends what it reads to Anthropic, so the
    /// sensitive-data shield has nothing to stand between (see <see cref="SensitivityPolicy"/>).</summary>
    public static ProviderProfile Route(string alias, bool wsl = false, string? distro = null) =>
        new(ProviderKind.OpenAICompat, RouteName, Address(wsl, distro), alias)
        {
            IsPrivate = false,
            Vision = true,
        };

    /// <summary>How the model menu and the rows name a route's model ("Claude Code · Sonnet").</summary>
    public static string ModelTitle(string alias) =>
        Models.FirstOrDefault(m => string.Equals(m.Alias, alias, StringComparison.OrdinalIgnoreCase))?.Title ?? alias;

    // MARK: Windows the CLI reported

    private static readonly ConcurrentDictionary<string, int> LearnedWindows = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The context window a turn's result reported for <paramref name="alias"/> (null until one has).</summary>
    public static int? LearnedWindow(string alias) => LearnedWindows.TryGetValue(alias, out var window) ? window : null;

    /// <summary>Remember the window the CLI reported for the model behind <paramref name="alias"/>.</summary>
    public static void LearnWindow(string alias, int window)
    {
        if (window > 0) LearnedWindows[alias] = window;
    }

    // MARK: Words

    /// <summary>The engine choice's one-line description (wizard card, Settings).</summary>
    public const string Summary =
        "Your Claude Pro or Max plan, through the official Claude Code app on this computer — it signs in and does the talking.";

    /// <summary>What Settings and the wizard say about how it works.</summary>
    public const string Explanation =
        "Vision runs the official Claude Code command-line app (claude) on this computer for chats you put on it, using the " +
        "Claude sign-in you made there — your Pro or Max plan. Vision never sees or stores that sign-in: Claude Code talks to " +
        "Anthropic itself. Claude Code works in the chat's project folder with its own tools; Vision shows each step, asks you " +
        "before anything your permission setting doesn't allow, and keeps the conversation going turn after turn.";

    /// <summary>Why a Sensitive chat can't use it (the same sentence the policy gives).</summary>
    public const string SensitiveReason =
        "Claude Code reads your files itself and sends them to its own service, so the sensitive-data shield can't mask " +
        "anything on the way.";
}
