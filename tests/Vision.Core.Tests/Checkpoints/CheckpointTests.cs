using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace Vision.Core.Tests;

/// <summary>Checkpoints and Rewind (10-19 #20): what the file tools and shell commands change is kept, the turn's record says
/// what changed (in Vision for macOS's JSON), and a rewind puts the files back — asking about any that changed since — as a
/// turn of its own.</summary>
public sealed class CheckpointTests : IDisposable
{
    private readonly TempDirectory _data = new("vision-checkpoints");
    private readonly ToolTestContext _tools = new();
    private CheckpointStore Store => new(_data["checkpoints"]);

    public void Dispose()
    {
        _data.Dispose();
        _tools.Dispose();
    }

    private (TurnRecorder Recorder, ToolContext Context) Open(string title = "Fix the stock bug", string turnId = "turn-1")
    {
        var recorder = new TurnRecorder(Store, "chat-1", turnId, _tools.Root.Path, title);
        return (recorder, _tools.Context with { Checkpoint = recorder });
    }

    private static string Text(byte[]? bytes) => Encoding.UTF8.GetString(bytes!);

    [Fact]
    public async Task FileToolsKeepEachFileAsItWasBeforeItsFirstChangeInTheTurn()
    {
        var stock = _tools.Root.Write("src/Stock.cs", "line one\nline two\n");
        var (recorder, context) = Open();
        await new EditTool().ExecuteAsync(Args.Json(new { file_path = "src/Stock.cs", old_string = "line two", new_string = "line 2" }), context, default);
        await new EditTool().ExecuteAsync(Args.Json(new { file_path = "src/Stock.cs", old_string = "line one", new_string = "line 1" }), context, default);
        await new WriteFileTool().ExecuteAsync(Args.Json(new { file_path = "src/New.cs", content = "new\n" }), context, default);

        var turn = recorder.Finish();

        Assert.NotNull(turn);
        Assert.Equal("Files changed (2)", turn.Heading);
        var edited = turn.Files.Single(f => f.Path == stock);
        Assert.Equal(CheckpointChange.Modified, edited.Change);
        Assert.Equal("line one\nline two\n", Text(Store.Get(edited.Before!)));
        Assert.Equal("line 1\nline 2\n", Text(Store.Get(edited.After!)));
        Assert.Equal((2, 2), (edited.Added, edited.Removed));
        Assert.Equal("edit", edited.By);
        var created = turn.Files.Single(f => f.Path.EndsWith("New.cs", StringComparison.Ordinal));
        Assert.Equal(CheckpointChange.Created, created.Change);
        Assert.Null(created.Before);
        Assert.Equal("Fix the stock bug", Store.Load("chat-1", "turn-1")!.Title);
    }

