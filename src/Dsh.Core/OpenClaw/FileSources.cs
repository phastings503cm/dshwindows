using System.Text;

namespace Dsh.Core;

// MARK: - Where the files are
//
// The OpenClaw importer reads a machine's files: this PC's, or a remote one's over SSH. Everything it
// needs from a machine fits in this interface, so the scanner and reader are written once and work on
// either — and are tested against a temp folder (local) and against a real shell (the remote path).

/// <summary>One entry of a directory listing.</summary>
public sealed record FileEntry(string Name, string FullPath, bool IsDirectory);

public interface IFileSource : IDisposable
{
    /// <summary>"This PC" or "sam@spark-3".</summary>
    string Label { get; }
    bool IsRemote { get; }
    /// <summary>The address the machine answers on when it is remote (what a "localhost" there means to us); null for this PC.</summary>
    string? NetworkHost { get; }
    /// <summary>The user's home folder on that machine.</summary>
    Task<string> HomeAsync(CancellationToken cancellationToken);
    /// <summary>The value of an environment variable there, or null when unset/unknowable.</summary>
    Task<string?> EnvironmentAsync(string name, CancellationToken cancellationToken);
    /// <summary>The entries of a directory, or null when it doesn't exist or can't be read.</summary>
    Task<IReadOnlyList<FileEntry>?> ListAsync(string directory, CancellationToken cancellationToken);
    /// <summary>A file's bytes, or null when it is missing, unreadable or larger than <paramref name="maxBytes"/>.</summary>
    Task<byte[]?> ReadAsync(string path, long maxBytes, CancellationToken cancellationToken);
    /// <summary>Several files at once, in order; null for one that is missing, unreadable or larger than
    /// <paramref name="maxBytesEach"/>. One round trip on a remote machine instead of one per file.</summary>
    Task<IReadOnlyList<byte[]?>> ReadManyAsync(IReadOnlyList<string> paths, long maxBytesEach, CancellationToken cancellationToken);
    /// <summary>Files under <paramref name="root"/>, relative to it and "/"-separated, at most
    /// <paramref name="maxDepth"/> levels down and <paramref name="maxFiles"/> in all; hidden folders and
    /// dependency folders (.git, node_modules…) are skipped.</summary>
    Task<IReadOnlyList<string>> ListFilesAsync(string root, int maxDepth, int maxFiles, CancellationToken cancellationToken);
    /// <summary>Paths of files called any of <paramref name="fileNames"/> under <paramref name="roots"/>, at
    /// most <paramref name="maxDepth"/> levels down. Best effort: unreadable places are skipped.</summary>
    Task<IReadOnlyList<string>> FindAsync(IReadOnlyList<string> roots, IReadOnlyList<string> fileNames, int maxDepth,
                                          CancellationToken cancellationToken);
    string Combine(string directory, string name);
    /// <summary>The folder containing <paramref name="path"/>, or null at the root.</summary>
    string? Parent(string path);
}

/// <summary>Text helpers shared by the sources.</summary>
public static class FileSourceExtensions
{
    public static async Task<string?> ReadTextAsync(this IFileSource source, string path, long maxBytes, CancellationToken cancellationToken)
    {
        var bytes = await source.ReadAsync(path, maxBytes, cancellationToken).ConfigureAwait(false);
        if (bytes is null) return null;
        // UTF-8 with or without a byte-order mark.
        var start = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        return Encoding.UTF8.GetString(bytes, start, bytes.Length - start);
    }

    public static async Task<bool> DirectoryExistsAsync(this IFileSource source, string path, CancellationToken cancellationToken) =>
        await source.ListAsync(path, cancellationToken).ConfigureAwait(false) is not null;
}

// MARK: - This PC

