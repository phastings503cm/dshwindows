using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;

namespace Dsh.Core;

// MARK: - Import

public sealed record ImportCandidate
{
    /// <summary>The full path of the skill's file.</summary>
    public required string Id { get; init; }
    public required Skill Skill { get; init; }
    /// <summary>A project-knowledge file (CLAUDE.md, AGENTS.md, .cursorrules) that becomes an always-on rule.</summary>
    public bool IsInstructionFile { get; init; }
    public int FileCount { get; init; }
    public long TotalBytes { get; init; }
    /// <summary>Bundled scripts — listed so the user knows; never run by importing.</summary>
    public bool HasScripts { get; init; }
    public IReadOnlyList<string> Issues { get; init; } = [];

    public string Name => Skill.Name;
    public string Description => Skill.Description;
    public SkillKind Kind => Skill.Kind;
    public SkillOrigin Origin => Skill.Origin;
}

/// <param name="Source">The folder, file or https link that was scanned.</param>
/// <param name="Candidates">What can be imported.</param>
/// <param name="Notes">Things found but not importable (e.g. Claude subagent definitions).</param>
/// <param name="TemporaryRoot">A temporary download/extraction/clone to delete when done
/// (<see cref="SkillImporter.Dispose"/>).</param>
public sealed record ImportPlan(string Source, IReadOnlyList<ImportCandidate> Candidates, IReadOnlyList<string> Notes,
                                string? TemporaryRoot);

public sealed record ImportResult
{
    /// <summary>SKILL.md files written to the active store.</summary>
    public IReadOnlyList<string> Imported { get; init; } = [];
    public IReadOnlyList<SkillDraft> Drafts { get; init; } = [];
    /// <summary>Skill name → why it wasn't imported.</summary>
    public IReadOnlyDictionary<string, string> Skipped { get; init; } = new Dictionary<string, string>();
}

public static class SkillImporter
{
    internal static readonly IReadOnlySet<string> ScriptExtensions = new HashSet<string>(StringComparer.Ordinal)
    {
        "sh", "bash", "zsh", "py", "rb", "pl", "js", "mjs", "ts", "command", "swift", "lua", "php", "ps1", "psm1",
        "bat", "cmd", "vbs", "exe", "gd",
    };

    internal static readonly IReadOnlySet<string> SkipDirectories = new HashSet<string>(StringComparer.Ordinal)
    {
        ".git", "node_modules", ".build", ".godot", "worktrees", "__pycache__", ".venv", "venv", "Pods", "DerivedData",
    };

