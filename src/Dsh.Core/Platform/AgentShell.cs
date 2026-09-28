using System.Diagnostics;
using System.Text;

namespace Dsh.Core;

public enum ShellKind { PowerShell, Cmd, Bash }

/// <summary>The shell the agent's run_shell_command (and plugin tools) run commands with, and how to
/// launch one command in it.
///
/// On Windows the default is PowerShell 7 (pwsh) when installed, otherwise Windows PowerShell 5.1;
/// cmd.exe and Git Bash can be chosen in Settings. Elsewhere (tests, CI on Linux) it is /bin/bash.</summary>
public sealed record AgentShell(ShellKind Kind, string Executable, string DisplayName)
{
    /// <summary>Values the settings store for the shell preference.</summary>
    public const string Auto = "auto";
    public const string PowerShellPreference = "powershell";
    public const string WindowsPowerShellPreference = "windowspowershell";
    public const string CmdPreference = "cmd";
    public const string BashPreference = "bash";

    private static readonly Lazy<AgentShell> DefaultShell = new(() => Resolve(Auto));

    /// <summary>The best shell available on this machine.</summary>
    public static AgentShell Default => DefaultShell.Value;

    /// <summary>Resolve a preference ("auto", "powershell", "windowspowershell", "cmd", "bash", or a
    /// path to an executable) to a shell that exists, falling back to the default.</summary>
    public static AgentShell Resolve(string? preference)
    {
        var pref = (preference ?? Auto).Trim();
        if (!OperatingSystem.IsWindows())
        {
            var bash = File.Exists("/bin/bash") ? "/bin/bash" : "/bin/sh";
            return new AgentShell(ShellKind.Bash, bash, Path.GetFileName(bash));
        }
        switch (pref.ToLowerInvariant())
        {
            case CmdPreference:
                return CmdShell();
            case BashPreference:
                return GitBash() ?? PowerShellShell();
            case WindowsPowerShellPreference:
                return WindowsPowerShell();
            case PowerShellPreference:
            case Auto:
            case "":
                return PowerShellShell();
        }
        if (File.Exists(pref))
        {
            var name = Path.GetFileNameWithoutExtension(pref).ToLowerInvariant();
            var kind = name switch
            {
                "pwsh" or "powershell" => ShellKind.PowerShell,
                "cmd" => ShellKind.Cmd,
                _ => ShellKind.Bash,
            };
            return new AgentShell(kind, pref, Path.GetFileName(pref));
        }
        return PowerShellShell();
    }

    private static AgentShell PowerShellShell()
    {
        var pwsh = FindPwsh();
        if (pwsh is not null)
        {
            var version = FileVersion(pwsh);
            return new AgentShell(ShellKind.PowerShell, pwsh,
                version is null ? "PowerShell 7 (pwsh)" : $"PowerShell {version} (pwsh)");
        }
        return WindowsPowerShell();
    }

    private static AgentShell WindowsPowerShell()
    {
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var path = Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe");
        return new AgentShell(ShellKind.PowerShell, File.Exists(path) ? path : "powershell.exe", "Windows PowerShell 5.1");
    }

    private static AgentShell CmdShell()
    {
        var comspec = Environment.GetEnvironmentVariable("ComSpec");
        var path = !string.IsNullOrEmpty(comspec) && File.Exists(comspec)
            ? comspec
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        return new AgentShell(ShellKind.Cmd, path, "Command Prompt (cmd.exe)");
    }

    /// <summary>Git for Windows' bash. System32\bash.exe is WSL's launcher (a different filesystem
    /// view), so it is never picked implicitly.</summary>
    public static AgentShell? GitBash()
    {
        foreach (var path in GitBashCandidates())
        {
            if (File.Exists(path)) return new AgentShell(ShellKind.Bash, path, "Git Bash");
        }
        return null;
    }

