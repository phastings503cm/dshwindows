using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Authentication;

namespace Dsh.Core.Tests;

/// <summary>A client whose first <paramref name="failures"/> calls stream <paramref name="partial"/>
/// text and then throw <paramref name="error"/> (a stream cut off mid-reply); later calls play
/// <paramref name="turns"/>.</summary>
public sealed class OutageClient(int failures, Func<Exception> error, string partial = "", params Turn[] turns) : ILlmClient
{
    private readonly Lock _lock = new();
    private int _failuresLeft = failures;
    private readonly Queue<Turn> _turns = new(turns);
    private readonly List<string> _models = [];

    public int Calls
    {
        get
        {
            lock (_lock) return _models.Count;
        }
    }

    public IReadOnlyList<string> Models
    {
        get
        {
            lock (_lock) return [.. _models];
        }
    }

    public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(LlmRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        bool fail;
        Turn? turn = null;
        lock (_lock)
        {
            _models.Add(request.Model);
            fail = _failuresLeft > 0;
            if (fail) _failuresLeft--;
            else turn = _turns.Count > 0 ? _turns.Dequeue() : new Turn("done");
        }
        await Task.Yield();
        if (fail)
        {
            if (partial.Length > 0) yield return new LlmStreamEvent.Text(partial);
            throw error();
        }
        foreach (var ch in turn!.Text) yield return new LlmStreamEvent.Text(ch.ToString());
        yield return new LlmStreamEvent.Done(turn.Calls ?? [], "stop", turn.Usage);
    }

    public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>(["outage"]);
}

/// <summary>Answers each call from a closure (1-based call number): text, or an exception.</summary>
public sealed class CallbackClient(Func<int, (string? Text, Exception? Error)> answer) : ILlmClient
{
    private int _n;

    public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(LlmRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var (text, error) = answer(Interlocked.Increment(ref _n));
        await Task.Yield();
        if (error is not null) throw error;
        yield return new LlmStreamEvent.Text(text ?? "");
        yield return new LlmStreamEvent.Done([], "stop", null);
    }

    public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>([]);
}

/// <summary>Plays scripted turns, then fails every later call with the given error.</summary>
public sealed class ScriptedThenFailing(Exception error, params Turn[] turns) : ILlmClient
{
    private readonly Queue<Turn> _turns = new(turns);
    private readonly Lock _lock = new();

    public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(LlmRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Turn? turn;
        lock (_lock) turn = _turns.Count > 0 ? _turns.Dequeue() : null;
        await Task.Yield();
        if (turn is null) throw error;
        foreach (var ch in turn.Text) yield return new LlmStreamEvent.Text(ch.ToString());
        yield return new LlmStreamEvent.Done(turn.Calls ?? [], "stop", null);
    }

    public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>([]);
}

/// <summary>Ported from RequestRetryTests.swift: which model-call failures are retried, and the
/// engine riding out outages without duplicating text or losing work.</summary>
public sealed class RequestRetryTests : IDisposable
{
    private readonly TempDirectory _root = new("dsh-retry");
    private static readonly RetryPolicy Instant = new() { Delay = _ => TimeSpan.Zero };

    public void Dispose() => _root.Dispose();

    private Engine MakeEngine(ILlmClient client, RetryPolicy? retry = null, ToolRegistry? registry = null,
                              RerouteHook? reroute = null, CompactionHook? compaction = null, int? window = null)
    {
        return new Engine(client, registry ?? new ToolRegistry([new EchoTool()]), "system",
            new EngineConfig("m1")
            {
                MaxIterations = 5,
                ToolTimeout = TimeSpan.FromSeconds(5),
                Retry = retry ?? Instant,
                ContextWindow = window,
            },
            _root.Path, new PermissionPolicy(PermissionPreset.FullAccess, _root.Path), Gates.Allow)
        {
            Reroute = reroute,
            Compactor = compaction,
        };
    }

    private static HttpRequestException Http(HttpRequestError kind, Exception? inner = null) =>
        new(kind, "failed", inner);

    // MARK: Classification

