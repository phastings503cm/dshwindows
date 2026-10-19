using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using Vision.App.Model;
using Vision.Core;
using MessageRole = Vision.App.Model.MessageRole;

namespace Vision.App.Tests;

/// <summary>What the agent itself needs (10-19 #20), end to end through AgentHost against a fake model server: a turn's
/// checkpoint and its rewind (and the model hearing of it), the sensitivity policy refusing or masking, and an MCP server's
/// tools offered and run after the chat allows them.</summary>
public sealed class PowerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"vision-power-{Guid.NewGuid():N}");
    private readonly FakeModelServer _server = new();

    public PowerTests()
    {
        Directory.CreateDirectory(_dir);
        _server.Reset((_, _) => new FakeReply.Text("Done."));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Cleaned eventually.
        }
    }

    private string Project(string name)
    {
        var path = Path.Combine(_dir, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private AgentHost MakeHost()
    {
        var config = new AppConfig(Path.Combine(_dir, "settings.json"));
        config.Activate(new ProviderProfile(ProviderKind.OpenAICompat, "stub", "http://stub.test/v1", "stub-model"));
        config.Preset = PermissionPreset.FullAccess.RawValue();
        config.ComputerToolsEnabled = false;
        config.MemoryEnabled = false;
        return new AgentHost(config, new ConversationLog(Path.Combine(_dir, "log")), Dispatcher.CurrentDispatcher,
            queueFile: Path.Combine(_dir, "task-queue.json"),
            skillLocations: new SkillLocations(_dir, Path.Combine(_dir, "support")),
            vault: new CredentialVault(Path.Combine(_dir, "vault"), new MemoryBlobStore()),
            memory: new MemoryStore(Path.Combine(_dir, "memory")))
        {
            HttpHandlerForTesting = _server,
            RetryPolicy = new RetryPolicy { Delay = _ => TimeSpan.FromMilliseconds(50) },
        };
    }

    private void Run(Func<AgentHost, Task> body) => UiThread.Run(async () =>
    {
        var host = MakeHost();
        host.AdoptProject(Project("main"));
        try
        {
            await body(host);
        }
        finally
        {
            host.StopAll();
            await Task.Delay(50);
        }
    });

    private static async Task WaitUntil(string what, Func<bool> condition, double seconds = 15)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail($"timed out waiting for: {what}");
            await Task.Delay(10);
        }
    }

    private static List<string> Notes(SessionVM vm, MessageRole role = MessageRole.Notice) =>
        vm.Entries.OfType<MessageEntryVM>().Where(m => m.Role == role).Select(m => m.Text).ToList();

    [Fact]
    public void ATurnThatChangesFilesEndsWithItsCheckpointAndRewindPutsThemBack() => Run(async host =>
    {
        var project = Project("main");
        var file = Path.Combine(project, "notes.md");
        File.WriteAllText(file, "v1\n");
        _server.Reset((_, n) => n == 1
            ? new FakeReply.ToolCall("write_file", JsonSerializer.Serialize(new { file_path = "notes.md", content = "v2\n" }))
            : new FakeReply.Text("Updated the notes."));
        var vm = host.NewSession(project);
        host.Send("update the notes", vm.Id);
        await WaitUntil("turn done", () => !vm.Running && _server.Seen.Count >= 2);

        var row = Assert.Single(vm.Entries.OfType<CheckpointEntryVM>());
        Assert.Equal("Files changed (1)", row.Title);
        Assert.Equal("+1 −1", row.LineSummary);
        Assert.True(row.CanRewind);
        Assert.Equal("v2\n", File.ReadAllText(file));
        Assert.Contains(AgentHost.LogRows(vm.Entries, vm.Id), r => r.Kind == "checkpoint" && r.ArgSummary == row.TurnId);

        var result = host.Rewind(vm, host.PlanRewind(vm, row.TurnId)!, overwriteConflicts: false);
        Assert.Equal(1, result.Changed);
        Assert.Equal("v1\n", File.ReadAllText(file));
        Assert.True(row.Turn!.IsRewound);
        Assert.True(row.CanUndo);
        Assert.Contains("Rewound 1 file to before this turn.", Notes(vm));

        // The model hears of it with its next message.
        _server.Reset((_, _) => new FakeReply.Text("ok"));
        host.Send("what now?", vm.Id);
        await WaitUntil("second turn", () => !vm.Running && _server.Seen.Count >= 1);
        Assert.Contains("rewound the file changes of an earlier turn", _server.Seen[0].LastUser);
        Assert.Contains("notes.md", _server.Seen[0].LastUser);
        Assert.EndsWith("what now?", _server.Seen[0].LastUser);

        // A relaunch reads the row back with its checkpoint.
        var again = MakeHost();
        var reopened = again.Session(vm.Id)!;
        again.Hydrate(reopened);
        var restored = Assert.Single(reopened.Entries.OfType<CheckpointEntryVM>());
        Assert.True(restored.Turn!.IsRewound);
    });

    [Fact]
    public void RewindWithoutAWindowListsTheTurnsOrSaysThereAreNone() => Run(async host =>
    {
        var vm = host.NewSession(Project("main"));
        host.Send("/rewind", vm.Id);
        await WaitUntil("answered", () => Notes(vm).Count > 0);
        Assert.Equal(AgentHost.NothingToRewind, Notes(vm).Last());
    });

    [Fact]
    public void ASensitiveChatRefusesAnOnlineModelWhenMaskedUseIsOffAndOtherwiseMasksEvenWithTheShieldOff() => Run(async host =>
    {
        var vm = host.NewSession(Project("main"));
        host.Config.Privacy = host.Config.Privacy with { Mask = false };
        host.SetChatSensitive(vm, true);
        Assert.True(host.IsSensitive(vm));
        Assert.Contains("This chat is now Sensitive", Notes(vm).Last());

        host.Config.SensitiveAllowsShielded = false;
        Assert.NotNull(host.SensitiveRefusal(vm));
        host.Send("hello", vm.Id);
        await WaitUntil("refused", () => !vm.Running && Notes(vm, MessageRole.Error).Count > 0);
        Assert.Empty(_server.Seen);
        Assert.Contains("Sensitive chats use private models only", Notes(vm, MessageRole.Error).Last());

        host.Config.SensitiveAllowsShielded = true;
        host.Send("mail jane.doe@example.com about it", vm.Id);
        await WaitUntil("sent", () => !vm.Running && _server.Seen.Count >= 1);
        Assert.Contains("⟦EMAIL_1⟧", _server.Seen[0].LastUser);
        Assert.DoesNotContain("jane.doe@example.com", _server.Seen[0].AllUserText);
        Assert.Contains("--- Sensitive chat ---", _server.Seen[0].System);

        // Standard again, with the shield off: it goes as it is.
        host.SetChatSensitive(vm, false);
        _server.Reset((_, _) => new FakeReply.Text("ok"));
        host.Send("and cc bob@example.com", vm.Id);
        await WaitUntil("sent again", () => !vm.Running && _server.Seen.Count >= 1);
        Assert.Contains("bob@example.com", _server.Seen[0].LastUser);
    });

    [Fact]
    public void AProjectsDefaultMakesItsChatsSensitiveUnlessAChatChoseForItself() => UiThread.Run(() =>
    {
        var host = MakeHost();
        var project = Project("clinic");
        var first = host.NewSession(project);
        Assert.False(host.IsSensitive(first));
        host.SetProjectSensitive(project, true);
        Assert.True(host.IsSensitive(first));
        Assert.True(host.IsSensitive(host.NewSession(project)));
        host.SetChatSensitive(first, false);
        Assert.False(host.IsSensitive(first));
        Assert.False(host.IsSensitive(host.NewSession(Project("other"))));
        // The chat's own choice is stored with it.
        Assert.False(host.Log.List().Single(s => s.Id == first.Id).Sensitive);
        return Task.CompletedTask;
    });

    [Fact]
    public void AnMcpServersToolsAreOfferedAndRunOnceTheChatAllowsThem() => Run(async host =>
    {
        McpManager.TransportOverride = _ => new LoopbackMcpTransport(new McpLoopbackServer());
        try
        {
            host.McpServers.Save(new McpServerConfig { Id = "demo", Name = "demo", Command = "demo-server" });
            await host.Mcp.SyncAsync();
            _server.Reset((_, n) => n == 1
                ? new FakeReply.ToolCall("mcp__demo__echo", """{"text":"from the server"}""")
                : new FakeReply.Text("It said hello."));
            var vm = host.NewSession(Project("main"));
            host.Send("use the echo tool", vm.Id);
            await WaitUntil("asked", () => vm.PendingGates.Count > 0);
            Assert.Contains("MCP server “demo” wants to run echo", vm.PendingGates[0].Detail);
            host.AnswerGate(vm.Id, vm.PendingGates[0].Id, true);
            await WaitUntil("done", () => !vm.Running && _server.Seen.Count >= 2);

            Assert.Contains("mcp__demo__echo", _server.Seen[0].Tools);
            Assert.Contains("--- MCP servers ---", _server.Seen[0].System);
            Assert.Contains(_server.Seen[1].Messages, m => m.Role == "tool" && m.Content == "from the server");
        }
        finally
        {
            McpManager.TransportOverride = null;
        }
    });
}
