using System.Text.Json;
using System.Text.Json.Nodes;

namespace Vision.Core.Tests;

/// <summary>New: bringing the data folder of DSH (Vision's former name) over to Vision's, once — on temp folders, with
/// renames made to fail and crashes injected after every step. The tests seed legacy DSH data on purpose.</summary>
public sealed class DataFolderMigrationTests : IDisposable
{
    private readonly TempDirectory _dir = new("vision-migration");
    private readonly string _legacy;
    private readonly string _root;

    public DataFolderMigrationTests()
    {
        _legacy = Path.Combine(_dir.Path, "DSH");
        _root = Path.Combine(_dir.Path, "Vision");
    }

    public void Dispose() => _dir.Dispose();

    private sealed class Crash(string step) : Exception($"crashed after {step}");

    private string L(params string[] parts) => Path.Combine([_legacy, .. parts]);
    private string R(params string[] parts) => Path.Combine([_root, .. parts]);

    private static void WriteJson(string path, object value, bool indented = true)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = indented }));
    }

    /// <summary>A DSH data folder as a user has it: settings naming skill files in it, a chat without a project whose
    /// scratch folder is in it, a project chat, a task list, a vault, files in a scratch folder.</summary>
    private void SeedLegacy()
    {
        WriteJson(L("settings.json"), new
        {
            activeRoute = "Spark|qwen",
            disabledSkills = new[] { L("skills", "old", "SKILL.md"), @"C:\code\app\.claude\skills\x\SKILL.md" },
            sessionSkills = new Dictionary<string, object> { ["chat-1"] = new { pinned = new[] { L("skills", "mine", "SKILL.md") }, auto = true } },
            codeStates = new Dictionary<string, object> { [L("Chats", "chat-1")] = new { files = new[] { L("Chats", "chat-1", "a.md") }, active = (string?)null } },
            recentProjects = new[] { "/work/app" },
            wizardCompleted = true,
        });
        WriteJson(L("conversations", "chat-1.json"), new
        {
            session = new { id = "chat-1", title = "Plain", cwd = L("Chats", "chat-1") },
            items = new object[] { new { seq = 0, kind = "user", text = $"save it to {L("Chats", "chat-1")}" } },
        }, indented: false);
        WriteJson(L("conversations", "chat-2.json"), new { session = new { id = "chat-2", title = "Project", cwd = "/work/app" }, items = Array.Empty<object>() }, indented: false);
        WriteJson(L("task-queue.json"), new { tasks = new object[] { new { id = "t1", title = "a", cwd = L("Chats", "chat-1") }, new { id = "t2", title = "b", cwd = "/work/app" } } });
        File.WriteAllBytes(L("vault.bin"), [1, 2, 3, 250, 0, 7]);
        Directory.CreateDirectory(L("Chats", "chat-1"));
        File.WriteAllText(L("Chats", "chat-1", "a.md"), "draft");
        Directory.CreateDirectory(L("skills", "empty-folder"));
    }

    /// <summary>Every file under <paramref name="folder"/> (relative path → bytes), leaving the marker out.</summary>
    private static Dictionary<string, byte[]> Snapshot(string folder) =>
        Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Where(f => Path.GetFileName(f) != DataFolderMigration.MarkerFileName)
            .ToDictionary(f => Path.GetRelativePath(folder, f).Replace('\\', '/'), File.ReadAllBytes);

    private void AssertBroughtOver(Dictionary<string, byte[]> before)
    {
        var after = Snapshot(_root);
        Assert.Equal(before.Keys.Order(StringComparer.Ordinal), after.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(before["vault.bin"], after["vault.bin"]);
        Assert.Equal("draft", File.ReadAllText(R("Chats", "chat-1", "a.md")));
        Assert.True(Directory.Exists(R("skills", "empty-folder")));

        var settings = JsonNode.Parse(File.ReadAllText(R("settings.json")))!;
        Assert.Equal(R("skills", "old", "SKILL.md"), settings["disabledSkills"]![0]!.GetValue<string>());
        Assert.Equal(@"C:\code\app\.claude\skills\x\SKILL.md", settings["disabledSkills"]![1]!.GetValue<string>());
        Assert.Equal(R("skills", "mine", "SKILL.md"), settings["sessionSkills"]!["chat-1"]!["pinned"]![0]!.GetValue<string>());
        Assert.Equal(R("Chats", "chat-1", "a.md"), settings["codeStates"]![R("Chats", "chat-1")]!["files"]![0]!.GetValue<string>());
        Assert.Equal("Spark|qwen", settings["activeRoute"]!.GetValue<string>());
        Assert.True(settings["wizardCompleted"]!.GetValue<bool>());

        var chat = JsonNode.Parse(File.ReadAllText(R("conversations", "chat-1.json")))!;
        Assert.Equal(R("Chats", "chat-1"), chat["session"]!["cwd"]!.GetValue<string>());
        // What was said stays as it was said.
        Assert.Equal($"save it to {L("Chats", "chat-1")}", chat["items"]![0]!["text"]!.GetValue<string>());
        // A file with nothing to rewrite isn't touched at all.
        Assert.Equal(before["conversations/chat-2.json"], after["conversations/chat-2.json"]);

        var queue = JsonNode.Parse(File.ReadAllText(R("task-queue.json")))!;
        Assert.Equal(R("Chats", "chat-1"), queue["tasks"]![0]!["cwd"]!.GetValue<string>());
        Assert.Equal("/work/app", queue["tasks"]![1]!["cwd"]!.GetValue<string>());

        var marker = DataFolderMigration.ReadMarker(R(DataFolderMigration.MarkerFileName), out var unreadable);
        Assert.False(unreadable);
        Assert.Equal(DataMigrationMarker.Done, marker!.State);
        Assert.Equal(_legacy, marker.From);
        Assert.NotNull(marker.CompletedAt);
    }

    [Fact]
    public void FilesTheAppItselfWroteAreReadBackByTheAppWithTheNewPaths()
    {
        // Written and read by the app's own stores, not hand-made JSON: the far end of the rewrite.
        var scratch = L("Chats", "c1");
        var log = new ConversationLog(L("conversations"));
        log.Upsert("c1", scratch, "A chat without a project", "workspace-write");
        log.RecordItem("c1", "user", "hello");
        log.RecordItem("c1", "assistant", $"saved to {scratch}");
        log.Upsert("c2", "/work/app", "A project chat", null);
        var queue = new TaskQueue(L(TaskQueue.FileName));
        queue.Add("Convert the data", "details", cwd: scratch, chatId: "c1");
        queue.Add("Project task", cwd: "/work/app", chatId: "c2");
        Directory.CreateDirectory(scratch);

        var outcome = new DataFolderMigration(_legacy, _root).Run();
        Assert.Equal(DataMigrationResult.Moved, outcome.Result);

        var reopened = new ConversationLog(R("conversations"));
        var sessions = reopened.List().ToDictionary(s => s.Id);
        Assert.Equal(R("Chats", "c1"), sessions["c1"].Cwd);
        Assert.Equal("/work/app", sessions["c2"].Cwd);
        Assert.Equal("A chat without a project", sessions["c1"].Title);
        Assert.Equal("workspace-write", sessions["c1"].Preset);
        Assert.Equal(["hello", $"saved to {scratch}"], reopened.LoadItems("c1").Select(i => i.Text));
        var tasks = new TaskQueue(R(TaskQueue.FileName));
        Assert.Null(tasks.LoadProblem);
        Assert.Equal([R("Chats", "c1"), "/work/app"], tasks.Tasks.Select(t => t.Cwd));
        Assert.Equal(["c1", "c2"], tasks.Tasks.Select(t => t.SessionId));
        Assert.True(Directory.Exists(R("Chats", "c1")));
    }

    [Fact]
    public void NoOldFolderMeansNothingToDo()
    {
        var migration = new DataFolderMigration(_legacy, _root);
        Assert.False(migration.IsPending);
        Assert.Equal(DataMigrationResult.NothingToDo, migration.Run().Result);
        Assert.False(Directory.Exists(_root));

        Directory.CreateDirectory(_legacy); // an empty one too
        Assert.False(migration.IsPending);
        Assert.Equal(DataMigrationResult.NothingToDo, migration.Run().Result);
        Assert.Null(DataFolderMigration.PendingNotice(_root));
    }

    [Fact]
    public void TheOldFolderIsMovedAndItsPathsRewritten()
    {
        SeedLegacy();
        var before = Snapshot(_legacy);
        var migration = new DataFolderMigration(_legacy, _root);
        Assert.True(migration.IsPending);

        var outcome = migration.Run();

        Assert.Equal(DataMigrationResult.Moved, outcome.Result);
        Assert.True(outcome.BroughtOver);
        Assert.Equal(_root, outcome.DataFolder);
        Assert.Null(outcome.Problem);
        Assert.Equal(3, outcome.RewrittenFiles); // settings, chat-1, the task list
        Assert.False(Directory.Exists(_legacy));
        Assert.False(Directory.Exists(migration.MovingFolder));
        AssertBroughtOver(before);
        Assert.False(migration.IsPending);
    }

    [Fact]
    public void RunningAgainChangesNothing()
    {
        SeedLegacy();
        new DataFolderMigration(_legacy, _root).Run();
        var once = Snapshot(_root);
        var marker = File.ReadAllText(R(DataFolderMigration.MarkerFileName));

        var again = new DataFolderMigration(_legacy, _root).Run();

        Assert.Equal(DataMigrationResult.AlreadyDone, again.Result);
        Assert.Equal(_root, again.DataFolder);
        var twice = Snapshot(_root);
        Assert.Equal(once.Keys, twice.Keys);
        foreach (var (name, bytes) in once) Assert.Equal(bytes, twice[name]);
        Assert.Equal(marker, File.ReadAllText(R(DataFolderMigration.MarkerFileName)));
    }

    [Fact]
    public void AnOldDshMakingANewOldFolderLaterDoesntStartItAgain()
    {
        SeedLegacy();
        new DataFolderMigration(_legacy, _root).Run();
        // An old copy of DSH run afterwards starts a fresh folder of its own.
        WriteJson(L("settings.json"), new { wizardCompleted = false });

        var migration = new DataFolderMigration(_legacy, _root);
        Assert.False(migration.IsPending);
        Assert.Equal(DataMigrationResult.AlreadyDone, migration.Run().Result);
        Assert.True(JsonNode.Parse(File.ReadAllText(R("settings.json")))!["wizardCompleted"]!.GetValue<bool>());
    }

    [Fact]
    public void WhenTheFolderCantBeRenamedItIsCopiedCheckedAndLeftAsItWas()
    {
        SeedLegacy();
        var before = Snapshot(_legacy);
        var migration = new DataFolderMigration(_legacy, _root)
        {
            // A file in the old folder is open somewhere: Windows refuses to rename the folder.
            MoveDirectory = (from, to) =>
            {
                if (from == _legacy) throw new IOException("The process cannot access the file because it is being used by another process.");
                Directory.Move(from, to);
            },
        };

        var outcome = migration.Run();

        Assert.Equal(DataMigrationResult.Copied, outcome.Result);
        Assert.Equal(_root, outcome.DataFolder);
        AssertBroughtOver(before);
        // The original is exactly as it was.
        var legacyAfter = Snapshot(_legacy);
        Assert.Equal(before.Keys.Order(StringComparer.Ordinal), legacyAfter.Keys.Order(StringComparer.Ordinal));
        foreach (var (name, bytes) in before) Assert.Equal(bytes, legacyAfter[name]);
        Assert.False(Directory.Exists(migration.CopyingFolder));
        var notice = DataFolderMigration.PendingNotice(_root);
        Assert.NotNull(notice);
        Assert.True(notice!.LegacyLeft);
        Assert.Equal("copied", notice.Method);
    }

    [Fact]
    public void APortableCopyAlwaysCopies()
    {
        SeedLegacy();
        var before = Snapshot(_legacy);
        var outcome = new DataFolderMigration(_legacy, _root, allowMove: false).Run();
        Assert.Equal(DataMigrationResult.Copied, outcome.Result);
        Assert.True(Directory.Exists(_legacy));
        AssertBroughtOver(before);
    }

    [Fact]
    public void ACopyThatDoesntMatchTheOriginalIsThrownAwayAndNothingChanges()
    {
        SeedLegacy();
        var before = Snapshot(_legacy);
        var migration = new DataFolderMigration(_legacy, _root, allowMove: false)
        {
            // Something changes the copy between copying and checking (a failing disk, say).
            AfterStep = step =>
            {
                if (step == "copied") File.WriteAllBytes(Path.Combine(_root + ".copying", "vault.bin"), [9, 9, 9, 9, 9, 9]);
            },
        };

        var outcome = migration.Run();

        Assert.Equal(DataMigrationResult.Failed, outcome.Result);
        Assert.Equal(_legacy, outcome.DataFolder); // run on the old folder this time
        Assert.Contains("vault.bin", outcome.Problem);
        Assert.False(Directory.Exists(_root));
        Assert.False(Directory.Exists(migration.CopyingFolder));
        Assert.Equal(before.Keys.Order(StringComparer.Ordinal), Snapshot(_legacy).Keys.Order(StringComparer.Ordinal));
        Assert.True(migration.IsPending); // tried again next launch

        // And the next launch, without the trouble, gets there.
        Assert.Equal(DataMigrationResult.Copied, new DataFolderMigration(_legacy, _root, allowMove: false).Run().Result);
        AssertBroughtOver(before);
    }

    [Theory]
    [InlineData("moved-to-staging")]
    [InlineData("marked-staging")]
    [InlineData("moved-into-place")]
    [InlineData("rewritten")]
    public void AMoveInterruptedAfterAnyStepIsFinishedNextTime(string step)
    {
        SeedLegacy();
        var before = Snapshot(_legacy);
        var crashing = new DataFolderMigration(_legacy, _root)
        {
            AfterStep = s => { if (s == step) throw new Crash(s); },
        };
        Assert.Throws<Crash>(() => crashing.Run()); // the app died here

        var next = new DataFolderMigration(_legacy, _root);
        Assert.True(next.IsPending);
        var outcome = next.Run();

        Assert.Equal(DataMigrationResult.Moved, outcome.Result);
        Assert.False(Directory.Exists(_legacy));
        Assert.False(Directory.Exists(next.MovingFolder));
        AssertBroughtOver(before);
        Assert.NotNull(DataFolderMigration.PendingNotice(_root));
    }

    [Theory]
    [InlineData("copied")]
    [InlineData("verified")]
    [InlineData("moved-into-place")]
    [InlineData("rewritten")]
    public void ACopyInterruptedAfterAnyStepIsFinishedNextTime(string step)
    {
        SeedLegacy();
        var before = Snapshot(_legacy);
        var crashing = new DataFolderMigration(_legacy, _root, allowMove: false)
        {
            AfterStep = s => { if (s == step) throw new Crash(s); },
        };
        Assert.Throws<Crash>(() => crashing.Run());

        var outcome = new DataFolderMigration(_legacy, _root, allowMove: false).Run();

        Assert.Equal(DataMigrationResult.Copied, outcome.Result);
        Assert.True(Directory.Exists(_legacy));
        Assert.False(Directory.Exists(_root + ".copying"));
        AssertBroughtOver(before);
    }

    [Fact]
    public void AMoveStoppedHalfWayTellsTheAppNotToStartAndIsFinishedNextTime()
    {
        SeedLegacy();
        var before = Snapshot(_legacy);
        var migration = new DataFolderMigration(_legacy, _root)
        {
            // The second rename fails (an antivirus holding the staging folder, say).
            MoveDirectory = (from, to) =>
            {
                if (to == _root) throw new IOException("Access to the path is denied.");
                Directory.Move(from, to);
            },
        };

        var outcome = migration.Run();

        Assert.Equal(DataMigrationResult.Failed, outcome.Result);
        Assert.Null(outcome.DataFolder); // neither the old nor the new folder holds the data
        Assert.Contains(migration.MovingFolder, outcome.Problem);
        Assert.True(Directory.Exists(migration.MovingFolder));

        var next = new DataFolderMigration(_legacy, _root).Run();
        Assert.Equal(DataMigrationResult.Moved, next.Result);
        AssertBroughtOver(before);
    }

    [Fact]
    public void AFolderAlreadyAtTheNewPlaceIsSetAsideNotLost()
    {
        SeedLegacy();
        var before = Snapshot(_legacy);
        // A build that ran before this one could bring the old folder over made a folder of its own.
        WriteJson(R("settings.json"), new { wizardCompleted = false, marker = "made by an early build" });

        var outcome = new DataFolderMigration(_legacy, _root).Run();

        Assert.Equal(DataMigrationResult.Moved, outcome.Result);
        Assert.NotNull(outcome.SetAside);
        Assert.StartsWith(_root + ".before-migration-", outcome.SetAside);
        Assert.Contains("made by an early build", File.ReadAllText(Path.Combine(outcome.SetAside!, "settings.json")));
        AssertBroughtOver(before);
    }

    [Fact]
    public void AnEmptyFolderAlreadyAtTheNewPlaceIsSimplyUsed()
    {
        SeedLegacy();
        Directory.CreateDirectory(_root);
        var outcome = new DataFolderMigration(_legacy, _root).Run();
        Assert.Equal(DataMigrationResult.Moved, outcome.Result);
        Assert.Null(outcome.SetAside);
    }

    [Fact]
    public void TheNoticeShowsOnce()
    {
        SeedLegacy();
        new DataFolderMigration(_legacy, _root).Run();
        var notice = DataFolderMigration.PendingNotice(_root);
        Assert.NotNull(notice);
        Assert.False(notice!.LegacyLeft);

        DataFolderMigration.MarkNoticeShown(_root);

        Assert.Null(DataFolderMigration.PendingNotice(_root));
        Assert.Equal(DataMigrationResult.AlreadyDone, new DataFolderMigration(_legacy, _root).Run().Result);
    }

    [Fact]
    public void ADamagedFileIsLeftAsItIs()
    {
        SeedLegacy();
        File.WriteAllText(L("conversations", "broken.json"), "{ not json " + L("Chats"));
        var outcome = new DataFolderMigration(_legacy, _root).Run();
        Assert.Equal(DataMigrationResult.Moved, outcome.Result);
        Assert.Equal("{ not json " + L("Chats"), File.ReadAllText(R("conversations", "broken.json")));
    }

    [Fact]
    public void LinksInTheFolderAreCopiedAsLinksNotFollowed()
    {
        SeedLegacy();
        using var elsewhere = new TempDirectory("vision-link-target");
        elsewhere.Write("big/file.txt", "outside the data folder");
        try
        {
            Directory.CreateSymbolicLink(L("skills", "linked"), Path.Combine(elsewhere.Path, "big"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return; // this machine can't make links (Windows without the right); nothing to test
        }

        var outcome = new DataFolderMigration(_legacy, _root, allowMove: false).Run();

        Assert.Equal(DataMigrationResult.Copied, outcome.Result);
        var copied = new DirectoryInfo(R("skills", "linked"));
        Assert.Equal(Path.Combine(elsewhere.Path, "big"), copied.LinkTarget);
        Assert.Null(outcome.SkippedLinks);
        Assert.True(File.Exists(Path.Combine(elsewhere.Path, "big", "file.txt"))); // the target wasn't touched
    }

    // MARK: - Rewriting a path

    [Theory]
    [InlineData(@"C:\Users\pat\AppData\Roaming\DSH\Chats\abc", @"C:\Users\pat\AppData\Roaming\Vision\Chats\abc")]
    [InlineData(@"c:\users\PAT\appdata\roaming\dsh\skills\x\SKILL.md", @"C:\Users\pat\AppData\Roaming\Vision\skills\x\SKILL.md")]
    [InlineData(@"C:/Users/pat/AppData/Roaming/DSH/Chats/abc", @"C:\Users\pat\AppData\Roaming\Vision/Chats/abc")]
    [InlineData(@"C:\Users\pat\AppData\Roaming\DSH", @"C:\Users\pat\AppData\Roaming\Vision")]
    [InlineData(@"C:\Users\pat\AppData\Roaming\DSH\", @"C:\Users\pat\AppData\Roaming\Vision\")]
    [InlineData(@"C:\Users\pat\AppData\Roaming\DSH2\Chats\abc", null)]
    [InlineData(@"C:\Users\pat\AppData\Roaming\DS", null)]
    [InlineData(@"D:\Users\pat\AppData\Roaming\DSH\Chats", null)]
    [InlineData(@"C:\code\app", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void OnlyPathsInsideTheOldFolderAreRewritten(string? value, string? expected) =>
        Assert.Equal(expected, DataFolderPaths.Rewrite(value, @"C:\Users\pat\AppData\Roaming\DSH", @"C:\Users\pat\AppData\Roaming\Vision\"));

    [Fact]
    public void TheSameFolderNamedBothWaysKeepsOneEntry()
    {
        Directory.CreateDirectory(_root);
        var legacyChat = Path.Combine(_legacy, "Chats", "x");
        var newChat = Path.Combine(_root, "Chats", "x");
        WriteJson(R("settings.json"), new
        {
            codeStates = new Dictionary<string, object>
            {
                [newChat] = new { files = Array.Empty<string>(), active = "new" },
                [legacyChat] = new { files = Array.Empty<string>(), active = "old" },
            },
        });

        Assert.Equal(1, DataFolderPaths.RewriteAll(_root, _legacy, _root));

        var states = JsonNode.Parse(File.ReadAllText(R("settings.json")))!["codeStates"]!.AsObject();
        Assert.Equal("new", Assert.Single(states).Value!["active"]!.GetValue<string>());
    }
}
