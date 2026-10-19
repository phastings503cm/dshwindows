using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Vision.Core;

// MARK: - Bringing DSH's data folder over to Vision
//
// Vision was called DSH before 0.9, and kept everything in %APPDATA%\DSH (a portable copy: DSH\data). The first
// Vision launch brings that folder over to %APPDATA%\Vision (Vision\data) once, before anything else opens a file:
//
// 1. Move. The folder is renamed to "<new>.moving" and then to its new name: two renames on the same disk, each
//    all-or-nothing, so the data is always in exactly one complete folder. A marker file (data-migration.json,
//    state "rewriting") is written in between, so a crash at any point is picked up where it stopped.
// 2. Copy, when the folder can't be renamed (a file in it is open, say a terminal that still sits in a chat's
//    folder) or must stay where it is (a portable copy's old folder): copied to "<new>.copying", every file checked
//    against the original (length and SHA-256), then renamed into place. The old folder is left as it was.
// 3. Rewrite the absolute paths the data keeps of itself: skill switches and other settings that name files in the
//    folder, the folder of each chat without a project (its scratch folder), task lists' folders. Each file is
//    rewritten whole (temp file, then rename) and only when a path changes, so running it again changes nothing.
// 4. The marker's state becomes "done", last of all. Nothing is ever deleted that isn't a copy this class made.
//
// When there is nothing to bring over (a new user, or a launch after the move), Settle records that the folder is
// Vision's own with a "done" marker, so an old DSH folder that turns up later (an old copy run once, a restore from a
// backup) never displaces it. If the folder's paths still name an old folder because its data was moved over by hand,
// Settle rewrites them first.

/// <summary>How bringing the old data folder over went.</summary>
public enum DataMigrationResult
{
    /// <summary>There was no old folder (a new user), or nothing in it.</summary>
    NothingToDo,
    /// <summary>An earlier launch already brought it over.</summary>
    AlreadyDone,
    /// <summary>The old folder was renamed into place.</summary>
    Moved,
    /// <summary>The old folder was copied (and checked); the original is still there.</summary>
    Copied,
    /// <summary>It couldn't be brought over; nothing was changed (see <see cref="DataMigrationOutcome.Problem"/>).</summary>
    Failed,
    /// <summary>Nothing to bring over, but the folder's data had been moved here from the old folder by hand: the paths
    /// it kept of its old place were rewritten (<see cref="DataFolderMigration.Settle"/>).</summary>
    Adopted,
}

/// <summary>The result of <see cref="DataFolderMigration.Run"/>.</summary>
/// <param name="DataFolder">Where the user's data is now, so where the app should run this session: the new folder
/// once it is there; the old one when bringing it over failed before anything moved (it is tried again next launch);
/// null when a move stopped half-way (the data is safe in the staging folder named in <paramref name="Problem"/>, and
/// the app must not start on an empty folder — the next launch finishes the move).</param>
/// <param name="Problem">What went wrong, for <see cref="DataMigrationResult.Failed"/>, or what is left to do (the
/// paths weren't all rewritten yet; the next launch finishes).</param>
/// <param name="SetAside">A folder that was already at the new place and was renamed out of the way (never deleted).</param>
/// <param name="SkippedLinks">Links (junctions, symbolic links) inside the folder a copy couldn't recreate. The old
/// folder still has them.</param>
public sealed record DataMigrationOutcome(DataMigrationResult Result, string From, string To, string? DataFolder, string? Problem = null,
                                          string? SetAside = null, int RewrittenFiles = 0, IReadOnlyList<string>? SkippedLinks = null)
{
    public bool BroughtOver => Result is DataMigrationResult.Moved or DataMigrationResult.Copied;
}

/// <summary>What <c>data-migration.json</c> in the new folder records.</summary>
public sealed record DataMigrationMarker
{
    public const string Rewriting = "rewriting";
    public const string Done = "done";

    public const string Moved = "moved";
    public const string Copied = "copied";
    /// <summary>Nothing was brought over: the folder was Vision's own from the start.</summary>
    public const string None = "none";
    /// <summary>Nothing was brought over, but the paths of data moved here by hand were rewritten.</summary>
    public const string Adopted = "adopted";