    [Fact]
    public void TheRecordIsVisionForMacsJson()
    {
        var file = _tools.Root.Write("a.txt", "a");
        var (recorder, _) = Open("Make it so", "turn-7");
        recorder.BeforeChange(file, "write_file");
        File.WriteAllText(file, "b");
        recorder.Finish(new DateTimeOffset(2026, 10, 19, 12, 30, 15, 123, TimeSpan.FromHours(-7)));

        var json = JsonNode.Parse(File.ReadAllText(_data["checkpoints/turns/chat-1/turn-7.json"]))!.AsObject();

        Assert.Equal(1, json["version"]!.GetValue<int>());
        Assert.Equal("turn-7", json["id"]!.GetValue<string>());
        Assert.Equal("chat-1", json["chatID"]!.GetValue<string>());
        Assert.Equal("Make it so", json["title"]!.GetValue<string>());
        // Swift's ISO 8601: UTC, whole seconds.
        Assert.Equal("2026-10-19T19:30:15Z", json["updatedAt"]!.GetValue<string>());
        Assert.Matches(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ$", json["startedAt"]!.GetValue<string>());
        var entry = json["files"]![0]!.AsObject();
        Assert.Equal(file, entry["path"]!.GetValue<string>());
        var a = CheckpointStore.Hash("a"u8.ToArray());
        Assert.Equal(a, entry["before"]!.GetValue<string>());
        Assert.Equal(CheckpointStore.Hash("b"u8.ToArray()), entry["after"]!.GetValue<string>());
        Assert.Equal("write_file", entry["by"]!.GetValue<string>());
        Assert.False(entry.ContainsKey("skipped"));
        Assert.True(File.Exists(_data[$"checkpoints/objects/{a[..2]}/{a}"]));
    }

    [Fact]
    public void ATurnThatChangesNothingOrChangesAFileBackHasNoRecord()
    {
        var file = _tools.Root.Write("a.txt", "same");
        var (recorder, _) = Open();
        recorder.BeforeChange(file, "edit");
        File.WriteAllText(file, "different");
        File.WriteAllText(file, "same");

        Assert.Null(recorder.Finish());
        Assert.Empty(Store.List("chat-1"));
    }

    [Fact]
    public async Task RewindPutsFilesBackRemovesCreatedOnesAndIsATurnOfItsOwn()
    {
        var stock = _tools.Root.Write("src/Stock.cs", "original\n");
        var (recorder, context) = Open();
        await new WriteFileTool().ExecuteAsync(Args.Json(new { file_path = "src/Stock.cs", content = "changed\n" }), context, default);
        await new WriteFileTool().ExecuteAsync(Args.Json(new { file_path = "src/Extra.cs", content = "extra\n" }), context, default);
        var turn = recorder.Finish()!;

        var plan = CheckpointRewind.Plan(Store, turn);
        Assert.Empty(plan.Conflicts);
        Assert.Equal(2, plan.Safe.Count);
        Assert.Equal("1 file go back to how they were before this turn (src/Stock.cs).\n\n1 file it created is removed (src/Extra.cs).",
            CheckpointRewind.Confirmation(plan, _tools.Root.Path));
        var rewind = new TurnRecorder(Store, "chat-1", "rewind-1", _tools.Root.Path, $"Rewind of “{turn.Title}”");
        var result = CheckpointRewind.Apply(Store, plan, includeConflicts: false, rewind);

        Assert.Equal("↺ Rewound “Fix the stock bug”: put back 1 file (src/Stock.cs) and removed 1 file it created (src/Extra.cs).",
            CheckpointRewind.Summary(result, turn.Title, _tools.Root.Path));
        Assert.Equal("original\n", File.ReadAllText(stock));
        Assert.False(File.Exists(_tools.Root["src/Extra.cs"]));
        Assert.True(Store.Load("chat-1", "turn-1")!.IsRewound);
        Assert.Contains("the user rewound the file changes of an earlier turn (“Fix the stock bug”). These files are back to how they were before it: src/Stock.cs, src/Extra.cs.",
            CheckpointRewind.ModelNote(result, turn.Title, _tools.Root.Path));

        // The rewind is a turn of its own: rewinding it puts the turn's changes back.
        var undo = rewind.Finish()!;
        Assert.Equal("Rewind of “Fix the stock bug”", undo.Title);
        Assert.Equal(2, undo.Changed.Count);
        CheckpointRewind.Apply(Store, CheckpointRewind.Plan(Store, undo), includeConflicts: false);
        Assert.Equal("changed\n", File.ReadAllText(stock));
        Assert.Equal("extra\n", File.ReadAllText(_tools.Root["src/Extra.cs"]));
    }

    [Fact]
    public async Task AFileChangedSinceTheTurnIsAConflictLeftAloneUnlessRewindAllIsChosen()
    {
        var a = _tools.Root.Write("a.txt", "a0");
        var b = _tools.Root.Write("b.txt", "b0");
        var (recorder, context) = Open();
        await new WriteFileTool().ExecuteAsync(Args.Json(new { file_path = "a.txt", content = "a1" }), context, default);
        await new WriteFileTool().ExecuteAsync(Args.Json(new { file_path = "b.txt", content = "b1" }), context, default);
        var turn = recorder.Finish()!;
        File.WriteAllText(b, "b2 — edited afterwards");

        var plan = CheckpointRewind.Plan(Store, turn);
        var conflict = Assert.Single(plan.Conflicts);
        Assert.Equal((b, "changed since this turn"), (conflict.Path, conflict.Conflict));
        Assert.Contains("1 file changed since this turn (b.txt): rewinding it throws those later changes away.", CheckpointRewind.Confirmation(plan, _tools.Root.Path));
        var onlyOthers = CheckpointRewind.Apply(Store, plan, includeConflicts: false);
        Assert.Equal("a0", File.ReadAllText(a));
        Assert.StartsWith("b2", File.ReadAllText(b));
        Assert.EndsWith("Left 1 file as they are (b.txt).", CheckpointRewind.Summary(onlyOthers, turn.Title, _tools.Root.Path));

        CheckpointRewind.Apply(Store, CheckpointRewind.Plan(Store, Store.Load("chat-1", "turn-1")!), includeConflicts: true);
        Assert.Equal("b0", File.ReadAllText(b));
    }

    [Fact]
    public void AFileAlreadyBackNeedsNothingADeletedOneComesBackAndATooLargeOneIsSkipped()
    {
        var keep = _tools.Root.Write("keep.txt", "k0");
        var gone = _tools.Root.Write("gone.txt", "g0");
        var big = _tools.Root["big.bin"];
        File.WriteAllBytes(big, new byte[CheckpointStore.MaxFileBytes + 1]);
        var (recorder, _) = Open();
        recorder.BeforeChange(keep, "edit");
        File.WriteAllText(keep, "k1");
        recorder.BeforeChange(gone, "run_shell_command");
        File.Delete(gone);
        recorder.BeforeChange(big, "write_file");
        File.WriteAllBytes(big, [1, 2, 3]);
        var turn = recorder.Finish()!;
        Assert.Equal(CheckpointChange.Deleted, turn.Files.Single(f => f.Path == gone).Change);
        Assert.StartsWith("too large to keep", turn.Files.Single(f => f.Path == big).Skipped);
        File.WriteAllText(keep, "k0"); // put back by hand

        var plan = CheckpointRewind.Plan(Store, turn);
        Assert.True(plan.Items.Single(i => i.Path == keep).AlreadyThere);
        Assert.Equal(big, Assert.Single(plan.Skipped).Path);
        CheckpointRewind.Apply(Store, plan, includeConflicts: false);
        Assert.Equal("g0", File.ReadAllText(gone));
        Assert.Equal(3, new FileInfo(big).Length);
    }

    [Fact]
    public void AChangeAfterTheTurnEndedJoinsItAndRewindsOnlyWhenConfirmed()
    {
        var file = _tools.Root.Write("late.txt", "before");
        var other = _tools.Root.Write("first.txt", "1");
        var (recorder, _) = Open();
        recorder.BeforeChange(other, "edit");
        File.WriteAllText(other, "2");
        recorder.Finish();
        // A background agent of that turn writes after it ended.
        recorder.BeforeChange(file, "edit");
        File.WriteAllText(file, "after");

        var saved = Store.Load("chat-1", "turn-1")!;
        Assert.True(saved.Files.Single(f => f.Path == file).IsLate);
        Assert.Contains(CheckpointRewind.Plan(Store, saved).Conflicts, c => c.Path == file);
    }

    [Fact]
    public void PruningDropsOldRecordsThenTheOldestPastTheCapButNeverRecentContents()
    {
        var store = Store;
        var now = DateTimeOffset.Now;
        void Make(string id, DateTimeOffset at, string content)
        {
            var path = _tools.Root.Write(id + ".txt", content);
            var recorder = new TurnRecorder(store, "chat-1", id, _tools.Root.Path, id, at);
            recorder.BeforeChange(path, "edit");
            File.WriteAllText(path, content + " changed");
            recorder.Finish(at);
        }
        Make("old", now.AddDays(-40), new string('o', 1000));
        Make("middle", now.AddDays(-2), new string('m', 1000));
        Make("new", now.AddHours(-1), new string('n', 1000));

        // The contents were all written just now: they stay this time (a turn may still be using them)...
        var first = store.Prune(TimeSpan.FromDays(30), long.MaxValue, now);
        Assert.Equal(1, first.Records);
        Assert.Equal(0, first.Objects);
        Assert.Equal(["new", "middle"], store.List("chat-1").Select(t => t.Id));
        // ...and go once they are older than the grace period.
        var later = store.Prune(TimeSpan.FromDays(30), long.MaxValue, now.AddHours(2));
        Assert.Equal(2, later.Objects);

        // A cap smaller than both records: the older goes; the newest always stays.
        var capped = store.Prune(TimeSpan.FromDays(30), 10, now.AddHours(2));
        Assert.Equal(1, capped.Records);
        Assert.Equal("new", Assert.Single(store.List("chat-1")).Id);
    }

    [Fact]
    public void ATurnStillRecordingKeepsItsContentsThroughAPrune()
    {
        var store = Store;
        var file = _tools.Root.Write("wip.txt", "work in progress");
        var recorder = new TurnRecorder(store, "chat-1", "running", _tools.Root.Path, "still going");
        recorder.BeforeChange(file, "edit");
        var hash = recorder.Snapshot.Files.Single().Before!;

        store.Prune(TimeSpan.FromDays(30), long.MaxValue, DateTimeOffset.Now.AddHours(3));

        Assert.True(store.Has(hash));
    }

    [Fact]
    public void ContentIsStoredOnceHoweverOftenItIsKept()
    {
        var store = Store;
        Assert.Equal(store.Put("same bytes"u8.ToArray()), store.Put("same bytes"u8.ToArray()));
        Assert.Single(Directory.EnumerateFiles(_data["checkpoints/objects"], "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task TheEngineRecordsIntoTheTurnItsHostOpened()
    {
        _tools.Root.Write("notes.md", "v1\n");
        var slot = new CheckpointSlot();
        var recorder = new TurnRecorder(Store, "chat-1", "turn-9", _tools.Root.Path, "update notes");
        slot.Current = recorder;
        var client = new ScriptedClient(
            Turn.Calling(new ToolCall("c1", WriteFileTool.ToolName, Args.Json(new { file_path = "notes.md", content = "v2\n" }))),
            new Turn("done"));
        var engine = new Engine(client, ToolRegistry.Standard(), "sys", new EngineConfig("m"), _tools.Root.Path,
            new PermissionPolicy(PermissionPreset.WorkspaceWrite, _tools.Root.Path), static (_, _, _) => Task.FromResult(true))
        {
            Checkpoints = slot,
        };
        await engine.RunAsync([], "go");

        Assert.Equal("v1\n", Text(Store.Get(recorder.Finish()!.Files.Single().Before!)));
    }

    [Fact]
    public void DiffsAreUnifiedWithTheirPaths()
    {
        var file = _tools.Root.Write("src/a.txt", "one\ntwo\nthree\n");
        var (recorder, _) = Open();
        recorder.BeforeChange(file, "edit");
        File.WriteAllText(file, "one\n2\nthree\nfour\n");
        var turn = recorder.Finish()!;

        Assert.Equal("--- a/src/a.txt\n+++ b/src/a.txt\n@@ -1,3 +1,4 @@\n one\n-two\n+2\n three\n+four\n",
            CheckpointRewind.Diff(Store, turn.Files.Single(), _tools.Root.Path));
    }

    [Theory]
    [InlineData("Fix the build\nand the tests", "Fix the build")]
    [InlineData("   \n  Second line first  ", "Second line first")]
    [InlineData("", "Carrying on")]
    public void ATurnsTitleIsItsPromptsFirstLine(string prompt, string title) => Assert.Equal(title, TurnCheckpoint.TitleOf(prompt));

    // MARK: Shell commands, through git

    private static bool HasGit()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("git", "--version") { RedirectStandardOutput = true, UseShellExecute = false });
            process!.WaitForExit(5000);
            return process.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void Git(params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = _tools.Root.Path, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        start.Environment["GIT_AUTHOR_NAME"] = start.Environment["GIT_COMMITTER_NAME"] = "Test";
        start.Environment["GIT_AUTHOR_EMAIL"] = start.Environment["GIT_COMMITTER_EMAIL"] = "test@example.com";
        using var process = Process.Start(start)!;
        process.WaitForExit(20_000);
        Assert.Equal(0, process.ExitCode);
    }

    [Fact]
    public void AShellCommandsChangesToTrackedFilesAreFoundThroughGit()
    {
        if (!HasGit()) return; // CI and developer machines have git; without it shell changes simply aren't attributed.
        var clean = _tools.Root.Write("clean.txt", "clean v1\n");
        var dirty = _tools.Root.Write("dirty.txt", "dirty v1\n");
        var removed = _tools.Root.Write("removed.txt", "removed v1\n");
        Git("init", "-q");
        Git("add", ".");
        Git("commit", "-q", "-m", "start");
        File.WriteAllText(dirty, "dirty v2 (uncommitted, before the command)\n");

        var (recorder, _) = Open();
        var before = recorder.BeforeShell(_tools.Root.Path);
        Assert.NotNull(before);
        // "The command": it edits a clean file and the dirty one, deletes one, and makes an untracked one.
        File.WriteAllText(clean, "clean v2\n");
        File.WriteAllText(dirty, "dirty v3\n");
        File.Delete(removed);
        _tools.Root.Write("untracked.txt", "new");
        var found = recorder.AfterShell(before, "run_shell_command");

        Assert.Equal(3, found.Count);
        var turn = recorder.Finish()!;
        string Before(string path) => Text(Store.Get(turn.Files.Single(f => f.Path == Path.GetFullPath(path)).Before!));
        Assert.Equal("clean v1\n", Before(clean));
        Assert.Equal("dirty v2 (uncommitted, before the command)\n", Before(dirty));
        Assert.Equal("removed v1\n", Before(removed));
        Assert.DoesNotContain(turn.Files, f => f.Path.EndsWith("untracked.txt", StringComparison.Ordinal));

        CheckpointRewind.Apply(Store, CheckpointRewind.Plan(Store, turn), includeConflicts: false);
        Assert.Equal("clean v1\n", File.ReadAllText(clean));
        Assert.Equal("dirty v2 (uncommitted, before the command)\n", File.ReadAllText(dirty));
        Assert.Equal("removed v1\n", File.ReadAllText(removed));
    }

    [Fact]
    public void OutsideARepositoryAShellCommandIsntTracked()
    {
        var (recorder, _) = Open();
        Assert.Null(recorder.BeforeShell(_tools.Root.Path));
    }

    [Fact]
    public void GitStatusPathsIncludeBothSidesOfARename() =>
        Assert.Equal(["a.txt", "new name.txt", "old.txt"], GitFiles.ParseStatus(" M a.txt\0R  new name.txt\0old.txt\0"));
}
