using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Vision.Core;

// MARK: - Finding the claude CLI, and how to start it

/// <summary>Where Vision looks for the claude CLI. <see cref="ForThisMachine"/> is the real list; tests build their own.</summary>
public sealed record ClaudeCodeSearch
{
    /// <summary>A path the person picked in Settings › Claude Code; tried first.</summary>
    public string? ExplicitPath { get; init; }
    /// <summary>Folders searched for <see cref="FileNames"/>, in order (PATH — what the person's own terminal runs).</summary>
    public IReadOnlyList<string> PathDirectories { get; init; } = [];
    /// <summary>What the CLI is called in a folder, most preferred first (claude.exe before the npm shim claude.cmd).</summary>
    public IReadOnlyList<string> FileNames { get; init; } = ["claude"];
    /// <summary>Full paths where the official installers put it.</summary>
    public IReadOnlyList<string> KnownLocations { get; init; } = [];
    /// <summary>Folders holding the copies the Claude desktop app keeps (one folder per version, newest wins); a last resort,
    /// since the desktop app signs its copy in for itself, not for the command line.</summary>
    public IReadOnlyList<string> BundledRoots { get; init; } = [];
    public Func<string, bool> FileExists { get; init; } = File.Exists;
    public Func<string, IEnumerable<string>> Directories { get; init; } = SafeDirectories;
    public bool IgnoreCase { get; init; } = true;

    private static IEnumerable<string> SafeDirectories(string path)
    {
        try
        {
            return Directory.Exists(path) ? Directory.GetDirectories(path) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>The real search. On Windows PATH is read three ways — this process's copy and the user and machine values
    /// Windows has now (an installer that ran after Vision started only changed the latter) — and the places the native
    /// installer (%USERPROFILE%\.local\bin), the old local install (%USERPROFILE%\.claude\local) and npm (%APPDATA%\npm) use.</summary>
    public static ClaudeCodeSearch ForThisMachine(string? explicitPath = null)
    {
        static IEnumerable<string> Split(string? path) =>
            (path ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(d => d.Trim('"')).Where(d => d.Length > 0);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows())
        {
            var dirs = Split(Environment.GetEnvironmentVariable("PATH"))
                .Concat(Split(Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User)))
                .Concat(Split(Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.Machine)))
                .Select(Environment.ExpandEnvironmentVariables)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return new ClaudeCodeSearch
            {
                ExplicitPath = explicitPath,
                PathDirectories = dirs,
                FileNames = ["claude.exe", "claude.cmd"],
                KnownLocations =
                [
                    Path.Combine(home, ".local", "bin", "claude.exe"),
                    Path.Combine(home, ".claude", "local", "claude.exe"),
                    Path.Combine(home, ".claude", "local", "claude.cmd"),
                    Path.Combine(appData, "npm", "claude.cmd"),
                    Path.Combine(local, "Programs", "claude", "claude.exe"),
                ],
                BundledRoots = [Path.Combine(appData, "Claude", "claude-code"), Path.Combine(local, "AnthropicClaude", "claude-code")],
            };
        }
        return new ClaudeCodeSearch
        {
            ExplicitPath = explicitPath,
            PathDirectories = Split(Environment.GetEnvironmentVariable("PATH")).Distinct(StringComparer.Ordinal).ToList(),
            FileNames = ["claude"],
            KnownLocations =
            [
                Path.Combine(home, ".local", "bin", "claude"),
                Path.Combine(home, ".claude", "local", "claude"),
                "/opt/homebrew/bin/claude",
                "/usr/local/bin/claude",
            ],
            BundledRoots = OperatingSystem.IsMacOS() ? [Path.Combine(home, "Library", "Application Support", "Claude", "claude-code")] : [],
            IgnoreCase = false,
        };
    }
}

/// <summary>How to start the CLI: the program, what goes before Claude Code's own arguments, and — inside WSL — which
/// distribution. <see cref="Display"/> is what Settings shows.</summary>
public sealed record ClaudeCodeLaunch(string Program, IReadOnlyList<string> Prefix, string Display)
{
    /// <summary>The CLI runs inside WSL (Program is wsl.exe).</summary>
    public bool Wsl { get; init; }
    public string? Distro { get; init; }

    /// <summary>Run claude itself (claude.exe, or the macOS/Linux binary).</summary>
    public static ClaudeCodeLaunch Direct(string path) => new(path, [], path);

