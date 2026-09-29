using System.IO;

namespace Dsh.Windows;

/// <summary>The keyboard tool acts as the user. Typing into a terminal would run shell commands
/// without going through the permission gate that covers <c>run_shell_command</c> and
/// <c>process_start</c>, and typing into DSH itself would talk to the agent's own composer. Both are
/// refused; the agent is told to use the shell tools, which ask when a command could change
/// things. On Windows the same hole has more doors — the Run box, Start search, the Explorer
/// address bar and launcher palettes all run what is typed into them — so those are refused
/// too. Right/middle clicks paste the clipboard in most terminals, so the mouse tool asks the same
/// question before one.</summary>
public static class TerminalGuard
{
    /// <summary>Terminal emulators, console hosts, shells and SSH clients (executable names without
    /// ".exe").</summary>
    internal static readonly HashSet<string> Terminals = new(StringComparer.OrdinalIgnoreCase)
    {
        "WindowsTerminal", "OpenConsole", "conhost", "cmd", "powershell", "pwsh", "powershell_ise",
        "wezterm", "wezterm-gui", "alacritty", "mintty", "ConEmu", "ConEmu64", "ConEmuC", "ConEmuC64", "Cmder",
        "Hyper", "Tabby", "Warp", "WindTerm", "FluentTerminal.App", "Rio", "Termius", "putty", "kitty", "MobaXterm",
        "wsl", "wslhost", "bash",
    };

    /// <summary>Shell surfaces that launch whatever is typed into them: Explorer (Run box, address
    /// bar, desktop), Start/search, launcher palettes and Task Manager's "Run new task".</summary>
    internal static readonly HashSet<string> Launchers = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "SearchHost", "SearchApp", "SearchUI", "StartMenuExperienceHost", "PowerToys.PowerLauncher",
        "Microsoft.CmdPal.UI", "Taskmgr",
    };

    /// <summary>Window classes of terminal windows whatever process owns them (a classic console
    /// window reports the program running in it, e.g. python.exe, as its owner).</summary>
    internal static readonly HashSet<string> TerminalClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "ConsoleWindowClass", "CASCADIA_HOSTING_WINDOW_CLASS", "PseudoConsoleWindow", "mintty",
        "VirtualConsoleClass", "org.wezfurlong.wezterm", "PuTTY", "KiTTY",
    };

    private const string DshExecutable = "DSH";

    /// <summary>Whether a window with these facts is off limits for typing: a terminal, a launcher
    /// or DSH. Any window of this very process counts as DSH itself, whatever the executable is
    /// called.</summary>
    public static bool IsBlocked(string? executablePath, string? className, int processId = 0)
    {
        if (processId != 0 && processId == Environment.ProcessId) return true;
        if (IsTerminal(executablePath, className)) return true;
        var name = ExecutableName(executablePath);
        return name.Equals(DshExecutable, StringComparison.OrdinalIgnoreCase) || Launchers.Contains(name);
    }

    /// <summary>Whether a window with these facts is a terminal (the paste-click check).</summary>
    public static bool IsTerminal(string? executablePath, string? className) =>
        (!string.IsNullOrEmpty(className) && TerminalClasses.Contains(className)) || Terminals.Contains(ExecutableName(executablePath));

    /// <summary>Whether the top-level window containing <paramref name="window"/> is off limits for typing.</summary>
    public static bool IsBlocked(IntPtr window) =>
        Describe(window) is { } target && IsBlocked(target.Path, target.ClassName, target.ProcessId);

    /// <summary>Whether the top-level window containing <paramref name="window"/> is a terminal.</summary>
    public static bool IsTerminal(IntPtr window) => Describe(window) is { } target && IsTerminal(target.Path, target.ClassName);

    /// <summary>The app name to put in a refusal for <paramref name="window"/>.</summary>
    public static string AppName(IntPtr window)
    {
        if (Describe(window) is not { } target) return "that window";
        var name = ExecutableName(target.Path);
        return name.Length > 0 ? name : target.ClassName;
    }

    /// <summary>"C:\Windows\System32\cmd.exe" → "cmd".</summary>
    internal static string ExecutableName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        var name = Path.GetFileName(path.Trim().TrimEnd('\\', '/'));
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    private static (string? Path, string ClassName, int ProcessId)? Describe(IntPtr window)
    {
        if (window == IntPtr.Zero) return null;
        using var dpi = DpiScope.PerMonitor();
        var root = Native.GetAncestor(window, Native.GA_ROOT);
        if (root == IntPtr.Zero) root = window;
        var className = WindowServices.ClassName(root);
        var pid = WindowServices.ProcessIdOf(root, className);
        return (WindowServices.ImagePath(pid) ?? WindowServices.ProcessName(pid), className, pid);
    }

    /// <summary>The message the keyboard tool returns when it refuses.</summary>
    public static string Refusal(string appName) =>
        $"Error: not typing into {appName}: keystrokes there run commands as you without the usual approval. Use run_shell_command or process_start (they ask before anything risky), or ask the user to type it.";

    /// <summary>The message the mouse tool returns for a paste-click into a terminal.</summary>
    public static string ClickRefusal(string appName, string button) =>
        $"Error: not {button}-clicking into {appName}: a {button} click there pastes the clipboard as typed input, which runs commands as you without the usual approval. Use run_shell_command or process_start (they ask before anything risky), or ask the user to do it.";
}
