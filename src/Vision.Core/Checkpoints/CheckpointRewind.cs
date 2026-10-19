namespace Vision.Core;

// MARK: - Rewinding a turn
//
// Each file the turn changed goes back to how it was before the turn: rewritten from the store, deleted (the turn created
// it) or recreated (the turn deleted it). A file that changed again since the turn ended — by the person, another turn, a
// build — is a conflict: the plan lists it, and it is overwritten only when the person says so. What each file held just
// before the rewind is kept, so the rewind itself can be undone.

public enum RewindAction
{
    /// <summary>Write back the content it had before the turn.</summary>
    Restore,
    /// <summary>The turn created it: delete it.</summary>
    Delete,
    /// <summary>Already as it was before the turn.</summary>
    Nothing,
    /// <summary>Can't: too large to have been kept, or its snapshot was pruned.</summary>
    Unavailable,
}

/// <summary>What rewinding does to one file. <paramref name="Conflict"/>: it changed since the turn ended (or a background
/// agent changed it after the turn, so nobody can tell).</summary>
public sealed record RewindItem(CheckpointFile File, RewindAction Action, bool Conflict, string? Current);

public sealed record RewindPlan(TurnCheckpoint Turn, IReadOnlyList<RewindItem> Items)
{
    public IReadOnlyList<RewindItem> Conflicts => Items.Where(i => i.Conflict && i.Action is RewindAction.Restore or RewindAction.Delete).ToList();
    public IReadOnlyList<RewindItem> Unavailable => Items.Where(i => i.Action == RewindAction.Unavailable).ToList();
    public int Actionable => Items.Count(i => i.Action is RewindAction.Restore or RewindAction.Delete);
}

/// <summary>What a rewind did.</summary>
public sealed record RewindResult(int Restored, int Deleted, int Skipped, IReadOnlyList<string> Failed)
{
    public int Changed => Restored + Deleted;

    /// <summary>"Rewound 3 files to before this turn." (with what was skipped or failed) — the same words on every harness.</summary>
    public string Summary
    {
        get
        {
            var text = Changed == 0 ? "Nothing to rewind: the files are already as they were before this turn."
                : $"Rewound {Changed} {(Changed == 1 ? "file" : "files")} to before this turn.";
            if (Skipped > 0) text += $" Left {Skipped} {(Skipped == 1 ? "file" : "files")} that changed since as {(Skipped == 1 ? "it is" : "they are")}.";
            if (Failed.Count > 0) text += $" Couldn't write {string.Join(", ", Failed.Select(System.IO.Path.GetFileName))}.";
            return text;
        }
    }
}

public static class CheckpointRewind
{
    /// <summary>What rewinding <paramref name="turn"/> would do, looking at the files as they are now.</summary>
    public static RewindPlan Plan(CheckpointStore store, TurnCheckpoint turn)
    {
        var items = new List<RewindItem>();
        foreach (var file in turn.Changed)
        {
            var current = Current(file.Path);
            if (current == file.Before)
            {
                items.Add(new RewindItem(file, RewindAction.Nothing, false, current));
                continue;
            }
            if (file.Before is not null && (file.TooLarge || !store.Has(file.Before)))
            {
                items.Add(new RewindItem(file, RewindAction.Unavailable, false, current));
                continue;
            }
            var conflict = !file.AfterKnown || current != file.After;
            var action = file.Before is null ? (current is null ? RewindAction.Nothing : RewindAction.Delete) : RewindAction.Restore;
            items.Add(new RewindItem(file, action, conflict && action != RewindAction.Nothing, current));
        }
        return new RewindPlan(turn, items);
    }

