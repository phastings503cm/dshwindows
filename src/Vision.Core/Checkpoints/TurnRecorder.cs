namespace Vision.Core;

// MARK: - Recording a turn's checkpoint
//
// The file tools call BeforeChange just before they write; a shell command (and a local MCP server's tool that isn't
// read-only) takes a git snapshot before it runs and hands it back after. The first time a turn touches a file, the file
// as it was is kept; later touches in the same turn change nothing (the checkpoint is "before the turn", not "before each
// step"). Finish takes each file's state at the end of the turn and saves the record. Subagents write through their parent
// turn's recorder, so their edits rewind with it; a background agent still writing after the turn ended adds to it, marked
// as changed after the turn.

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

    /// <param name="title">The turn's title (see <see cref="TurnCheckpoint.TitleOf"/>).</param>
    public TurnRecorder(CheckpointStore store, string chatId, string turnId, string? workspace, string title, DateTimeOffset? at = null)
    {
        _store = store;
        _files = new Dictionary<string, CheckpointFile>(PathComparer);
        var started = at ?? DateTimeOffset.Now;
        _turn = new TurnCheckpoint { Id = turnId, ChatId = chatId, Workspace = workspace, Title = title, StartedAt = started, UpdatedAt = started };
        store.Track(this, live: true);
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

    /// A tool is about to change <paramref name="path"/>: keep it as it is now, unless this turn already did.
    public void BeforeChange(string path, string by)
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
        var (hash, skipped) = Keep(full);
        Add(new CheckpointFile { Path = full, Before = hash, After = hash, By = by, Skipped = skipped });
    }

    /// <summary>Before a shell command: the repository's changed tracked files as they are now. Null when the folder isn't in a git
    /// repository (or git isn't there, or too much is already changed to keep).</summary>
    public ShellSnapshot? BeforeShell(string workspace)
    {
        var root = RepoRoot(workspace);
        if (root is null || GitFiles.ChangedTracked(root) is not { } changed || changed.Count > GitFiles.MaxDirtyFiles) return null;
        var dirty = new Dictionary<string, string?>(PathComparer);
        foreach (var relative in changed)
        {
            if (Full(root, relative) is not { } full) continue;
            dirty[full] = Keep(full).Hash;
        }
        return new ShellSnapshot { RepoRoot = root, Dirty = dirty };
    }

    /// After a shell command: every tracked file whose status or content moved since <paramref name="before"/> is one it
    /// changed (unless this turn already kept it). Returns the changes found, for the editor and the chat's file chips.
    public IReadOnlyList<FileChange> AfterShell(ShellSnapshot? before, string by)
    {
        var found = new List<FileChange>();
        if (before is null || GitFiles.Status(before.RepoRoot) is not { } changed) return found;
        var candidates = new HashSet<string>(before.Dirty.Keys, PathComparer);
        var relativeOf = new Dictionary<string, string>(PathComparer);
        var untracked = new HashSet<string>(PathComparer);
        foreach (var (status, relative) in changed)
        {
            if (Full(before.RepoRoot, relative) is not { } full) continue;
            candidates.Add(full);
            relativeOf[full] = relative;
            if (status == "??") untracked.Add(full);
        }
        foreach (var full in candidates)
        {
            lock (_lock)
            {
                if (_files.ContainsKey(full)) continue;
            }
            var now = CurrentHash(full);
            string? was;
            string? skipped = null;
            if (before.Dirty.TryGetValue(full, out var kept))
            {
                was = kept;
                if (was is not null && !_store.Has(was)) skipped = "it was too large to keep";
            }
            else
            {
                // Clean before the command: what git has is what it was — or, when git has nothing, the command created it.
                var relative = relativeOf.GetValueOrDefault(full) ?? Path.GetRelativePath(before.RepoRoot, full).Replace('\\', '/');
                var content = untracked.Contains(full) ? null : GitFiles.IndexContent(before.RepoRoot, relative);
                if (content is null && !untracked.Contains(full)) continue; // git couldn't say what it was: leave it alone
                if (content is null) was = null;
                else if (content.LongLength > CheckpointStore.MaxFileBytes)
                {
                    was = CheckpointStore.Hash(content);
                    skipped = TooLarge(content.LongLength);
                }
                else
                {
                    was = Put(content);
                }
            }
            if (was == now) continue;
            Add(new CheckpointFile { Path = full, Before = was, After = was, By = by, Skipped = skipped });
            found.Add(new FileChange(full, was is null ? FileChangeKind.Created : now is null ? FileChangeKind.Deleted : FileChangeKind.Modified));
        }
        return found;
    }

    /// <summary>The turn is over: take each file's state now, count the lines, save the record. Null when the turn changed nothing
    /// (a file changed and put back counts as nothing).</summary>
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
            _turn.UpdatedAt = at ?? DateTimeOffset.Now;
            _turn.Files = _files.Values.OrderBy(f => f.Path, PathComparer).Select(f => f.Clone()).ToList();
            snapshot = _turn.Clone();
        }
        _store.Track(this, live: false);
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
        // A background agent of a turn that is already over: it joins the turn, what it leaves unknown.
        file.Late = true;
        TurnCheckpoint snapshot;
        lock (_lock)
        {
            _turn.UpdatedAt = DateTimeOffset.Now;
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
        if (file.IsLate) return;
        var bytes = ReadOrNull(file.Path);
        if (bytes is null) file.After = null;
        else if (bytes.LongLength > CheckpointStore.MaxFileBytes)
        {
            file.After = CheckpointStore.Hash(bytes);
            file.Skipped ??= TooLarge(bytes.LongLength);
        }
        else file.After = Put(bytes);
        if (file.Skipped is not null) return;
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

    /// What is at <paramref name="full"/> now: its hash once kept (null when there is no file), or why it can't be kept.
    private (string? Hash, string? Skipped) Keep(string full)
    {
        var bytes = ReadOrNull(full);
        if (bytes is null) return (null, File.Exists(full) ? "it couldn't be read" : null);
        if (bytes.LongLength > CheckpointStore.MaxFileBytes) return (CheckpointStore.Hash(bytes), TooLarge(bytes.LongLength));
        return (Put(bytes), null);
    }

    private static string TooLarge(long bytes) => $"too large to keep ({AttachmentFormat.Bytes(bytes)})";

    private static string? CurrentHash(string full) => ReadOrNull(full) is { } bytes ? CheckpointStore.Hash(bytes) : null;

    private string Put(byte[] bytes)
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
