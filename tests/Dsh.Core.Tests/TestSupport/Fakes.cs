using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Dsh.Core.Tests;

// Test doubles shared by the engine, agent, compaction and tool-image tests (ports of the Swift
// ScriptedClient / EchoTool / FailingTool helpers, plus a few the C# tests need).

/// <summary>One scripted model turn: text streamed in pieces, then the tool calls and usage on Done.</summary>
public sealed record Turn(string Text = "", IReadOnlyList<ToolCall>? Calls = null, LlmUsage? Usage = null)
{
    /// <summary>Handed back on Done, as a provider that needs its signed reasoning again would.</summary>
    public string? ProviderState { get; init; }

    /// <summary>A turn with no text that only asks for tools.</summary>
    public static Turn Calling(params ToolCall[] calls) => new("", calls);
}

/// <summary>A scripted client: each <see cref="StreamAsync"/> call serves the next queued turn ("done"
/// once the script runs out) as one Text event per character — a text element, like Swift's Character,
/// so a surrogate pair is never split — then Done(calls, "stop", usage), the way a real stream arrives
/// in pieces. A test can drive the engine through full tool round-trips without a network. Requests are
/// recorded thread-safely.</summary>
public sealed class ScriptedClient : ILlmClient
{
    private readonly Lock _lock = new();
    private readonly Queue<Turn> _turns;
    private readonly List<LlmRequest> _requests = [];

    public ScriptedClient(params IEnumerable<Turn> turns) => _turns = new Queue<Turn>(turns);

    /// <summary>Answers matching requests out of band (e.g. compaction summaries) without consuming
    /// the script. Return null to fall through to the queue.</summary>
    public Func<LlmRequest, Turn?>? Intercept { get; init; }

    /// <summary>Every request seen so far, in order.</summary>
    public IReadOnlyList<LlmRequest> Requests
    {
        get
        {
            lock (_lock) return [.. _requests];
        }
    }

    private Turn Next(LlmRequest request)
    {
        lock (_lock)
        {
            _requests.Add(request);
            if (Intercept?.Invoke(request) is { } intercepted) return intercepted;
            return _turns.Count > 0 ? _turns.Dequeue() : new Turn("done");
        }
    }

    public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(LlmRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var turn = Next(request);
        await Task.Yield();
        var elements = StringInfo.GetTextElementEnumerator(turn.Text);
        while (elements.MoveNext())
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new LlmStreamEvent.Text(elements.GetTextElement());
        }
        yield return new LlmStreamEvent.Done(turn.Calls ?? [], "stop", turn.Usage) { ProviderState = turn.ProviderState };
    }

    public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>(["scripted"]);
}

/// <summary>A client whose first <paramref name="failures"/> calls throw <paramref name="error"/>;
/// later calls are served by <paramref name="then"/> (or throw too when there is none).</summary>
public sealed class FlakyClient(Func<Exception> error, int failures = int.MaxValue, ScriptedClient? then = null) : ILlmClient
{
    private readonly Lock _lock = new();
    private readonly List<LlmRequest> _requests = [];

    public IReadOnlyList<LlmRequest> Requests
    {
        get
        {
            lock (_lock) return [.. _requests];
        }
    }

    private int Record(LlmRequest request)
    {
        lock (_lock)
        {
            _requests.Add(request);
            return _requests.Count;
        }
    }

    public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(LlmRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var call = Record(request);
        await Task.Yield();
        if (call <= failures || then is null) throw error();
        await foreach (var ev in then.StreamAsync(request, cancellationToken))
            yield return ev;
    }

    public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>(["flaky"]);
}

/// <summary>Records what it was handed and returns a fixed answer plus a file change.</summary>
public sealed class EchoTool : IToolExecutor
{
    public const string ToolName = "echo";
    public string Name => ToolName;
    public ToolSpec Spec { get; } = new(ToolName, "echo",
        """{"type":"object","properties":{"text":{"type":"string"}}}""");

    public Task<ToolResult> ExecuteAsync(string arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var text = JsonArgs.String(arguments, "text") ?? "";
        return Task.FromResult(new ToolResult($"echoed: {text}")
        {
            Files = [new FileChange(Path.Combine(context.Workspace, "out.txt"), FileChangeKind.Created)],
        });
    }
}

/// <summary>Reports failure the way tools are supposed to: output starting with "Error:".</summary>
public sealed class FailingTool : IToolExecutor
{
    public const string ToolName = "boom";
    public string Name => ToolName;
    public ToolSpec Spec { get; } = new(ToolName, "always fails", "{}");