    /// <summary>Carry out <paramref name="plan"/>: every file that can go back does, except a conflict when
    /// <paramref name="overwriteConflicts"/> is false. The turn's manifest records the rewind (and what the files held, so
    /// it can be undone).</summary>
    public static RewindResult Apply(CheckpointStore store, RewindPlan plan, bool overwriteConflicts, DateTimeOffset? at = null)
    {
        int restored = 0, deleted = 0, skipped = 0;
        var failed = new List<string>();
        var from = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var item in plan.Items)
        {
            if (item.Action is not (RewindAction.Restore or RewindAction.Delete)) continue;
            if (item.Conflict && !overwriteConflicts)
            {
                skipped++;
                continue;
            }
            var path = item.File.Path;
            try
            {
                // Keep what is there now, so the rewind can be undone.
                if (TurnRecorder.ReadOrNull(path) is { } now) from[path] = store.Put(now);
                else from[path] = null;
                lock (FileText.LockFor(path))
                {
                    if (item.Action == RewindAction.Delete)
                    {
                        if (File.Exists(path)) File.Delete(path);
                        deleted++;
                    }
                    else
                    {
                        var bytes = store.Get(item.File.Before!) ?? throw new IOException("its snapshot is gone");
                        WriteAtomically(path, bytes);
                        restored++;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                failed.Add(path);
            }
        }
        var turn = plan.Turn;
        if (restored + deleted > 0)
        {
            turn.RewoundAt = at ?? DateTimeOffset.Now;
            turn.RewoundCount = restored + deleted;
            turn.RewoundFrom = from;
            try
            {
                store.Save(turn);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The files went back; only the record of it is missing.
            }
        }
        return new RewindResult(restored, deleted, skipped, failed);
    }

    /// <summary>Undo a rewind: put back what each file held just before it.</summary>
    public static RewindResult Undo(CheckpointStore store, TurnCheckpoint turn)
    {
        if (turn.RewoundFrom is not { Count: > 0 } from) return new RewindResult(0, 0, 0, []);
        int restored = 0, deleted = 0;
        var failed = new List<string>();
        foreach (var (path, hash) in from)
        {
            try
            {
                lock (FileText.LockFor(path))
                {
                    if (hash is null)
                    {
                        if (File.Exists(path)) File.Delete(path);
                        deleted++;
                    }
                    else
                    {
                        WriteAtomically(path, store.Get(hash) ?? throw new IOException("its snapshot is gone"));
                        restored++;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                failed.Add(path);
            }
        }
        turn.RewoundAt = null;
        turn.RewoundCount = 0;
        turn.RewoundFrom = null;
        try
        {
            store.Save(turn);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Recorded next time.
        }
        return new RewindResult(restored, deleted, 0, failed);
    }

    /// <summary>The unified diff of one file across the turn (empty for a binary file or one whose snapshot is gone).</summary>
    public static string Diff(CheckpointStore store, CheckpointFile file, string? root = null)
    {
        var name = root is not null ? Relative(root, file.Path) : Path.GetFileName(file.Path);
        if (file.TooLarge) return $"{name}: too large to keep a copy of ({AttachmentFormat.Bytes(Math.Max(file.BeforeBytes, file.AfterBytes))}).";
        var before = file.Before is { } b ? store.Get(b) : [];
        var after = file.After is { } a ? store.Get(a) : file.AfterKnown ? [] : TurnRecorder.ReadOrNull(file.Path) ?? [];
        if (before is null || after is null) return $"{name}: its snapshot is no longer kept.";
        var beforeText = FileText.Decode(before)?.Text;
        var afterText = FileText.Decode(after)?.Text;
        if (beforeText is null || afterText is null) return $"{name}: binary file ({AttachmentFormat.Bytes(before.LongLength)} → {AttachmentFormat.Bytes(after.LongLength)}).";
        var diff = LineDiff.Unified(beforeText, afterText, file.Before is null ? "/dev/null" : "a/" + name, file.AfterKnown && file.After is null ? "/dev/null" : "b/" + name);
        return diff.Length == 0 ? $"{name}: no changes." : diff;
    }

    public static string Relative(string root, string path)
    {
        try
        {
            var relative = Path.GetRelativePath(root, path);
            return relative.StartsWith("..", StringComparison.Ordinal) ? path : relative.Replace('\\', '/');
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return path;
        }
    }

    private static string? Current(string path) => TurnRecorder.ReadOrNull(path) is { } bytes ? CheckpointStore.Hash(bytes) : null;

    private static void WriteAtomically(string path, byte[] bytes)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var temp = path + ".vision-tmp";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, path, overwrite: true);
    }
}