    /// <summary>Run claude inside WSL through a login shell, so the PATH its installer set up applies. wsl.exe gets
    /// <c>--exec</c>: with a plain <c>--</c> it hands the rest of its command line to the default shell's <c>-c</c>, which
    /// would expand the script's "$@" (to nothing) and re-split every argument.</summary>
    public static ClaudeCodeLaunch InWsl(string? distro) =>
        new("wsl.exe", [], distro is null ? "claude in WSL" : $"claude in WSL ({distro})") { Wsl = true, Distro = distro };

    /// <summary>The process to start for <paramref name="args"/> in <paramref name="workingDirectory"/>.</summary>
    public ProcessStartInfo StartInfo(IEnumerable<string> args, string? workingDirectory)
    {
        var info = new ProcessStartInfo(Program)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = TextUtil.Utf8NoBom,
            StandardErrorEncoding = TextUtil.Utf8NoBom,
        };
        if (Wsl)
        {
            if (Distro is { Length: > 0 }) { info.ArgumentList.Add("-d"); info.ArgumentList.Add(Distro); }
            if (workingDirectory is { Length: > 0 }) { info.ArgumentList.Add("--cd"); info.ArgumentList.Add(workingDirectory); }
            info.ArgumentList.Add("--exec");
            info.ArgumentList.Add("bash");
            info.ArgumentList.Add("-lc");
            info.ArgumentList.Add("exec claude \"$@\"");
            info.ArgumentList.Add("claude");
        }
        else
        {
            if (workingDirectory is { Length: > 0 }) info.WorkingDirectory = workingDirectory;
            foreach (var p in Prefix) info.ArgumentList.Add(p);
        }
        foreach (var a in args) info.ArgumentList.Add(a);
        ClaudeCodeEnvironment.Prepare(info.Environment);
        return info;
    }
}

/// <summary>The environment the CLI starts with: the person's own, minus the variables that mark a parent Claude Code
/// session (Vision itself started from a Claude Code terminal would otherwise make the child think it is nested).
/// Credentials pass through untouched and unread — the CLI decides how it signs in.</summary>
public static class ClaudeCodeEnvironment
{
    public static readonly IReadOnlyList<string> ParentSessionVariables =
    [
        "CLAUDECODE", "CLAUDE_CODE_ENTRYPOINT", "CLAUDE_CODE_SESSION_ID", "CLAUDE_CODE_HOST_SESSION_ID", "CLAUDE_CODE_CHILD_SESSION",
        "CLAUDE_CODE_SSE_PORT", "CLAUDE_CODE_EXECPATH", "CLAUDE_AGENT_SDK_VERSION", "CLAUDE_CODE_MESSAGING_SOCKET",
        "CLAUDE_CODE_MESSAGING_TOKEN", "CLAUDE_CODE_SDK_HAS_HOST_AUTH_REFRESH", "CLAUDE_PID",
        // A parent's bare mode, which never reads the plan's sign-in.
        "CLAUDE_CODE_SIMPLE",
    ];

    /// <summary>Tests: variables set for every CLI Vision starts (a stand-in API address, an isolated config folder).</summary>
    public static IReadOnlyDictionary<string, string?>? OverridesForTesting { get; set; }

    public static void Prepare(IDictionary<string, string?> environment)
    {
        foreach (var name in ParentSessionVariables) environment.Remove(name);
        if (OverridesForTesting is { } overrides)
            foreach (var (name, value) in overrides)
            {
                if (value is null) environment.Remove(name);
                else environment[name] = value;
            }
    }
}

// MARK: - Whether it's there and signed in

/// <summary>What `claude auth status --json` said (no credential is in it — only whether, and how, the CLI is signed in).</summary>
public sealed record ClaudeCodeAuth(bool LoggedIn, string? Method, string? Provider, string? Email, string? Plan)
{
    /// <summary>"your Claude subscription", "an Anthropic API key", "Amazon Bedrock" — how the CLI pays for what it does.</summary>
    public string Describe()
    {
        var method = (Method ?? "").ToLowerInvariant();
        var provider = (Provider ?? "").ToLowerInvariant();
        if (provider is "bedrock") return "Amazon Bedrock (the CLI's own AWS settings)";
        if (provider is "vertex") return "Google Vertex AI (the CLI's own settings)";
        if (provider is "foundry") return "Microsoft Foundry (the CLI's own settings)";
        if (method.Contains("api", StringComparison.Ordinal) && method.Contains("key", StringComparison.Ordinal))
            return "an Anthropic API key — billed to that key, not your subscription";
        var plan = Plan is { Length: > 0 } p ? $"your Claude {char.ToUpperInvariant(p[0])}{p[1..]} plan" : "your Claude subscription";
        return plan;
    }
}

