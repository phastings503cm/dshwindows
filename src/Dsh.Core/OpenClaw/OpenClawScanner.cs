namespace Dsh.Core;

// MARK: - Finding OpenClaw
//
// OpenClaw (earlier Clawdbot, then Moltbot) keeps its state in one folder — usually ~/.openclaw — holding
// openclaw.json (JSON5), the agent workspace (skills/, MEMORY.md, memory/, …), per-agent auth profiles and
// so on. Where that folder is varies: another profile (~/.openclaw-work), another user's home on a server,
// a Docker bind mount, an OPENCLAW_STATE_DIR someone set. The scanner asks the machine rather than
// assuming: environment variables first, then the usual places, then a bounded search for the config file.

/// <summary>One OpenClaw state folder found on a machine.</summary>
public sealed record OpenClawInstall
{
    /// <summary>The state folder (e.g. /home/sam/.openclaw).</summary>
    public string StateDir { get; init; } = "";
    /// <summary>The config file inside it, when there is one.</summary>
    public string? ConfigPath { get; init; }
    /// <summary>"openclaw", "clawdbot" or "moltbot" — the name the folder or config carries.</summary>
    public string Flavor { get; init; } = "openclaw";
    /// <summary>How it was found ("~/.openclaw", "OPENCLAW_STATE_DIR", "search").</summary>
    public string FoundBy { get; init; } = "";

    public string Title => $"{StateDir}" + (Flavor == "openclaw" ? "" : $" ({Flavor})");
}

/// <summary>Where the scanner looks besides the user's own home. Tests narrow these so a run never wanders
/// through the machine's real folders.</summary>
public sealed record OpenClawScanOptions
{
    /// <summary>Folders holding other users' homes (remote machines only).</summary>
    public IReadOnlyList<string> HomeParents { get; init; } = ["/home", "/Users"];
    /// <summary>Places a service install or container tends to keep its data (remote machines only).</summary>
    public IReadOnlyList<string> KnownFolders { get; init; } = OpenClawScanner.DefaultKnownFolders;
    /// <summary>Roots searched for the config file (remote machines only; this PC searches its home).</summary>
    public IReadOnlyList<string> SearchRoots { get; init; } = OpenClawScanner.DefaultSearchRoots;
    public int SearchDepth { get; init; } = 6;
}

public static class OpenClawScanner
{
    public static readonly IReadOnlyList<string> ConfigNames = ["openclaw.json", "clawdbot.json", "moltbot.json", "openclaw.json5"];

    private static readonly string[] HomeNames =
    [
        ".openclaw", ".clawdbot", ".moltbot", "openclaw", "clawdbot", "moltbot", "clawd", ".config/openclaw",
        ".local/share/openclaw", "OpenClaw",
    ];

    internal static readonly string[] DefaultKnownFolders =
    [
        "/opt/openclaw", "/opt/clawdbot", "/opt/moltbot", "/srv/openclaw", "/srv/clawdbot", "/var/lib/openclaw",
        "/var/lib/clawdbot", "/data/.openclaw", "/data/openclaw", "/app/.openclaw", "/home/node/.openclaw", "/root/.openclaw",
        "/usr/local/openclaw",
    ];

    internal static readonly string[] DefaultSearchRoots = ["/home", "/root", "/opt", "/srv", "/var/lib", "/data", "/mnt", "/usr/local", "/Users"];

    /// <summary>Look for OpenClaw on <paramref name="source"/>. Best match first: an explicit environment
    /// variable, then the home folder, then anywhere else.</summary>
    public static async Task<IReadOnlyList<OpenClawInstall>> FindAsync(IFileSource source, IProgress<string>? progress = null,
                                                                        CancellationToken cancellationToken = default,
                                                                        OpenClawScanOptions? options = null)
    {
        options ??= new OpenClawScanOptions();
        var candidates = new List<(string Path, string FoundBy)>();
        void Add(string? path, string foundBy)
        {
            if (!string.IsNullOrWhiteSpace(path)) candidates.Add((path.Trim().TrimEnd('/', '\\') is { Length: > 0 } t ? t : path.Trim(), foundBy));
        }

        progress?.Report($"Looking at {source.Label}…");
        var home = await source.HomeAsync(cancellationToken).ConfigureAwait(false);
        // Looking-around steps that finished (other homes, folders judged, the search), and the last one that failed: if none
        // finished, the machine went away after the first look and "not found" would be the wrong thing to say.
        var finished = 0;
        Exception? lastFailure = null;

        // 1. Where the user said it is.
        foreach (var variable in new[] { "OPENCLAW_STATE_DIR", "CLAWDBOT_STATE_DIR", "MOLTBOT_STATE_DIR" })
            Add(await source.EnvironmentAsync(variable, cancellationToken).ConfigureAwait(false), variable);
        foreach (var variable in new[] { "OPENCLAW_CONFIG_PATH", "CLAWDBOT_CONFIG_PATH", "MOLTBOT_CONFIG_PATH" })
        {
            if (await source.EnvironmentAsync(variable, cancellationToken).ConfigureAwait(false) is { } config)
                Add(source.Parent(config), variable);
        }
        if (await source.EnvironmentAsync("OPENCLAW_HOME", cancellationToken).ConfigureAwait(false) is { } openClawHome)
            Add(source.Combine(openClawHome, ".openclaw"), "OPENCLAW_HOME");

        // 2. The usual names in this user's home, and profile folders (.openclaw-work).
        await AddHomeAsync(source, home, "~", Add, cancellationToken).ConfigureAwait(false);

        // 3. Other users' homes on the same machine.
        if (source.IsRemote)
        {
            foreach (var parent in options.HomeParents)
            {
                progress?.Report($"Checking {parent}…");
                try
                {
                    var homes = await source.ListAsync(parent, cancellationToken).ConfigureAwait(false);
                    foreach (var entry in homes?.Where(e => e.IsDirectory).Take(60) ?? [])
                    {
                        if (entry.FullPath == home) continue;
                        await AddHomeAsync(source, entry.FullPath, entry.FullPath, Add, cancellationToken).ConfigureAwait(false);
                    }
                    finished++;
                }
                catch (Exception ex) when (Recoverable(ex))
                {
                    lastFailure = ex;
                    progress?.Report($"Couldn't look in {parent}: {ex.Message}");
                }
            }
        }

        // 4. System-wide guesses (a service install, a container's data folder).
        if (source.IsRemote)
        {
            foreach (var folder in options.KnownFolders) Add(folder, "known location");
        }

        // Judge the candidates so far; keep the ones that really are an OpenClaw state folder.
        var installs = new List<OpenClawInstall>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var judged = 0;
        async Task JudgeAsync()
        {
            while (judged < candidates.Count)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (path, foundBy) = candidates[judged++];
                if (!seen.Add(path)) continue;
                try
                {
                    if (await InspectAsync(source, path, foundBy, cancellationToken).ConfigureAwait(false) is { } install)
                    {
                        progress?.Report($"Found OpenClaw in {install.StateDir}");
                        installs.Add(install);
                    }
                    finished++;
                }
                catch (Exception ex) when (Recoverable(ex))
                {
                    lastFailure = ex;
                    progress?.Report($"Couldn't look at {path}: {ex.Message}"); // one bad folder must not lose the installs already found
                }
            }
        }
        await JudgeAsync().ConfigureAwait(false);

