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

    /// <summary><see cref="Rewriting"/> while paths are being rewritten, <see cref="Done"/> after.</summary>
    public string State { get; init; } = Rewriting;
    /// <summary>"moved" or "copied".</summary>
    public string Method { get; init; } = "moved";
    /// <summary>The old folder.</summary>
    public string From { get; init; } = "";
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    /// <summary>The user has seen the one-time "your chats came with you" notice.</summary>
    public bool NoticeShown { get; init; }
    /// <summary>The old folder is still there (a copy): the notice says it can be deleted.</summary>
    public bool LegacyLeft { get; init; }
    public IReadOnlyList<string>? SkippedLinks { get; init; }
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
    /// old copy of the app that is still running must be closed first.</summary>
    public bool IsPending
    {
        get
        {
            if (ReadMarker(MarkerPath, out var unreadable) is { State: DataMigrationMarker.Done } && !unreadable) return false;
            return Directory.Exists(MovingFolder) || File.Exists(MarkerPath) || HasEntries(LegacyRoot);
        }
    }

    /// <summary>Bring the old folder over (see the notes at the top of this file). Never throws for an I/O
    /// problem: a failure comes back as <see cref="DataMigrationResult.Failed"/>, with the old folder untouched.</summary>
    public DataMigrationOutcome Run()
    {
        try
        {
            return RunSteps();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
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
        if (string.Equals(LegacyRoot, Root, StringComparison.OrdinalIgnoreCase))
            return new DataMigrationOutcome(DataMigrationResult.NothingToDo, LegacyRoot, Root, Root);
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
            var marker = ReadMarker(Path.Combine(MovingFolder, MarkerFileName), out _) ?? NewMarker("moved", legacyLeft: false);
            MoveDirectory(MovingFolder, Root);
            Step("moved-into-place");
            return Finish(marker, setAside);
        }

        if (!HasEntries(LegacyRoot)) return new DataMigrationOutcome(DataMigrationResult.NothingToDo, LegacyRoot, Root, Root);
        setAside = ClearTheWay();

        if (AllowMove && TryMove()) return Finish(NewMarker("moved", legacyLeft: false), setAside);
        return Copy(setAside);
    }

    /// <summary>Rename the old folder to the staging name, mark it, rename it into place. False when the first
    /// rename fails (nothing changed then).</summary>
    private bool TryMove()
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
        WriteMarker(Path.Combine(MovingFolder, MarkerFileName), NewMarker("moved", legacyLeft: false));
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
                NewMarker("copied", legacyLeft: true) with { SkippedLinks = skipped.Count == 0 ? null : skipped });
            MoveDirectory(CopyingFolder, Root);
            Step("moved-into-place");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            try { if (Directory.Exists(CopyingFolder)) Directory.Delete(CopyingFolder, recursive: true); } catch (Exception) { }
            return new DataMigrationOutcome(DataMigrationResult.Failed, LegacyRoot, Root, LegacyRoot, ex.Message, setAside);
        }
        var marker = ReadMarker(MarkerPath, out _) ?? NewMarker("copied", legacyLeft: true);
        return Finish(marker, setAside);
    }

    /// <summary>The data is in the new folder: rewrite its paths, then mark it done. A problem here leaves the marker
    /// at "rewriting" (the next launch tries again) but the data stays where it is — the app runs on it, and still
    /// recognises the old paths in the meantime.</summary>
    private DataMigrationOutcome Finish(DataMigrationMarker marker, string? setAside)
    {
        var from = string.IsNullOrEmpty(marker.From) ? LegacyRoot : marker.From;
        var result = marker.Method == "copied" ? DataMigrationResult.Copied : DataMigrationResult.Moved;
        int rewritten;
        try
        {
            rewritten = DataFolderPaths.RewriteAll(Root, from, Root);
            Step("rewritten");
            WriteMarker(MarkerPath, marker with { State = DataMigrationMarker.Done, CompletedAt = Clock.GetUtcNow() });
            Step("done");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new DataMigrationOutcome(result, from, Root, Root, ex.Message, setAside, 0, marker.SkippedLinks);
        }
        return new DataMigrationOutcome(result, from, Root, Root, null, setAside, rewritten, marker.SkippedLinks);
    }

    /// <summary>A folder already at the new place (a build of Vision that ran before this one could bring the old
    /// folder over): an empty one is removed, anything else is renamed out of the way and kept.</summary>
    private string? ClearTheWay()
    {
        if (!Directory.Exists(Root)) return null;
        if (!HasEntries(Root))
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

    private DataMigrationMarker NewMarker(string method, bool legacyLeft) =>
        new() { State = DataMigrationMarker.Rewriting, Method = method, From = LegacyRoot, StartedAt = Clock.GetUtcNow(), LegacyLeft = legacyLeft };

    private void Step(string name) => AfterStep?.Invoke(name);

    // MARK: - The one-time notice

    /// <summary>The marker of a finished migration whose notice the user hasn't seen yet, or null.</summary>
    public static DataMigrationMarker? PendingNotice(string root)
    {
        var marker = ReadMarker(Path.Combine(root, MarkerFileName), out _);
        return marker is { State: DataMigrationMarker.Done, NoticeShown: false } ? marker : null;
    }

    /// <summary>Remember that the notice was shown, so it never comes back.</summary>
    public static void MarkNoticeShown(string root)
    {
        var path = Path.Combine(root, MarkerFileName);
        if (ReadMarker(path, out _) is { } marker && !marker.NoticeShown)
            WriteMarker(path, marker with { NoticeShown = true });
    }

    // MARK: - Files

    internal static DataMigrationMarker? ReadMarker(string path, out bool unreadable)
    {
        unreadable = false;
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<DataMigrationMarker>(File.ReadAllText(path), MarkerJson);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            unreadable = true;
            return null;
        }
    }

    internal static void WriteMarker(string path, DataMigrationMarker marker) =>
        DataFolderPaths.WriteAtomically(path, JsonSerializer.Serialize(marker, MarkerJson));

    private static bool HasEntries(string folder)
    {
        try
        {
            return Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
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
    /// conversations\*.json, and each task's folder in task-queue.json. A file that can't be read as JSON is left
    /// as it is. Returns how many files changed.</summary>
    public static int RewriteAll(string root, string from, string to)
    {
        var changed = 0;
        if (RewriteFile(Path.Combine(root, "settings.json"), node => RewriteEverything(node, from, to), indented: true)) changed++;
        var conversations = Path.Combine(root, "conversations");
        if (Directory.Exists(conversations))
        {
            foreach (var file in Directory.EnumerateFiles(conversations, "*.json"))
            {
                if (RewriteFile(file, node => RewriteProperty(node?["session"], "cwd", from, to), indented: false)) changed++;
            }
        }
        if (RewriteFile(Path.Combine(root, TaskQueue.FileName), node =>
            {
                var any = false;
                if (node?["tasks"] is JsonArray tasks)
                    foreach (var task in tasks) any |= RewriteProperty(task, "cwd", from, to);
                return any;
            }, indented: true)) changed++;
        return changed;
    }

    /// <summary>Parse <paramref name="path"/>, let <paramref name="rewrite"/> change it, and when it did, write it
    /// back atomically. False when nothing changed, the file isn't there, or it isn't JSON.</summary>
    private static bool RewriteFile(string path, Func<JsonNode?, bool> rewrite, bool indented)
    {
        if (!File.Exists(path)) return false;
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            return false; // the app sets an unreadable file aside itself; it isn't this class's to fix
        }
        if (!rewrite(node)) return false;
        var text = node?.ToJsonString(new JsonSerializerOptions { WriteIndented = indented }) ?? "null";
        WriteAtomically(path, text);
        return true;
    }

    private static bool RewriteProperty(JsonNode? owner, string name, string from, string to)
    {
        if (owner is not JsonObject obj || obj[name] is not JsonValue value || !value.TryGetValue<string>(out var text)) return false;
        if (Rewrite(text, from, to) is not { } rewritten) return false;
        obj[name] = rewritten;
        return true;
    }

    /// <summary>Every string value and every property name in the tree.</summary>
    private static bool RewriteEverything(JsonNode? node, string from, string to)
    {
        var any = false;
        switch (node)
        {
            case JsonObject obj:
            {
                var entries = obj.ToList();
                var renames = entries.Any(e => Rewrite(e.Key, from, to) is not null);
                if (renames) obj.Clear();
                foreach (var (key, child) in entries)
                {
                    var value = child;
                    if (value is JsonValue leaf && leaf.TryGetValue<string>(out var text) && Rewrite(text, from, to) is { } rewritten)
                    {
                        value = JsonValue.Create(rewritten);
                        if (!renames) obj[key] = value;
                        any = true;
                    }
                    else
                    {
                        any |= RewriteEverything(value, from, to);
                    }
                    if (!renames) continue;
                    var newKey = Rewrite(key, from, to) ?? key;
                    // The same folder named both ways (once under each name): the first one stays.
                    if (!obj.ContainsKey(newKey)) obj[newKey] = value;
                }
                any |= renames;
                break;
            }
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    if (array[i] is JsonValue leaf && leaf.TryGetValue<string>(out var text) && Rewrite(text, from, to) is { } rewritten)
                    {
                        array[i] = JsonValue.Create(rewritten);
                        any = true;
                    }
                    else
                    {
                        any |= RewriteEverything(array[i], from, to);
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
