namespace Vision.Core;

// MARK: - Recording a turn's checkpoint
//
// The file tools call BeforeChange just before they write; a shell command takes a git snapshot before it runs and hands it
// back after. The first time a turn touches a file, the file as it was is kept; later touches in the same turn change
// nothing (the checkpoint is "before the turn", not "before each step"). Finish takes each file's state at the end of the
// turn and saves the manifest. Subagents write through their parent turn's recorder, so their edits rewind with it; a
// background agent still writing after the turn ended adds to it, marked as changed after the turn.

/// <summary>Where a chat's engine finds the recorder of the turn running now (the engine is built once and reused).</summary>
public sealed class CheckpointSlot
{
    private TurnRecorder? _current;

    public TurnRecorder? Current
    {
        get => Volatile.Read(ref _current);
        set => Volatile.Write(ref _current, value);
    }
}

/// <summary>Records the files one turn changes. Thread-safe: subagents running side by side share it.</summary>
public sealed class TurnRecorder
{
    private readonly CheckpointStore _store;
    private readonly TurnCheckpoint _turn;
    private readonly Dictionary<string, CheckpointFile> _files;
    private readonly Lock _lock = new();
    private bool _finished;
    private string? _repoRoot;
    private bool _repoLooked;

    public static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public TurnRecorder(CheckpointStore store, string chatId, string turnId, string? workspace, string prompt, DateTimeOffset? at = null)
    {
        _store = store;
        _files = new Dictionary<string, CheckpointFile>(PathComparer);
        var firstLine = prompt.Replace("\r", "").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
        _turn = new TurnCheckpoint
        {
            Id = turnId, ChatId = chatId, Workspace = workspace, Prompt = TextUtil.Prefix(firstLine, 200), At = at ?? DateTimeOffset.Now,
        };
    }

    public string TurnId => _turn.Id;
    public CheckpointStore Store => _store;

    /// <summary>How many files the turn has touched so far.</summary>
    public int Count
    {
        get
        {
            lock (_lock) return _files.Count;
        }
    }