    [Fact]
    public void TransientFailuresRetryUntilAvailable()
    {
        Exception[] transient =
        [
            new TimeoutException(),
            new SocketException((int)SocketError.ConnectionRefused),
            new IOException("connection reset"),
            Http(HttpRequestError.ConnectionError),
            Http(HttpRequestError.NameResolutionError),
            Http(HttpRequestError.ResponseEnded),
            LlmException.Sse("cut off"),
            LlmException.Connection("refused"),
            LlmException.Connection("the request timed out", new TaskCanceledException()),
        ];
        foreach (var error in transient)
            Assert.IsType<RetryDisposition.UntilAvailable>(RequestRetry.Disposition(error));
        foreach (var status in new[] { 408, 429, 502, 503, 504 })
            Assert.IsType<RetryDisposition.UntilAvailable>(RequestRetry.Disposition(LlmException.Http(status, "")));
        // Mid-swap: the old model id is gone until the route re-resolves.
        Assert.IsType<RetryDisposition.UntilAvailable>(
            RequestRetry.Disposition(LlmException.Http(404, """{"message":"The model `qwen` does not exist."}""")));
        Assert.IsType<RetryDisposition.UntilAvailable>(
            RequestRetry.Disposition(LlmException.Http(400, "Model is loading, please wait")));
    }

    [Fact]
    public void PermanentFailuresSurfaceImmediately()
    {
        Assert.IsType<RetryDisposition.Fail>(RequestRetry.Disposition(new OperationCanceledException()));
        Assert.IsType<RetryDisposition.Fail>(RequestRetry.Disposition(LlmException.Unsupported("bad base URL: x")));
        // An untrusted certificate or rejected credentials won't fix themselves.
        Assert.IsType<RetryDisposition.Fail>(RequestRetry.Disposition(
            LlmException.Connection("ssl", Http(HttpRequestError.SecureConnectionError, new AuthenticationException("untrusted root")))));
        Assert.IsType<RetryDisposition.Fail>(RequestRetry.Disposition(Http(HttpRequestError.UserAuthenticationError)));
        foreach (var status in new[] { 400, 401, 403, 404, 422 })
            Assert.IsType<RetryDisposition.Fail>(RequestRetry.Disposition(LlmException.Http(status, "nope")));
        Assert.IsType<RetryDisposition.Fail>(RequestRetry.Disposition(LlmException.NoModel()));
        Assert.IsType<RetryDisposition.Fail>(RequestRetry.Disposition(LlmException.Overflow(10, "")));
        // A 500 is retried, but not forever.
        Assert.Equal(new RetryDisposition.Limited(RequestRetry.ServerErrorRetries),
            RequestRetry.Disposition(LlmException.Http(500, "boom")));
        Assert.False(RetryPolicy.Standard.ShouldRetry(LlmException.Http(500, ""), RequestRetry.ServerErrorRetries + 1));
        Assert.True(RetryPolicy.Standard.ShouldRetry(new TimeoutException(), 10_000));
        Assert.False(RetryPolicy.Off.ShouldRetry(new TimeoutException(), 1));
    }

    [Fact]
    public void BackoffGrowsThenHoldsAtThirtySeconds()
    {
        Assert.Equal([2, 4, 8, 16, 30, 30, 30], Enumerable.Range(1, 7).Select(n => RequestRetry.Backoff(n).TotalSeconds));
    }

    [Fact]
    public void ReasonsAreShortAndPlain()
    {
        Assert.Equal("the request timed out", RequestRetry.Reason(new TimeoutException()));
        Assert.Equal("the server refused the connection", RequestRetry.Reason(Http(HttpRequestError.ConnectionError)));
        Assert.Equal("HTTP 503", RequestRetry.Reason(LlmException.Http(503, "")));
        Assert.True(RequestRetry.Reason(LlmException.Http(502, new string('x', 500))).Length < 100);
        Assert.Equal("server unreachable (refused)", RequestRetry.Reason(LlmException.Connection("refused")));
    }

    // MARK: Engine

