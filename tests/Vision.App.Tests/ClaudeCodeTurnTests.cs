using System.IO;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using Vision.App.Model;
using Vision.Core;
using Vision.Core.Tests;
using MessageRole = Vision.App.Model.MessageRole;

namespace Vision.App.Tests;

/// <summary>A chat on Claude Code, through AgentHost, against a stand-in CLI replaying the real CLI's captures: the turn's
/// rows and usage line, the session carried on with --resume, /goal one turn per round, permission questions on the chat's
/// own card, a lost session replaced with a recap, a CLI that isn't signed in, and Sensitive chats refused.</summary>
public sealed class ClaudeCodeTurnTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"vision-claudecode-{Guid.NewGuid():N}");
    private readonly FakeModelServer _server = new();

    private static readonly ProviderProfile Spark = new(ProviderKind.OpenAICompat, "Spark", "http://spark.test/v1", "stub-model");
    private static readonly ProviderProfile Sonnet = ClaudeCodeRoutes.Route("sonnet");
    private const string FixtureSession = "1bbd318c-7964-4079-9816-4fbe5b039d85";

    public ClaudeCodeTurnTests()
    {
        Directory.CreateDirectory(_dir);
        _server.Reset((_, _) => new FakeReply.Text("Earlier answer from the Spark."));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private AgentHost MakeHost(PermissionPreset preset, params ScriptedClaudeProcess[] processes)
    {
        var config = new AppConfig(Path.Combine(_dir, "settings.json"));
        config.Activate(Spark);
        foreach (var model in ClaudeCodeRoutes.Models) config.Store(ClaudeCodeRoutes.Route(model.Alias));
        config.Preset = preset.RawValue();
        config.ComputerToolsEnabled = false;
        return new AgentHost(config, new ConversationLog(Path.Combine(_dir, "log")), Dispatcher.CurrentDispatcher,
            queueFile: Path.Combine(_dir, "task-queue.json"),
            skillLocations: new SkillLocations(_dir, Path.Combine(_dir, "support")),
            vault: new CredentialVault(Path.Combine(_dir, "vault"), new MemoryBlobStore()),
            memory: new MemoryStore(Path.Combine(_dir, "memory")))
        {
            HttpHandlerForTesting = _server,
            RetryPolicy = new RetryPolicy { Delay = _ => TimeSpan.FromMilliseconds(50) },
            GoalErrorBackoff = _ => TimeSpan.FromMilliseconds(50),
            ClaudeCodeLaunchForTesting = ClaudeCodeLaunch.Direct("claude"),
            ClaudeCodeStarterForTesting = ScriptedClaudeProcess.Starter(processes),
        };
    }

    private SessionVM ClaudeChat(AgentHost host)
    {
        var project = Path.Combine(_dir, "proj");
        Directory.CreateDirectory(project);
        var vm = host.NewSession(project, host.Config.AsPreset);
        host.SetChatRoute(vm, Sonnet.RouteId);
        return vm;
    }

    private static async Task Finish(SessionVM vm, double seconds = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (vm.Running)
        {
            if (DateTime.UtcNow > deadline) Assert.Fail("the turn didn't finish");
            await Task.Delay(10);
        }
        await Task.Delay(30); // let queued UI work land
    }

    private static async Task WaitUntil(string what, Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail($"timed out waiting for: {what}");
            await Task.Delay(10);
        }
    }

    private static List<string> Texts(SessionVM vm, params MessageRole[] roles) =>
        vm.Entries.OfType<MessageEntryVM>().Where(m => roles.Contains(m.Role)).Select(m => m.Text).ToList();

    private static string PromptOf(ScriptedClaudeProcess process) =>
        string.Concat(JsonNode.Parse(process.Written[0])!["message"]!["content"]!.AsArray().Select(c => (string?)c!["text"] ?? ""));

    private static string ArgAfter(ScriptedClaudeProcess process, string flag)
    {
        var index = process.Args.ToList().IndexOf(flag);
        return index < 0 ? "" : process.Args[index + 1];
    }

    private static ScriptedClaudeProcess Reply(string session, string text)
    {
        var quoted = System.Text.Json.JsonSerializer.Serialize(text);
        string Fill(string line) => line.Replace("SESSION", session).Replace("\"TEXT\"", quoted);
        return ScriptedClaudeProcess.Replay(new[]
        {
            """{"type":"system","subtype":"init","cwd":"/work","session_id":"SESSION","tools":["Bash"],"mcp_servers":[],"model":"claude-sonnet-5-5","permissionMode":"default","apiKeySource":"none","claude_code_version":"2.1.299","uuid":"i"}""",
            """{"type":"assistant","message":{"id":"m","type":"message","role":"assistant","model":"claude-sonnet-5-5","content":[{"type":"text","text":"TEXT"}],"usage":{"input_tokens":10,"output_tokens":5}},"parent_tool_use_id":null,"session_id":"SESSION","uuid":"a"}""",
            """{"type":"result","subtype":"success","is_error":false,"result":"TEXT","session_id":"SESSION","num_turns":1,"total_cost_usd":0.02,"usage":{"input_tokens":10,"output_tokens":5,"cache_read_input_tokens":0,"cache_creation_input_tokens":0},"modelUsage":{"claude-sonnet-5-5":{"contextWindow":1000000}},"permission_denials":[]}""",
        }.Select(Fill));
    }

    [Fact]
    public void AChatOnClaudeCodeShowsItsTurnAndCarriesOnTheSameSession() => UiThread.Run(async () =>
    {
        var first = ScriptedClaudeProcess.Replay(ClaudeFixtures.Lines("write-partial.jsonl"));
        var second = ScriptedClaudeProcess.Replay(ClaudeFixtures.Lines("resume.jsonl"));
        var host = MakeHost(PermissionPreset.FullAccess, first, second);
        var vm = ClaudeChat(host);

        host.Send("write hello.txt", vm.Id);
        Assert.True(vm.Running);
        await Finish(vm);

        Assert.Contains("Done — the file is written.", Texts(vm, MessageRole.Assistant));
        Assert.Contains(vm.Entries.OfType<ToolEntryVM>(), t => t.Name == "write_file" && t.Summary == "Created hello.txt");
        var usage = Assert.Single(Texts(vm, MessageRole.Notice), n => n.StartsWith("Claude Code: ", StringComparison.Ordinal));
        Assert.Contains("3 steps", usage);
        Assert.Contains("at API prices", usage);
        Assert.Contains(Texts(vm, MessageRole.Notice), n => n.Contains("ANTHROPIC_API_KEY", StringComparison.Ordinal)); // the capture ran on a key
        Assert.Equal("claude-sonnet-5-5", vm.TurnModel);
        Assert.Equal(FixtureSession, host.Log.Row(vm.Id)!.ClaudeSession);
        Assert.Equal(1_000_000, host.ContextLimit(vm));
        // The first turn opened a session of its own, with the prompt as the person wrote it, in the chat's folder.
        Assert.True(Guid.TryParse(ArgAfter(first, "--session-id"), out _));
        Assert.Equal("sonnet", ArgAfter(first, "--model"));
        Assert.Equal("acceptEdits", ArgAfter(first, "--permission-mode"));
        Assert.Equal("write hello.txt", PromptOf(first));
        Assert.Equal(vm.WorkingFolder, first.WorkingDirectory);

        host.Send("and now say hello", vm.Id);
        await Finish(vm);
        Assert.Equal(FixtureSession, ArgAfter(second, "--resume"));
        Assert.Contains("Hello from the fixture server. You said: And now just say hello", Texts(vm, MessageRole.Assistant));
        // Its share of the session's cost: 0.01191 - 0.00794.
        Assert.Contains(Texts(vm, MessageRole.Notice), n => n.StartsWith("Claude Code: 1 step", StringComparison.Ordinal) && n.Contains("under $0.01"));
        host.StopAll();
    });

    [Fact]
    public void ASensitiveChatCannotUseClaudeCodeAndSaysWhy() => UiThread.Run(async () =>
    {
        var host = MakeHost(PermissionPreset.WorkspaceWrite); // no process: starting one would fail the test
        var vm = ClaudeChat(host);
        host.SetSensitive(vm, true);
        host.Send("read the payroll export", vm.Id);
        await Finish(vm);
        var error = Assert.Single(Texts(vm, MessageRole.Error));
        Assert.Equal(SensitivityPolicy.RefusalMessage(Sonnet, host.ShieldOn), error);
        Assert.Contains("Claude Code reads your files itself", error);
        Assert.Contains(Texts(vm, MessageRole.Notice), n => n == AgentHost.UndeliveredPrefix + "read the payroll export");
        Assert.True(host.Log.Row(vm.Id)!.Sensitive);
        Assert.NotNull(host.SensitivityRefusal(vm, Sonnet));
        Assert.Null(host.SensitivityRefusal(vm, Spark));
    });

    [Fact]
    public void AGoalRunsOneClaudeCodeTurnPerRoundUntilItSaysDone() => UiThread.Run(async () =>
    {
        var first = Reply("s-goal", "Wrote the file; the tests are next.");
        var second = Reply("s-goal", "Tests pass.\nGOAL_COMPLETE");
        var host = MakeHost(PermissionPreset.FullAccess, first, second);
        var vm = ClaudeChat(host);
        host.Send("/goal make the tests pass", vm.Id);
        await Finish(vm, 30);

        Assert.Contains(ClaudeCodeGoalNote(), PromptOf(first));
        Assert.Contains("GOAL: make the tests pass", PromptOf(first));
        Assert.Equal("s-goal", ArgAfter(second, "--resume"));
        Assert.Contains("[Goal round 2]", PromptOf(second));
        Assert.Contains(Texts(vm, MessageRole.Notice), n => n.StartsWith("✅ Goal complete after 2 rounds", StringComparison.Ordinal));
    });

    private static string ClaudeCodeGoalNote() => AgentHost.ClaudeCodeGoalNote;

    [Fact]
    public void APermissionQuestionAsksThePersonOnTheChatsOwnCard() => UiThread.Run(async () =>
    {
        var process = ScriptedClaudeProcess.Replay(ClaudeFixtures.Lines("bash-allow.jsonl"));
        var host = MakeHost(PermissionPreset.WorkspaceWrite, process);
        var vm = ClaudeChat(host);
        host.Send("make a folder", vm.Id);
        await WaitUntil("the permission card", () => vm.PendingGates.Count == 1);
        var gate = vm.PendingGates[0];
        Assert.Equal("run_shell_command", gate.Name);
        Assert.Equal("Run command: mkdir -p made-by-allow && touch made-by-allow/a.txt", gate.Detail);
        Assert.Equal("default", ArgAfter(process, "--permission-mode"));
        host.AnswerGate(vm.Id, gate.Id, allow: true);
        await Finish(vm);
        var answer = JsonNode.Parse(process.Written[1])!["response"]!["response"]!;
        Assert.Equal("allow", (string?)answer["behavior"]);
        Assert.Empty(vm.PendingGates);
    });

    [Fact]
    public void ALostSessionIsReplacedByANewOneThatHearsTheChatSoFar() => UiThread.Run(async () =>
    {
        var missing = ScriptedClaudeProcess.Replay(ClaudeFixtures.Lines("resume-missing.jsonl"));
        var fresh = ScriptedClaudeProcess.Replay(ClaudeFixtures.Lines("write-partial.jsonl"));
        var host = MakeHost(PermissionPreset.FullAccess, missing, fresh);
        var project = Path.Combine(_dir, "proj");
        Directory.CreateDirectory(project);
        var vm = host.NewSession(project, PermissionPreset.FullAccess);
        host.Send("what is in the repo?", vm.Id); // on the Spark
        await Finish(vm);
        Assert.Contains("Earlier answer from the Spark.", Texts(vm, MessageRole.Assistant));

        host.SetChatRoute(vm, Sonnet.RouteId);
        host.Log.SetClaudeSession(vm.Id, "11111111-2222-3333-4444-555555555555", null); // a session the CLI no longer has
        host.Send("write hello.txt", vm.Id);
        await Finish(vm);

        Assert.Equal("11111111-2222-3333-4444-555555555555", ArgAfter(missing, "--resume"));
        Assert.True(Guid.TryParse(ArgAfter(fresh, "--session-id"), out _));
        var prompt = PromptOf(fresh);
        Assert.StartsWith(Vision.Core.ClaudeCodeRecap.Header, prompt);
        Assert.Contains("Earlier answer from the Spark.", prompt);
        Assert.EndsWith("write hello.txt", prompt);
        Assert.Contains(Texts(vm, MessageRole.Notice), n => n.StartsWith("This chat now uses Claude Code", StringComparison.Ordinal));
        Assert.Contains(Texts(vm, MessageRole.Notice), n => n.StartsWith("Claude Code no longer had this chat's session", StringComparison.Ordinal));
        Assert.Equal(FixtureSession, host.Log.Row(vm.Id)!.ClaudeSession);
    });

    [Fact]
    public void BackOnClaudeCodeItHearsWhatOtherModelsAnsweredInBetween() => UiThread.Run(async () =>
    {
        var first = Reply("s-back", "Claude Code wrote the parser.");
        var later = Reply("s-back", "Picked up where the Spark left off.");
        var host = MakeHost(PermissionPreset.FullAccess, first, later);
        var vm = ClaudeChat(host);
        host.Send("write the parser", vm.Id);
        await Finish(vm);
        Assert.NotNull(host.Log.Row(vm.Id)!.ClaudeSyncedAt);

        host.SetChatRoute(vm, Spark.RouteId);
        host.Send("add a quick note to the README", vm.Id); // the Spark answers this one
        await Finish(vm);
        host.SetChatRoute(vm, Sonnet.RouteId);
        host.Send("now wire it up", vm.Id);
        await Finish(vm);

        Assert.Equal("s-back", ArgAfter(later, "--resume"));
        var prompt = PromptOf(later);
        Assert.StartsWith(Vision.Core.ClaudeCodeRecap.Header, prompt);
        Assert.Contains("Person: add a quick note to the README", prompt);
        Assert.Contains("Earlier answer from the Spark.", prompt);
        Assert.DoesNotContain("write the parser", prompt); // the session already has its own turn
        Assert.EndsWith("now wire it up", prompt);
        // ...and a turn right after a Claude Code one carries no recap.
        Assert.Equal("write the parser", PromptOf(first));
    });

    [Fact]
    public void AClaudeCodeThatIsNotSignedInSaysHowToSignIn() => UiThread.Run(async () =>
    {
        var host = MakeHost(PermissionPreset.WorkspaceWrite, ScriptedClaudeProcess.Replay(ClaudeFixtures.Lines("not-signed-in.jsonl")));
        var vm = ClaudeChat(host);
        host.Send("hello", vm.Id);
        await Finish(vm);
        var error = Assert.Single(Texts(vm, MessageRole.Error));
        Assert.Contains("isn't signed in", error);
        Assert.Contains("claude auth login", error);
        Assert.Contains(Texts(vm, MessageRole.Notice), n => n == AgentHost.UndeliveredPrefix + "hello");
    });

    [Fact]
    public void SensitiveIsSetWithACommandAndKeptWithTheChat() => UiThread.Run(async () =>
    {
        var host = MakeHost(PermissionPreset.WorkspaceWrite);
        var vm = ClaudeChat(host);
        host.Send("/sensitive on", vm.Id);
        await Task.Delay(30);
        Assert.True(vm.Sensitive);
        Assert.StartsWith("This chat is Sensitive now. Claude Code reads your files itself", Texts(vm, MessageRole.Error).Single());
        host.Send("/sensitive off", vm.Id);
        await Task.Delay(30);
        Assert.False(vm.Sensitive);
        Assert.Null(host.Log.Row(vm.Id)!.Sensitive);
    });
}