    internal static readonly IReadOnlySet<string> InstructionFileNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "CLAUDE.md", "AGENTS.md", "QWEN.md", ".cursorrules", "CLAUDE.local.md",
    };

    /// <summary>Archive limits: an import is skills, not a whole repository.</summary>
    internal const int MaxZipEntries = 5_000;
    internal const long MaxZipBytes = 150_000_000;
    internal const long MaxDownloadBytes = 60_000_000;
    internal static readonly TimeSpan DownloadTimeout = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan CloneTimeout = TimeSpan.FromSeconds(90);

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    // MARK: Scanning

    /// <summary>Scan a folder, a .zip, or a single .md / .mdc / .cursorrules file.</summary>
    public static ImportPlan Scan(string source)
    {
        if (Directory.Exists(source)) return ScanFolder(source, temporaryRoot: null);
        if (!File.Exists(source)) throw SkillException.NotFound($"{SkillFiles.LastComponent(source)} doesn't exist.");
        if (Extension(source) == "zip")
        {
            var dir = Unzip(source);
            try
            {
                return ScanFolder(dir, temporaryRoot: dir, displaySource: source);
            }
            catch
            {
                SkillFiles.DeleteQuietly(dir);
                throw;
            }
        }
        return ScanFile(source);
    }

    // MARK: Remote

    private static readonly SocketsHttpHandler SharedHandler = new()
    {
        AutomaticDecompression = DecompressionMethods.All,
        ConnectTimeout = TimeSpan.FromSeconds(15),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    };

    /// <summary>The handler remote imports download with; null uses a shared one. Tests put a fake here
    /// so they never touch the network.</summary>
    public static HttpMessageHandler? HttpHandler { get; set; }

    /// <summary>Fetch from https://…: a .zip, a .md/.mdc file, or a git repository. GitHub repository
    /// links (…/tree/&lt;branch&gt;/&lt;folder&gt; imports just that folder) are downloaded as an
    /// archive, so no git is needed; other hosts are cloned with git.</summary>
    public static async Task<ImportPlan> ScanRemoteAsync(string url, CancellationToken cancellationToken = default)
    {
        var raw = url.Trim();
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || string.IsNullOrEmpty(uri.Host))
            throw SkillException.Invalid("Enter an https:// link to a repository, .zip, or .md file.");
        // Segments are unescaped one at a time, so an escaped "/" can't create new path levels.
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToArray();
        var fileName = segments.Length > 0 ? segments[^1] : "";
        var ext = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
        if (ext is "zip" or "md" or "mdc")
            return await ScanDownloadAsync(new UriBuilder(uri) { Fragment = "" }.Uri, fileName, ext, cancellationToken).ConfigureAwait(false);
        if (uri.Host is "github.com" or "www.github.com")
            return await ScanGitHubAsync(segments, raw, cancellationToken).ConfigureAwait(false);
        return await ScanCloneAsync(uri, raw, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ImportPlan> ScanDownloadAsync(Uri url, string fileName, string ext, CancellationToken cancellationToken)
    {
        var tmp = SkillFiles.StagingDirectory();
        Directory.CreateDirectory(tmp);
        try
        {
            var file = Path.Combine(tmp, SafeFileName(fileName, $"download.{ext}"));
            if (await DownloadAsync(url, file, cancellationToken).ConfigureAwait(false) is { } status)
                throw SkillException.Io($"The server replied {status}.");
            var plan = Scan(file);
            // A zip was extracted to its own folder; the download itself is no longer needed.
            if (plan.TemporaryRoot is not null)
            {
                SkillFiles.DeleteQuietly(tmp);
                return plan with { Source = url.ToString() };
            }
            return plan with { Source = url.ToString(), TemporaryRoot = tmp };
        }
        catch
        {
            SkillFiles.DeleteQuietly(tmp);
            throw;
        }
    }

    /// <summary>github.com/&lt;owner&gt;/&lt;repo&gt;[/tree/&lt;branch&gt;/&lt;subfolder&gt;]: download the
    /// codeload archive, extract it like any zip, descend into the single top-level folder GitHub adds,
    /// then into the subfolder.</summary>
    private static async Task<ImportPlan> ScanGitHubAsync(string[] segments, string raw, CancellationToken cancellationToken)
    {
        if (segments.Length < 2 || !segments.Take(2).All(IsPlainSegment)) throw SkillException.Invalid("That link isn't valid.");
        var owner = segments[0];
        var repo = segments[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? segments[1][..^4] : segments[1];
        string? branch = null;
        string[] subfolder = [];
        if (segments.Length >= 4 && segments[2] == "tree")
        {
            branch = segments[3];
            subfolder = segments[4..];
        }
        if (repo.Length == 0 || subfolder.Any(s => !IsPlainSegment(s) || s.Contains(':')))
            throw SkillException.Invalid("That link isn't valid.");

        var baseUrl = $"https://codeload.github.com/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/zip/";
        string[] archives = branch is null
            ? [baseUrl + "HEAD"]
            : [baseUrl + "refs/heads/" + Uri.EscapeDataString(branch), baseUrl + "refs/tags/" + Uri.EscapeDataString(branch)];

        var tmp = SkillFiles.StagingDirectory();
        Directory.CreateDirectory(tmp);
        string dest;
        try
        {
            var file = Path.Combine(tmp, "repository.zip");
            int? status = null;
            foreach (var archive in archives)
            {
                status = await DownloadAsync(new Uri(archive), file, cancellationToken).ConfigureAwait(false);
                if (status != (int)HttpStatusCode.NotFound) break; // a tree link may name a tag rather than a branch
            }
            if (status == (int)HttpStatusCode.NotFound)
            {
                var at = branch is null ? "" : $" at “{branch}”";
                throw SkillException.Io($"Couldn't fetch the repository: GitHub has no {owner}/{repo}{at}. Check the link — a private repository can't be fetched this way; clone it and import the folder instead.");
            }
            if (status is { } code) throw SkillException.Io($"Couldn't fetch the repository: the server replied {code}.");
            dest = Unzip(file);
        }
        finally
        {
            SkillFiles.DeleteQuietly(tmp);
        }

        try
        {
            var entries = Directory.GetFileSystemEntries(dest);
            var top = entries.Length == 1 && Directory.Exists(entries[0]) ? entries[0] : dest;
            var root = subfolder.Length == 0 ? top : Path.Combine([top, .. subfolder]);
            if (!Directory.Exists(root) || !IsInside(root, dest))
                throw SkillException.NotFound($"The folder “{string.Join("/", subfolder)}” isn't in that repository.");
            return ScanFolder(root, temporaryRoot: dest, displaySource: raw);
        }
        catch
        {
            SkillFiles.DeleteQuietly(dest);
            throw;
        }
    }

    /// <summary>Any other https host: a shallow git clone.</summary>
    private static async Task<ImportPlan> ScanCloneAsync(Uri uri, string raw, CancellationToken cancellationToken)
    {
        var repoPath = uri.AbsolutePath.TrimEnd('/');
        var cloneUrl = uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.UserInfo, UriFormat.UriEscaped) + repoPath;
        var dest = SkillFiles.StagingDirectory();
        try
        {
            await CloneAsync(cloneUrl, branch: null, dest, cancellationToken).ConfigureAwait(false);
            if (!Directory.Exists(dest)) throw SkillException.NotFound("The folder “” isn't in that repository.");
            return ScanFolder(dest, temporaryRoot: dest, displaySource: raw);
        }
        catch
        {
            SkillFiles.DeleteQuietly(dest);
            throw;
        }
    }

    /// <summary>A plain path segment: not empty, not "." or "..", no separators.</summary>
    private static bool IsPlainSegment(string s) =>
        s.Length > 0 && s is not ("." or "..") && s.IndexOfAny(['/', '\\', '\0']) < 0;

    /// <summary>Whether <paramref name="path"/> is <paramref name="root"/> or somewhere below it.</summary>
    private static bool IsInside(string path, string root)
    {
        var prefix = SkillFiles.Normalize(root) + Path.DirectorySeparatorChar;
        return SkillFiles.SamePath(path, root) || SkillFiles.Normalize(path).StartsWith(prefix, PathComparison);
    }

    /// <summary>The last component of a name taken from a link, safe to create in a folder.</summary>
    private static string SafeFileName(string name, string fallback)
    {
        var last = name.Split('/', '\\').LastOrDefault() ?? "";
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(last.Select(c => invalid.Contains(c) || c == ':' ? '_' : c).ToArray()).Trim();
        return cleaned.Length == 0 || cleaned.Trim('.').Length == 0 ? fallback : cleaned;
    }

    /// <summary>GET <paramref name="url"/> into <paramref name="file"/>. Returns null on success, or the
    /// HTTP status of a reply that wasn't 2xx. At most 60 MB; gives up when the server goes quiet for
    /// 30 s.</summary>
    private static async Task<int?> DownloadAsync(Uri url, string file, CancellationToken cancellationToken)
    {
        using var http = new HttpClient(HttpHandler ?? SharedHandler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        using var quiet = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        quiet.CancelAfter(DownloadTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", $"DSH/{AppInfo.Version}");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, quiet.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return (int)response.StatusCode;
            if (response.Content.Headers.ContentLength > MaxDownloadBytes) throw SkillException.TooLarge("That download is too large.");
            var body = await response.Content.ReadAsStreamAsync(quiet.Token).ConfigureAwait(false);
            await using (body.ConfigureAwait(false))
            {
                var output = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None);
                await using (output.ConfigureAwait(false))
                {
                    var buffer = new byte[81_920];
                    long total = 0;
                    while (true)
                    {
                        quiet.CancelAfter(DownloadTimeout);
                        var read = await body.ReadAsync(buffer, quiet.Token).ConfigureAwait(false);
                        if (read == 0) break;
                        total += read;
                        if (total > MaxDownloadBytes) throw SkillException.TooLarge("That download is too large.");
                        await output.WriteAsync(buffer.AsMemory(0, read), quiet.Token).ConfigureAwait(false);
                    }
                }
            }
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw SkillException.Io("The request timed out.");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            // Unreachable host, a connection dropped mid-download, or the temp file couldn't be written.
            throw SkillException.Io($"Couldn't download that link: {ex.Message}", ex);
        }
    }

    /// <summary>git clone --depth 1 into <paramref name="dest"/>. git runs with a fixed argument list
    /// (never a shell string) and never prompts.</summary>
    private static async Task CloneAsync(string cloneUrl, string? branch, string dest, CancellationToken cancellationToken)
    {
        List<string> args = ["clone", "--depth", "1", "--single-branch", "--no-tags"];
        if (branch is not null) args.AddRange(["--branch", branch]);
        args.AddRange(["--", cloneUrl, dest]);
        var (status, error) = await RunGitAsync(args, CloneTimeout, cancellationToken).ConfigureAwait(false);
        if (status == 0) return;
        var why = error.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? "git clone failed";
        throw SkillException.Io($"Couldn't fetch the repository: {why}");
    }

    private static async Task<(int Status, string Error)> RunGitAsync(IEnumerable<string> arguments, TimeSpan timeout,
                                                                      CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo("git")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        // Git Credential Manager (Git for Windows) would otherwise open a sign-in window.
        info.Environment["GCM_INTERACTIVE"] = "never";

        using var process = new Process { StartInfo = info };
        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            throw SkillException.Io("Couldn't fetch the repository: Git isn't installed. Install Git for Windows, or use a GitHub link or a .zip.", ex);
        }
        try { process.StandardInput.Close(); } catch (IOException) { }
        var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var error = process.StandardError.ReadToEndAsync(CancellationToken.None);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
            {
                // Already gone.
            }
            if (cancellationToken.IsCancellationRequested) throw;
            throw SkillException.Io($"git timed out after {(int)timeout.TotalSeconds}s.");
        }
        await output.ConfigureAwait(false);
        return (process.ExitCode, await error.ConfigureAwait(false));
    }

    // MARK: Local

    internal static ImportPlan ScanFile(string file)
    {
        var ext = Extension(file);
        var fileName = Path.GetFileName(file);
        if (ext is not ("md" or "mdc") && fileName != ".cursorrules")
            throw SkillException.Unsupported("Choose a folder, a .zip, or a .md / .mdc file.");
        if (SkillFiles.ReadText(file) is not { } text) throw SkillException.Io("Can't read that file.");
        var doc = SkillDocument.Parse(text);
        var parent = Path.GetDirectoryName(Path.GetFullPath(file)) ?? ".";
        // SKILL.md alone: import its folder.
        if (fileName.Equals("skill.md", StringComparison.OrdinalIgnoreCase)) return ScanFolder(parent, temporaryRoot: null);
        var isInstruction = InstructionFileNames.Contains(fileName);
        var name = Path.GetFileNameWithoutExtension(file).Replace(".", "", StringComparison.Ordinal);
        var root = new SkillRoot(parent, 0, ext == "mdc" ? SkillOrigin.Cursor : SkillOrigin.Dsh, SkillScope.User,
                                 SkillRootLayout.CommandFiles);
        var skill = SkillCatalog.Build(doc, file, isInstruction ? SkillNaming.Slug(fileName) : name, root, SkillKind.Rule,
                                       claudeRuleStyle: ext == "md");
        if (isInstruction) skill = skill with { AlwaysApply = true };
        return new ImportPlan(file, [Candidate(skill, isInstruction)], [], null);
    }

    internal static ImportPlan ScanFolder(string folder, string? temporaryRoot, string? displaySource = null)
    {
        var candidates = new List<ImportCandidate>();
        var notes = new List<string>();
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var visited = 0;

        void Add(ImportCandidate c)
        {
            if (seen.Add(c.Id)) candidates.Add(c);
        }

        static SkillOrigin OriginOf(string path)
        {
            var parts = new HashSet<string>(Path.GetFullPath(path).Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries),
                                            StringComparer.Ordinal);
            if (parts.Contains(".claude") || parts.Contains(".claude-plugin")) return SkillOrigin.Claude;
            if (parts.Contains(".cursor")) return SkillOrigin.Cursor;
            if (parts.Contains(".agents")) return SkillOrigin.Agents;
            if (parts.Contains(".qwen")) return SkillOrigin.Qwen;
            return SkillOrigin.Dsh;
        }

        // A manifest, rule or knowledge file is read only when it is a real file: a link could point
        // anywhere (~/.ssh), and whatever is read here becomes the imported skill's text.
        static string? ReadPlainFile(string path) =>
            File.Exists(path) && !SkillFiles.IsLink(path) ? SkillFiles.ReadText(path) : null;

        // A folder that is itself a skill.
        var ownManifest = Path.Combine(folder, "SKILL.md");
        if (File.Exists(ownManifest))
        {
            var root = new SkillRoot(Path.GetDirectoryName(SkillFiles.Normalize(folder)) ?? folder, 0, OriginOf(folder),
                                     SkillScope.User, SkillRootLayout.SkillDirs);
            if (ReadPlainFile(ownManifest) is { } text)
            {
                Add(Candidate(SkillCatalog.Build(SkillDocument.Parse(text), ownManifest, SkillFiles.LastComponent(folder), root,
                                                 SkillKind.Skill)));
            }
            return new ImportPlan(displaySource ?? folder, candidates, notes, temporaryRoot);
        }

        // Top-level project knowledge.
        foreach (var name in InstructionFileNames.Order(StringComparer.Ordinal).Append(".claude/CLAUDE.md"))
        {
            var file = Path.Combine([folder, .. name.Split('/')]);
            if (ReadPlainFile(file) is not { } text || text.Trim().Length == 0) continue;
            var origin = name == ".cursorrules" ? SkillOrigin.Cursor
                : name.Contains("CLAUDE", StringComparison.Ordinal) ? SkillOrigin.Claude : SkillOrigin.Dsh;
            var root = new SkillRoot(folder, 0, origin, SkillScope.User, SkillRootLayout.CommandFiles);
            var doc = SkillDocument.Parse(text);
            if (doc["description"] is null) doc["description"] = $"Project instructions imported from {name}.";
            var skill = SkillCatalog.Build(doc, file, SkillNaming.Slug(name), root, SkillKind.Rule) with { AlwaysApply = true };
            Add(Candidate(skill, instruction: true));
        }

        void Walk(string dir, int depth)
        {
            if (depth > 6 || visited >= 4_000) return;
            List<FileSystemInfo> entries;
            try
            {
                entries = new DirectoryInfo(dir).EnumerateFileSystemInfos().OrderBy(e => e.Name, StringComparer.Ordinal).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                return;
            }
            var dirName = SkillFiles.LastComponent(dir);
            // Where Claude keeps commands/agents: .claude/, a plugin, or the top of what is imported.
            var isClaudeHome = dirName == ".claude" || Path.Exists(Path.Combine(dir, ".claude-plugin"))
                               || SkillFiles.SamePath(dir, folder);
            foreach (var entry in entries)
            {
                visited++;
                var name = entry.Name;
                if (SkillFiles.IsLink(entry)) continue;
                if (entry is not DirectoryInfo)
                {
                    // Loose rules: any .mdc, or .md under a `rules` folder.
                    var ext = Extension(name);
                    if (ext == "mdc" || (ext == "md" && dirName == "rules"))
                    {
                        var claude = Path.GetFullPath(entry.FullName).Split(['/', '\\']).Contains(".claude");
                        var root = new SkillRoot(dir, 0, OriginOf(entry.FullName), SkillScope.User, SkillRootLayout.RuleFiles,
                                                 ClaudeRuleStyle: claude);
                        if (SkillFiles.ReadText(entry.FullName) is { } text)
                        {
                            Add(Candidate(SkillCatalog.Build(SkillDocument.Parse(text), entry.FullName,
                                                             Path.GetFileNameWithoutExtension(name), root, SkillKind.Rule,
                                                             claudeRuleStyle: claude)));
                        }
                    }
                    continue;
                }
                if (SkipDirectories.Contains(name)) continue;
                var manifest = Path.Combine(entry.FullName, "SKILL.md");
                if (File.Exists(manifest) && ReadPlainFile(manifest) is { } manifestText)
                {
                    var root = new SkillRoot(dir, 0, OriginOf(entry.FullName), SkillScope.User, SkillRootLayout.SkillDirs);
                    Add(Candidate(SkillCatalog.Build(SkillDocument.Parse(manifestText), manifest, name, root, SkillKind.Skill)));
                    continue; // a skill's own folder is its content, not a place to search
                }
                if (name == "commands" && isClaudeHome)
                {
                    var root = new SkillRoot(entry.FullName, 0, SkillOrigin.Claude, SkillScope.User, SkillRootLayout.CommandFiles);
                    foreach (var command in SkillCatalog.ScanCommands(root))
                    {
                        if (!SkillFiles.IsLink(command.Path)) Add(Candidate(command));
                    }
                    continue;
                }
                if (name == "agents" && isClaudeHome)
                {
                    var count = 0;
                    try
                    {
                        count = Directory.EnumerateFileSystemEntries(entry.FullName)
                            .Count(p => Path.GetFileName(p).EndsWith(".md", StringComparison.Ordinal));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // Unreadable: nothing to report.
                    }
                    if (count > 0)
                        notes.Add($"{count} Claude subagent definition{(count == 1 ? "" : "s")} in {name}/ can't be imported (DSH has no subagent files).");
                    continue;
                }
                Walk(entry.FullName, depth + 1);
            }
        }

        Walk(folder, 0);
        if (candidates.Count == 0) notes.Add("No skills, rules, or commands were found there.");
        return new ImportPlan(displaySource ?? folder, candidates, notes, temporaryRoot);
    }

    internal static ImportCandidate Candidate(Skill skill, bool instruction = false)
    {
        var count = 1;
        long bytes = 0;
        var scripts = false;
        if (skill.Kind == SkillKind.Skill && Directory.Exists(skill.Directory))
        {
            count = 0;
            foreach (var file in RegularFiles(new DirectoryInfo(skill.Directory)))
            {
                count++;
                bytes += file.Length;
                if (ScriptExtensions.Contains(Extension(file.Name))) scripts = true;
                if (!OperatingSystem.IsWindows() && IsExecutable(file)) scripts = true;
            }
        }
        else
        {
            try
            {
                bytes = new FileInfo(skill.Path).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                bytes = 0;
            }
        }
        var text = SkillFiles.ReadText(skill.Path) ?? "";
        var issues = SkillLint.Check(text)
            .Where(i => i.Severity >= SkillIssueSeverity.Warning && !i.Message.Contains("frontmatter", StringComparison.Ordinal))
            .Select(i => i.Message)
            .ToList();
        return new ImportCandidate
        {
            Id = SkillFiles.Normalize(skill.Path),
            Skill = skill,
            IsInstructionFile = instruction,
            FileCount = count,
            TotalBytes = bytes,
            HasScripts = scripts,
            Issues = issues,
        };
    }

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static bool IsExecutable(FileInfo file)
    {
        try
        {
            return (file.UnixFileMode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Regular files under a folder, hidden ones included, never through a link.</summary>
    private static IEnumerable<FileInfo> RegularFiles(DirectoryInfo dir)
    {
        List<FileSystemInfo> entries;
        try
        {
            entries = dir.EnumerateFileSystemInfos().ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            yield break;
        }
        foreach (var entry in entries)
        {
            if (SkillFiles.IsLink(entry)) continue;
            if (entry is DirectoryInfo sub)
            {
                foreach (var nested in RegularFiles(sub)) yield return nested;
            }
            else if (entry is FileInfo file)
            {
                yield return file;
            }
        }
    }

    /// <summary>The extension without its dot, lowercased ("" when there is none).</summary>
    private static string Extension(string path) => Path.GetExtension(path).TrimStart('.').ToLowerInvariant();

    // MARK: Zip

    /// <summary>Extract a zip to a fresh staging folder, refusing archives with unsafe paths (rooted,
    /// drive letters, ".." segments), more than 5,000 entries or more than 150 MB uncompressed.
    /// __MACOSX/ is skipped and only files and folders are created — never links.</summary>
    internal static string Unzip(string zip)
    {
        ZipArchive archive;
        try
        {
            archive = ZipFile.OpenRead(zip);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or NotSupportedException
                                       or ArgumentException)
        {
            throw SkillException.Io("That doesn't look like a valid zip file.", ex);
        }
        using (archive)
        {
            var entries = archive.Entries;
            if (entries.Count > MaxZipEntries) throw SkillException.TooLarge("That archive has too many files.");
            foreach (var entry in entries)
            {
                if (IsUnsafeEntryName(entry.FullName))
                    throw SkillException.Invalid($"That archive has unsafe paths ({entry.FullName}); it was not opened.");
            }
            var declared = entries.Sum(e => e.Length);
            if (declared > MaxZipBytes)
                throw SkillException.TooLarge($"That archive expands to {declared / 1_000_000} MB — too large to be skills.");

            var dest = SkillFiles.StagingDirectory();
            Directory.CreateDirectory(dest);
            var prefix = SkillFiles.Normalize(dest) + Path.DirectorySeparatorChar;
            try
            {
                long written = 0;
                var buffer = new byte[81_920];
                foreach (var entry in entries)
                {
                    var name = entry.FullName.Replace('\\', '/');
                    if (name.StartsWith("__MACOSX/", StringComparison.Ordinal) || IsSymlinkEntry(entry)) continue;
                    var parts = name.Split('/', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 0) continue;
                    var target = Path.GetFullPath(Path.Combine([dest, .. parts]));
                    if (!target.StartsWith(prefix, PathComparison)) continue; // belt and braces: nothing outside dest
                    if (name.EndsWith('/'))
                    {
                        Directory.CreateDirectory(target);
                        continue;
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    using var input = entry.Open();
                    using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
                    int read;
                    while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        written += read;
                        // Declared sizes can lie; count what actually comes out.
                        if (written > MaxZipBytes)
                            throw SkillException.TooLarge($"That archive expands to over {MaxZipBytes / 1_000_000} MB — too large to be skills.");
                        output.Write(buffer, 0, read);
                    }
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException
                                           or NotSupportedException)
            {
                SkillFiles.DeleteQuietly(dest);
                throw SkillException.Io($"Couldn't extract the archive: {ex.Message}", ex);
            }
            catch
            {
                SkillFiles.DeleteQuietly(dest);
                throw;
            }
            return dest;
        }
    }

    /// <summary>Rooted ("/x", "\x"), drive-lettered ("C:x"), or containing a ".." segment — with either
    /// separator. On Windows any ":" is refused too (it would name an alternate data stream).</summary>
    internal static bool IsUnsafeEntryName(string name)
    {
        if (name.Length == 0) return false;
        if (name[0] is '/' or '\\' || Path.IsPathRooted(name)) return true;
        if (name.Length >= 2 && char.IsAsciiLetter(name[0]) && name[1] == ':') return true;
        if (name.Contains('\0') || (OperatingSystem.IsWindows() && name.Contains(':'))) return true;
        return name.Split('/', '\\').Any(part => part == "..");
    }

    /// <summary>A Unix symlink stored in the archive (S_IFLNK in the external attributes).</summary>
    private static bool IsSymlinkEntry(ZipArchiveEntry entry) => ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000;

    /// <summary>Delete a plan's temporary download/extraction/clone.</summary>
    public static void Dispose(ImportPlan plan) => SkillFiles.DeleteQuietly(plan.TemporaryRoot);

    // MARK: Importing

    /// <summary>Import the chosen candidates. As drafts, they wait in the approval queue; otherwise they
    /// go straight to the DSH store (the user's selection in the import sheet is the approval).</summary>
    public static ImportResult Perform(ImportPlan plan, IEnumerable<string> selecting, SkillScope scope, string? projectRoot,
                                       bool asDraft, ConflictPolicy conflict = ConflictPolicy.Rename,
                                       SkillLocations? locations = null)
    {
        locations ??= SkillLocations.Standard;
        var ids = selecting.ToHashSet(StringComparer.Ordinal);
        var imported = new List<string>();
        var drafts = new List<SkillDraft>();
        var skipped = new Dictionary<string, string>(StringComparer.Ordinal);
        var baseDir = asDraft ? null : SkillManager.Base(scope, projectRoot, locations);
        foreach (var candidate in plan.Candidates.Where(c => ids.Contains(c.Id)))
        {
            var slug = SkillFiles.NonEmpty(SkillNaming.Slug(candidate.Skill.Name), "imported-skill");
            var staged = SkillFiles.StagingDirectory();
            try
            {
                SkillConverter.WriteSkillFolder(candidate.Skill, staged, slug);
                if (baseDir is null)
                {
                    var draft = SkillDrafts.Stage(staged, scope, projectRoot, "import",
                        note: $"Imported from {candidate.Origin.Label()} {candidate.Kind.Label().ToLowerInvariant()} “{candidate.Skill.Name}”",
                        locations: locations);
                    drafts.Add(draft);
                    SkillFiles.DeleteQuietly(staged);
                }
                else
                {
                    var landed = SkillFiles.Place(staged, Path.Combine(baseDir, slug), conflict);
                    imported.Add(Path.Combine(landed, "SKILL.md"));
                }
            }
            catch (Exception ex) when (ex is SkillException or IOException or UnauthorizedAccessException or ArgumentException
                                           or NotSupportedException)
            {
                SkillFiles.DeleteQuietly(staged);
                skipped[candidate.Skill.Name] = ex.Message;
            }
        }
        return new ImportResult { Imported = imported, Drafts = drafts, Skipped = skipped };
    }
}

// MARK: - Export

public enum ExportFormat
{
    /// <summary>&lt;slug&gt;/SKILL.md — a plain skill folder, the shape Claude/Agent Skills uploads and
    /// most repos use.</summary>
    Portable,
    Claude,
    Cursor,
    Agents,
    Dsh,
}

public static class ExportFormats
{
    public static IReadOnlyList<ExportFormat> All { get; } =
        [ExportFormat.Portable, ExportFormat.Claude, ExportFormat.Cursor, ExportFormat.Agents, ExportFormat.Dsh];

    public static string RawValue(this ExportFormat format) => format switch
    {
        ExportFormat.Portable => "portable",
        ExportFormat.Claude => "claude",
        ExportFormat.Cursor => "cursor",
        ExportFormat.Agents => "agents",
        _ => "dsh",
    };

    public static string Label(this ExportFormat format) => format switch
    {
        ExportFormat.Portable => "Portable skill folders",
        ExportFormat.Claude => "Claude Code (.claude/skills)",
        ExportFormat.Cursor => "Cursor (.cursor/rules, .mdc)",
        ExportFormat.Agents => "Agent Skills (.agents/skills)",
        _ => "DSH (.dsh/skills)",
    };

    public static string Detail(this ExportFormat format) => format switch
    {
        ExportFormat.Portable => "One folder per skill with SKILL.md — zip and share anywhere.",
        ExportFormat.Claude => "Drop into a project (or ~/.claude) and Claude Code picks the skills up.",
        ExportFormat.Cursor => "Rules as .mdc files; skills with bundled files go to .cursor/skills.",
        ExportFormat.Agents => "The cross-tool .agents/skills layout.",
        _ => "This app's own layout.",
    };
}

public static class SkillExporter
{
    /// <summary>Lay <paramref name="skills"/> out under <paramref name="root"/> in
    /// <paramref name="format"/>. Returns the files/folders written.</summary>
    public static IReadOnlyList<string> Write(IEnumerable<Skill> skills, ExportFormat format, string root,
                                              ConflictPolicy conflict = ConflictPolicy.Fail)
    {
        var written = new List<string>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var skill in skills)
        {
            var slug = SkillNaming.Unique(SkillFiles.NonEmpty(SkillNaming.Slug(skill.Name), "skill"), used.Contains);
            used.Add(slug);
            if (format == ExportFormat.Cursor && (skill.Kind != SkillKind.Skill || skill.Resources().Count == 0))
            {
                if (SkillConverter.CursorRule(skill) is not { } text) continue;
                var dir = Path.Combine(root, ".cursor", "rules");
                var target = Path.Combine(dir, $"{slug}.mdc");
                if (Path.Exists(target))
                {
                    switch (conflict)
                    {
                        case ConflictPolicy.Fail:
                            throw SkillException.Exists($"{slug}.mdc already exists in .cursor/rules.");
                        case ConflictPolicy.Replace:
                            SkillFiles.Delete(target);
                            break;
                        default:
                            var name = SkillNaming.Unique(slug, n => Path.Exists(Path.Combine(dir, $"{n}.mdc")));
                            target = Path.Combine(dir, $"{name}.mdc");
                            break;
                    }
                }
                Directory.CreateDirectory(dir);
                SkillFiles.WriteText(target, text);
                written.Add(target);
                continue;
            }
            string[] sub = format switch
            {
                ExportFormat.Portable => [slug],
                ExportFormat.Claude => [".claude", "skills", slug],
                ExportFormat.Cursor => [".cursor", "skills", slug],
                ExportFormat.Agents => [".agents", "skills", slug],
                _ => [".dsh", "skills", slug],
            };
            var staged = SkillFiles.StagingDirectory();
            try
            {
                SkillConverter.WriteSkillFolder(skill, staged, slug);
                written.Add(SkillFiles.Place(staged, Path.Combine([root, .. sub]), conflict));
            }
            catch
            {
                SkillFiles.DeleteQuietly(staged);
                throw;
            }
        }
        return written;
    }

    /// <summary>Build a zip of <paramref name="skills"/> in <paramref name="format"/>. Portable zips hold
    /// the skill folders at the top; the other layouts hold their dot-folders plus a README.txt.</summary>
    public static void Zip(IEnumerable<Skill> skills, ExportFormat format, string zipPath)
    {
        var staging = SkillFiles.StagingDirectory();
        Directory.CreateDirectory(staging);
        try
        {
            Write(skills, format, staging, ConflictPolicy.Rename);
            if (format != ExportFormat.Portable)
            {
                var readme = $"""
                    Skills exported from DSH ({format.Label()}).

                    Unzip this into a project folder (or your home folder for user-wide skills); the tool picks them up from there.
                    Each skill is plain Markdown — read SKILL.md before trusting a skill you didn't write: skills can tell an agent to run commands.
                    """;
                SkillFiles.WriteText(Path.Combine(staging, "README.txt"), readme);
            }
            DeleteFileQuietly(zipPath); // a fresh archive, never an update of an old one
            try
            {
                using var stream = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None);
                using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
                AddFolder(archive, new DirectoryInfo(staging), "");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                DeleteFileQuietly(zipPath);
                throw SkillException.Io($"Couldn't create the zip: {ex.Message}", ex);
            }
        }
        finally
        {
            SkillFiles.DeleteQuietly(staging);
        }
    }

    /// <summary>Only ever a file: a folder picked by mistake as the zip path is left alone.</summary>
    private static void DeleteFileQuietly(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Creating the archive reports the real problem.
        }
    }

    /// <summary>Everything under <paramref name="dir"/>, with forward-slash entry names.</summary>
    private static void AddFolder(ZipArchive archive, DirectoryInfo dir, string prefix)
    {
        foreach (var entry in dir.EnumerateFileSystemInfos().OrderBy(e => e.Name, StringComparer.Ordinal))
        {
            if (SkillFiles.IsLink(entry)) continue;
            var name = prefix + entry.Name;
            if (entry is DirectoryInfo sub)
            {
                archive.CreateEntry(name + "/");
                AddFolder(archive, sub, name + "/");
            }
            else
            {
                archive.CreateEntryFromFile(entry.FullName, name, CompressionLevel.Optimal);
            }
        }
    }
}
