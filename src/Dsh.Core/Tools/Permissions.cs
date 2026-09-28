using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Dsh.Core;

// MARK: - Permission policy (workspace-write by default)

/// <summary>How much of the machine the agent may touch without asking.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PermissionPreset>))]
public enum PermissionPreset
{
    /// <summary>Reads anywhere; writes and commands stay inside the project folder.</summary>
    [JsonStringEnumMemberName("workspaceWrite")] WorkspaceWrite,
    /// <summary>Research only: every write and every command asks.</summary>
    [JsonStringEnumMemberName("plan")] Plan,
    /// <summary>Writes and commands allowed without asking.</summary>
    [JsonStringEnumMemberName("fullAccess")] FullAccess,
}

public static class PermissionPresets
{
    public static IReadOnlyList<PermissionPreset> All { get; } =
        [PermissionPreset.WorkspaceWrite, PermissionPreset.Plan, PermissionPreset.FullAccess];

    public static string RawValue(this PermissionPreset preset) => preset switch
    {
        PermissionPreset.Plan => "plan",
        PermissionPreset.FullAccess => "fullAccess",
        _ => "workspaceWrite",
    };

    public static PermissionPreset? FromRaw(string? raw) => raw switch
    {
        "workspaceWrite" => PermissionPreset.WorkspaceWrite,
        "plan" => PermissionPreset.Plan,
        "fullAccess" => PermissionPreset.FullAccess,
        _ => null,
    };

    public static string Label(this PermissionPreset preset) => preset switch
    {
        PermissionPreset.Plan => "Plan only",
        PermissionPreset.FullAccess => "Full access",
        _ => "Workspace write",
    };

    public static string Detail(this PermissionPreset preset) => preset switch
    {
        PermissionPreset.Plan =>
            "Research only. Every write and every shell command asks first, and the agent is told to produce a plan instead of changing things.",
        PermissionPreset.FullAccess =>
            "Writes and runs commands without asking. Only use this for a project you would hand the keys to.",
        _ => "Reads anywhere. Writes and commands inside the project run without asking; outside it, the agent asks.",
    };
}

public enum PermissionVerdict { Proceed, Deny, Ask }

/// <summary>Outcome of asking a tool call's permission gate.</summary>
public readonly record struct PermissionDecision(PermissionVerdict Verdict, string? Reason = null)
{
    public static PermissionDecision Proceed => new(PermissionVerdict.Proceed);
    public static PermissionDecision Ask => new(PermissionVerdict.Ask);
    public static PermissionDecision Deny(string reason) => new(PermissionVerdict.Deny, reason);
}

public sealed partial class PermissionPolicy
{
    public PermissionPreset Preset { get; }
    /// <summary>Root the write shell is confined to (absolute, normalized, no trailing separator
    /// except for a drive root).</summary>
    public string WorkspaceRoot { get; }