public sealed class LocalFileSource : IFileSource
{
    /// <summary>Folders not worth descending into while searching for a config file.</summary>
    private static readonly HashSet<string> SkipWhileSearching = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", ".git", ".cache", "AppData", "Library", "Windows", "Program Files",
        "Program Files (x86)", "$Recycle.Bin", ".Trash", "__pycache__", ".nuget", ".npm", ".cargo", ".rustup",
        ".gradle", ".m2", "venv", ".venv", "snap",
    };

    private readonly string? _home;
    private readonly IReadOnlyDictionary<string, string>? _environment;

    /// <param name="home">Overrides the home folder (tests).</param>
    /// <param name="environment">Overrides the environment variables (tests).</param>
    public LocalFileSource(string? home = null, IReadOnlyDictionary<string, string>? environment = null)
    {
        _home = home;
        _environment = environment;
    }

    public string Label => "This PC";
    public bool IsRemote => false;
    public string? NetworkHost => null;

    public Task<string> HomeAsync(CancellationToken cancellationToken) =>
        Task.FromResult(_home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    public Task<string?> EnvironmentAsync(string name, CancellationToken cancellationToken)
    {
        if (_environment is not null) return Task.FromResult(_environment.TryGetValue(name, out var v) ? v : null);
        var value = Environment.GetEnvironmentVariable(name);
        return Task.FromResult(string.IsNullOrEmpty(value) ? null : value);
    }

    public Task<IReadOnlyList<FileEntry>?> ListAsync(string directory, CancellationToken cancellationToken)
    {
        try
        {
            if (!Directory.Exists(directory)) return Task.FromResult<IReadOnlyList<FileEntry>?>(null);
            var entries = new DirectoryInfo(directory).EnumerateFileSystemInfos()
                .Select(i => new FileEntry(i.Name, i.FullName, i is DirectoryInfo))
                .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return Task.FromResult<IReadOnlyList<FileEntry>?>(entries);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult<IReadOnlyList<FileEntry>?>(null);
        }
    }

    public Task<byte[]?> ReadAsync(string path, long maxBytes, CancellationToken cancellationToken)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > maxBytes) return Task.FromResult<byte[]?>(null);
            return Task.FromResult<byte[]?>(File.ReadAllBytes(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult<byte[]?>(null);
        }
    }

    public async Task<IReadOnlyList<byte[]?>> ReadManyAsync(IReadOnlyList<string> paths, long maxBytesEach, CancellationToken cancellationToken)
    {
        var result = new List<byte[]?>(paths.Count);
        foreach (var path in paths) result.Add(await ReadAsync(path, maxBytesEach, cancellationToken).ConfigureAwait(false));
        return result;
    }

    public Task<IReadOnlyList<string>> ListFilesAsync(string root, int maxDepth, int maxFiles, CancellationToken cancellationToken)
    {
        var files = new List<string>();
        var queue = new Queue<(string Directory, int Depth)>();
        queue.Enqueue((root, 0));
        while (queue.Count > 0 && files.Count < maxFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (directory, depth) = queue.Dequeue();
            IEnumerable<FileSystemInfo> entries;
            try
            {
                entries = new DirectoryInfo(directory).EnumerateFileSystemInfos().ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var entry in entries.OrderBy(e => e.Name, StringComparer.Ordinal))
            {
                if (entry is DirectoryInfo dir)
                {
                    if (depth + 1 < maxDepth && dir.LinkTarget is null && !dir.Name.StartsWith('.') && !SkillImporter.SkipDirectories.Contains(dir.Name))
                        queue.Enqueue((dir.FullName, depth + 1));
                }
                else if (files.Count < maxFiles && entry.LinkTarget is null)
                {
                    // A link to a file could point anywhere (a key in ~/.ssh): a skill's own files are only what is inside it.
                    files.Add(Path.GetRelativePath(root, entry.FullName).Replace('\\', '/'));
                }
            }
        }
        files.Sort(StringComparer.Ordinal);
        return Task.FromResult<IReadOnlyList<string>>(files);
    }

    public Task<IReadOnlyList<string>> FindAsync(IReadOnlyList<string> roots, IReadOnlyList<string> fileNames, int maxDepth,
                                                 CancellationToken cancellationToken)
    {
        var found = new List<string>();
        var wanted = new HashSet<string>(fileNames, StringComparer.OrdinalIgnoreCase);
        var scanned = 0;

        void Walk(string directory, int depth)
        {
            if (depth > maxDepth || scanned > 150_000 || found.Count >= 200) return;
            cancellationToken.ThrowIfCancellationRequested();
            IEnumerable<FileSystemInfo> entries;
            try
            {
                entries = new DirectoryInfo(directory).EnumerateFileSystemInfos().ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return;
            }
            foreach (var entry in entries)
            {
                scanned++;
                if (entry is DirectoryInfo dir)
                {
                    if (dir.LinkTarget is not null || SkipWhileSearching.Contains(dir.Name)) continue;
                    Walk(dir.FullName, depth + 1);
                }
                else if (wanted.Contains(entry.Name))
                {
                    found.Add(entry.FullName);
                }
            }
        }

        foreach (var root in roots)
        {
            if (Directory.Exists(root)) Walk(root, 0);
        }
        return Task.FromResult<IReadOnlyList<string>>(found.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }

    // (Names arrive "/"-separated whatever the machine: "~/.openclaw/workspace" is joined to the home folder as a path of this PC,
    // or the same folder would turn up twice — once spelled with "/" and once as the listing spells it.)
    public string Combine(string directory, string name) => Path.Combine(directory, name.Replace('/', Path.DirectorySeparatorChar));

    public string? Parent(string path) => Path.GetDirectoryName(path.TrimEnd('\\', '/'));

    public void Dispose()
    {
    }
}

