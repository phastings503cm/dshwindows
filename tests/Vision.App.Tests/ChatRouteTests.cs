using System.IO;
using System.Windows.Threading;
using Vision.App.Model;
using Vision.Core;
using MessageRole = Vision.App.Model.MessageRole;

namespace Vision.App.Tests;

/// <summary>Each chat talks to its own model: two projects open with a prompt in each, on two routes, hit two servers at
/// once; new chats start with their project's last choice; /goal, the task list and subagents stay on the chat's route; a
/// chat whose route is gone falls back to the primary and says so; a renamed route keeps its chats.</summary>
public sealed class ChatRouteTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"vision-routes-{Guid.NewGuid():N}");
    private readonly FakeModelServer _server = new();

    private static readonly ProviderProfile RouteA = new(ProviderKind.OpenAICompat, "Spark A", "http://a.test/v1", "stub-model");
    private static readonly ProviderProfile RouteB = new(ProviderKind.OpenAICompat, "Spark B", "http://b.test/v1", "stub-model");

    public ChatRouteTests()
    {
        Directory.CreateDirectory(_dir);
        _server.Reset((_, _) => new FakeReply.Text("Done.\nGOAL_COMPLETE"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private string Project(string name)
    {
        var path = Path.Combine(_dir, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private AppConfig NewConfig()
    {
        var config = new AppConfig(Path.Combine(_dir, "settings.json"));
        config.Activate(RouteA);
        config.Store(RouteB);
        config.Preset = PermissionPreset.FullAccess.RawValue();
        config.ComputerToolsEnabled = false;
        return config;
    }

    private AgentHost MakeHost(AppConfig config) =>
        new(config, new ConversationLog(Path.Combine(_dir, "log")), Dispatcher.CurrentDispatcher,
            queueFile: Path.Combine(_dir, "task-queue.json"),
            skillLocations: new SkillLocations(_dir, Path.Combine(_dir, "support")),
            vault: new CredentialVault(Path.Combine(_dir, "vault"), new MemoryBlobStore()),
            memory: new MemoryStore(Path.Combine(_dir, "memory")))
        {
            HttpHandlerForTesting = _server,
            RetryPolicy = new RetryPolicy { Delay = _ => TimeSpan.FromMilliseconds(50) },
            GoalErrorBackoff = _ => TimeSpan.FromMilliseconds(50),
        };

    private void Run(Func<AgentHost, AppConfig, Task> body) => UiThread.Run(async () =>
    {
        var config = NewConfig();
        var host = MakeHost(config);
        try
        {
            await body(host, config);
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

    private static List<string> Replies(SessionVM vm) =>
        vm.Entries.OfType<MessageEntryVM>().Where(m => m.Role == MessageRole.Assistant).Select(m => m.Text).ToList();

    private static List<string> Notes(SessionVM vm) =>
        vm.Entries.OfType<MessageEntryVM>().Where(m => m.Role is MessageRole.Notice or MessageRole.Error).Select(m => m.Text).ToList();

    [Fact]
    public void TwoChatsOnTwoRoutesRunAtOnceEachOnItsOwnServer() => Run(async (host, config) =>
    {
        var gateA = new TaskCompletionSource();
        var gateB = new TaskCompletionSource();
        _server.Reset((request, _) => request.Host == "a.test"
            ? new FakeReply.Gated(gateA.Task, "answer from A")
            : new FakeReply.Gated(gateB.Task, "answer from B"));
        var first = host.NewSession(Project("one"));
        var second = host.NewSession(Project("two"));
        host.SetChatRoute(second, RouteB.RouteId);
        Assert.Equal(RouteA.RouteId, host.ChatRouteId(first));
        Assert.Equal(RouteB.RouteId, host.ChatRouteId(second));
        Assert.Equal(RouteA.RouteId, config.ActiveRoute); // choosing a model in a chat leaves the primary alone

        host.Send("question one", first.Id);
        host.Send("question two", second.Id);
        // Both in flight at the same moment, each on its own server.
        await WaitUntil("both requests in flight", () => _server.Seen.Count(r => r.Fresh) >= 2);
        var seen = _server.Seen.Where(r => r.Fresh).ToList();
        Assert.Equal("a.test", Assert.Single(seen, r => r.LastUser.Contains("question one", StringComparison.Ordinal)).Host);
        Assert.Equal("b.test", Assert.Single(seen, r => r.LastUser.Contains("question two", StringComparison.Ordinal)).Host);
        Assert.True(first.Running && second.Running);

        gateB.SetResult();
        await WaitUntil("second done", () => !second.Running);
        Assert.True(first.Running); // the other chat is still on its own server
        gateA.SetResult();
        await WaitUntil("first done", () => !first.Running);
        Assert.Contains("answer from A", Replies(first));
        Assert.Contains("answer from B", Replies(second));
        Assert.DoesNotContain("answer from B", Replies(first));

        // The menus read the selected chat only.
        Assert.Equal(RouteA.RouteId, host.ChatRouteId(first));
        Assert.Equal(RouteB.RouteId, host.ChatRouteId(second));
    });

    [Fact]
    public void NewChatsStartWithTheirProjectsLastChoiceAndTheChoiceIsStored() => Run(async (host, config) =>
    {
        var two = Project("two");
        var chat = host.NewSession(two);
        host.SetChatRoute(chat, RouteB.RouteId);
        host.SetChatThinking(chat, ThinkingLevel.Low);
        var next = host.NewSession(two);
        Assert.Equal(RouteB.RouteId, next.RouteId);
        Assert.Equal(ThinkingLevel.Low, next.Thinking);
        var elsewhere = host.NewSession(Project("one"));
        Assert.Equal(RouteA.RouteId, elsewhere.RouteId); // another project: the primary
        Assert.Null(elsewhere.Thinking);

        // Stored with the conversation and in the settings: a relaunch keeps both.
        config.SaveNow();
        var reloaded = MakeHost(new AppConfig(Path.Combine(_dir, "settings.json")));
        Assert.Equal(RouteB.RouteId, reloaded.Session(chat.Id)!.RouteId);
        Assert.Equal(ThinkingLevel.Low, reloaded.Session(chat.Id)!.Thinking);
        Assert.Equal(RouteA.RouteId, reloaded.Session(elsewhere.Id)!.RouteId);
        Assert.Equal(RouteB.RouteId, reloaded.NewSession(two).RouteId);
        await Task.CompletedTask;
    });

    [Fact]
    public void AGoalTheTaskListAndSubagentsStayOnTheChatsRoute() => Run(async (host, _) =>
    {
        var chat = host.NewSession(Project("two"));
        host.SetChatRoute(chat, RouteB.RouteId);
        var subagentAsked = 0;
        _server.Reset((request, _) =>
        {
            if (request.LastUser.Contains("SUBTASK-7", StringComparison.Ordinal)) return new FakeReply.Text("Subtask report.");
            // The goal's first round hands a side task to a subagent; with no workers it falls back to the chat's own server.
            if (request.WorkingOn("ship it") && Interlocked.Increment(ref subagentAsked) == 1)
                return new FakeReply.ToolCall("agent", """{"description":"check","prompt":"SUBTASK-7: count the files"}""");
            return new FakeReply.Text("Done.\nGOAL_COMPLETE");
        });
        host.Send("/goal ship it", chat.Id);
        await WaitUntil("goal done", () => !chat.Running && Notes(chat).Any(n => n.Contains("Goal complete", StringComparison.Ordinal)), 30);
        Assert.Contains(_server.Seen, r => r.LastUser.Contains("SUBTASK-7", StringComparison.Ordinal));
        Assert.All(_server.Seen, r => Assert.Equal("b.test", r.Host));

        var before = _server.Seen.Count;
        host.QueueAdd(chat.Id, "Tidy the README");
        host.StartQueue(chat.Id);
        await WaitUntil("task ran", () => _server.Seen.Count > before && !host.IsQueueRunning(chat.Id), 30);
        Assert.All(_server.Seen.Skip(before), r => Assert.Equal("b.test", r.Host));
    });

    [Fact]
    public void AChatWhoseRouteIsGoneUsesThePrimaryAndSaysSo() => Run(async (host, config) =>
    {
        var chat = host.NewSession(Project("two"));
        host.SetChatRoute(chat, RouteB.RouteId);
        config.RemoveProvider(RouteB);
        _server.Reset((_, _) => new FakeReply.Text("fine"));
        host.Send("hello", chat.Id);
        await WaitUntil("reply", () => !chat.Running && Replies(chat).Count > 0);
        Assert.Equal("a.test", Assert.Single(_server.Seen, r => r.Fresh).Host);
        Assert.Equal(RouteA.RouteId, chat.RouteId);
        Assert.Contains(Notes(chat), n => n.StartsWith("This chat's model is no longer set up (or is switched off), so it now uses Spark A", StringComparison.Ordinal));
    });

    [Fact]
    public void ARenamedRouteKeepsItsChats() => Run(async (host, config) =>
    {
        var chat = host.NewSession(Project("two"));
        host.SetChatRoute(chat, RouteB.RouteId);
        var renamed = config.ReplaceProvider(RouteB.RouteId, RouteB with { Name = "Spark B (desk)" });
        Assert.Equal(renamed.RouteId, chat.RouteId);
        Assert.Equal(renamed.RouteId, config.LastChoice(Project("two"))!.Route);
        _server.Reset((_, _) => new FakeReply.Text("fine"));
        host.Send("hello", chat.Id);
        await WaitUntil("reply", () => !chat.Running && Replies(chat).Count > 0);
        Assert.Equal("b.test", Assert.Single(_server.Seen, r => r.Fresh).Host);
        Assert.DoesNotContain(Notes(chat), n => n.Contains("no longer set up", StringComparison.Ordinal));
    });

    /// <summary>A parameter a server refuses is dropped, and the chat gets one line instead of an error.</summary>
    [Fact]
    public void ARefusedParameterIsAnnouncedInTheChat() => Run(async (host, config) =>
    {
        RequestAdaptationStore.Shared.Forget("http://a.test/v1");
        var stored = config.Providers.First(p => p.RouteId == RouteA.RouteId);
        stored.Temperature = 0.7;
        var chat = host.NewSession(Project("one"));
        _server.Reset((_, index) => index == 1
            ? new FakeReply.Http(400, """{"error":{"message":"invalid temperature: only 1 is allowed for this model","type":"invalid_request_error"}}""")
            : new FakeReply.Text("ok"));
        try
        {
            host.Send("hello", chat.Id);
            await WaitUntil("reply", () => !chat.Running && Replies(chat).Count > 0);
            Assert.Contains("ok", Replies(chat));
            Assert.Contains(Notes(chat), n => n == "Spark A (stub-model) doesn't take `temperature` — Vision stopped sending it for this model and carried on.");
            Assert.DoesNotContain(chat.Entries.OfType<MessageEntryVM>(), m => m.Role == MessageRole.Error);
        }
        finally
        {
            RequestAdaptationStore.Shared.Forget("http://a.test/v1");
        }
    });
}