    private static IEnumerable<string> GitBashCandidates()
    {
        foreach (var root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
                 })
        {
            if (!string.IsNullOrEmpty(root)) yield return Path.Combine(root, "Git", "bin", "bash.exe");
        }
        var git = FindOnPath("git.exe");
        if (git is not null)
        {
            // ...\Git\cmd\git.exe → ...\Git\bin\bash.exe
            var gitRoot = Path.GetDirectoryName(Path.GetDirectoryName(git));
            if (gitRoot is not null) yield return Path.Combine(gitRoot, "bin", "bash.exe");
        }
    }

    public static string? FindPwsh()
    {
        var onPath = FindOnPath("pwsh.exe");
        if (onPath is not null) return onPath;
        foreach (var folder in new[] { "7", "7-preview" })
        {
            var candidate = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", folder, "pwsh.exe");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    public static string? FindOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim('"'), fileName);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry.
            }
        }
        return null;
    }

    private static string? FileVersion(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            var v = info.ProductVersion ?? info.FileVersion;
            if (string.IsNullOrEmpty(v)) return null;
            var parts = v.Split('.', ' ', '+');
            return parts.Length >= 2 ? $"{parts[0]}.{parts[1]}" : parts[0];
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>A sentence for the tool description and the environment block telling the model
    /// which syntax to write.</summary>
    public string SyntaxHint => Kind switch
    {
        ShellKind.PowerShell => Executable.EndsWith("powershell.exe", StringComparison.OrdinalIgnoreCase)
            ? "Commands run as a Windows PowerShell 5.1 script: use PowerShell syntax (Get-ChildItem, Select-String, $env:NAME, `;` or new lines to chain; `&&` is NOT available in 5.1). Native tools on PATH (git, dotnet, npm, python) work as usual."
            : "Commands run as a PowerShell 7 script: use PowerShell syntax (Get-ChildItem, Select-String, $env:NAME, `;`, `&&` or new lines to chain). Native tools on PATH (git, dotnet, npm, python) work as usual.",
        ShellKind.Cmd => "Commands run with cmd.exe /c: use cmd syntax (dir, type, findstr, set NAME=value, `&&` to chain).",
        _ => OperatingSystem.IsWindows()
            ? "Commands run with bash -c (Git Bash): POSIX syntax and tools; Windows drives are /c/..., /d/...."
            : "Commands run with bash -c.",
    };

    /// <summary>How to start one command. Stdin is redirected (and closed by the runner) so nothing can
    /// wait for interactive input forever.</summary>
    public ProcessStartInfo CreateStartInfo(string command, string workingDirectory)
    {
        var psi = new ProcessStartInfo
        {
            FileName = Executable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["DSH_AGENT"] = "1";
        switch (Kind)
        {
            case ShellKind.PowerShell:
                foreach (var arg in PowerShellArguments(command)) psi.ArgumentList.Add(arg);
                break;
            case ShellKind.Cmd:
                // /s strips exactly the outer quotes, so the command reaches cmd verbatim. The code
                // page switch makes cmd's own output UTF-8.
                psi.Arguments = "/d /s /c \"chcp 65001>nul & " + command + "\"";
                break;
            default:
                psi.ArgumentList.Add("-c");
                psi.ArgumentList.Add(command);
                break;
        }
        return psi;
    }

    /// <summary>PowerShell reads the command from -EncodedCommand (UTF-16LE base64), which sidesteps
    /// every quoting rule. The wrapper makes the output UTF-8 and turns failures into an exit code:
    /// a failing native command's own code, otherwise 1 when the last statement failed.</summary>
    internal static IEnumerable<string> PowerShellArguments(string command)
    {
        var script = new StringBuilder()
            .AppendLine("$ProgressPreference = 'SilentlyContinue'")
            .AppendLine("try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8; $OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }")
            .AppendLine(command)
            .AppendLine("$__dshOk = $?")
            .AppendLine("if ($LASTEXITCODE -is [int] -and $LASTEXITCODE -ne 0) { exit $LASTEXITCODE }")
            .AppendLine("if (-not $__dshOk) { exit 1 }")
            .AppendLine("exit 0")
            .ToString();
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        string[] common = ["-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass"];
        // The Windows command line tops out at 32,767 characters; long scripts go through a file.
        if (encoded.Length < 28_000) return [.. common, "-EncodedCommand", encoded];
        var file = Path.Combine(Path.GetTempPath(), $"dsh-cmd-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(file, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return [.. common, "-File", file];
    }

    /// <summary>The default interactive shell for the terminal panel: a full command line.</summary>
    public static string DefaultTerminalCommandLine(string? preference = null)
    {
        if (!OperatingSystem.IsWindows()) return Resolve(preference).Executable;
        var trimmed = (preference ?? "").Trim();
        if (trimmed.Length > 0 && !string.Equals(trimmed, Auto, StringComparison.OrdinalIgnoreCase))
        {
            // A known keyword, or a full command line typed by the user.
            switch (trimmed.ToLowerInvariant())
            {
                case PowerShellPreference: return Quote(PowerShellShell().Executable) + " -NoLogo";
                case WindowsPowerShellPreference: return Quote(WindowsPowerShell().Executable) + " -NoLogo";
                case CmdPreference: return Quote(CmdShell().Executable);
                case BashPreference:
                    return GitBash() is { } bash ? Quote(bash.Executable) + " --login -i" : Quote(PowerShellShell().Executable) + " -NoLogo";
                default: return trimmed;
            }
        }
        return Quote(PowerShellShell().Executable) + " -NoLogo";
    }

    private static string Quote(string path) => path.Contains(' ') ? $"\"{path}\"" : path;
}