// MARK: - Another machine

/// <summary>Runs a command on the other machine (an SSH exec channel; a local shell in tests).</summary>
public interface IRemoteShell : IDisposable
{
    Task<RemoteResult> RunAsync(string command, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>Reads files over SFTP. Optional: without it the source falls back to the shell.</summary>
public interface IRemoteFiles : IDisposable
{
    Task<IReadOnlyList<FileEntry>?> ListAsync(string directory, CancellationToken cancellationToken);
    Task<byte[]?> ReadAsync(string path, long maxBytes, CancellationToken cancellationToken);
}

/// <summary>A Linux/macOS machine reached through a shell (and, when it will talk SFTP, a file channel).
/// Discovery uses one <c>find</c> instead of thousands of directory round trips; file contents come over
/// SFTP when possible, otherwise as base64 through the shell.</summary>
public sealed class RemoteFileSource : IFileSource
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(45);

    /// <summary>How long one SFTP call may take before the channel is given up on and the shell is used instead.</summary>
    internal TimeSpan SftpReadTimeout { get; init; } = TimeSpan.FromSeconds(60);

    private readonly IRemoteShell _shell;
    private readonly IRemoteFiles? _files;
    private string? _home;
    /// <summary>An SFTP read never answered (a pipe, a wedged server): don't send another down the same channel.</summary>
    private volatile bool _sftpStalled;

    public RemoteFileSource(string label, IRemoteShell shell, IRemoteFiles? files = null, string? host = null)
    {
        Label = label;
        _shell = shell;
        _files = files;
        NetworkHost = host;
    }

    public string Label { get; }
    public bool IsRemote => true;
    public string? NetworkHost { get; }
    public bool HasSftp => _files is not null;

    /// <summary>Runs a command and returns only what the command itself printed. A login script (a banner, an
    /// <c>echo</c> in .bashrc) prints into the same stream, and reading "the third line" of that goes wrong — files
    /// end up holding the text of the file before them. So the output is fenced by markers no login script would
    /// print, and the command's exit status rides in the closing one.</summary>
    private async Task<RemoteResult> RunAsync(string command, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var tag = "@@" + Guid.NewGuid().ToString("N")[..12];
        var script = $"printf '%s\n' '{tag}:BEGIN'; {{ {command}\n}}; rc=$?; printf '\n%s\n' \"{tag}:END:$rc\"";
        // Handed to sh whatever the account's own shell is: fish or another non-POSIX login shell would choke on `rc=$?`.
        var raw = await _shell.RunAsync("sh -c " + Quote(script), timeout, cancellationToken).ConfigureAwait(false);
        var text = raw.Output.Replace("\r", "");
        var begin = text.IndexOf(tag + ":BEGIN\n", StringComparison.Ordinal);
        var end = text.LastIndexOf(tag + ":END:", StringComparison.Ordinal);
        if (begin < 0 || end < begin)
        {
            // The shell did not run it as written (an account that only allows SFTP or prints "not available", a shell that died
            // half-way): whatever came back is not the command's output, so it is not taken for data. Say what it said.
            var said = raw.Output.Trim().Length > 0 ? raw.Output.Trim() : raw.Error.Trim();
            throw new IOException(said.Length > 0
                ? $"The other computer's shell didn't run the command: {TextUtil.Prefix(said, 200)}"
                : "The other computer's shell didn't run the command (it answered with nothing).");
        }
        var bodyStart = begin + tag.Length + ":BEGIN\n".Length;
        var body = text[bodyStart..end];
        if (body.EndsWith('\n')) body = body[..^1]; // the newline the closing printf put in front of its marker
        var codeStart = end + tag.Length + ":END:".Length;
        var codeEnd = text.IndexOf('\n', codeStart);
        var code = int.TryParse(text[codeStart..(codeEnd < 0 ? text.Length : codeEnd)], out var status) ? status : raw.ExitCode;
        return new RemoteResult(code, body, raw.Error);
    }

    /// <summary>Runs an SFTP operation, or gives up on the channel: one that doesn't answer in time (a hung mount, a pipe put
    /// where a file was) is not asked again — the shell takes over for the rest of the session — and its late failure is not
    /// left to surface as an unobserved exception.</summary>
    private async Task<T?> SftpAsync<T>(Func<Task<T?>> operation, CancellationToken cancellationToken) where T : class
    {
        var task = operation();
        try
        {
            return await task.WaitAsync(SftpReadTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _sftpStalled = true;
            Abandon(task);
            return null;
        }
        catch (OperationCanceledException)
        {
            Abandon(task);
            throw;
        }
    }

    private static void Abandon(Task task) =>
        task.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    public async Task<string> HomeAsync(CancellationToken cancellationToken)
    {
        if (_home is not null) return _home;
        var result = await RunAsync("printf '%s' \"$HOME\"", CommandTimeout, cancellationToken).ConfigureAwait(false);
        var home = result.Output.Trim();
        _home = home.StartsWith('/') ? home : "/";
        return _home;
    }

    public async Task<string?> EnvironmentAsync(string name, CancellationToken cancellationToken)
    {
        if (!IsSafeName(name) || name[0] == '-') return null; // (a leading "-" would be read as an option)
        var result = await RunAsync($"printenv {name}", CommandTimeout, cancellationToken).ConfigureAwait(false);
        var value = result.Output.Trim();
        return result.ExitCode == 0 && value.Length > 0 ? value : null;
    }

    public async Task<IReadOnlyList<FileEntry>?> ListAsync(string directory, CancellationToken cancellationToken)
    {
        if (!IsSafePath(directory)) return null;
        if (_files is not null && !_sftpStalled)
        {
            try
            {
                var listed = await SftpAsync(() => _files.ListAsync(directory, cancellationToken), cancellationToken).ConfigureAwait(false);
                if (listed is not null) return listed;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // SFTP refused (a restricted account): the shell may still allow it.
            }
        }
        // ls -1ApL: one name per line, directories end in '/' — a link to a folder too (-L). Portable to GNU and BSD.
        var result = await RunAsync($"LC_ALL=C ls -1ApL -- {Quote(directory)} 2>/dev/null", CommandTimeout, cancellationToken)
            .ConfigureAwait(false);
        // GNU ls exits 1 ("minor problems") for a link whose target is gone when it follows links (-L), and still lists
        // everything else; a folder that is missing or unreadable prints nothing at all.
        if (result.ExitCode != 0 && string.IsNullOrWhiteSpace(result.Output)) return null;
        var entries = new List<FileEntry>();
        foreach (var raw in result.Output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;
            var isDirectory = line.EndsWith('/');
            var name = isDirectory ? line[..^1] : line;
            if (name.Length == 0 || name.Contains('/')) continue;
            entries.Add(new FileEntry(name, Combine(directory, name), isDirectory));
        }
        return entries;
    }

    public async Task<byte[]?> ReadAsync(string path, long maxBytes, CancellationToken cancellationToken)
    {
        if (!IsSafePath(path)) return null;
        if (_files is not null && !_sftpStalled)
        {
            try
            {
                if (await SftpAsync(() => _files.ReadAsync(path, maxBytes, cancellationToken), cancellationToken).ConfigureAwait(false) is { } bytes) return bytes;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Fall through to the shell.
            }
        }
        var quoted = Quote(path);
        // Size first, so a huge file is never pulled through the shell.
        var size = await RunAsync($"test -f {quoted} && wc -c < {quoted}", CommandTimeout, cancellationToken).ConfigureAwait(false);
        if (size.ExitCode != 0 || !long.TryParse(size.Output.Trim(), out var length) || length > maxBytes) return null;
        if (length == 0) return [];
        var result = await RunAsync($"base64 < {quoted} | tr -d '\\n'", TimeSpan.FromSeconds(120), cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0) return null;
        try
        {
            // (Without base64 on the machine the output is empty, which decodes happily to nothing: a file that has bytes must
            // come back with them.)
            var bytes = Convert.FromBase64String(result.Output.Trim());
            return bytes.Length == length ? bytes : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>How many files one shell command carries.</summary>
    private const int ReadBatch = 40;

    public async Task<IReadOnlyList<byte[]?>> ReadManyAsync(IReadOnlyList<string> paths, long maxBytesEach, CancellationToken cancellationToken)
    {
        var result = new byte[]?[paths.Count];
        if (_files is not null)
        {
            // SFTP is one round trip per file already, and cheap.
            for (var i = 0; i < paths.Count; i++) result[i] = await ReadAsync(paths[i], maxBytesEach, cancellationToken).ConfigureAwait(false);
            return result;
        }
        for (var start = 0; start < paths.Count; start += ReadBatch)
        {
            var batch = paths.Skip(start).Take(ReadBatch).ToList();
            var nonce = Guid.NewGuid().ToString("N")[..10];
            // One numbered statement per file: "@@nonce:3:OK" and the base64 on the next line, or "@@nonce:3:NO". Each answer
            // names its file, so an extra line anywhere can't shift one file's bytes onto another.
            var script = new StringBuilder();
            for (var i = 0; i < batch.Count; i++)
            {
                if (!IsSafePath(batch[i])) continue;
                script.Append($"f={Quote(batch[i])}; if [ -f \"$f\" ] && n=$(wc -c < \"$f\") && [ \"$n\" -le {maxBytesEach} ]; then printf '@@{nonce}:{i}:OK:%s\\n' \"$n\"; base64 < \"$f\" | tr -d '\\n'; printf '\\n'; else printf '@@{nonce}:{i}:NO\\n'; fi\n");
            }
            if (script.Length == 0) continue;
            var output = await RunAsync(script.ToString(), TimeSpan.FromSeconds(180), cancellationToken).ConfigureAwait(false);
            var lines = output.Output.Split('\n');
            var header = $"@@{nonce}:";
            for (var at = 0; at < lines.Length; at++)
            {
                var line = lines[at];
                if (!line.StartsWith(header, StringComparison.Ordinal) || at + 1 >= lines.Length) continue;
                // "@@nonce:3:OK:1234": file 3, and the 1234 bytes it has.
                var parts = line[header.Length..].Split(':');
                if (parts.Length != 3 || parts[1] != "OK" || !int.TryParse(parts[0], out var index) || index < 0 || index >= batch.Count
                    || !long.TryParse(parts[2].Trim(), out var size))
                    continue;
                try
                {
                    var bytes = Convert.FromBase64String(lines[at + 1].Trim());
                    // (A machine without base64 answers with nothing, which decodes to an empty file: only what has its size counts.)
                    result[start + index] = bytes.Length == size ? bytes : null;
                }
                catch (FormatException)
                {
                    result[start + index] = null;
                }
            }
        }
        return result;
    }

    public async Task<IReadOnlyList<string>> ListFilesAsync(string root, int maxDepth, int maxFiles, CancellationToken cancellationToken)
    {
        if (!IsSafePath(root)) return [];
        var pruned = string.Join(" -o ", new[] { ".*", "node_modules", "__pycache__", "venv", "Pods", "DerivedData" }.Select(n => $"-name {Quote(n)}"));
        // -H: the folder itself may be a link (a skill kept in a git checkout elsewhere); links inside it are not followed.
        // The list is cut after sorting by depth, in C#: a skill of more files than the cap must keep its SKILL.md.
        // (The files right in the folder come first and on their own, so SKILL.md is never the one cut from a skill that holds thousands.)
        var top = $"find -H {Quote(root)} -mindepth 1 -maxdepth 1 -type f -print 2>/dev/null | head -n 2000";
        // (The deep search starts at depth 1 too, so a hidden or dependency folder right in the skill is pruned before it is entered;
        // the top-level files it prints again are dropped as duplicates.)
        var deep = $"find -H {Quote(root)} -mindepth 1 -maxdepth {Math.Clamp(maxDepth, 1, 8)} \\( -type d \\( {pruned} \\) -prune \\) -o -type f -print 2>/dev/null | head -n {Math.Clamp(maxFiles * 20, 1, 20_000)}";
        var command = maxDepth <= 1 ? top : $"{top}; {deep}";
        var result = await RunAsync(command, TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false);
        var prefix = root.TrimEnd('/') + "/";
        var files = result.Output.Split('\n').Select(l => l.TrimEnd('\r'))
            .Where(l => l.StartsWith(prefix, StringComparison.Ordinal)).Select(l => l[prefix.Length..]).Distinct(StringComparer.Ordinal)
            .OrderBy(f => f.Count(c => c == '/')).ThenBy(f => f, StringComparer.Ordinal)
            .Take(Math.Clamp(maxFiles, 1, 2000)).ToList();
        files.Sort(StringComparer.Ordinal);
        return files;
    }

    public async Task<IReadOnlyList<string>> FindAsync(IReadOnlyList<string> roots, IReadOnlyList<string> fileNames, int maxDepth,
                                                       CancellationToken cancellationToken)
    {
        var safeRoots = roots.Where(IsSafePath).Select(Quote).ToList();
        var safeNames = fileNames.Where(n => IsSafeName(n.Replace(".", "")) || IsSafePath(n)).ToList();
        if (safeRoots.Count == 0 || safeNames.Count == 0) return [];
        var names = string.Join(" -o ", safeNames.Select(n => $"-name {Quote(n)}"));
        // Skip the folders that only slow a search down; ignore places we may not read.
        // (By name at any depth, but only the ones that are never anyone's work: a folder called "dev" in a home directory is.)
        var pruned = string.Join(" -o ", new[] { "node_modules", ".git", ".cache", ".npm", ".cargo", "snap" }.Select(n => $"-name {Quote(n)}"))
                     + " -o -path /proc -o -path /sys -o -path /dev";
        var find = $"find -H {string.Join(" ", safeRoots)} -maxdepth {Math.Clamp(maxDepth, 1, 8)} \\( -type d \\( {pruned} \\) -prune \\) -o \\( -type f \\( {names} \\) -print \\) 2>/dev/null | head -n 200";
        // Ask the machine to give up on its own: closing our end of the connection does not stop a search it is running.
        var command = $"if command -v timeout >/dev/null 2>&1; then timeout 60 {find}; else {find}; fi";
        var result = await RunAsync(command, TimeSpan.FromSeconds(90), cancellationToken).ConfigureAwait(false);
        return result.Output.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.StartsWith('/')).Distinct(StringComparer.Ordinal).ToList();
    }

    public string Combine(string directory, string name) =>
        directory.EndsWith('/') ? directory + name : directory + "/" + name;

    public string? Parent(string path)
    {
        var trimmed = path.TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');
        if (slash < 0) return null;
        return slash == 0 ? "/" : trimmed[..slash];
    }

    public void Dispose()
    {
        _files?.Dispose();
        _shell.Dispose();
    }

    // MARK: Quoting

    /// <summary>'single-quoted' for a POSIX shell, with embedded quotes escaped.</summary>
    public static string Quote(string value) => "'" + value.Replace("'", "'\\''") + "'";

    /// <summary>Paths built from what we found or from the user's own input — but never with control
    /// characters or NULs, which have no business in a command line.</summary>
    internal static bool IsSafePath(string path) => path.Length > 0 && path.Length < 1_024 && path.All(c => c >= ' ' && c != '\u007f');

    private static bool IsSafeName(string name) => name.Length > 0 && name.Length <= 80 && name.All(c => char.IsLetterOrDigit(c) || c is '_' or '-');
}