    [Fact]
    public async Task TimeoutsAreRetriedUntilTheModelAnswers()
    {
        var client = new OutageClient(3, () => LlmException.Sse("no data"), "Hel", new Turn("Hello"));
        var events = new EventRecorder();
        var result = await MakeEngine(client).RunAsync([], "hi", sink: events.Record);
        Assert.Equal("Hello", result.FinalText);
        Assert.Equal(4, client.Calls);
        Assert.Equal([1, 2, 3], events.Of<EngineEvent.Retrying>().Select(r => r.Attempt));
        Assert.StartsWith("stream cut off", events.Of<EngineEvent.Retrying>().First().Reason);
        Assert.Equal([3], events.Of<EngineEvent.Recovered>().Select(r => r.Attempts));
        // The cut-off partial "Hel"s are void: text as the UI shows it (deltas since the last retry)
        // and the transcript carry the reply once.
        var visible = "";
        foreach (var e in events.Events)
        {
            if (e is EngineEvent.TextDelta d) visible += d.Text;
            if (e is EngineEvent.Retrying) visible = "";
        }
        Assert.Equal("Hello", visible);
        Assert.Equal(["Hello"], result.Messages.Where(m => m.Role == MessageRole.Assistant).Select(m => m.Content));
    }

    [Fact]
    public async Task PermanentErrorIsNotRetried()
    {
        var client = new OutageClient(1, () => LlmException.Http(401, "bad key"));
        var events = new EventRecorder();
        await Assert.ThrowsAsync<LlmException>(() => MakeEngine(client).RunAsync([], "hi", sink: events.Record));
        Assert.Equal(1, client.Calls);
        Assert.Empty(events.Of<EngineEvent.Retrying>());
    }

    [Fact]
    public async Task RetryingCanBeTurnedOff()
    {
        var client = new OutageClient(1, () => new TimeoutException());
        await Assert.ThrowsAsync<TimeoutException>(() => MakeEngine(client, RetryPolicy.Off).RunAsync([], "hi"));
    }

    [Fact]
    public async Task StopDuringTheWaitEndsTheRunPromptly()
    {
        var client = new OutageClient(1_000, () => LlmException.Connection("refused"));
        var slow = new RetryPolicy { Delay = _ => TimeSpan.FromSeconds(30) };
        using var cts = new CancellationTokenSource();
        var run = MakeEngine(client, slow).RunAsync([], "hi", cancellationToken: cts.Token);
        await Task.Delay(200);
        var stopped = DateTimeOffset.Now;
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.True(DateTimeOffset.Now - stopped < TimeSpan.FromSeconds(5));
        Assert.Equal(1, client.Calls);
    }

    [Fact]
    public async Task RetryFollowsARerouteToTheNewModel()
    {
        var first = new OutageClient(10, () => LlmException.Http(404, "model m1 does not exist"));
        var second = new ScriptedClient(new Turn("served by m2"));
        var result = await MakeEngine(first, reroute: _ => Task.FromResult<(ILlmClient, string)?>((second, "m2")))
            .RunAsync([], "hi");
        Assert.Equal("served by m2", result.FinalText);
        Assert.Equal(1, first.Calls);
        Assert.Equal("m2", second.Requests[0].Model);
    }

    [Fact]
    public async Task ToolWorkBeforeAFailureIsSalvaged()
    {
        // Round 1: a tool call runs. Round 2: the server returns a permanent error.
        var call = new ToolCall("c1", "echo", """{"text":"hi"}""");
        var client = new ScriptedThenFailing(LlmException.Http(400, "bad request"), Turn.Calling(call));
        var progress = new RunProgress();
        await Assert.ThrowsAsync<LlmException>(() => MakeEngine(client).RunAsync(
            [LlmMessage.User("earlier"), LlmMessage.Assistant("ok", [])], "go", progress: progress));
        var kept = Assert.IsAssignableFrom<IReadOnlyList<LlmMessage>>(progress.Salvaged);
        Assert.Equal([MessageRole.User, MessageRole.Assistant, MessageRole.User, MessageRole.Assistant, MessageRole.Tool],
            kept.Select(m => m.Role));
        Assert.Equal("echoed: hi", kept[^1].Content);
    }

    [Fact]
    public async Task NothingIsSalvagedWhenTheRunNeverGotGoing()
    {
        var client = new OutageClient(1, () => LlmException.Http(400, "bad"));
        var progress = new RunProgress();
        await Assert.ThrowsAsync<LlmException>(() => MakeEngine(client).RunAsync(
            [LlmMessage.User("a"), LlmMessage.Assistant("b", [])], "c", progress: progress));
        Assert.Null(progress.Salvaged);
    }