        // 5. Let the machine look for the config file itself (another profile, a service install, an odd place). On a
        // remote machine it gives up on its own after a minute, and whatever was found above is kept if it fails.
        progress?.Report("Searching for OpenClaw's config file…");
        var roots = source.IsRemote ? options.SearchRoots.Prepend(home).Distinct().ToList() : [home];
        try
        {
            foreach (var found in await source.FindAsync(roots, ConfigNames, options.SearchDepth, cancellationToken).ConfigureAwait(false))
                Add(source.Parent(found), "search");
            finished++;
        }
        catch (Exception ex) when (Recoverable(ex))
        {
            lastFailure = ex;
            progress?.Report($"The search didn't finish: {ex.Message}");
        }
        await JudgeAsync().ConfigureAwait(false);
        // Every step failed: the link dropped after it was made. Say that, not "no OpenClaw here".
        if (installs.Count == 0 && finished == 0 && lastFailure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(lastFailure).Throw();
        return installs;
    }

    /// <summary>Failures of one step of a scan that should not end it: a folder that can't be read, a command that timed out, a link that dropped.</summary>
    private static bool Recoverable(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or TimeoutException or System.Net.Sockets.SocketException or ObjectDisposedException;

    /// <summary>Check one folder the user pointed at (its state folder, or a workspace inside it).</summary>
    public static async Task<OpenClawInstall?> InspectAsync(IFileSource source, string path, string foundBy,
                                                            CancellationToken cancellationToken = default)
    {
        var entries = await source.ListAsync(path, cancellationToken).ConfigureAwait(false);
        if (entries is null) return null;
        // (Two entries that differ only in case — "Skills" and "skills" on a case-sensitive disk — must not end the scan.)
        var names = new Dictionary<string, FileEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries) names.TryAdd(entry.Name, entry);
        var config = ConfigNames.Select(n => names.GetValueOrDefault(n)).FirstOrDefault(e => e is { IsDirectory: false });
        // Without a config file, a state folder still shows itself by what it holds.
        var looksLikeState = names.ContainsKey("agents") && (names.ContainsKey("workspace") || names.ContainsKey("skills") || names.ContainsKey("credentials"));
        // A bare workspace (MEMORY.md, skills/, SOUL.md…) can be imported too.
        var looksLikeWorkspace = names.ContainsKey("skills") && (names.ContainsKey("MEMORY.md") || names.ContainsKey("SOUL.md") || names.ContainsKey("AGENTS.md"));
        if (config is null && !looksLikeState && !looksLikeWorkspace) return null;
        var lower = (config?.Name ?? path).ToLowerInvariant();
        var flavor = lower.Contains("clawdbot") || path.Contains("clawd", StringComparison.OrdinalIgnoreCase) ? "clawdbot"
            : lower.Contains("moltbot") ? "moltbot" : "openclaw";
        return new OpenClawInstall { StateDir = path, ConfigPath = config?.FullPath, Flavor = flavor, FoundBy = foundBy };
    }

    private static async Task AddHomeAsync(IFileSource source, string home, string label, Action<string?, string> add,
                                           CancellationToken cancellationToken)
    {
        foreach (var name in HomeNames) add(source.Combine(home, name), label + "/" + name);
        // Profile folders: ~/.openclaw-work, ~/.clawdbot-dev …
        var entries = await source.ListAsync(home, cancellationToken).ConfigureAwait(false);
        foreach (var entry in entries ?? [])
        {
            if (!entry.IsDirectory) continue;
            var lower = entry.Name.ToLowerInvariant();
            if (lower.StartsWith(".openclaw-", StringComparison.Ordinal) || lower.StartsWith(".clawdbot-", StringComparison.Ordinal)
                || lower.StartsWith(".moltbot-", StringComparison.Ordinal))
                add(entry.FullPath, label + "/" + entry.Name);
        }
    }
}
