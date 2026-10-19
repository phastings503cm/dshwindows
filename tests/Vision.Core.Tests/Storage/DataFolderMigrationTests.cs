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
    /// scratch folder is in it, a project chat, a task list, notes the agent saved in that chat (filed under its scratch
    /// folder), a vault, files in a scratch folder.</summary>
    private void SeedLegacy() => SeedDataFolder(_legacy);

    /// <summary>The memory log line the seed writes for a note a chat without a project saved, and one that no version
    /// can read: both must come through with only the note's project changed.</summary>
    private const string UnreadableMemoryLine = "{\"op\":\"put\",\"item\": not json";

    private static void SeedDataFolder(string folder)
    {
        string F(params string[] parts) => Path.Combine([folder, .. parts]);
        WriteJson(F("settings.json"), new
        {
            activeRoute = "Spark|qwen",
            disabledSkills = new[] { F("skills", "old", "SKILL.md"), @"C:\code\app\.claude\skills\x\SKILL.md" },
            sessionSkills = new Dictionary<string, object> { ["chat-1"] = new { pinned = new[] { F("skills", "mine", "SKILL.md") }, auto = true } },
            codeStates = new Dictionary<string, object> { [F("Chats", "chat-1")] = new { files = new[] { F("Chats", "chat-1", "a.md") }, active = (string?)null } },
            recentProjects = new[] { "/work/app" },
            wizardCompleted = true,
        });
        WriteJson(F("conversations", "chat-1.json"), new
        {
            session = new { id = "chat-1", title = "Plain", cwd = F("Chats", "chat-1") },
            items = new object[] { new { seq = 0, kind = "user", text = $"save it to {F("Chats", "chat-1")}" } },
        }, indented: false);
        WriteJson(F("conversations", "chat-2.json"), new { session = new { id = "chat-2", title = "Project", cwd = "/work/app" }, items = Array.Empty<object>() }, indented: false);
        WriteJson(F("task-queue.json"), new { tasks = new object[] { new { id = "t1", title = "a", cwd = F("Chats", "chat-1") }, new { id = "t2", title = "b", cwd = "/work/app" } } });
        Directory.CreateDirectory(F("memory"));
        File.WriteAllText(F("memory", MemoryStore.FileName), string.Join("\n",
            JsonSerializer.Serialize(new { op = "put", item = new { id = "aa11bb22", title = "Agenda format", body = "Three bullets.", kind = "fact", project = F("Chats", "chat-1") } }),
            JsonSerializer.Serialize(new { op = "put", item = new { id = "cc33dd44", title = "Tabs", body = "The user prefers tabs.", kind = "preference" } }),
            UnreadableMemoryLine,
            JsonSerializer.Serialize(new { op = "use", ids = new[] { "aa11bb22" } })) + "\n");
        File.WriteAllBytes(F("vault.bin"), [1, 2, 3, 250, 0, 7]);
        Directory.CreateDirectory(F("Chats", "chat-1"));
        File.WriteAllText(F("Chats", "chat-1", "a.md"), "draft");
        Directory.CreateDirectory(F("skills", "empty-folder"));
    }

    /// <summary>Every file under <paramref name="folder"/> (relative path → bytes), leaving the marker out.</summary>
    private static Dictionary<string, byte[]> Snapshot(string folder) =>
        Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Where(f => Path.GetFileName(f) != DataFolderMigration.MarkerFileName)
            .ToDictionary(f => Path.GetRelativePath(folder, f).Replace('\\', '/'), File.ReadAllBytes);

    /// <param name="from">The folder the data was seeded in (the old folder, unless said otherwise).</param>
    private void AssertBroughtOver(Dictionary<string, byte[]> before, string? from = null)
    {
        from ??= _legacy;
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
        Assert.Equal($"save it to {Path.Combine(from, "Chats", "chat-1")}", chat["items"]![0]!["text"]!.GetValue<string>());
        // A file with nothing to rewrite isn't touched at all.
        Assert.Equal(before["conversations/chat-2.json"], after["conversations/chat-2.json"]);

        var queue = JsonNode.Parse(File.ReadAllText(R("task-queue.json")))!;
        Assert.Equal(R("Chats", "chat-1"), queue["tasks"]![0]!["cwd"]!.GetValue<string>());
        Assert.Equal("/work/app", queue["tasks"]![1]!["cwd"]!.GetValue<string>());

        // The note the chat without a project saved is filed under its new scratch folder; every other line is as it was.
        var memory = File.ReadAllText(R("memory", MemoryStore.FileName)).Split('\n');
        Assert.Equal(5, memory.Length); // four lines and the newline after the last
        Assert.Equal(R("Chats", "chat-1"), JsonNode.Parse(memory[0])!["item"]!["project"]!.GetValue<string>());
        Assert.Equal("Agenda format", JsonNode.Parse(memory[0])!["item"]!["title"]!.GetValue<string>());
        var beforeMemory = System.Text.Encoding.UTF8.GetString(before["memory/" + MemoryStore.FileName]).Split('\n');
        Assert.Equal(beforeMemory[1..], memory[1..]);
        Assert.Equal(UnreadableMemoryLine, memory[2]);

        var marker = DataFolderMigration.ReadMarker(R(DataFolderMigration.MarkerFileName), out var unreadable);
        Assert.False(unreadable);
        Assert.Equal(DataMigrationMarker.Done, marker!.State);
        Assert.Equal(from, marker.From);
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
        Assert.Equal(4, outcome.RewrittenFiles); // settings, chat-1, the task list, the memory log
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
        // The notice names it: the chats in it are no longer the ones the app shows.
        Assert.Equal(outcome.SetAside, DataFolderMigration.PendingNotice(_root)!.SetAside);
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

    // MARK: - Files of a shape the app never writes

    [Theory]
    [InlineData("conversations/odd.json", "[]")]
    [InlineData("conversations/odd.json", "42")]
    [InlineData("conversations/odd.json", "\"just text\"")]
    [InlineData("conversations/odd.json", "null")]
    [InlineData("conversations/odd.json", "{\"session\":\"not an object\"}")]
    [InlineData("conversations/odd.json", "{\"session\":{\"cwd\":42}}")]
    public void AFileOfAShapeTheAppNeverWritesIsLeftAsItIsAndEverythingElseComesOver(string name, string text)
    {
        SeedLegacy();
        var odd = Path.Combine(_legacy, name.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(odd)!);
        File.WriteAllText(odd, text);
        var before = Snapshot(_legacy);

        var outcome = new DataFolderMigration(_legacy, _root).Run();

        Assert.Equal(DataMigrationResult.Moved, outcome.Result);
        Assert.Null(outcome.Problem);
        Assert.Equal(text, File.ReadAllText(Path.Combine(_root, name.Replace('/', Path.DirectorySeparatorChar))));
        AssertBroughtOver(before);
        Assert.NotNull(DataFolderMigration.PendingNotice(_root));
        // And the next launch finds it done, rather than failing on the same file again.
        Assert.Equal(DataMigrationResult.AlreadyDone, new DataFolderMigration(_legacy, _root).Run().Result);
    }

    [Theory]
    [InlineData("[{\"id\":\"t1\",\"cwd\":\"<scratch>\"}]")]
    [InlineData("{\"tasks\":{\"t1\":{\"cwd\":\"<scratch>\"}}}")]
    [InlineData("{\"tasks\":[1,\"<scratch>\",[\"<scratch>\"],null]}")]
    [InlineData("{\"tasks\":[{\"id\":\"t1\",\"cwd\":[\"<scratch>\"]}]}")]
    public void ATaskListOfAShapeTheAppNeverWritesIsLeftAsItIs(string shape)
    {
        SeedLegacy();
        var text = shape.Replace("<scratch>", JsonEncodedText.Encode(L("Chats", "chat-1")).ToString());
        File.WriteAllText(L(TaskQueue.FileName), text);

        var outcome = new DataFolderMigration(_legacy, _root).Run();

        Assert.Equal(DataMigrationResult.Moved, outcome.Result);
        Assert.Null(outcome.Problem);
        Assert.Equal(text, File.ReadAllText(R(TaskQueue.FileName)));
        Assert.Equal(R("Chats", "chat-1"), JsonNode.Parse(File.ReadAllText(R("conversations", "chat-1.json")))!["session"]!["cwd"]!.GetValue<string>());
        Assert.Equal(DataMigrationMarker.Done, DataFolderMigration.MarkerIn(_root)!.State);
    }

    [Fact]
    public void APropertyNamedTwiceIsReadTheWayTheAppReadsItTheLastOneWins()
    {
        SeedLegacy();
        // Hand-edited files the app's own readers take without complaint (System.Text.Json keeps the last value).
        var skill = JsonEncodedText.Encode(L("skills", "old", "SKILL.md")).ToString();
        File.WriteAllText(L("settings.json"),
            $$$"""{"theme":"dark","disabledSkills":["{{{skill}}}"],"wizardCompleted":true,"theme":"light","nested":{"a":1,"a":2}}""");
        var scratch = JsonEncodedText.Encode(L("Chats", "chat-1")).ToString();
        var other = JsonEncodedText.Encode(L("Chats", "chat-9")).ToString();
        File.WriteAllText(L("conversations", "chat-1.json"),
            $$$"""{"session":{"id":"chat-1","cwd":"{{{other}}}","cwd":"{{{scratch}}}"},"items":[{"seq":0,"kind":"user","text":"hi","text":"hello"}]}""");
        // The app's own reader takes the last one.
        Assert.Equal(L("Chats", "chat-1"), new ConversationLog(L("conversations")).List().Single(c => c.Id == "chat-1").Cwd);

        var outcome = new DataFolderMigration(_legacy, _root).Run();

        Assert.Equal(DataMigrationResult.Moved, outcome.Result);
        Assert.Null(outcome.Problem);
        var settings = JsonNode.Parse(File.ReadAllText(R("settings.json")))!;
        Assert.Equal(R("skills", "old", "SKILL.md"), settings["disabledSkills"]![0]!.GetValue<string>());
        Assert.Equal("light", settings["theme"]!.GetValue<string>());
        Assert.Equal(2, settings["nested"]!["a"]!.GetValue<int>());
        Assert.True(settings["wizardCompleted"]!.GetValue<bool>());
        var chat = JsonNode.Parse(File.ReadAllText(R("conversations", "chat-1.json")))!;
        Assert.Equal(R("Chats", "chat-1"), chat["session"]!["cwd"]!.GetValue<string>());
        Assert.Equal("hello", chat["items"]![0]!["text"]!.GetValue<string>());
        Assert.Equal(DataMigrationResult.AlreadyDone, new DataFolderMigration(_legacy, _root).Run().Result);
    }

    [Fact]
    public void AnErrorNoOneForesawNeverEscapesRun()
    {
        SeedLegacy();
        var before = Snapshot(_legacy);
        var migration = new DataFolderMigration(_legacy, _root)
        {
            MoveDirectory = (from, to) =>
            {
                if (to == _root) throw new InvalidOperationException("something no one foresaw");
                Directory.Move(from, to);
            },
        };

        var outcome = migration.Run();

        Assert.Equal(DataMigrationResult.Failed, outcome.Result);
        Assert.Null(outcome.DataFolder); // the data is in the staging folder: don't start on an empty one
        Assert.Contains("something no one foresaw", outcome.Problem);
        Assert.Equal(DataMigrationResult.Moved, new DataFolderMigration(_legacy, _root).Run().Result);
        AssertBroughtOver(before);
    }

    [Fact]
    public void AFileThatCantBeReadOnceTheDataIsInPlaceLetsTheAppStartAndIsFinishedNextTime()
    {
        // The data is in its new place when a file in it can't be read (on Windows: held open by another program; here:
        // its permissions). The app starts on the data — never on an error it meets again at every start — and a
        // later start rewrites what is left.
        SeedLegacy();
        var before = Snapshot(_legacy);
        var chat = R("conversations", "chat-1.json");
        FileStream? held = null;
        var blocked = false;
        var migration = new DataFolderMigration(_legacy, _root)
        {
            AfterStep = step => { if (step == "moved-into-place") blocked = Block(chat, out held); },
        };
        try
        {
            var outcome = migration.Run();
            if (!blocked) return; // running as root: permissions block nothing here
            Assert.Equal(DataMigrationResult.Moved, outcome.Result);
            Assert.Equal(_root, outcome.DataFolder);
            Assert.NotNull(outcome.Problem);
            Assert.False(Directory.Exists(_legacy));
            Assert.Equal(DataMigrationMarker.Rewriting, DataFolderMigration.MarkerIn(_root)!.State);
            Assert.Null(DataFolderMigration.PendingNotice(_root)); // not done yet: the notice waits
            Assert.True(new DataFolderMigration(_legacy, _root).IsPending);
            // Still blocked at the next start: the same answer, never an exception.
            var again = new DataFolderMigration(_legacy, _root).Run();
            Assert.Equal(DataMigrationResult.Moved, again.Result);
            Assert.NotNull(again.Problem);
        }
        finally
        {
            Unblock(chat, held);
        }

        var finished = new DataFolderMigration(_legacy, _root).Run();

        Assert.Equal(DataMigrationResult.Moved, finished.Result);
        Assert.Null(finished.Problem);
        AssertBroughtOver(before);
        Assert.NotNull(DataFolderMigration.PendingNotice(_root));
        Assert.False(new DataFolderMigration(_legacy, _root).IsPending);
    }

    /// <summary>Make <paramref name="path"/> unreadable: held open with no sharing on Windows, no permissions elsewhere.
    /// False when that blocks nothing (tests running as root).</summary>
    private static bool Block(string path, out FileStream? held)
    {
        held = null;
        if (OperatingSystem.IsWindows())
        {
            held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return true;
        }
        File.SetUnixFileMode(path, UnixFileMode.None);
        try
        {
            File.ReadAllBytes(path);
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static void Unblock(string path, FileStream? held)
    {
        held?.Dispose();
        if (!OperatingSystem.IsWindows() && File.Exists(path)) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("\"done\"")]
    [InlineData("{\"state\":5}")]
    [InlineData("{\"state\":\"done\",\"startedAt\":\"not a date\"}")]
    public void AMarkerOfAShapeNoVersionWritesIsFinishedNotTakenForNothingThere(string text)
    {
        // The data is in place, and the marker beside it was damaged (edited by hand, a write the disk lost).
        SeedLegacy();
        var before = Snapshot(_legacy);
        Assert.Equal(DataMigrationResult.Moved, new DataFolderMigration(_legacy, _root).Run().Result);
        File.WriteAllText(R(DataFolderMigration.MarkerFileName), text);

        var migration = new DataFolderMigration(_legacy, _root);
        Assert.True(migration.IsPending);
        var outcome = migration.Run();

        Assert.Equal(DataMigrationResult.Moved, outcome.Result);
        Assert.Equal(_root, outcome.DataFolder);
        Assert.Null(outcome.Problem);
        AssertBroughtOver(before);
        Assert.False(new DataFolderMigration(_legacy, _root).IsPending);

        // So an old folder that turns up later (an old copy of DSH run once) never displaces the data now in place.
        var mine = Snapshot(_root);
        SeedLegacy();
        var later = new DataFolderMigration(_legacy, _root);
        Assert.False(later.IsPending);
        Assert.Equal(DataMigrationResult.AlreadyDone, later.Run().Result);
        Assert.Equal(mine.Keys.Order(StringComparer.Ordinal), Snapshot(_root).Keys.Order(StringComparer.Ordinal));
        Assert.Empty(Directory.GetDirectories(_dir.Path, "Vision.before-migration-*"));
    }

    [Fact]
    public void ANoteLogEditedByHandKeepsItsLineEndings()
    {
        SeedLegacy();
        var log = L("memory", MemoryStore.FileName);
        var crlf = File.ReadAllText(log).Replace("\n", "\r\n");
        File.WriteAllText(log, crlf);

        Assert.Equal(DataMigrationResult.Moved, new DataFolderMigration(_legacy, _root).Run().Result);

        var lines = File.ReadAllText(R("memory", MemoryStore.FileName)).Split("\r\n");
        Assert.Equal(5, lines.Length); // four lines, each ending \r\n
        Assert.Equal(R("Chats", "chat-1"), JsonNode.Parse(lines[0])!["item"]!["project"]!.GetValue<string>());
        Assert.Equal(crlf.Split("\r\n")[1..], lines[1..]);
        Assert.Equal(2, new MemoryStore(R("memory")).All().Count);
    }

    // MARK: - Notes saved in a chat without a project

    [Fact]
    public async Task NotesAChatWithoutAProjectSavedAreFoundFromItsNewScratchFolder()
    {
        // Saved and searched with the agent's own tools: the far end of the memory log rewrite.
        using var tools = new ToolTestContext();
        var oldScratch = L("Chats", "c1");
        Directory.CreateDirectory(oldScratch);
        var store = new MemoryStore(L("memory"));
        var saved = await new MemorySaveTool(store).ExecuteAsync(
            """{"title":"Agenda format","content":"Standup agendas are three bullets: release, bugs, asks.","kind":"fact"}""",
            tools.Context with { Workspace = oldScratch, Policy = new PermissionPolicy(PermissionPreset.WorkspaceWrite, oldScratch) }, CancellationToken.None);
        Assert.StartsWith("Saved note", saved.Output);
        Assert.Equal(oldScratch, Assert.Single(store.All()).Project);

        Assert.Equal(DataMigrationResult.Moved, new DataFolderMigration(_legacy, _root).Run().Result);

        var newScratch = R("Chats", "c1");
        var found = await new MemorySearchTool(new MemoryStore(R("memory"))).ExecuteAsync("""{"query":"standup agenda bullets"}""",
            tools.Context with { Workspace = newScratch, Policy = new PermissionPolicy(PermissionPreset.WorkspaceWrite, newScratch) }, CancellationToken.None);
        Assert.Contains("Agenda format", found.Output);
        Assert.Contains("this project", found.Output);
    }

    // MARK: - Nothing to bring over

    [Fact]
    public void AFirstStartWithNothingToBringMarksTheFolderAsVisionsOwn()
    {
        var migration = new DataFolderMigration(_legacy, _root);
        Assert.False(migration.IsPending);

        var settled = migration.Settle();

        Assert.Equal(DataMigrationResult.NothingToDo, settled.Result);
        Assert.Null(settled.Problem);
        var marker = DataFolderMigration.MarkerIn(_root)!;
        Assert.Equal(DataMigrationMarker.Done, marker.State);
        Assert.Equal(DataMigrationMarker.None, marker.Method);
        Assert.Null(DataFolderMigration.PendingNotice(_root)); // nothing came over: nothing to tell
        Assert.Equal(DataMigrationResult.AlreadyDone, migration.Settle().Result);
    }

    [Fact]
    public void AnOldFolderThatTurnsUpLaterNeverDisplacesVisionsOwnData()
    {
        // A new user: Vision's own folder, in use, with nothing to bring over at its first start.
        WriteJson(R("settings.json"), new { wizardCompleted = true, theme = "dark" });
        WriteJson(R("conversations", "mine.json"), new { session = new { id = "mine", title = "Mine" }, items = Array.Empty<object>() }, indented: false);
        Assert.Equal(DataMigrationResult.NothingToDo, new DataFolderMigration(_legacy, _root).Settle().Result);
        var mine = Snapshot(_root);

        // Later an old DSH folder turns up (an old copy run once, a backup restored).
        SeedLegacy();
        var legacy = Snapshot(_legacy);
        var migration = new DataFolderMigration(_legacy, _root);
        Assert.False(migration.IsPending);
        Assert.Equal(DataMigrationResult.AlreadyDone, migration.Run().Result);
        Assert.Equal(DataMigrationResult.AlreadyDone, migration.Settle().Result);

        Assert.Equal(mine.Keys.Order(StringComparer.Ordinal), Snapshot(_root).Keys.Order(StringComparer.Ordinal));
        Assert.Equal(legacy.Keys.Order(StringComparer.Ordinal), Snapshot(_legacy).Keys.Order(StringComparer.Ordinal));
        Assert.Empty(Directory.GetDirectories(_dir.Path, "Vision.before-migration-*"));
    }

    [Fact]
    public void NothingIsRecordedWhileTheOldFolderHasDataOrCantBeLookedInto()
    {
        SeedLegacy();
        var migration = new DataFolderMigration(_legacy, _root);
        Assert.True(migration.IsPending);
        Assert.Equal(DataMigrationResult.NothingToDo, migration.Settle().Result);
        Assert.Null(DataFolderMigration.MarkerIn(_root));
        Assert.True(migration.IsPending); // still brought over at the next start

        if (OperatingSystem.IsWindows()) return;
        // An old folder that can't be read now (a roaming profile not synced yet, say) may hold the user's data.
        Directory.Delete(_legacy, recursive: true);
        Directory.CreateDirectory(_legacy);
        File.SetUnixFileMode(_legacy, UnixFileMode.None);
        try
        {
            try { _ = Directory.EnumerateFileSystemEntries(_legacy).Any(); return; } // running as root: nothing to test
            catch (UnauthorizedAccessException) { }
            var settled = new DataFolderMigration(_legacy, _root).Settle();
            Assert.Equal(DataMigrationResult.NothingToDo, settled.Result);
            Assert.NotNull(settled.Problem);
            Assert.Null(DataFolderMigration.MarkerIn(_root));
        }
        finally
        {
            File.SetUnixFileMode(_legacy, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void DataMovedOverByHandHasItsPathsRewritten()
    {
        SeedLegacy();
        var before = Snapshot(_legacy);
        Directory.Move(_legacy, _root); // the user moved %APPDATA%\DSH to %APPDATA%\Vision themselves

        var migration = new DataFolderMigration(_legacy, _root);
        Assert.False(migration.IsPending);
        var settled = migration.Settle();

        Assert.Equal(DataMigrationResult.Adopted, settled.Result);
        Assert.Equal(_legacy, settled.From);
        Assert.Equal(4, settled.RewrittenFiles);
        AssertBroughtOver(before);
        Assert.Equal(DataMigrationMarker.Adopted, DataFolderMigration.MarkerIn(_root)!.Method);
        Assert.Null(DataFolderMigration.PendingNotice(_root));
        Assert.Equal(DataMigrationResult.AlreadyDone, migration.Settle().Result);
    }

    [Fact]
    public void APortableCopysDataMovedOverByHandHasItsPathsRewritten()
    {
        // A portable DSH's data folder (E:\DSH\data) moved into a portable Vision's (E:\Vision\data) by hand: there is no
        // DSH beside Vision any more, so the folder is its own old one, and its paths name the old place.
        var portableData = Path.Combine(_dir.Path, "usb", "DSH", "data");
        SeedDataFolder(portableData);
        var before = Snapshot(portableData);
        Directory.Move(portableData, _root);

        var migration = new DataFolderMigration(_root, _root, allowMove: false);
        Assert.False(migration.IsPending);
        var settled = migration.Settle();

        Assert.Equal(DataMigrationResult.Adopted, settled.Result);
        Assert.Equal(portableData, settled.From);
        AssertBroughtOver(before, from: portableData);
        Assert.Equal(portableData, DataFolderMigration.MarkerIn(_root)!.From);
    }

    [Fact]
    public void AnInterruptedRewriteIsFinishedEvenWhenTheOldCopyIsGoneSince()
    {
        // A portable copy: the copy was in place, the app died while rewriting, and the old portable DSH was deleted
        // before the next start, so nothing is beside Vision any more.
        SeedLegacy();
        var before = Snapshot(_legacy);
        var crashing = new DataFolderMigration(_legacy, _root, allowMove: false) { AfterStep = s => { if (s == "moved-into-place") throw new Crash(s); } };
        Assert.Throws<Crash>(() => crashing.Run());
        Directory.Delete(_legacy, recursive: true);

        var next = new DataFolderMigration(_root, _root, allowMove: false);
        Assert.True(next.IsPending);
        var outcome = next.Run();

        Assert.Equal(DataMigrationResult.Copied, outcome.Result);
        Assert.Equal(_legacy, outcome.From);
        AssertBroughtOver(before);
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
