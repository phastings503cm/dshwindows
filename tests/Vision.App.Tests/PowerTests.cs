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
        Assert.Equal("update the notes", row.TurnTitle);
        Assert.Equal("+1 −1", row.LineSummary);
        Assert.True(row.CanRewind);
        Assert.Equal("v2\n", File.ReadAllText(file));
        Assert.Contains(AgentHost.LogRows(vm.Entries, vm.Id), r => r.Kind == "checkpoint" && r.ArgSummary == row.TurnId);

        var result = host.Rewind(vm, host.PlanRewind(vm, row.TurnId)!, includeConflicts: false);
        Assert.Equal(1, result.ChangedCount);
        Assert.Equal("v1\n", File.ReadAllText(file));
        Assert.True(row.IsRewound);
        Assert.Equal("Rewound", row.Status);
        Assert.Contains("↺ Rewound “update the notes”: put back 1 file (notes.md).", Notes(vm));
        // The rewind is a turn of its own, with its own row (so it can be rewound in turn).
        var rewind = vm.Entries.OfType<CheckpointEntryVM>().Last();
        Assert.Equal("Rewind of “update the notes”", rewind.TurnTitle);

        // The model hears of it with its next message.
        _server.Reset((_, _) => new FakeReply.Text("ok"));
        host.Send("what now?", vm.Id);
        await WaitUntil("second turn", () => !vm.Running && _server.Seen.Count >= 1);
        Assert.Contains("the user rewound the file changes of an earlier turn (“update the notes”)", _server.Seen[0].LastUser);
        Assert.Contains("notes.md", _server.Seen[0].LastUser);
        Assert.EndsWith("what now?", _server.Seen[0].LastUser);

        // A relaunch reads the rows back with their records.
        var again = MakeHost();
        var reopened = again.Session(vm.Id)!;
        again.Hydrate(reopened);
        Assert.True(reopened.Entries.OfType<CheckpointEntryVM>().First().IsRewound);
    });

    [Fact]
    public void RewindWithNothingToListSaysSo() => Run(async host =>
    {
        var vm = host.NewSession(Project("main"));
        host.Send("/rewind", vm.Id);
        await WaitUntil("answered", () => Notes(vm).Count > 0);
        Assert.Equal(AgentHost.NothingToRewind, Notes(vm).Last());
    });

    [Fact]
    public void ASensitiveChatIsRefusedAnOnlineModelWhileTheShieldIsOffAndMaskedWhileItIsOn() => Run(async host =>
    {
        var vm = host.NewSession(Project("main"));
        host.Config.Privacy = host.Config.Privacy with { Mask = false };
        host.SetChatSensitive(vm, true);
        Assert.StartsWith("🔒 This chat is now Sensitive.", Notes(vm).Last());
        Assert.NotNull(host.SensitiveRefusal(vm));

        host.Send("hello", vm.Id);
        await WaitUntil("refused", () => !vm.Running && Notes(vm, MessageRole.Error).Count > 0);
        Assert.Empty(_server.Seen);
        Assert.StartsWith("This chat is Sensitive. stub is an online service and the sensitive-data shield is switched off", Notes(vm, MessageRole.Error).Last());

        host.Config.Privacy = host.Config.Privacy with { Mask = true };
        Assert.Null(host.SensitiveRefusal(vm));
        Assert.Equal("Sensitive — stub isn't private: what the shield detects is masked before sending.", host.SensitivityHeader(vm));
        host.Send("mail jane.doe@example.com about it", vm.Id);
        await WaitUntil("sent", () => !vm.Running && _server.Seen.Count >= 1);
        Assert.Contains("⟦EMAIL_1⟧", _server.Seen[0].LastUser);
        Assert.DoesNotContain("jane.doe@example.com", _server.Seen[0].AllUserText);
        Assert.Contains("--- Sensitive chat ---", _server.Seen[0].System);

        host.SetChatSensitive(vm, false);
        Assert.Null(host.SensitivityHeader(vm));
        Assert.Equal("This chat is no longer Sensitive: every model may be used, with the usual masking for models that aren't private.", Notes(vm).Last());
    });

    [Fact]
    public void NewChatsOfAProjectSetToSensitiveStartSensitive() => UiThread.Run(() =>
    {
        var host = MakeHost();
        var project = Project("clinic");
        var earlier = host.NewSession(project);
        host.SetProjectStartsSensitive(project, true);
        Assert.False(earlier.Sensitive); // a chat keeps what it started with
        var fresh = host.NewSession(project);
        Assert.True(fresh.Sensitive);
        Assert.True(host.Log.List().Single(s => s.Id == fresh.Id).Sensitive);
        Assert.False(host.NewSession(Project("other")).Sensitive);
        Assert.Null(host.Log.List().Single(s => s.Id == earlier.Id).Sensitive);
        return Task.CompletedTask;
    });

    [Fact]
    public void AnMcpServersToolsAreOfferedAndRunOnceTheChatAllowsThem() => Run(async host =>
    {
        McpManager.TransportOverride = _ => new LoopbackMcpTransport(new McpLoopbackServer());
        try
        {
            host.Config.Preset = PermissionPreset.WorkspaceWrite.RawValue();
            host.McpServers.Save(new McpServerConfig { Id = "demo-id", Name = "demo", Command = "demo-server" });
            await host.Mcp.SyncAsync();
            _server.Reset((_, n) => n == 1
                ? new FakeReply.ToolCall("mcp__demo__echo", """{"text":"from the server"}""")
                : new FakeReply.Text("It said hello."));
            var vm = host.NewSession(Project("main"));
            host.Send("use the echo tool", vm.Id);
            await WaitUntil("asked", () => vm.PendingGates.Count > 0);
            Assert.Equal("MCP server “demo” wants to run its tool “echo” with {\"text\":\"from the server\"}", vm.PendingGates[0].Detail);
            host.AnswerGate(vm.Id, vm.PendingGates[0].Id, true);
            await WaitUntil("done", () => !vm.Running && _server.Seen.Count >= 2);

            Assert.Contains("mcp__demo__echo", _server.Seen[0].Tools);
            Assert.Contains("MCP servers connected (their tools are named mcp__<server>__<tool>): demo (2 tools).", _server.Seen[0].System);
            Assert.Contains(_server.Seen[1].Messages, m => m.Role == "tool" && m.Content == "from the server");
        }
        finally
        {
            McpManager.TransportOverride = null;
        }
    });
}