    public Task<ToolResult> ExecuteAsync(string arguments, ToolContext context, CancellationToken cancellationToken) =>
        Task.FromResult<ToolResult>("Error: it broke");
}

/// <summary>Breaks the tool contract by throwing instead of returning "Error: …".</summary>
public sealed class ThrowingTool(string message = "kaput") : IToolExecutor
{
    public const string ToolName = "throws";
    public string Name => ToolName;
    public ToolSpec Spec { get; } = new(ToolName, "throws", "{}");

    public async Task<ToolResult> ExecuteAsync(string arguments, ToolContext context, CancellationToken cancellationToken)
    {
        await Task.Yield();
        throw new InvalidOperationException(message);
    }
}

/// <summary>Waits until its token is cancelled. <see cref="Started"/> completes when it begins and
/// <see cref="Cancelled"/> when it observes the cancellation.</summary>
public sealed class BlockingTool(string name = "block") : IToolExecutor
{
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Started => _started.Task;
    public Task Cancelled => _cancelled.Task;
    public string Name => name;
    public ToolSpec Spec => new(name, "waits until cancelled", "{}");

    public async Task<ToolResult> ExecuteAsync(string arguments, ToolContext context, CancellationToken cancellationToken)
    {
        _started.TrySetResult();
        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            _cancelled.TrySetResult();
            throw;
        }
        return "unreachable";
    }
}

/// <summary>Records the <see cref="ToolContext"/> the engine threaded through to it.</summary>
public sealed class CapturingTool : IToolExecutor
{
    public const string ToolName = "capture";
    private ToolContext? _captured;

    public ToolContext? Captured => Volatile.Read(ref _captured);
    public string Name => ToolName;
    public ToolSpec Spec { get; } = new(ToolName, "capture", "{}");

    public Task<ToolResult> ExecuteAsync(string arguments, ToolContext context, CancellationToken cancellationToken)
    {
        Volatile.Write(ref _captured, context);
        return Task.FromResult<ToolResult>("ok");
    }
}

/// <summary>A tool with any name and a fixed answer (stands in for mouse, process_start, …).</summary>
public sealed class StubTool(string name, string output = "ok") : IToolExecutor
{
    public string Name => name;
    public ToolSpec Spec => new(name, "stub", "{}");

    public Task<ToolResult> ExecuteAsync(string arguments, ToolContext context, CancellationToken cancellationToken) =>
        Task.FromResult<ToolResult>(output);
}

/// <summary>A stub "screenshot" tool that returns a 64x48 PNG.</summary>
public sealed class FakeShotTool : IToolExecutor
{
    public const string ToolName = "screenshot";
    public string Name => ToolName;
    public ToolSpec Spec { get; } = new(ToolName, "fake", "{}");

    public Task<ToolResult> ExecuteAsync(string arguments, ToolContext context, CancellationToken cancellationToken) =>
        Task.FromResult(new ToolResult("Screenshot 64x48")
        {
            Images = [new MessageAttachment(AttachmentKind.Image, "shot.png", TestImages.Png(64, 48))],
        });
}

/// <summary>Collects engine events; the engine calls the sink from pool threads.</summary>
public sealed class EventRecorder
{
    private readonly Lock _lock = new();
    private readonly List<EngineEvent> _events = [];

    public void Record(EngineEvent ev)
    {
        lock (_lock) _events.Add(ev);
    }

    public IReadOnlyList<EngineEvent> Events
    {
        get
        {
            lock (_lock) return [.. _events];
        }
    }

    public IReadOnlyList<T> Of<T>() where T : EngineEvent => Events.OfType<T>().ToList();
}

/// <summary>A thread-safe list of strings (what a permission gate was asked, …).</summary>
public sealed class LockedList
{
    private readonly Lock _lock = new();
    private readonly List<string> _items = [];

    public void Add(string item)
    {
        lock (_lock) _items.Add(item);
    }

    public IReadOnlyList<string> Items
    {
        get
        {
            lock (_lock) return [.. _items];
        }
    }
}

public static class Args
{
    /// <summary>Tool-call arguments as the model would send them (property names kept as written, so
    /// <c>new { file_path = … }</c> becomes <c>{"file_path":…}</c>; paths with backslashes are escaped).</summary>
    public static string Json(object value) => JsonSerializer.Serialize(value);
}

public static class Gates
{
    public static readonly PermissionGate Allow = static (_, _, _) => Task.FromResult(true);
    public static readonly PermissionGate Deny = static (_, _, _) => Task.FromResult(false);
}
