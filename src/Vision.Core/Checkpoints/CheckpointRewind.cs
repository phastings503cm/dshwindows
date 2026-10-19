namespace Vision.Core;

// MARK: - Rewinding a turn
//
// Every file the turn changed goes back to what it held before the turn (a file the turn created is removed). A file
// that changed again since the turn — by you, by a later turn — is a conflict: rewinding it would throw that change away,
// so the person decides (rewind it anyway, or leave it). A file whose content wasn't kept can't be rewound and is left
// alone. The rewind is itself recorded as a turn of its own, so it can be rewound in turn. The words and the rules are
// Vision for macOS's (Rewind.swift).

/// <summary>What rewinding does to one file.</summary>
/// <param name="Restore">The content to put back (null = remove the file: the turn created it).</param>
/// <param name="Conflict">Why it would throw away a later change ("changed since this turn"); null = safe.</param>
/// <param name="Skipped">Why it can't be rewound at all; null = it can.</param>
/// <param name="AlreadyThere">It already holds what it held before the turn: nothing to do.</param>
/// <param name="Seen">What it held when the plan was made (its hash; null = no file) — checked again just before writing.</param>
public sealed record RewindItem(CheckpointFile File, string? Restore, string? Conflict, string? Skipped, bool AlreadyThere, string? Seen = null)
{
    public string Path => File.Path;
}

public sealed record RewindPlan(TurnCheckpoint Turn, IReadOnlyList<RewindItem> Items)
{
    /// <summary>The files a rewind changes without a question.</summary>
    public IReadOnlyList<RewindItem> Safe => Items.Where(i => i.Conflict is null && i.Skipped is null && !i.AlreadyThere).ToList();
    /// <summary>The files that changed since the turn.</summary>
    public IReadOnlyList<RewindItem> Conflicts => Items.Where(i => i.Conflict is not null && i.Skipped is null && !i.AlreadyThere).ToList();
    /// <summary>The files that can't be rewound.</summary>
    public IReadOnlyList<RewindItem> Skipped => Items.Where(i => i.Skipped is not null).ToList();
    /// <summary>Nothing left to put back.</summary>
    public bool IsEmpty => Safe.Count == 0 && Conflicts.Count == 0;
}

/// <summary>What a rewind did.</summary>
public sealed class RewindResult
{
    /// <summary>Files put back to their earlier content.</summary>
    public List<string> Restored { get; } = [];
    /// <summary>Files the turn created, removed.</summary>
    public List<string> Removed { get; } = [];
    /// <summary>Files left as they are (changed since and kept, or not rewindable).</summary>
    public List<string> Left { get; } = [];
    /// <summary>Files that couldn't be written, and why.</summary>
    public Dictionary<string, string> Failed { get; } = new(StringComparer.Ordinal);

    public int ChangedCount => Restored.Count + Removed.Count;
}

public static class CheckpointRewind
{
    /// <summary>What rewinding <paramref name="turn"/> would do now, file by file.</summary>
    public static RewindPlan Plan(CheckpointStore store, TurnCheckpoint turn)
    {
        var items = new List<RewindItem>();
        foreach (var file in turn.Changed)
        {
            var skipped = file.Skipped;
            if (skipped is null && file.Before is { } before && !store.Has(before)) skipped = "its earlier content is no longer kept";
            string? conflict = null;
            var already = false;
            var (now, readable) = Current(file.Path);
            if (!readable) skipped ??= "it can't be read now";
            else if (now == file.Before) already = true;
            else if (file.IsLate || now != file.After) conflict = now is null ? "deleted since this turn" : "changed since this turn";
            items.Add(new RewindItem(file, file.Before, conflict, skipped, already, now));
        }
        return new RewindPlan(turn, items);
    }

    /// <summary>Why a file the person confirmed as safe wasn't written: it changed between the plan and the rewind.</summary>
    public const string ChangedWhileDeciding = "it changed after you confirmed — left as it is";

