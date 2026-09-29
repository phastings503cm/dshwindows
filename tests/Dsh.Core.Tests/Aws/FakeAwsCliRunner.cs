using System.Text;

namespace Dsh.Core.Tests;

/// <summary>One step of a scripted interactive CLI run.</summary>
public abstract record FakeCliStep
{
    /// <summary>A finished line of output.</summary>
    public sealed record Line(string Text, bool IsError = false) : FakeCliStep;
    /// <summary>A question without a newline; the run waits for the answer (forever, like the real CLI,
    /// when nobody answers — only cancelling ends it).</summary>
    public sealed record Prompt(string Text) : FakeCliStep;
    /// <summary>Wait until the run is cancelled.</summary>
    public sealed record Hang : FakeCliStep;
}

/// <summary>A scripted <see cref="IAwsCliRunner"/>: each rule matches calls whose arguments start with a
/// given command, answers with fixed output (or a script with prompts), and every call is recorded.
/// Nothing is launched and nothing touches AWS.</summary>
public sealed class FakeAwsCliRunner : IAwsCliRunner
{
    public sealed record Call(IReadOnlyList<string> Args, AwsCliRunOptions? Options)
    {
        /// <summary>"--profile" followed by "p" somewhere in the arguments.</summary>
        public bool HasPair(string name, string value)
        {
            for (var i = 0; i + 1 < Args.Count; i++)
                if (Args[i] == name && Args[i + 1] == value) return true;
            return false;
        }

        public string Joined => string.Join(' ', Args);
    }

    private readonly Lock _lock = new();
    private readonly List<(string[] Prefix, Func<Call, CancellationToken, Task<AwsCliResult>> Respond)> _rules = [];
    private readonly List<Call> _calls = [];
    private readonly List<(string Prompt, string? Answer)> _answers = [];

    public IReadOnlyList<Call> Calls
    {
        get
        {
            lock (_lock) return [.. _calls];
        }
    }

    /// <summary>Every prompt offered to the answerer and what it answered.</summary>
    public IReadOnlyList<(string Prompt, string? Answer)> Answers
    {
        get
        {
            lock (_lock) return [.. _answers];
        }
    }

    public IReadOnlyList<Call> CallsTo(params string[] prefix) => Calls.Where(c => StartsWith(c.Args, prefix)).ToList();

    /// <summary>Calls starting with <paramref name="prefix"/> get this output (first matching rule wins).</summary>
    public FakeAwsCliRunner On(string[] prefix, string stdout = "", string stderr = "", int exitCode = 0) =>
        On(prefix, (_, _) => Task.FromResult(new AwsCliResult(exitCode, stdout, stderr)));

    /// <summary>A failure as the CLI prints it (legacy error format).</summary>
    public FakeAwsCliRunner Fail(string[] prefix, string stderr, int exitCode = 254) => On(prefix, "", "\n" + stderr + "\n", exitCode);

    /// <summary>Successive calls get successive results; the last one repeats.</summary>
    public FakeAwsCliRunner OnSequence(string[] prefix, params AwsCliResult[] results)
    {
        var index = 0;
        return On(prefix, (_, _) =>
        {
            var result = results[Math.Min(Interlocked.Increment(ref index) - 1, results.Length - 1)];
            return Task.FromResult(result);
        });
    }

    public FakeAwsCliRunner On(string[] prefix, Func<Call, CancellationToken, Task<AwsCliResult>> respond)
    {
        lock (_lock) _rules.Add((prefix, respond));
        return this;
    }

    /// <summary>An interactive run: lines go to OnLine, prompts to the answerer (whose answers are
    /// recorded), then it ends with <paramref name="final"/> (its stdout appended to what was printed).</summary>
    public FakeAwsCliRunner OnInteractive(string[] prefix, IReadOnlyList<FakeCliStep> steps, AwsCliResult final) =>
        On(prefix, async (call, cancellationToken) =>
        {
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            var options = call.Options ?? new AwsCliRunOptions();
            foreach (var step in steps)
            {
                switch (step)
                {
                    case FakeCliStep.Line line:
                        (line.IsError ? stderr : stdout).Append(line.Text).Append('\n');
                        options.OnLine?.Invoke(new AwsCliLine(line.Text, line.IsError));
                        break;
                    case FakeCliStep.Prompt prompt:
                        stdout.Append(prompt.Text);
                        var answer = options.AnswerPrompt is { } ask ? await ask(prompt.Text, cancellationToken) : null;
                        lock (_lock) _answers.Add((prompt.Text, answer));
                        if (answer is null) await Task.Delay(Timeout.Infinite, cancellationToken);
                        stdout.Append('\n');
                        break;
                    case FakeCliStep.Hang:
                        await Task.Delay(Timeout.Infinite, cancellationToken);
                        break;
                }
            }
            return final with { Stdout = stdout + final.Stdout, Stderr = stderr + final.Stderr };
        });

    public async Task<AwsCliResult> RunAsync(IReadOnlyList<string> args, AwsCliRunOptions? options = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var call = new Call([.. args], options);
        Func<Call, CancellationToken, Task<AwsCliResult>>? respond;
        lock (_lock)
        {
            _calls.Add(call);
            respond = _rules.FirstOrDefault(r => StartsWith(args, r.Prefix)).Respond;
        }
        if (respond is null) throw new InvalidOperationException($"No fake AWS CLI answer for: aws {call.Joined}");
        return await respond(call, cancellationToken);
    }

    private static bool StartsWith(IReadOnlyList<string> args, string[] prefix) =>
        args.Count >= prefix.Length && prefix.Select((p, i) => args[i] == p).All(x => x);
}

/// <summary>A clock that jumps forward whenever something waits on it: Task.Delay(…, clock) completes at
/// once and moves the time on by the delay, so a 15-minute polling loop runs instantly and exactly.</summary>
public sealed class JumpingClock(DateTimeOffset start) : TimeProvider
{
    private readonly Lock _lock = new();
    private DateTimeOffset _now = start;
    private readonly List<TimeSpan> _delays = [];

    public JumpingClock() : this(ManualClock.Epoch) { }

    public IReadOnlyList<TimeSpan> Delays
    {
        get
        {
            lock (_lock) return [.. _delays];
        }
    }

    public override DateTimeOffset GetUtcNow()
    {
        lock (_lock) return _now;
    }

    public void Advance(TimeSpan by)
    {
        lock (_lock) _now += by;
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        if (dueTime != Timeout.InfiniteTimeSpan)
        {
            lock (_lock)
            {
                _now += dueTime;
                _delays.Add(dueTime);
            }
            ThreadPool.QueueUserWorkItem(_ => callback(state));
        }
        return new NoTimer();
    }

    private sealed class NoTimer : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>Progress reported on the calling thread, in order (Progress&lt;T&gt; posts to the thread pool).</summary>
public sealed class ListProgress<T> : IProgress<T>
{
    private readonly Lock _lock = new();
    private readonly List<T> _items = [];
    public Action<T>? OnReport { get; init; }

    public IReadOnlyList<T> Items
    {
        get
        {
            lock (_lock) return [.. _items];
        }
    }

    public void Report(T value)
    {
        lock (_lock) _items.Add(value);
        OnReport?.Invoke(value);
    }
}