/// <summary>Whether Claude Code can be used on this computer.</summary>
public abstract record ClaudeCodeStatus
{
    public abstract string Summary { get; }

    public sealed record Missing : ClaudeCodeStatus
    {
        public override string Summary => "Claude Code isn't installed on this computer (Vision looked on PATH and where its installers put it).";
    }

    public sealed record Broken(string Path, string Problem) : ClaudeCodeStatus
    {
        public override string Summary => $"Claude Code at {Path} didn't start properly ({Problem}).";
    }

    public sealed record SignedOut(string Version, ClaudeCodeLaunch Launch) : ClaudeCodeStatus
    {
        public override string Summary => $"Claude Code {Version} is installed but not signed in. Sign in once in a terminal: claude auth login";
    }

    /// <summary>Installed and signed in (<paramref name="Auth"/> null: an older CLI that can't say — the first message will).</summary>
    public sealed record Ready(string Version, ClaudeCodeLaunch Launch, ClaudeCodeAuth? Auth) : ClaudeCodeStatus
    {
        public override string Summary => Auth is { } auth
            ? $"Claude Code {Version} is ready — signed in{(auth.Email is { Length: > 0 } email ? $" as {email}" : "")}, using {auth.Describe()}."
            : $"Claude Code {Version} is installed. (This version can't say whether it's signed in — your first message will.)";
    }
}

/// <summary>The output of one short CLI command (--version, auth status).</summary>
public sealed record ClaudeCodeCommandResult(int ExitCode, string Stdout, string Stderr, bool TimedOut);

/// <summary>Runs a short CLI command; tests replace it.</summary>
public delegate Task<ClaudeCodeCommandResult> ClaudeCodeCommandRunner(ClaudeCodeLaunch launch, IReadOnlyList<string> args, TimeSpan timeout,
                                                                      CancellationToken cancellationToken);

public static partial class ClaudeCodeCli
{
    /// <summary>The official installer, as one line for a terminal (PowerShell on Windows).</summary>
    public static string InstallCommand => OperatingSystem.IsWindows() ? "irm https://claude.ai/install.ps1 | iex" : "curl -fsSL https://claude.ai/install.sh | bash";

    /// <summary>The npm route, for a computer that already has Node.js.</summary>
    public const string NpmInstallCommand = "npm install -g @anthropic-ai/claude-code";

    /// <summary>Signing in once: Claude Code opens the browser to sign in to the Claude account.</summary>
    public const string SignInCommand = "claude auth login";

    /// <summary>The steps Settings and the wizard show when it's missing.</summary>
    public static readonly IReadOnlyList<string> InstallSteps = OperatingSystem.IsWindows()
        ?
        [
            "Open PowerShell and run:  irm https://claude.ai/install.ps1 | iex",
            "Or, with Node.js 18+ installed:  npm install -g @anthropic-ai/claude-code",
            "Then sign in once:  claude auth login  (choose your Claude account — Pro or Max)",
            "Come back here and press Check again.",
        ]
        :
        [
            "Open Terminal and run:  curl -fsSL https://claude.ai/install.sh | bash",
            "Or, with Node.js 18+ installed:  npm install -g @anthropic-ai/claude-code",
            "Then sign in once:  claude auth login  (choose your Claude account — Pro or Max)",
            "Come back here and press Check again.",
        ];