    /// <summary>A tool is about to change <paramref name="path"/>: keep it as it is now, unless this turn already did.</summary>
    public void BeforeChange(string path, string source)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return;
        }
        lock (_lock)
        {
            if (_files.ContainsKey(full)) return;
        }
        if (Directory.Exists(full)) return;
        var file = new CheckpointFile { Path = full, Source = source };
        Keep(file, full, before: true);
        Add(file);
    }

    /// <summary>Before a shell command: the repository's changed tracked files as they are now. Null when the folder isn't in
    /// a git repository (or git isn't there, or too much is already changed to keep).</summary>
    public ShellSnapshot? BeforeShell(string workspace)
    {
        var root = RepoRoot(workspace);
        if (root is null || GitFiles.ChangedTracked(root) is not { } changed || changed.Count > GitFiles.MaxDirtyFiles) return null;
        var dirty = new Dictionary<string, string?>(PathComparer);
        foreach (var relative in changed)
        {
            var full = Full(root, relative);
            if (full is null) continue;
            dirty[full] = HashOf(full, out _);
        }
        return new ShellSnapshot { RepoRoot = root, Dirty = dirty };
    }

    /// <summary>After a shell command: every tracked file whose status or content moved since <paramref name="before"/> is one it
    /// changed (unless this turn already kept it). Returns the changes found, for the editor and the chat's file chips.</summary>
    public IReadOnlyList<FileChange> AfterShell(ShellSnapshot? before, string source)
    {
        var found = new List<FileChange>();
        if (before is null || GitFiles.ChangedTracked(before.RepoRoot) is not { } changed) return found;
        var candidates = new HashSet<string>(before.Dirty.Keys, PathComparer);
        var relativeOf = new Dictionary<string, string>(PathComparer);
        foreach (var relative in changed)
        {
            if (Full(before.RepoRoot, relative) is not { } full) continue;
            candidates.Add(full);
            relativeOf[full] = relative;
        }
        foreach (var full in candidates)
        {
            lock (_lock)
            {
                if (_files.ContainsKey(full)) continue;
            }
            var now = HashOf(full, out _);
            string? was;
            if (before.Dirty.TryGetValue(full, out var kept))
            {
                was = kept;
            }
            else
            {
                // Clean before the command: what git has is what it was.
                var relative = relativeOf.GetValueOrDefault(full) ?? Path.GetRelativePath(before.RepoRoot, full).Replace('\\', '/');
                var content = GitFiles.IndexContent(before.RepoRoot, relative);
                if (content is null) continue;
                if (content.LongLength > CheckpointStore.MaxFileBytes)
                {
                    Add(new CheckpointFile { Path = full, Source = source, TooLarge = true, BeforeBytes = content.LongLength, Before = CheckpointStore.Hash(content) });
                    found.Add(new FileChange(full, now is null ? FileChangeKind.Deleted : FileChangeKind.Modified));
                    continue;
                }
                was = Put(content);
            }
            if (was == now) continue;
            var file = new CheckpointFile { Path = full, Source = source, Before = was };
            if (was is not null && !_store.Has(was)) file.TooLarge = true; // kept as a hash only (it was too large to keep)
            file.BeforeBytes = was is null ? 0 : _store.Get(was)?.LongLength ?? 0;
            Add(file);
            found.Add(new FileChange(full, was is null ? FileChangeKind.Created : now is null ? FileChangeKind.Deleted : FileChangeKind.Modified));
        }
        return found;
    }

    /// <summary>The turn is over: take each file's state now, count the lines, save the manifest. Null when the turn changed
    /// nothing (a file changed and changed back counts as nothing).</summary>
    public TurnCheckpoint? Finish(DateTimeOffset? at = null)
    {
        List<CheckpointFile> files;
        lock (_lock)
        {
            _finished = true;
            files = _files.Values.ToList();
        }
        foreach (var file in files) TakeAfter(file);
        TurnCheckpoint snapshot;
        lock (_lock)
        {
            _turn.FinishedAt = at ?? DateTimeOffset.Now;
            _turn.Files = _files.Values.OrderBy(f => f.Path, PathComparer).Select(f => f.Clone()).ToList();
            snapshot = _turn.Clone();
        }
        if (snapshot.Changed.Count == 0) return null;
        TrySave(snapshot);
        return snapshot;
    }

    /// <summary>The turn as recorded so far.</summary>
    public TurnCheckpoint Snapshot
    {
        get
        {
            lock (_lock)
            {
                var copy = _turn.Clone();
                copy.Files = _files.Values.Select(f => f.Clone()).ToList();
                return copy;
            }
        }
    }

    // MARK: Helpers

    private void Add(CheckpointFile file)
    {
        bool late;
        lock (_lock)
        {
            if (!_files.TryAdd(file.Path, file)) return;
            late = _finished;
        }
        if (!late) return;
        // A background agent of a turn that is already over: it joins the turn, its end state unknown.
        file.AfterKnown = false;
        TurnCheckpoint snapshot;
        lock (_lock)
        {
            _turn.Files = _files.Values.OrderBy(f => f.Path, PathComparer).Select(f => f.Clone()).ToList();
            snapshot = _turn.Clone();
        }
        TrySave(snapshot);
    }

    private void TrySave(TurnCheckpoint turn)
    {
        try
        {
            _store.Save(turn);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The turn just can't be rewound later.
        }
    }

    private void TakeAfter(CheckpointFile file)
    {
        if (file.AfterKnown) return;
        var bytes = ReadOrNull(file.Path);
        file.AfterKnown = true;
        file.AfterBytes = bytes?.LongLength ?? 0;
        if (bytes is null)
        {
            file.After = null;
        }
        else if (bytes.LongLength > CheckpointStore.MaxFileBytes)
        {
            file.After = CheckpointStore.Hash(bytes);
            file.TooLarge = true;
        }
        else
        {
            file.After = Put(bytes);
        }
        if (file.TooLarge) return;
        var before = file.Before is { } b ? _store.Get(b) : [];
        var beforeText = before is null ? null : FileText.Decode(before)?.Text;
        var afterText = bytes is null ? "" : FileText.Decode(bytes)?.Text;
        if (beforeText is null || afterText is null)
        {
            file.Binary = true;
            return;
        }
        (file.Added, file.Removed) = LineDiff.Count(beforeText, afterText);
    }

    private void Keep(CheckpointFile file, string full, bool before)
    {
        var bytes = ReadOrNull(full);
        if (bytes is null)
        {
            if (before) file.Before = null;
            return;
        }
        file.BeforeBytes = bytes.LongLength;
        if (bytes.LongLength > CheckpointStore.MaxFileBytes)
        {
            file.Before = CheckpointStore.Hash(bytes);
            file.TooLarge = true;
            return;
        }
        file.Before = Put(bytes);
    }

    private string? HashOf(string full, out byte[]? bytes)
    {
        bytes = ReadOrNull(full);
        if (bytes is null) return null;
        return bytes.LongLength > CheckpointStore.MaxFileBytes ? CheckpointStore.Hash(bytes) : Put(bytes);
    }

    private string? Put(byte[] bytes)
    {
        try
        {
            return _store.Put(bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return CheckpointStore.Hash(bytes);
        }
    }

    internal static byte[]? ReadOrNull(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private string? RepoRoot(string workspace)
    {
        lock (_lock)
        {
            if (_repoLooked) return _repoRoot;
        }
        var root = GitFiles.RepoRoot(workspace);
        lock (_lock)
        {
            _repoLooked = true;
            _repoRoot = root;
        }
        return root;
    }

    private static string? Full(string root, string relative)
    {
        try
        {
            return Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