    public PermissionPolicy(PermissionPreset preset, string workspaceRoot)
    {
        Preset = preset;
        WorkspaceRoot = Normalize(workspaceRoot);
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static string Normalize(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? "";
        return full.Length > root.Length ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : full;
    }

    public static string HomeDirectory => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    // MARK: - Path containment

    /// <summary>Resolve a model-supplied path (relative, ~/…, or absolute) against the workspace root
    /// and say whether it stays inside it.</summary>
    public (string Path, bool Inside) Resolve(string raw)
    {
        var resolved = Normalize(Path.GetFullPath(Expand(raw), WorkspaceRoot));
        return (resolved, IsInside(resolved));
    }

    public bool IsInside(string fullPath)
    {
        if (string.Equals(fullPath, WorkspaceRoot, PathComparison)) return true;
        var prefix = WorkspaceRoot.EndsWith(Path.DirectorySeparatorChar) ? WorkspaceRoot : WorkspaceRoot + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(prefix, PathComparison);
    }

    /// <summary>Expand ~ and make relative paths absolute against the workspace.</summary>
    public string Expand(string raw)
    {
        var t = raw.Trim();
        if (t.Length >= 2 && t[0] == '"' && t[^1] == '"') t = t[1..^1];
        if (t == "~") return HomeDirectory;
        if (t.StartsWith("~/", StringComparison.Ordinal) || t.StartsWith("~\\", StringComparison.Ordinal))
            return Path.Join(HomeDirectory, t[2..]);
        if (OperatingSystem.IsWindows())
        {
            // Git Bash / MSYS spelling of a drive: /c/Users/me → C:\Users\me.
            var msys = MsysDrive().Match(t);
            if (msys.Success)
                return char.ToUpperInvariant(msys.Groups[1].Value[0]) + ":\\" + t[msys.Length..].Replace('/', '\\');
        }
        if (Path.IsPathFullyQualified(t)) return t;
        // Rooted without a drive ("\src") resolves on the workspace's drive; everything else is
        // relative to the workspace.
        return Path.GetFullPath(t, WorkspaceRoot);
    }

    [GeneratedRegex(@"^/([a-zA-Z])(/|$)")]
    private static partial Regex MsysDrive();

    // MARK: - Gates

    /// <summary>File-write tools.</summary>
    public PermissionDecision CheckWrite(string path)
    {
        if (Preset == PermissionPreset.FullAccess) return PermissionDecision.Proceed;
        // Plan mode promises the user nothing gets modified, so a write asks wherever it lands
        // rather than only outside the project.
        if (Preset == PermissionPreset.Plan) return PermissionDecision.Ask;
        return Resolve(path).Inside ? PermissionDecision.Proceed : PermissionDecision.Ask;
    }

    /// <summary>Shell commands. The command string is never parsed deeply; under workspace-write,
    /// commands that only read run without asking, and anything that looks like it could change
    /// something asks.</summary>
    public PermissionDecision CheckShell(string command) => Preset switch
    {
        PermissionPreset.Plan => PermissionDecision.Ask,
        PermissionPreset.FullAccess => PermissionDecision.Proceed,
        _ => LooksMutating(command) ? PermissionDecision.Ask : PermissionDecision.Proceed,
    };

    /// <summary>The Unix vocabulary (bash / Git Bash), matched as plain substrings.</summary>
    private static readonly string[] UnixMutating =
    [
        "rm ", "sudo", "> ", ">>", "mv ", "cp ", "mkdir", "chmod", "chown", "dd ", "git push", "git commit",
        "git checkout", "git reset", "brew install", "pip install", "npm install", "git branch -d",
        "git rebase", "git merge",
    ];

    public static bool LooksMutating(string command)
    {
        foreach (var m in UnixMutating)
        {
            if (command.Contains(m, StringComparison.Ordinal)) return true;
        }
        return WindowsMutating().IsMatch(command);
    }

    /// <summary>PowerShell / cmd.exe verbs and package managers, case-insensitive, matched as words so
    /// "cmd /c" doesn't read as "md".</summary>
    [GeneratedRegex(
        @"\b(remove-item|move-item|copy-item|rename-item|new-item|set-content|add-content|out-file|clear-content|" +
        @"set-itemproperty|new-itemproperty|remove-itemproperty|stop-process|restart-computer|stop-computer|" +
        @"set-executionpolicy|install-module|install-package|uninstall-module|uninstall-package|start-process|" +
        @"expand-archive|compress-archive|set-acl|new-service|remove-service)\b" +
        @"|(^|[\s;&|(])(del|erase|rd|rmdir|ri|move|mi|copy|cpi|xcopy|robocopy|ren|rni|ni|md|taskkill|icacls|takeown|attrib|setx|mklink|format|shutdown)(\s|$)" +
        @"|\b(winget|choco|scoop)\s+(install|uninstall|upgrade|remove)\b" +
        @"|\breg(\.exe)?\s+(add|delete|import|copy|restore)\b" +
        @"|\bnpm\s+(i|install|uninstall|ci|update|publish)\b|\b(yarn|pnpm)\s+(add|remove|install|up|upgrade)\b" +
        @"|\bpip3?\s+(install|uninstall)\b|\bdotnet\s+(add|remove|new|publish|tool\s+install|workload\s+install)\b" +
        @"|\bgit\s+(push|commit|checkout|reset|rebase|merge|clean|stash|pull|switch|restore|rm|mv|tag|cherry-pick|revert|am|apply)\b" +
        @"|\bgit\s+branch\s+-[dDmM]\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WindowsMutating();

    /// <summary>Web fetch is read-only.</summary>
    public PermissionDecision CheckFetch() => PermissionDecision.Proceed;

    /// <summary>Seeing or driving the rest of the machine. Screenshots can capture anything on screen
    /// (and are sent to the model), and mouse/keyboard control acts as the user in any app, so neither
    /// is implicit: the first use in a chat asks, and an approval holds for that chat. Plan mode never
    /// drives the machine.</summary>
    public PermissionDecision CheckComputer(ComputerAccess access) => Preset switch
    {
        PermissionPreset.FullAccess => PermissionDecision.Proceed,
        PermissionPreset.Plan => access == ComputerAccess.Control
            ? PermissionDecision.Deny("Plan mode is read-only: no mouse or keyboard control.")
            : PermissionDecision.Ask,
        _ => PermissionDecision.Ask,
    };
}

/// <summary>What a computer-use tool needs from the machine.</summary>
public enum ComputerAccess
{
    /// <summary>Read-only: screenshots, window lists, accessibility trees, inspecting processes.</summary>
    Observe,
    /// <summary>Acts as the user: mouse, keyboard, bringing apps to the front.</summary>
    Control,
}

public static class ComputerAccessInfo
{
    /// <summary>The access a tool needs, or null for tools that don't touch the machine outside the project.</summary>
    public static ComputerAccess? ForTool(string name) => name switch
    {
        "screenshot" or "list_windows" or "screen_watch" or "ui_tree" or "inspect_process" => ComputerAccess.Observe,
        "mouse" or "keyboard" or "focus_app" => ComputerAccess.Control,
        _ => null,
    };

    public static string Prompt(this ComputerAccess access) => access == ComputerAccess.Observe
        ? "Screen access — the agent wants to look at your screen: screenshots of apps and windows, window titles, and process details. What it captures is sent to the model. Allowing covers the rest of this chat."
        : "Computer control — the agent wants to move the mouse, click, type, and bring apps to the front, acting as you. Allowing covers the rest of this chat. Press Ctrl+. to stop it at any time.";
}

/// <summary>Approvals the user has given in one chat. A class so it survives the engine being
/// rebuilt between turns.</summary>
public sealed class ComputerGrants
{
    private readonly Lock _lock = new();
    private readonly HashSet<ComputerAccess> _granted = [];

    public bool Has(ComputerAccess access)
    {
        lock (_lock)
        {
            // Control implies the ability to see what you are controlling.
            return _granted.Contains(access) || (access == ComputerAccess.Observe && _granted.Contains(ComputerAccess.Control));
        }
    }

    public void Grant(ComputerAccess access)
    {
        lock (_lock) _granted.Add(access);
    }

    public void RevokeAll()
    {
        lock (_lock) _granted.Clear();
    }
}