    /// <summary>Every existing claude program, most preferred first: the explicit path, then PATH, then the installers'
    /// folders, then the desktop app's copies (newest version first).</summary>
    public static IReadOnlyList<string> Candidates(ClaudeCodeSearch search)
    {
        var comparer = search.IgnoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var seen = new HashSet<string>(comparer);
        var found = new List<string>();
        void Consider(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            string full;
            try
            {
                full = Path.GetFullPath(path);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return;
            }
            if (seen.Add(full) && search.FileExists(full)) found.Add(full);
        }
        Consider(search.ExplicitPath);
        foreach (var dir in search.PathDirectories)
        {
            foreach (var name in search.FileNames)
            {
                try
                {
                    Consider(Path.Combine(dir, name));
                }
                catch (ArgumentException)
                {
                    // A malformed PATH entry.
                }
            }
        }
        foreach (var known in search.KnownLocations) Consider(known);
        foreach (var root in search.BundledRoots)
        {
            var versions = search.Directories(root)
                .Select(d => (Dir: d, Version: ParseVersion(Path.GetFileName(d))))
                .Where(v => v.Version is not null)
                .OrderByDescending(v => v.Version);
            foreach (var (dir, _) in versions)
            {
                foreach (var build in search.Directories(dir))
                {
                    Consider(Path.Combine(build, "claude.app", "Contents", "MacOS", "claude"));
                    Consider(Path.Combine(build, "claude.exe"));
                    Consider(Path.Combine(build, "claude"));
                }
            }
        }
        return found;
    }

    /// <summary>How to start the program at <paramref name="path"/>. An npm shim (claude.cmd) is started as `node cli.js`
    /// when the package sits next to it — cmd.exe would mangle the arguments; otherwise through cmd.exe /c.</summary>
    public static ClaudeCodeLaunch LaunchFor(string path, Func<string, bool>? fileExists = null)
    {
        fileExists ??= File.Exists;
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".cmd" or ".bat")
        {
            var dir = Path.GetDirectoryName(path) ?? "";
            var script = Path.Combine(dir, "node_modules", "@anthropic-ai", "claude-code", "cli.js");
            if (fileExists(script))
            {
                var node = Path.Combine(dir, "node.exe");
                return new ClaudeCodeLaunch(fileExists(node) ? node : "node", [script], path);
            }
            return new ClaudeCodeLaunch(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe", ["/d", "/s", "/c", path], path);
        }
        return ClaudeCodeLaunch.Direct(path);
    }

    /// <summary>"2.1.299 (Claude Code)" → "2.1.299"; null when there's no version in it.</summary>
    public static string? ParseVersionText(string text) => VersionPattern().Match(text) is { Success: true } m ? m.Groups[1].Value : null;

    public static Version? ParseVersion(string text) =>
        ParseVersionText(text) is { } v && Version.TryParse(v, out var parsed) ? parsed : null;

    [GeneratedRegex(@"(?<![\d.])(\d+\.\d+\.\d+)(?![\d])")]
    private static partial Regex VersionPattern();

