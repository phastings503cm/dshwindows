using System.Diagnostics;

namespace Vision.Core.Tests;

/// <summary>Checkpoints and Rewind (10-19 #20): what the file tools and shell commands change is kept, the turn's manifest says
/// what changed, and a rewind puts the files back — asking about any that changed since.</summary>
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

    private (TurnRecorder Recorder, ToolContext Context) Open(string prompt = "Fix the stock bug", string turnId = "turn-1")
    {
        var recorder = new TurnRecorder(Store, "chat-1", turnId, _tools.Root.Path, prompt);
        return (recorder, _tools.Context with { Checkpoint = recorder });
    }

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
        Assert.Equal("Files changed (2)", turn.Title);
        var edited = turn.Files.Single(f => f.Path == stock);
        Assert.Equal(CheckpointChange.Modified, edited.Change);
        Assert.Equal("line one\nline two\n", System.Text.Encoding.UTF8.GetString(Store.Get(edited.Before!)!));
        Assert.Equal((2, 2), (edited.Added, edited.Removed));
        Assert.Equal("edit", edited.Source);
        var created = turn.Files.Single(f => f.Path.EndsWith("New.cs", StringComparison.Ordinal));
        Assert.Equal(CheckpointChange.Created, created.Change);
        Assert.Null(created.Before);
        // The manifest is on disk under the chat and turn.
        Assert.Equal("Fix the stock bug", Store.Load("chat-1", "turn-1")!.Prompt);
    }

    [Fact]
    public void ATurnThatChangesNothingOrChangesAFileBackHasNoCheckpoint()
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
    public async Task RewindPutsModifiedFilesBackAndDeletesCreatedOnesThenUndoReturnsThem()
    {
        var stock = _tools.Root.Write("src/Stock.cs", "original\n");
        var (recorder, context) = Open();
        await new WriteFileTool().ExecuteAsync(Args.Json(new { file_path = "src/Stock.cs", content = "changed\n" }), context, default);
        await new WriteFileTool().ExecuteAsync(Args.Json(new { file_path = "src/Extra.cs", content = "extra\n" }), context, default);
        var turn = recorder.Finish()!;

        var plan = CheckpointRewind.Plan(Store, turn);
        Assert.Empty(plan.Conflicts);
        Assert.Equal(2, plan.Actionable);
        var result = CheckpointRewind.Apply(Store, plan, overwriteConflicts: false);

        Assert.Equal((1, 1), (result.Restored, result.Deleted));
        Assert.Equal("Rewound 2 files to before this turn.", result.Summary);
        Assert.Equal("original\n", File.ReadAllText(stock));
        Assert.False(File.Exists(_tools.Root["src/Extra.cs"]));
        var saved = Store.Load("chat-1", "turn-1")!;
        Assert.True(saved.IsRewound);
        Assert.Equal(2, saved.RewoundCount);

        var undone = CheckpointRewind.Undo(Store, saved);
        Assert.Equal(2, undone.Changed);
        Assert.Equal("changed\n", File.ReadAllText(stock));
        Assert.Equal("extra\n", File.ReadAllText(_tools.Root["src/Extra.cs"]));
        Assert.False(Store.Load("chat-1", "turn-1")!.IsRewound);
    }

    [Fact]
    public async Task AFileChangedSinceTheTurnIsAConflictAndIsLeftAloneUnlessOverwriteIsChosen()
    {
        var a = _tools.Root.Write("a.txt", "a0");
        var b = _tools.Root.Write("b.txt", "b0");
        var (recorder, context) = Open();
        await new WriteFileTool().ExecuteAsync(Args.Json(new { file_path = "a.txt", content = "a1" }), context, default);
        await new WriteFileTool().ExecuteAsync(Args.Json(new { file_path = "b.txt", content = "b1" }), context, default);
        var turn = recorder.Finish()!;
        File.WriteAllText(b, "b2 — the person edited it afterwards");

        var plan = CheckpointRewind.Plan(Store, turn);
        Assert.Equal(b, Assert.Single(plan.Conflicts).File.Path);
        var skipped = CheckpointRewind.Apply(Store, plan, overwriteConflicts: false);
        Assert.Equal(1, skipped.Skipped);
        Assert.Equal("a0", File.ReadAllText(a));
        Assert.StartsWith("b2", File.ReadAllText(b));
        Assert.Contains("Left 1 file that changed since as it is.", skipped.Summary);

        var again = CheckpointRewind.Plan(Store, Store.Load("chat-1", "turn-1")!);
        CheckpointRewind.Apply(Store, again, overwriteConflicts: true);
        Assert.Equal("b0", File.ReadAllText(b));
    }

    [Fact]
    public void AFileAlreadyBackAsItWasNeedsNothingAndADeletedFileIsRecreated()
    {
        var keep = _tools.Root.Write("keep.txt", "k0");
        var gone = _tools.Root.Write("gone.txt", "g0");
        var (recorder, _) = Open();
        recorder.BeforeChange(keep, "edit");
        File.WriteAllText(keep, "k1");
        recorder.BeforeChange(gone, "run_shell_command");
        File.Delete(gone);
        var turn = recorder.Finish()!;
        Assert.Equal(CheckpointChange.Deleted, turn.Files.Single(f => f.Path == gone).Change);
        File.WriteAllText(keep, "k0"); // the person undid it by hand

        var plan = CheckpointRewind.Plan(Store, turn);
        Assert.Equal(RewindAction.Nothing, plan.Items.Single(i => i.File.Path == keep).Action);
        Assert.Equal(RewindAction.Restore, plan.Items.Single(i => i.File.Path == gone).Action);
        CheckpointRewind.Apply(Store, plan, overwriteConflicts: false);
        Assert.Equal("g0", File.ReadAllText(gone));
    }

    [Fact]
    public void AChangeAfterTheTurnEndedJoinsItMarkedUnknownAndRewindsOnlyWhenConfirmed()
    {
        var file = _tools.Root.Write("late.txt", "before");
        var (recorder, _) = Open();
        var other = _tools.Root.Write("first.txt", "1");
        recorder.BeforeChange(other, "edit");
        File.WriteAllText(other, "2");
        recorder.Finish();
        // A background agent of that turn writes after it ended.
        recorder.BeforeChange(file, "edit");
        File.WriteAllText(file, "after");

        var saved = Store.Load("chat-1", "turn-1")!;
        var late = saved.Files.Single(f => f.Path == file);
        Assert.False(late.AfterKnown);
        var plan = CheckpointRewind.Plan(Store, saved);
        Assert.Contains(plan.Conflicts, c => c.File.Path == file);
    }

    [Fact]
    public void PruningDropsOldTurnsThenTheOldestPastTheCapThenUnnamedBlobs()
    {
        var store = Store;
        var now = DateTimeOffset.Now;
        string Make(string id, DateTimeOffset at, string content)
        {
            var path = _tools.Root.Write(id + ".txt", content);
            var recorder = new TurnRecorder(store, "chat-1", id, _tools.Root.Path, id, at);
            recorder.BeforeChange(path, "edit");
            File.WriteAllText(path, content + " changed");
            recorder.Finish(at);
            return path;
        }
        Make("old", now.AddDays(-40), new string('o', 1000));
        Make("middle", now.AddDays(-2), new string('m', 1000));
        Make("new", now.AddHours(-1), new string('n', 1000));

        var first = store.Prune(TimeSpan.FromDays(30), long.MaxValue, now);
        Assert.Equal(1, first.Manifests);
        Assert.True(first.Blobs >= 2); // the old turn's before and after
        Assert.Equal(["new", "middle"], store.List("chat-1").Select(t => t.Id));

        // A cap smaller than both: the older goes first, until it fits.
        var second = store.Prune(TimeSpan.FromDays(30), 2_500, now);
        Assert.Equal(1, second.Manifests);
        Assert.Equal("new", Assert.Single(store.List("chat-1")).Id);
    }

    [Fact]
    public void ContentIsStoredOnceHoweverOftenItIsKept()
    {
        var store = Store;
        var a = store.Put("same bytes"u8.ToArray());
        var b = store.Put("same bytes"u8.ToArray());
        Assert.Equal(a, b);
        Assert.Single(Directory.EnumerateFiles(_data["checkpoints/blobs"], "*", SearchOption.AllDirectories));
        Assert.Equal("same bytes"u8.ToArray(), store.Get(a));
    }

    [Fact]
    public async Task TheEngineRecordsIntoTheTurnItsHostOpenedAndSubagentsShareIt()
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

        var turn = recorder.Finish()!;
        Assert.Equal("v1\n", System.Text.Encoding.UTF8.GetString(Store.Get(turn.Files.Single().Before!)!));
    }

    [Fact]
    public void DiffsAreUnifiedWithTheirFileNames()
    {
        var file = _tools.Root.Write("src/a.txt", "one\ntwo\nthree\n");
        var (recorder, _) = Open();
        recorder.BeforeChange(file, "edit");
        File.WriteAllText(file, "one\n2\nthree\nfour\n");
        var turn = recorder.Finish()!;

        var diff = CheckpointRewind.Diff(Store, turn.Files.Single(), _tools.Root.Path);

        Assert.Equal("--- a/src/a.txt\n+++ b/src/a.txt\n@@ -1,3 +1,4 @@\n one\n-two\n+2\n three\n+four\n", diff);
    }

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
        string Before(string path) => System.Text.Encoding.UTF8.GetString(Store.Get(turn.Files.Single(f => f.Path == Path.GetFullPath(path)).Before!)!);
        Assert.Equal("clean v1\n", Before(clean));
        Assert.Equal("dirty v2 (uncommitted, before the command)\n", Before(dirty));
        Assert.Equal("removed v1\n", Before(removed));
        Assert.DoesNotContain(turn.Files, f => f.Path.EndsWith("untracked.txt", StringComparison.Ordinal));

        CheckpointRewind.Apply(Store, CheckpointRewind.Plan(Store, turn), overwriteConflicts: false);
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