    /// <summary>Put the files back. A conflict is rewound only with <paramref name="includeConflicts"/>. Each file is looked at
    /// again just before it is written: one that already holds its earlier content is left; a safe one that changed since the
    /// plan isn't written (<see cref="ChangedWhileDeciding"/>); a conflict "Rewind All" included is written whatever it holds. <paramref name="recorder"/>,
    /// when given, records the rewind as a turn of its own. The turn is marked rewound (its record stays, for the diff and
    /// for the rewind's own undo).</summary>
    public static RewindResult Apply(CheckpointStore store, RewindPlan plan, bool includeConflicts, TurnRecorder? recorder = null,
                                     DateTimeOffset? at = null)
    {
        var result = new RewindResult();
        foreach (var item in plan.Items)
        {
            if (item.AlreadyThere) continue;
            if (item.Skipped is not null || (item.Conflict is not null && !includeConflicts))
            {
                result.Left.Add(item.Path);
                continue;
            }
            var (current, readable) = Current(item.Path);
            if (!readable)
            {
                result.Failed[item.Path] = "it can't be read now";
                continue;
            }
            if (current == item.Restore) continue;
            if (item.Conflict is null && current != item.Seen)
            {
                result.Failed[item.Path] = ChangedWhileDeciding;
                continue;
            }
            recorder?.BeforeChange(item.Path, "rewind");
            try
            {
                lock (FileText.LockFor(item.Path))
                {
                    if (item.Restore is { } hash)
                    {
                        if (store.Get(hash) is not { } bytes)
                        {
                            result.Failed[item.Path] = "its earlier content is no longer kept";
                            continue;
                        }
                        WriteAtomically(item.Path, bytes);
                        result.Restored.Add(item.Path);
                    }
                    else
                    {
                        if (File.Exists(item.Path)) File.Delete(item.Path);
                        result.Removed.Add(item.Path);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                result.Failed[item.Path] = ex.Message;
            }
        }
        var turn = plan.Turn;
        turn.RewoundAt = at ?? DateTimeOffset.Now;
        turn.UpdatedAt = turn.RewoundAt.Value;
        try
        {
            store.Save(turn);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The files went back; only the record of it is missing.
        }
        return result;
    }

    /// <summary>What the chat says after a rewind: "↺ Rewound “Fix the build”: put back 2 files (a.cs, b.cs) and removed 1 file it
    /// created (c.cs). Left 1 file as they are (d.cs)."</summary>
    public static string Summary(RewindResult result, string title, string? root)
    {
        string Names(List<string> paths) =>
            string.Join(", ", paths.Take(4).Select(p => Display(p, root))) + (paths.Count > 4 ? $" and {paths.Count - 4} more" : "");
        static string Files(int n) => n == 1 ? "1 file" : $"{n} files";
        var parts = new List<string>();
        if (result.Restored.Count > 0) parts.Add($"put back {Files(result.Restored.Count)} ({Names(result.Restored)})");
        if (result.Removed.Count > 0) parts.Add($"removed {Files(result.Removed.Count)} it created ({Names(result.Removed)})");
        var text = $"↺ Rewound “{title}”: " + (parts.Count == 0 ? "nothing needed changing" : string.Join(" and ", parts)) + ".";
        if (result.Left.Count > 0) text += $" Left {Files(result.Left.Count)} as they are ({Names(result.Left)}).";
        if (result.Failed.Count > 0)
            text += " Couldn't rewind " + string.Join(", ", result.Failed.OrderBy(f => f.Key, StringComparer.Ordinal).Take(3)
                .Select(f => $"{Display(f.Key, root)} ({f.Value})")) + ".";
        return text;
    }

    /// <summary>What the model is told with the next message, so it doesn't trust what it remembers of those files.</summary>
    public static string ModelNote(RewindResult result, string title, string? root)
    {
        var paths = result.Restored.Concat(result.Removed).Select(p => Display(p, root));
        return $"[Automatic message: the user rewound the file changes of an earlier turn (“{title}”). " +
               $"These files are back to how they were before it: {string.Join(", ", paths)}. " +
               "Re-read them before relying on what you remember of their contents.]";
    }

    /// <summary>The confirmation's words: what goes back, what was created and goes, what changed since, what can't.</summary>
    public static string Confirmation(RewindPlan plan, string? root)
    {
        string Names(IReadOnlyList<RewindItem> items) =>
            string.Join(", ", items.Take(5).Select(i => Display(i.Path, root))) + (items.Count > 5 ? $" and {items.Count - 5} more" : "");
        static string S(int n) => n == 1 ? "" : "s";
        var parts = new List<string>();
        var restore = plan.Safe.Where(i => i.Restore is not null).ToList();
        var remove = plan.Safe.Where(i => i.Restore is null).ToList();
        if (restore.Count > 0) parts.Add($"{restore.Count} file{S(restore.Count)} go back to how they were before this turn ({Names(restore)}).");
        if (remove.Count > 0) parts.Add($"{remove.Count} file{S(remove.Count)} it created {(remove.Count == 1 ? "is" : "are")} removed ({Names(remove)}).");
        if (plan.Conflicts.Count > 0)
            parts.Add($"{plan.Conflicts.Count} file{S(plan.Conflicts.Count)} changed since this turn ({Names(plan.Conflicts)}): " +
                      $"rewinding {(plan.Conflicts.Count == 1 ? "it" : "them")} throws those later changes away.");
        if (plan.Skipped.Count > 0) parts.Add($"{plan.Skipped.Count} can't be rewound ({Names(plan.Skipped)}).");
        return string.Join("\n\n", parts);
    }

    /// <summary>The unified diff of one file across the turn, or a sentence saying why there is none.</summary>
    public static string Diff(CheckpointStore store, CheckpointFile file, string? root = null)
    {
        var name = Display(file.Path, root);
        if (file.Skipped is { } skipped && (file.Before is null || file.After is null || !store.Has(file.Before) || !store.Has(file.After)))
            return $"No diff: {skipped}.";
        var before = file.Before is { } b ? store.Get(b) : [];
        var after = file.IsLate ? TurnRecorder.ReadOrNull(file.Path) ?? [] : file.After is { } a ? store.Get(a) : [];
        if (before is null) return "The earlier content is no longer kept.";
        if (after is null) return "The turn's content is no longer kept.";
        var beforeText = FileText.Decode(before)?.Text;
        var afterText = FileText.Decode(after)?.Text;
        if (beforeText is null || afterText is null)
            return $"Binary file: {AttachmentFormat.Bytes(before.LongLength)} → {AttachmentFormat.Bytes(after.LongLength)}.";
        var (oldLines, newLines) = (LineDiff.Lines(beforeText).Length, LineDiff.Lines(afterText).Length);
        if (Math.Max(oldLines, newLines) > MaxDiffLines)
            return $"Too large to compare line by line ({oldLines.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} → " +
                   $"{newLines.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} lines).";
        var diff = LineDiff.Unified(beforeText, afterText, file.Before is null ? "/dev/null" : "a/" + name,
            file.After is null && !file.IsLate ? "/dev/null" : "b/" + name);
        return diff.Length == 0 ? "No line changes." : diff;
    }

    /// <summary>A file longer than this isn't compared line by line (the Mac's limit).</summary>
    public const int MaxDiffLines = 20_000;

    /// <summary>A path shown relative to the project when it is inside it (forward slashes), else as it is.</summary>
    public static string Display(string path, string? root)
    {
        if (string.IsNullOrEmpty(root)) return path;
        try
        {
            var relative = Path.GetRelativePath(root, path);
            return relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? path : relative.Replace('\\', '/');
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return path;
        }
    }

    /// <summary>The hash of what is at <paramref name="path"/> now (null = no file), and whether it could be read.</summary>
    private static (string? Hash, bool Readable) Current(string path)
    {
        if (Directory.Exists(path)) return (null, false);
        if (!File.Exists(path)) return (null, true);
        return TurnRecorder.ReadOrNull(path) is { } bytes ? (CheckpointStore.Hash(bytes), true) : (null, false);
    }

    private static void WriteAtomically(string path, byte[] bytes)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var temp = path + ".vision-tmp";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, path, overwrite: true);
    }
}