    /// <summary>What `claude auth status --json` printed, or null when it isn't that JSON (an older CLI).</summary>
    public static ClaudeCodeAuth? ParseAuth(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        try
        {
            using var doc = JsonDocument.Parse(text[start..(end + 1)]);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("loggedIn", out var logged)) return null;
            static string? Str(JsonElement e, params string[] names)
            {
                foreach (var n in names)
                    if (e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s) return s;
                return null;
            }
            var account = root.TryGetProperty("account", out var a) && a.ValueKind == JsonValueKind.Object ? a : root;
            return new ClaudeCodeAuth(logged.ValueKind == JsonValueKind.True,
                Str(root, "authMethod", "method"), Str(root, "apiProvider", "provider"),
                Str(account, "email", "emailAddress") ?? Str(root, "email"),
                Str(account, "subscriptionType", "plan", "subscription") ?? Str(root, "subscriptionType"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Find the CLI and check it: the first candidate that runs wins; its sign-in is checked with `auth status`.
    /// <paramref name="wsl"/> checks the CLI inside WSL instead.</summary>
    public static async Task<ClaudeCodeStatus> CheckAsync(ClaudeCodeSearch? search = null, ClaudeCodeCommandRunner? runner = null,
                                                          ClaudeCodeLaunch? wsl = null, CancellationToken cancellationToken = default)
    {
        runner ??= RunAsync;
        var launches = wsl is not null ? [wsl] : Candidates(search ?? ClaudeCodeSearch.ForThisMachine()).Select(p => LaunchFor(p)).ToList();
        ClaudeCodeStatus best = new ClaudeCodeStatus.Missing();
        foreach (var launch in launches)
        {
            ClaudeCodeCommandResult version;
            try
            {
                version = await runner(launch, ["--version"], TimeSpan.FromSeconds(45), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or Win32Exception or InvalidOperationException)
            {
                best = new ClaudeCodeStatus.Broken(launch.Display, ex.Message);
                continue;
            }
            if (ParseVersionText(version.Stdout + "\n" + version.Stderr) is not { } v)
            {
                best = new ClaudeCodeStatus.Broken(launch.Display, version.TimedOut ? "it didn't answer"
                    : wsl is not null && version.ExitCode != 0 ? "claude isn't installed in WSL" : Describe(version));
                continue;
            }
            ClaudeCodeAuth? auth = null;
            try
            {
                var status = await runner(launch, ["auth", "status", "--json"], TimeSpan.FromSeconds(45), cancellationToken).ConfigureAwait(false);
                auth = ParseAuth(status.Stdout);
            }
            catch (Exception ex) when (ex is IOException or Win32Exception or InvalidOperationException)
            {
                // An older CLI, or a hiccup: the first turn says whether it's signed in.
            }
            return auth is { LoggedIn: false } ? new ClaudeCodeStatus.SignedOut(v, launch) : new ClaudeCodeStatus.Ready(v, launch, auth);
        }
        return best;
    }

    private static string Describe(ClaudeCodeCommandResult result)
    {
        var text = (result.Stderr.Trim().Length > 0 ? result.Stderr : result.Stdout).Trim();
        return text.Length == 0 ? $"exit code {result.ExitCode}" : TextUtil.Prefix(text.Split('\n')[0].Trim(), 160);
    }

    /// <summary>Run a short command and collect what it printed (killed after <paramref name="timeout"/>).</summary>
    public static async Task<ClaudeCodeCommandResult> RunAsync(ClaudeCodeLaunch launch, IReadOnlyList<string> args, TimeSpan timeout,
                                                               CancellationToken cancellationToken = default)
    {
        var info = launch.StartInfo(args, workingDirectory: null);
        info.RedirectStandardInput = false;
        using var process = new Process { StartInfo = info };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (stdout) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (stderr) stderr.AppendLine(e.Data); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = !cancellationToken.IsCancellationRequested;
            KillTree(process);
            if (!timedOut) throw;
        }
        lock (stdout) lock (stderr)
            return new ClaudeCodeCommandResult(process.HasExited ? process.ExitCode : -1, stdout.ToString(), stderr.ToString(), timedOut);
    }

    internal static void KillTree(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // Already gone.
        }
    }

    /// <summary>The WSL distributions installed (`wsl.exe -l -q`), default first; empty when WSL isn't there.</summary>
    public static async Task<IReadOnlyList<string>> WslDistrosAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()) return [];
        try
        {
            var info = new ProcessStartInfo("wsl.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.Unicode };
            info.ArgumentList.Add("-l");
            info.ArgumentList.Add("-q");
            using var process = Process.Start(info);
            if (process is null) return [];
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(TimeSpan.FromSeconds(10)); // a WSL that is half installed can sit there
            try
            {
                var text = await process.StandardOutput.ReadToEndAsync(limit.Token).ConfigureAwait(false);
                await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
                return process.ExitCode != 0 ? [] : text.Replace("\0", "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                KillTree(process);
                return [];
            }
        }
        catch (Exception ex) when (ex is IOException or Win32Exception or InvalidOperationException)
        {
            return [];
        }
    }

    /// <summary>A path Claude Code reported from inside WSL (/mnt/c/Users/x) as Windows sees it (C:\Users\x).</summary>
    public static string FromWslPath(string path)
    {
        var m = WslMount().Match(path);
        if (!m.Success) return path;
        return char.ToUpperInvariant(m.Groups[1].Value[0]) + ":\\" + m.Groups[2].Value.Replace('/', '\\');
    }

    [GeneratedRegex(@"^/mnt/([a-zA-Z])/?(.*)$")]
    private static partial Regex WslMount();

    /// <summary>A Windows path (C:\Users\x) as Claude Code inside WSL reaches it (/mnt/c/Users/x); anything else as it is.</summary>
    public static string ToWslPath(string path)
    {
        var m = WindowsDrivePath().Match(path);
        if (!m.Success) return path;
        var rest = m.Groups[2].Value.Replace('\\', '/').TrimStart('/');
        return $"/mnt/{char.ToLowerInvariant(m.Groups[1].Value[0])}" + (rest.Length > 0 ? "/" + rest : "");
    }

    [GeneratedRegex(@"^([a-zA-Z]):([\\/].*)?$")]
    private static partial Regex WindowsDrivePath();
}