    [Fact]
    public void DanglingToolCallsAreClosed()
    {
        var calls = new[] { new ToolCall("a", "echo", "{}"), new ToolCall("b", "echo", "{}") };
        IReadOnlyList<LlmMessage> messages =
            [LlmMessage.User("x"), LlmMessage.Assistant("", calls), LlmMessage.ToolOutput("a", "echo", "ok")];
        var closed = Engine.ClosingDanglingToolCalls(messages);
        Assert.Equal(4, closed.Count);
        Assert.Equal("b", closed[^1].ToolCallId);
        Assert.Contains("interrupted", closed[^1].Content);
        // Already complete: untouched.
        Assert.Equal(closed, Engine.ClosingDanglingToolCalls(closed));
        Assert.Equal(2, Engine.ClosingDanglingToolCalls([LlmMessage.User("x"), LlmMessage.Assistant("hi", [])]).Count);
    }

    [Fact]
    public async Task LastReplyTextSurvivesAnEmptyFinalMessage()
    {
        // The model declares completion alongside a final tool call, then ends with an empty
        // message: the goal loop must still see the verdict.
        var call = new ToolCall("c1", "echo", """{"text":"x"}""");
        var client = new ScriptedClient(new Turn("All done.\nGOAL_COMPLETE", [call]), new Turn(""));
        var result = await MakeEngine(client).RunAsync([], "go");
        Assert.Equal("", result.FinalText);
        Assert.Equal(new GoalStatus.Complete(), GoalProtocol.Status(result.LastReplyText));
    }

    [Fact]
    public async Task WorkAfterAnInRunCompactionIsStillSalvaged()
    {
        // A long transcript compacts in-run to something shorter than where it started; the tool
        // round after it must survive a later failure.
        var call = new ToolCall("c1", "echo", """{"text":"after"}""");
        var client = new ScriptedThenFailing(LlmException.Http(400, "bad"), Turn.Calling(call));
        var big = Enumerable.Range(0, 40).SelectMany(i => new[]
        {
            LlmMessage.User(string.Concat(Enumerable.Repeat($"u{i} ", 200))), LlmMessage.Assistant($"a{i}", []),
        }).ToList();
        var engine = MakeEngine(client, window: 2_000,
            compaction: (_, messages, _) => Task.FromResult<IReadOnlyList<LlmMessage>>(
                [LlmMessage.SystemText("summary"), messages[^1]]));
        var progress = new RunProgress();
        await Assert.ThrowsAsync<LlmException>(() => engine.RunAsync(big, "go", progress: progress));
        var kept = Assert.IsAssignableFrom<IReadOnlyList<LlmMessage>>(progress.Salvaged);
        Assert.Equal("echoed: after", kept[^1].Content);
        Assert.Equal("summary", kept[0].Content);
    }

    [Fact]
    public async Task ServerErrorAllowanceIsSeparateFromOutageRetries()
    {
        // 12 outage failures, then a 500, then success: the 500 is still retried.
        var client = new CallbackClient(n => n switch
        {
            <= 12 => (null, LlmException.Connection("refused")),
            13 => (null, LlmException.Http(500, "oops")),
            _ => ("ok", null),
        });
        var result = await MakeEngine(client).RunAsync([], "hi");
        Assert.Equal("ok", result.FinalText);
    }

    [Fact]
    public async Task StopWhileAPermissionQuestionIsOpenIsNotARefusal()
    {
        var call = new ToolCall("c1", "run_shell_command", """{"command":"rm -rf /tmp/x"}""");
        var client = new ScriptedClient(Turn.Calling(call));
        using var cts = new CancellationTokenSource();
        var engine = new Engine(client, ToolRegistry.Standard(), "s", new EngineConfig("m"), _root.Path,
            new PermissionPolicy(PermissionPreset.Plan, _root.Path),
            async (_, _, _) =>
            {
                // The app answers a pending question "no" when Stop is pressed.
                try { await Task.Delay(5_000, cts.Token); } catch (OperationCanceledException) { }
                return false;
            });
        var progress = new RunProgress();
        var run = engine.RunAsync([], "go", cancellationToken: cts.Token, progress: progress);
        await Task.Delay(200);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        var kept = progress.Salvaged ?? [];
        Assert.DoesNotContain(kept, m => (m.Content ?? "").Contains("Permission denied"));
        Assert.Contains("interrupted", kept[^1].Content);
    }
}