    /// <summary><see cref="Rewriting"/> while paths are being rewritten, <see cref="Done"/> after.</summary>
    public string State { get; init; } = Rewriting;
    /// <summary><see cref="Moved"/>, <see cref="Copied"/>, <see cref="None"/> or <see cref="Adopted"/>.</summary>
    public string Method { get; init; } = Moved;
    /// <summary>The old folder.</summary>
    public string From { get; init; } = "";
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    /// <summary>The user has seen the one-time "your chats came with you" notice.</summary>
    public bool NoticeShown { get; init; }
    /// <summary>The old folder is still there (a copy): the notice says it can be deleted.</summary>
    public bool LegacyLeft { get; init; }
    public IReadOnlyList<string>? SkippedLinks { get; init; }
    /// <summary>A folder that was already at the new place, renamed out of the way and kept: the notice names it.</summary>
    public string? SetAside { get; init; }
}

/// <summary>Brings an old data folder (DSH's) over to the new one (Vision's), once. See the notes at the top of
/// this file. Not thread-safe; the app runs it once, at launch, before it opens any data file.</summary>
public sealed class DataFolderMigration
{
    /// <summary>The marker's file name in the data folder.</summary>
    public const string MarkerFileName = "data-migration.json";

    private static readonly JsonSerializerOptions MarkerJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    /// <param name="legacyRoot">The old folder (%APPDATA%\DSH, or a portable copy's DSH\data).</param>
    /// <param name="root">The new folder (%APPDATA%\Vision, or Vision\data).</param>
    /// <param name="allowMove">False: always copy, leaving the old folder as it is (a portable copy's old folder,
    /// which the old copy of the app may still run from).</param>
    public DataFolderMigration(string legacyRoot, string root, bool allowMove = true)
    {
        LegacyRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(legacyRoot));
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        AllowMove = allowMove;
    }

    public string LegacyRoot { get; }
    public string Root { get; }
    public bool AllowMove { get; }

    /// <summary>Where a rename is staged; it holds the user's data whenever it exists.</summary>
    public string MovingFolder => Root + ".moving";
    /// <summary>Where a copy is made; only ever a copy of a folder that still exists.</summary>
    public string CopyingFolder => Root + ".copying";
    public string MarkerPath => Path.Combine(Root, MarkerFileName);

    /// <summary>Renames a folder (tests make it fail, to take the copy path).</summary>
    public Action<string, string> MoveDirectory { get; init; } = Directory.Move;

    /// <summary>Called after each step with its name (tests throw from it to stand for a crash there).</summary>
    public Action<string>? AfterStep { get; init; }

    public TimeProvider Clock { get; init; } = TimeProvider.System;

    /// <summary>Whether <see cref="Run"/> has anything to do: the old folder has something in it and the new
    /// folder's marker doesn't say done, or a step was interrupted. Cheap; the app asks before deciding whether an
    /// old copy of the app that is still running must be closed first. When it is false, <see cref="Settle"/>.</summary>
    public bool IsPending
    {
        get
        {
            if (ReadMarker(MarkerPath, out var unreadable) is { State: DataMigrationMarker.Done } && !unreadable) return false;
            // An interrupted step is finished whatever is beside the folder now (a portable DSH deleted since, say).
            if (Directory.Exists(MovingFolder) || File.Exists(MarkerPath)) return true;
            return !SameFolder(LegacyRoot, Root) && StateOf(LegacyRoot) == FolderState.HasEntries;
        }
    }

    /// <summary>Bring the old folder over (see the notes at the top of this file). Never throws: a failure comes back
    /// as <see cref="DataMigrationResult.Failed"/> with the old folder untouched (or, once the data is in place, as
    /// Moved or Copied with <see cref="DataMigrationOutcome.Problem"/> set — the next launch finishes).</summary>
    public DataMigrationOutcome Run()
    {
        try
        {
            return RunSteps();
        }
        catch (Exception ex) when (!IsStepFault(ex))
        {
            if (Directory.Exists(MovingFolder))
                return new DataMigrationOutcome(DataMigrationResult.Failed, LegacyRoot, Root, null,
                    $"{ex.Message} Your data is safe in {MovingFolder}; the next start finishes moving it.");
            return new DataMigrationOutcome(DataMigrationResult.Failed, LegacyRoot, Root,
                Directory.Exists(Root) && File.Exists(MarkerPath) ? Root : LegacyRoot, ex.Message);
        }
    }

    private DataMigrationOutcome RunSteps()
    {
        var existing = ReadMarker(MarkerPath, out var unreadable);
        if (existing is { State: DataMigrationMarker.Done }) return new DataMigrationOutcome(DataMigrationResult.AlreadyDone, LegacyRoot, Root, Root);
        if (existing is not null || unreadable)
        {
            // The data is in place; the paths were being rewritten when the app stopped.
            var marker = existing ?? new DataMigrationMarker { From = LegacyRoot, StartedAt = Clock.GetUtcNow() };
            return Finish(marker, setAside: null);
        }

        string? setAside = null;
        if (Directory.Exists(MovingFolder))
        {
            // A rename got as far as the staging name: that folder is the user's data, complete.
            setAside = ClearTheWay();
            var marker = ReadMarker(Path.Combine(MovingFolder, MarkerFileName), out _) ?? NewMarker(DataMigrationMarker.Moved, legacyLeft: false, setAside);
            MoveDirectory(MovingFolder, Root);
            Step("moved-into-place");
            return Finish(marker, setAside);
        }

        if (SameFolder(LegacyRoot, Root) || StateOf(LegacyRoot) != FolderState.HasEntries)
            return new DataMigrationOutcome(DataMigrationResult.NothingToDo, LegacyRoot, Root, Root);
        setAside = ClearTheWay();

        if (AllowMove && TryMove(setAside)) return Finish(NewMarker(DataMigrationMarker.Moved, legacyLeft: false, setAside), setAside);
        return Copy(setAside);
    }

    /// <summary>Rename the old folder to the staging name, mark it, rename it into place. False when the first
    /// rename fails (nothing changed then).</summary>
    private bool TryMove(string? setAside)
    {
        try
        {
            MoveDirectory(LegacyRoot, MovingFolder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        Step("moved-to-staging");
        WriteMarker(Path.Combine(MovingFolder, MarkerFileName), NewMarker(DataMigrationMarker.Moved, legacyLeft: false, setAside));
        Step("marked-staging");
        MoveDirectory(MovingFolder, Root);
        Step("moved-into-place");
        return true;
    }

    private DataMigrationOutcome Copy(string? setAside)
    {
        var skipped = new List<string>();
        try
        {
            // Only ever a half-made copy of ours: the folder it was copied from is still here.
            if (Directory.Exists(CopyingFolder)) Directory.Delete(CopyingFolder, recursive: true);
            CopyTree(LegacyRoot, CopyingFolder, skipped);
            Step("copied");
            Verify(LegacyRoot, CopyingFolder);
            Step("verified");
            WriteMarker(Path.Combine(CopyingFolder, MarkerFileName),
                NewMarker(DataMigrationMarker.Copied, legacyLeft: true, setAside) with { SkippedLinks = skipped.Count == 0 ? null : skipped });
            MoveDirectory(CopyingFolder, Root);
            Step("moved-into-place");
        }
        catch (Exception ex) when (!IsStepFault(ex))
        {
            try { if (Directory.Exists(CopyingFolder)) Directory.Delete(CopyingFolder, recursive: true); } catch (Exception) { }
            return new DataMigrationOutcome(DataMigrationResult.Failed, LegacyRoot, Root, LegacyRoot, ex.Message, setAside);
        }
        var marker = ReadMarker(MarkerPath, out _) ?? NewMarker(DataMigrationMarker.Copied, legacyLeft: true, setAside);
        return Finish(marker, setAside);
    }

    /// <summary>The data is in the new folder: rewrite its paths, then mark it done. A problem here leaves the marker
    /// at "rewriting" (the next launch tries again) but the data stays where it is — the app runs on it, and still
    /// recognises the old paths in the meantime.</summary>
    private DataMigrationOutcome Finish(DataMigrationMarker marker, string? setAside)
    {
        var from = string.IsNullOrEmpty(marker.From) ? LegacyRoot : marker.From;
        var result = marker.Method == DataMigrationMarker.Copied ? DataMigrationResult.Copied : DataMigrationResult.Moved;
        setAside ??= marker.SetAside;
        int rewritten;
        try
        {
            rewritten = DataFolderPaths.RewriteAll(Root, from, Root);
            Step("rewritten");
            WriteMarker(MarkerPath, marker with { State = DataMigrationMarker.Done, CompletedAt = Clock.GetUtcNow(), SetAside = setAside });
            Step("done");
        }
        catch (Exception ex) when (!IsStepFault(ex))
        {
            // Whatever it was (a file in use, or something no one foresaw), the data is in place and the app can start.
            return new DataMigrationOutcome(result, from, Root, Root, ex.Message, setAside, 0, marker.SkippedLinks);
        }
        return new DataMigrationOutcome(result, from, Root, Root, null, setAside, rewritten, marker.SkippedLinks);
    }

    /// <summary>A folder already at the new place (a build of Vision that ran before this one could bring the old
    /// folder over, or one whose marker was deleted): an empty one is removed, anything else is renamed out of the way
    /// and kept — and named in the notice.</summary>
    private string? ClearTheWay()
    {
        if (!Directory.Exists(Root)) return null;
        if (StateOf(Root) != FolderState.HasEntries)
        {
            Directory.Delete(Root);
            return null;
        }
        var stamp = Clock.GetLocalNow().ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var target = $"{Root}.before-migration-{stamp}";
        for (var n = 2; Directory.Exists(target) || File.Exists(target); n++) target = $"{Root}.before-migration-{stamp}-{n}";
        MoveDirectory(Root, target);
        Step("set-aside");
        return target;
    }

    private DataMigrationMarker NewMarker(string method, bool legacyLeft, string? setAside) =>
        new() { State = DataMigrationMarker.Rewriting, Method = method, From = LegacyRoot, StartedAt = Clock.GetUtcNow(), LegacyLeft = legacyLeft, SetAside = setAside };

    /// <summary>What <see cref="AfterStep"/> threw: it stands for the app dying at that step, so it is never caught.</summary>
    private Exception? _stepFault;

    private void Step(string name)
    {
        try
        {
            AfterStep?.Invoke(name);
        }
        catch (Exception ex)
        {
            _stepFault = ex;
            throw;
        }
    }

    private bool IsStepFault(Exception ex) => ReferenceEquals(ex, _stepFault);

    // MARK: - Nothing to bring over

    /// <summary>When <see cref="IsPending"/> is false: record, with a "done" marker, that this folder is Vision's own,
    /// so an old folder that turns up later (an old copy of DSH run once, a restore from a backup) never displaces
    /// it. When the folder has no marker yet but holds data whose paths still name the old folder — moved over by
    /// hand rather than by <see cref="Run"/> — those paths are rewritten first. Never throws; when the old folder
    /// can't be looked into, it records nothing and is asked again next launch.</summary>
    public DataMigrationOutcome Settle()
    {
        try
        {
            var existing = ReadMarker(MarkerPath, out var unreadable);
            if (existing is { State: DataMigrationMarker.Done }) return new DataMigrationOutcome(DataMigrationResult.AlreadyDone, LegacyRoot, Root, Root);
            if (existing is not null || unreadable || File.Exists(MarkerPath) || Directory.Exists(MovingFolder))
                return new DataMigrationOutcome(DataMigrationResult.NothingToDo, LegacyRoot, Root, Root, "Bringing the old folder over is still pending.");
            if (!SameFolder(LegacyRoot, Root) && StateOf(LegacyRoot) is FolderState.HasEntries or FolderState.Unreadable)
                return new DataMigrationOutcome(DataMigrationResult.NothingToDo, LegacyRoot, Root, Root, $"Couldn't tell whether {LegacyRoot} holds anything.");

            var rewritten = 0;
            string? from = null;
            if (StateOf(Root) == FolderState.HasEntries)
            {
                foreach (var former in FormerRoots())
                {
                    var count = DataFolderPaths.RewriteAll(Root, former, Root);
                    if (count == 0) continue;
                    rewritten += count;
                    from ??= former;
                }
            }
            Directory.CreateDirectory(Root);
            var now = Clock.GetUtcNow();
            WriteMarker(MarkerPath, new DataMigrationMarker
            {
                State = DataMigrationMarker.Done,
                Method = from is null ? DataMigrationMarker.None : DataMigrationMarker.Adopted,
                From = from ?? "",
                StartedAt = now,
                CompletedAt = now,
                NoticeShown = true, // nothing came over that the user has to be told about
            });
            return from is null
                ? new DataMigrationOutcome(DataMigrationResult.NothingToDo, LegacyRoot, Root, Root)
                : new DataMigrationOutcome(DataMigrationResult.Adopted, from, Root, Root, RewrittenFiles: rewritten);
        }
        catch (Exception ex)
        {
            // Nothing is recorded; the next launch asks again. The folder is as it was or has some paths rewritten,
            // which rewriting again leaves as they are.
            return new DataMigrationOutcome(DataMigrationResult.NothingToDo, LegacyRoot, Root, Root, ex.Message);
        }
    }

    /// <summary>The old folders this folder's paths may still name: the old folder this was made with, and any
    /// portable DSH's data folder (…\DSH\data) its files name.</summary>
    private IEnumerable<string> FormerRoots()
    {
        var seen = new List<string>();
        if (!SameFolder(LegacyRoot, Root))
        {
            seen.Add(LegacyRoot);
            yield return LegacyRoot;
        }
        foreach (var found in DataFolderPaths.LegacyPortableRootsNamedIn(Root))
        {
            if (SameFolder(found, Root) || seen.Any(s => SameFolder(s, found))) continue;
            seen.Add(found);
            yield return found;
        }
    }

    private static bool SameFolder(string a, string b) =>
        DataFolderPaths.Rewrite(a, b, "") is { } rest && rest.Trim('\\', '/').Length == 0;

    // MARK: - The one-time notice

    /// <summary>The marker of a finished migration whose notice the user hasn't seen yet, or null.</summary>
    public static DataMigrationMarker? PendingNotice(string root)
    {
        var marker = ReadMarker(Path.Combine(root, MarkerFileName), out _);
        return marker is { State: DataMigrationMarker.Done, NoticeShown: false, Method: DataMigrationMarker.Moved or DataMigrationMarker.Copied } ? marker : null;
    }

    /// <summary>Remember that the notice was shown, so it never comes back.</summary>
    public static void MarkNoticeShown(string root)
    {
        var path = Path.Combine(root, MarkerFileName);
        if (ReadMarker(path, out _) is { } marker && !marker.NoticeShown)
            WriteMarker(path, marker with { NoticeShown = true });
    }

    /// <summary>The marker in <paramref name="root"/>, or null when there is none (or it can't be read).</summary>
    public static DataMigrationMarker? MarkerIn(string root) => ReadMarker(Path.Combine(root, MarkerFileName), out _);

    // MARK: - Files

    /// <summary>The marker at <paramref name="path"/>, or null: there is none, or (<paramref name="unreadable"/>) there
    /// is one that can't be read — not JSON, or JSON of a shape no version writes (null, a list, a number where a
    /// date belongs). An unreadable marker is never taken for "nothing here": the step it stood for is finished.</summary>
    internal static DataMigrationMarker? ReadMarker(string path, out bool unreadable)
    {
        unreadable = false;
        try
        {
            if (!File.Exists(path)) return null;
            var marker = JsonSerializer.Deserialize<DataMigrationMarker>(File.ReadAllText(path), MarkerJson);
            unreadable = marker is null; // the file says "null"
            return marker;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            unreadable = true;
            return null;
        }
    }

    internal static void WriteMarker(string path, DataMigrationMarker marker) =>
        DataFolderPaths.WriteAtomically(path, JsonSerializer.Serialize(marker, MarkerJson));

    private enum FolderState { Missing, Empty, HasEntries, Unreadable }

    private static FolderState StateOf(string folder)
    {
        try
        {
            if (!Directory.Exists(folder)) return FolderState.Missing;
            return Directory.EnumerateFileSystemEntries(folder).Any() ? FolderState.HasEntries : FolderState.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return FolderState.Unreadable;
        }
    }

    /// <summary>Copy <paramref name="source"/> into <paramref name="target"/>: every folder and file, keeping
    /// timestamps. A link (junction, symbolic link) is recreated as a link to the same place, never followed; one
    /// that can't be recreated is listed in <paramref name="skipped"/>.</summary>
    internal static void CopyTree(string source, string target, List<string> skipped)
    {
        Directory.CreateDirectory(target);
        foreach (var entry in new DirectoryInfo(source).EnumerateFileSystemInfos())
        {
            var destination = Path.Combine(target, entry.Name);
            if (entry.LinkTarget is { } link)
            {
                try
                {
                    if (entry is DirectoryInfo) Directory.CreateSymbolicLink(destination, link);
                    else File.CreateSymbolicLink(destination, link);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    skipped.Add(Path.GetRelativePath(source, entry.FullName));
                }
                continue;
            }
            if (entry is DirectoryInfo directory)
            {
                var inner = new List<string>();
                CopyTree(directory.FullName, destination, inner);
                skipped.AddRange(inner.Select(s => Path.Combine(entry.Name, s)));
                Directory.SetLastWriteTimeUtc(destination, directory.LastWriteTimeUtc);
            }
            else
            {
                File.Copy(entry.FullName, destination, overwrite: false);
            }
        }
    }

    /// <summary>Every file and folder of <paramref name="source"/> is in <paramref name="copy"/>, every file with the
    /// same length and SHA-256. Links are not followed (and not compared). Throws <see cref="InvalidDataException"/>
    /// naming the first difference.</summary>
    internal static void Verify(string source, string copy)
    {
        foreach (var entry in new DirectoryInfo(source).EnumerateFileSystemInfos())
        {
            if (entry.LinkTarget is not null) continue;
            var other = Path.Combine(copy, entry.Name);
            if (entry is DirectoryInfo directory)
            {
                if (!Directory.Exists(other)) throw new InvalidDataException($"The copy is missing the folder {entry.Name}.");
                Verify(directory.FullName, other);
                continue;
            }
            var copied = new FileInfo(other);
            if (!copied.Exists) throw new InvalidDataException($"The copy is missing {entry.Name}.");
            if (copied.Length != ((FileInfo)entry).Length || !SameContents(entry.FullName, other))
                throw new InvalidDataException($"The copy of {entry.Name} doesn't match the original.");
        }
    }

    private static bool SameContents(string a, string b)
    {
        using var first = File.OpenRead(a);
        using var second = File.OpenRead(b);
        return SHA256.HashData(first).AsSpan().SequenceEqual(SHA256.HashData(second));
    }
}

/// <summary>Rewrites the absolute paths a data folder keeps of itself, after the folder moved.</summary>
public static class DataFolderPaths
{
    /// <summary><paramref name="value"/> with the folder <paramref name="from"/> at its start replaced by
    /// <paramref name="to"/>, or null when it doesn't start with that folder. Compared the way Windows compares
    /// paths: case doesn't matter, and \ and / are the same; "…\DSH2" does not start with "…\DSH".</summary>
    public static string? Rewrite(string? value, string from, string to)
    {
        if (string.IsNullOrEmpty(value)) return null;
        // Both separators, on every platform: the paths are Windows paths even where tests run elsewhere.
        from = from.TrimEnd('\\', '/');
        to = to.TrimEnd('\\', '/');
        if (from.Length == 0 || value.Length < from.Length) return null;
        for (var i = 0; i < from.Length; i++)
        {
            var a = value[i];
            var b = from[i];
            if (IsSeparator(a) && IsSeparator(b)) continue;
            if (char.ToUpperInvariant(a) != char.ToUpperInvariant(b)) return null;
        }
        if (value.Length > from.Length && !IsSeparator(value[from.Length])) return null;
        return to + value[from.Length..];
    }

    private static bool IsSeparator(char c) => c is '\\' or '/';

    /// <summary>Rewrite every file of <paramref name="root"/> that keeps paths into the folder: settings.json (any
    /// value or key — skill switches, pinned skills, code-mode files, recent folders), each chat's folder in
    /// conversations\*.json, each task's folder in task-queue.json, and the project of each note in
    /// memory\memories.jsonl (a chat without a project files its notes under its scratch folder). A file that can't
    /// be read as JSON, or whose JSON isn't shaped the way the app writes it, is left as it is. Returns how many files
    /// changed.</summary>
    public static int RewriteAll(string root, string from, string to) => RewritePaths(root, value => Rewrite(value, from, to));

    /// <summary>The data folders of a portable DSH (…\DSH\data) that the files of <paramref name="root"/> name, as
    /// their own folder: a folder moved over by hand from a portable copy of DSH still names its old place.</summary>
    public static IReadOnlyList<string> LegacyPortableRootsNamedIn(string root)
    {
        var found = new List<string>();
        RewritePaths(root, value =>
        {
            if (LegacyPortableRootOf(value) is { } legacy && !found.Contains(legacy, StringComparer.OrdinalIgnoreCase)) found.Add(legacy);
            return null;
        });
        return found;
    }

    /// <summary>Legacy: a portable copy of DSH (Vision's former name) kept its data in a folder named data beside
    /// DSH.exe, in a folder named DSH. When <paramref name="path"/> is inside such a data folder's Chats, skills,
    /// plugins or agents, that data folder (<c>E:\DSH\data</c>); otherwise null.</summary>
    public static string? LegacyPortableRootOf(string? path)
    {
        if (string.IsNullOrEmpty(path) || path.IndexOf("DSH", StringComparison.OrdinalIgnoreCase) < 0) return null;
        var match = LegacyPortablePath.Match(path);
        return match.Success ? match.Groups["root"].Value : null;
    }

    /// <summary>An absolute path (drive, UNC share, or /) through …\DSH\data\ into one of the folders a data folder
    /// keeps paths of.</summary>
    private static readonly System.Text.RegularExpressions.Regex LegacyPortablePath = new(
        @"^(?<root>(?:[A-Za-z]:|[\\/]{2}[^\\/]+[\\/][^\\/]+)?(?:[\\/][^\\/]+)*?[\\/]DSH[\\/]data)[\\/](?:Chats|skills|plugins|agents)(?:[\\/]|$)",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>Apply <paramref name="map"/> (a path → its new value, or null to leave it) to every path the data
    /// folder's files keep (see <see cref="RewriteAll"/>); write back each file it changed. Returns how many.</summary>
    private static int RewritePaths(string root, Func<string, string?> map)
    {
        var changed = 0;
        if (RewriteFile(Path.Combine(root, "settings.json"), node => RewriteEverything(node, map), indented: true)) changed++;
        var conversations = Path.Combine(root, "conversations");
        if (Directory.Exists(conversations))
        {
            foreach (var file in Directory.EnumerateFiles(conversations, "*.json"))
            {
                if (RewriteFile(file, node => RewriteProperty((node as JsonObject)?["session"], "cwd", map), indented: false)) changed++;
            }
        }
        if (RewriteFile(Path.Combine(root, TaskQueue.FileName), node =>
            {
                var any = false;
                if ((node as JsonObject)?["tasks"] is JsonArray tasks)
                    foreach (var task in tasks) any |= RewriteProperty(task, "cwd", map);
                return any;
            }, indented: true)) changed++;
        if (RewriteMemoryLog(Path.Combine(root, "memory", MemoryStore.FileName), map)) changed++;
        return changed;
    }

    /// <summary>Parse <paramref name="path"/>, let <paramref name="rewrite"/> change it, and when it did, write it
    /// back atomically. False when nothing changed, the file isn't there, it isn't JSON, or its JSON has a shape the
    /// app never writes (a list where an object belongs, say): such a file is left as it is — the app sets a file it
    /// can't read aside itself; it isn't this class's to fix.</summary>
    private static bool RewriteFile(string path, Func<JsonNode?, bool> rewrite, bool indented)
    {
        if (!File.Exists(path)) return false;
        var text = File.ReadAllText(path);
        JsonNode? node;
        bool changed;
        try
        {
            try
            {
                node = JsonNode.Parse(text);
                changed = rewrite(node);
            }
            catch (ArgumentException)
            {
                // The same property twice in one object (a hand-edited file). JsonNode refuses that; the app's own
                // readers take the last one, so read it the way they do.
                node = ParseLastWins(text);
                changed = rewrite(node);
            }
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or FormatException)
        {
            return false;
        }
        if (!changed) return false;
        var json = node?.ToJsonString(new JsonSerializerOptions { WriteIndented = indented }) ?? "null";
        WriteAtomically(path, json);
        return true;
    }

    /// <summary><paramref name="text"/> as a tree in which a property named twice in one object keeps its last
    /// value, the way System.Text.Json's typed readers (AppConfig's, the conversation log's) read it.</summary>
    private static JsonNode? ParseLastWins(string text)
    {
        using var document = JsonDocument.Parse(text);
        return Convert(document.RootElement.Clone());

        static JsonNode? Convert(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                {
                    var obj = new JsonObject();
                    foreach (var property in element.EnumerateObject()) obj[property.Name] = Convert(property.Value);
                    return obj;
                }
                case JsonValueKind.Array:
                {
                    var array = new JsonArray();
                    foreach (var item in element.EnumerateArray()) array.Add(Convert(item));
                    return array;
                }
                case JsonValueKind.Null:
                    return null;
                default:
                    // Numbers keep their exact text; strings and true/false their values.
                    return JsonValue.Create(element);
            }
        }
    }

    /// <summary>The memory log (one JSON object per line, see <see cref="MemoryStore"/>): the project of each note
    /// that names a path <paramref name="map"/> changes. Every other line is kept exactly as it was, including lines
    /// this can't read (the store keeps those too) and a last line a crash cut off.</summary>
    private static bool RewriteMemoryLog(string path, Func<string, string?> map)
    {
        if (!File.Exists(path)) return false;
        var lines = File.ReadAllText(path).Split('\n');
        var any = false;
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].IndexOf("\"project\"", StringComparison.Ordinal) < 0) continue;
            try
            {
                if (JsonNode.Parse(lines[i]) is JsonObject entry && RewriteProperty(entry["item"], "project", map))
                {
                    // A line ending of a file edited by hand (\r\n) stays as it was.
                    lines[i] = entry.ToJsonString() + (lines[i].EndsWith('\r') ? "\r" : "");
                    any = true;
                }
            }
            catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or FormatException)
            {
                // Not a line this can make sense of: it stays as it is.
            }
        }
        if (!any) return false;
        WriteAtomically(path, string.Join('\n', lines));
        return true;
    }

    private static bool RewriteProperty(JsonNode? owner, string name, Func<string, string?> map)
    {
        if (owner is not JsonObject obj || obj[name] is not JsonValue value || !value.TryGetValue<string>(out var text)) return false;
        if (map(text) is not { } rewritten) return false;
        obj[name] = rewritten;
        return true;
    }

    /// <summary>Every string value and every property name in the tree.</summary>
    private static bool RewriteEverything(JsonNode? node, Func<string, string?> map)
    {
        var any = false;
        switch (node)
        {
            case JsonObject obj:
            {
                var entries = obj.ToList();
                var renames = entries.Any(e => map(e.Key) is not null);
                if (renames) obj.Clear();
                foreach (var (key, child) in entries)
                {
                    var value = child;
                    if (value is JsonValue leaf && leaf.TryGetValue<string>(out var text) && map(text) is { } rewritten)
                    {
                        value = JsonValue.Create(rewritten);
                        if (!renames) obj[key] = value;
                        any = true;
                    }
                    else
                    {
                        any |= RewriteEverything(value, map);
                    }
                    if (!renames) continue;
                    var newKey = map(key) ?? key;
                    // The same folder named both ways (once under each name): the first one stays.
                    if (!obj.ContainsKey(newKey)) obj[newKey] = value;
                }
                any |= renames;
                break;
            }
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    if (array[i] is JsonValue leaf && leaf.TryGetValue<string>(out var text) && map(text) is { } rewritten)
                    {
                        array[i] = JsonValue.Create(rewritten);
                        any = true;
                    }
                    else
                    {
                        any |= RewriteEverything(array[i], map);
                    }
                }
                break;
        }
        return any;
    }

    /// <summary>Write <paramref name="text"/> to a temporary file beside <paramref name="path"/>, then rename it over
    /// <paramref name="path"/>: a crash leaves either the old file or the new one, never half of one.</summary>
    internal static void WriteAtomically(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".migrating";
        File.WriteAllText(temp, text, TextUtil.Utf8NoBom);
        File.Move(temp, path, overwrite: true);
    }
}
