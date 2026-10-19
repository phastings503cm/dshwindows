using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace Vision.Core;

// MARK: - Running one turn on the claude CLI

/// <summary>The running CLI as the runner sees it; tests replace it with a scripted one.</summary>
public interface IClaudeCodeProcess : IDisposable
{
    /// <summary>The next stdout line (null once stdout has closed).</summary>
    Task<string?> ReadLineAsync(CancellationToken cancellationToken);
    Task WriteLineAsync(string line, CancellationToken cancellationToken);
    /// <summary>Close stdin: Claude Code finishes what it is doing (and any background work) and exits.</summary>
    void CloseInput();
    bool InputClosed { get; }
    /// <summary>What it printed on stderr so far (capped).</summary>
    string Stderr { get; }
    bool HasExited { get; }
    int? ExitCode { get; }
    Task WaitForExitAsync(CancellationToken cancellationToken);
    /// <summary>Kill it and everything it started.</summary>
    void Kill();
}

/// <summary>Starts the CLI for one turn.</summary>
public delegate IClaudeCodeProcess ClaudeCodeProcessStarter(ClaudeCodeLaunch launch, IReadOnlyList<string> args, string workingDirectory);

/// <summary>Why a turn didn't finish the ordinary way.</summary>
public enum ClaudeCodeFailureKind
{
    /// <summary>The CLI isn't signed in (no claude.ai login, no key).</summary>
    NotSignedIn,
    /// <summary>--resume named a session the CLI doesn't have (deleted, or another computer's).</summary>
    ResumeMissing,
    /// <summary>--session-id named one that already exists.</summary>
    SessionInUse,
    /// <summary>This CLI doesn't know an optional flag (<see cref="ClaudeCodeFailure.Detail"/> names it).</summary>
    UnknownOption,
    /// <summary>The Claude plan's limit is used up for now.</summary>
    RateLimited,
    /// <summary>No claude CLI that runs was found (or the one chosen in Settings doesn't start).</summary>
    NotInstalled,
    /// <summary>The folder has Claude Code settings of its own that this CLI can't be told to leave out (<see cref="ClaudeCodeTrust"/>).</summary>
    UntrustedFolder,
    /// <summary>Anything else; the message says what the CLI said.</summary>
    Failed,
}

public sealed record ClaudeCodeFailure(ClaudeCodeFailureKind Kind, string Message, string? Detail = null)
{
    /// <summary>Another try can't fix it without the person (install, sign in, wait for the plan, trust or update): a /goal or
    /// a task stops at once instead of retrying.</summary>
    public bool Permanent => Kind is ClaudeCodeFailureKind.NotSignedIn or ClaudeCodeFailureKind.RateLimited
        or ClaudeCodeFailureKind.NotInstalled or ClaudeCodeFailureKind.UntrustedFolder;
}

/// <summary>A turn the CLI couldn't do, in plain words (what the chat shows).</summary>
public sealed class ClaudeCodeException(ClaudeCodeFailure failure) : Exception(failure.Message)
{
    public ClaudeCodeFailure Failure { get; } = failure;
}

/// <summary>How a turn went: what the stream said (session, replies, usage) and, when it didn't finish, why.</summary>
public sealed record ClaudeCodeTurnResult(ClaudeCodeStream Stream, ClaudeCodeFailure? Failure)
{
    public ClaudeCodeResult? Result => Stream.Result;
    /// <summary>Tokens of every model turn the CLI ran for this prompt (background work can add turns after the first result).</summary>
    public LlmUsage? Usage { get; init; }
}

public static class ClaudeCodeRunner
{
    /// <summary>How long a Stop waits for Claude Code to end the turn itself before the process is killed.</summary>
    public static TimeSpan InterruptGrace { get; set; } = TimeSpan.FromSeconds(8);

    /// <summary>How long stdout may stay open once the process is gone (a program it started in the background inherited the
    /// pipe and still holds it) before the turn stops waiting for its end.</summary>
    public static TimeSpan DrainGrace { get; set; } = TimeSpan.FromSeconds(3);

    public static ClaudeCodeProcessStarter DefaultStarter { get; } = (launch, args, cwd) => ClaudeCodeProcess.Start(launch.StartInfo(args, cwd));

    /// <summary>The turns running now: their process, and how to stop them when Vision closes.</summary>
    private static readonly ConcurrentDictionary<IClaudeCodeProcess, CancellationTokenSource> Live = new();

    /// <summary>How many claude processes Vision is running now.</summary>
    public static int RunningCount => Live.Count;

    /// <summary>Vision is closing: every running turn is interrupted, gets <paramref name="grace"/> to end, and whatever is
    /// still running then is killed with everything it started — no claude process outlives Vision.</summary>
    public static void StopAll(TimeSpan? grace = null)
    {
        var runs = Live.ToArray();
        if (runs.Length == 0) return;
        foreach (var (_, shutdown) in runs)
        {
            try
            {
                shutdown.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // That turn just ended.
            }
        }
        var deadline = DateTime.UtcNow + (grace ?? TimeSpan.FromSeconds(1.5));
        while (DateTime.UtcNow < deadline && runs.Any(r => !r.Key.HasExited)) Thread.Sleep(50);
        foreach (var (process, _) in runs)
            if (!process.HasExited) process.Kill();
    }

    /// <summary>Run one turn: start `claude -p`, send the prompt, turn every line into events, answer its permission
    /// questions with <paramref name="decide"/> (which may ask the person), and wait for it to finish — including any
    /// background work it waits for after its reply. A Stop (<paramref name="cancellationToken"/>) interrupts the turn
    /// the way Claude Code's own Esc does, then kills the process if it doesn't end; it surfaces as
    /// <see cref="OperationCanceledException"/>. A question whose answer can no longer reach Claude Code — stdin closed once
    /// the reply is in, the process gone, the CLI withdrew it — is cancelled (<paramref name="decide"/>'s token), so a card
    /// nobody can answer never holds the turn.</summary>
    public static async Task<ClaudeCodeTurnResult> RunAsync(ClaudeCodeLaunch launch, ClaudeCodeTurnOptions options, string workingDirectory,
        string userMessage, ClaudeCodeStream stream, Func<ClaudeCodePermissionRequest, CancellationToken, Task<ClaudeCodePermissionDecision>> decide,
        CancellationToken cancellationToken, ClaudeCodeProcessStarter? starter = null, Action<string?>? activity = null)
    {
        starter ??= DefaultStarter;
        // Stopped before it started: nothing runs.
        cancellationToken.ThrowIfCancellationRequested();
        IClaudeCodeProcess started;
        try
        {
            started = starter(launch, ClaudeCodeCommand.Args(options), workingDirectory);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            // Moved, uninstalled, or not a program: another try won't help until the person looks.
            throw new ClaudeCodeException(new ClaudeCodeFailure(ClaudeCodeFailureKind.NotInstalled,
                $"Claude Code at {launch.Display} didn't start ({ex.Message}). Settings › Claude Code shows how to install it and sign in."));
        }
        using var process = started;
        using var shutdown = new CancellationTokenSource(); // Vision is closing (StopAll)
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, shutdown.Token);
        Live[process] = shutdown;
        var writeLock = new SemaphoreSlim(1, 1);
        async Task Write(string line)
        {
            await writeLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (!process.InputClosed && !process.HasExited) await process.WriteLineAsync(line, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
                // It went away; the read loop sees stdout close.
            }
            finally
            {
                writeLock.Release();
            }
        }

        using var stopping = new CancellationTokenSource(); // the turn is over: no kill timer
        using var answerable = new CancellationTokenSource(); // answers can still reach Claude Code
        var questions = new Dictionary<string, CancellationTokenSource>(StringComparer.Ordinal); // request id → its own cancel
        var pending = new List<Task>();
        var interrupted = false;
        var results = 0;
        LlmUsage? usage = null;

        void Ask(ClaudeCodePermissionRequest question)
        {
            // Standard input is closed (the reply is in): no answer could reach it, so nobody is asked.
            if (process.InputClosed || answerable.IsCancellationRequested) return;
            var own = CancellationTokenSource.CreateLinkedTokenSource(answerable.Token);
            lock (questions) questions[question.RequestId] = own;
            pending.Add(Task.Run(async () =>
            {
                ClaudeCodePermissionDecision decision;
                try
                {
                    decision = await decide(question, own.Token).WaitAsync(own.Token).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Cancelled (the answer can't be delivered any more) or the question couldn't be put: no.
                    decision = new ClaudeCodePermissionDecision.Deny(ClaudeCodePermissions.Declined);
                }
                finally
                {
                    lock (questions) questions.Remove(question.RequestId);
                }
                if (!own.IsCancellationRequested)
                {
                    await Write(decision switch
                    {
                        ClaudeCodePermissionDecision.Allow => ClaudeCodeCommand.Allow(question.RequestId, question.Input, question.ToolUseId),
                        ClaudeCodePermissionDecision.Deny deny => ClaudeCodeCommand.Deny(question.RequestId, deny.Message, question.ToolUseId),
                        _ => ClaudeCodeCommand.Deny(question.RequestId, ClaudeCodePermissions.Declined, question.ToolUseId),
                    }).ConfigureAwait(false);
                }
                own.Dispose();
            }, CancellationToken.None));
        }

        void Unanswerable()
        {
            try
            {
                answerable.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        await using var onStop = stop.Token.Register(() =>
        {
            interrupted = true;
            _ = Task.Run(async () =>
            {
                // The reply is already in and stdin closed (it is finishing background work): nothing to interrupt — end it.
                if (process.InputClosed)
                {
                    if (!stopping.IsCancellationRequested) process.Kill();
                    return;
                }
                await Write(ClaudeCodeCommand.Interrupt("vision-stop-" + Guid.NewGuid().ToString("N")[..8])).ConfigureAwait(false);
                try
                {
                    await Task.Delay(InterruptGrace, stopping.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return; // it ended by itself
                }
                process.Kill();
            });
        });

        await Write(userMessage).ConfigureAwait(false);
        try
        {
            var gone = GoneAsync();
            Task<string?>? read = null;
            while (true)
            {
                read ??= ReadAsync();
                if (await Task.WhenAny(read, gone).ConfigureAwait(false) != read) break; // gone, stdout held open by a child
                var line = await read.ConfigureAwait(false);
                read = null;
                if (line is null) break;
                var before = stream.Result;
                var question = stream.Feed(line, out var unhandled);
                if (question is not null)
                {
                    Ask(question);
                }
                else if (unhandled is { } request)
                {
                    await Write(ClaudeCodeCommand.Unhandled(request.RequestId, request.Subtype)).ConfigureAwait(false);
                }
                if (stream.CancelledRequest is { } withdrawn)
                {
                    // Claude Code no longer waits for that answer (it decided another way, or the turn was interrupted).
                    lock (questions)
                        if (questions.TryGetValue(withdrawn, out var own)) own.Cancel();
                }
                if (stream.Result is { } result && !ReferenceEquals(result, before))
                {
                    results++;
                    usage = LlmUsage.Sum(usage, result.Usage);
                    // The reply is in: let it finish (and wait for any background work it started) and exit.
                    if (!process.InputClosed)
                    {
                        await writeLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                        try { process.CloseInput(); } finally { writeLock.Release(); }
                        Unanswerable();
                    }
                    if (results == 1 && !interrupted) _ = NoteBackgroundWorkAsync();
                }
            }
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            stopping.Cancel();
            Unanswerable();
            try
            {
                await Task.WhenAll(pending).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Answers to a process that is gone.
            }
            if (!process.HasExited) process.Kill();
            Live.TryRemove(process, out _);
        }

        stream.CloseDangling(interrupted ? "Stopped before it finished" : "Not finished — Claude Code ended the turn first");
        if (interrupted) throw new OperationCanceledException(cancellationToken.IsCancellationRequested ? cancellationToken : stop.Token);
        return new ClaudeCodeTurnResult(stream, Classify(stream, process.Stderr, process.ExitCode)) { Usage = usage };

        async Task<string?> ReadAsync()
        {
            try
            {
                return await process.ReadLineAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
                return null;
            }
        }

        // The process is gone and DrainGrace has passed: whatever still holds stdout open isn't Claude Code.
        async Task GoneAsync()
        {
            try
            {
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                await Task.Delay(Timeout.Infinite).ConfigureAwait(false); // can't tell: only stdout's end counts
            }
            await Task.Delay(DrainGrace).ConfigureAwait(false);
        }

        async Task NoteBackgroundWorkAsync()
        {
            await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            if (!process.HasExited) activity?.Invoke("Claude Code is finishing work it started in the background…");
        }
    }

    /// <summary>Why a turn failed, in plain words — or null when it finished.</summary>
    public static ClaudeCodeFailure? Classify(ClaudeCodeStream stream, string stderr, int? exitCode)
    {
        var result = stream.Result;
        if (result is { IsError: false }) return null;
        var said = string.Join("\n", new[] { stream.ApiErrorText, result?.Text }.Concat(result?.Errors ?? []).Where(s => !string.IsNullOrWhiteSpace(s)));
        var everything = said + "\n" + stderr + "\n" + stream.Unparsed;
        if (stream.ApiError == "authentication_failed" || everything.Contains("Not logged in", StringComparison.OrdinalIgnoreCase)
            || everything.Contains("Please run /login", StringComparison.OrdinalIgnoreCase))
            return new ClaudeCodeFailure(ClaudeCodeFailureKind.NotSignedIn,
                "Claude Code isn't signed in on this computer. Open a terminal, run  claude auth login  (choose your Claude account), then send again.");
        if (everything.Contains("No conversation found with session ID", StringComparison.OrdinalIgnoreCase))
            return new ClaudeCodeFailure(ClaudeCodeFailureKind.ResumeMissing, "Claude Code no longer has this chat's session.");
        if (everything.Contains("is already in use", StringComparison.OrdinalIgnoreCase))
            return new ClaudeCodeFailure(ClaudeCodeFailureKind.SessionInUse, "Claude Code already has a session with this chat's id.");
        if (result is null && ClaudeCodeCommand.UnknownOption(stderr) is { } flag)
            return new ClaudeCodeFailure(ClaudeCodeFailureKind.UnknownOption, $"This version of Claude Code doesn't know {flag}.", flag);
        if (stream.RateLimit is { } limit || stream.ApiError == "rate_limit" && said.Contains("limit", StringComparison.OrdinalIgnoreCase))
        {
            var words = stream.RateLimit is { } l ? $"your Claude plan's {l.TypeWords} limit{l.ResetWords}" : "your Claude plan's usage limit";
            return new ClaudeCodeFailure(ClaudeCodeFailureKind.RateLimited,
                $"You've reached {words}. Pick another model for this chat from the model menu, or send again once it resets.");
        }
        if (result is null)
        {
            var why = FirstLines(stderr.Trim().Length > 0 ? stderr : stream.Unparsed.ToString());
            return new ClaudeCodeFailure(ClaudeCodeFailureKind.Failed,
                why.Length > 0 ? $"Claude Code stopped without answering: {why}" : $"Claude Code stopped without answering (exit code {exitCode?.ToString() ?? "unknown"}).");
        }
        var message = result.Subtype switch
        {
            "error_max_turns" => "Claude Code stopped: it reached its limit of steps for one turn. Send “keep going” to carry on.",
            "error_max_budget_usd" => "Claude Code stopped: it reached the spending cap for this turn.",
            _ => said.Length > 0 ? $"Claude Code: {FirstLines(said)}" : "Claude Code ended the turn with an error.",
        };
        return new ClaudeCodeFailure(ClaudeCodeFailureKind.Failed, message);
    }

    private static string FirstLines(string text)
    {
        var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Take(3);
        return TextUtil.Prefix(string.Join(" ", lines), 400);
    }
}

/// <summary>The real CLI process: stdout read line by line, stderr collected, stdin written as UTF-8.</summary>
public sealed class ClaudeCodeProcess : IClaudeCodeProcess
{
    private readonly Process _process;
    private readonly StringBuilder _stderr = new();
    private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _inputClosed;

    private ClaudeCodeProcess(Process process)
    {
        _process = process;
    }

    public static ClaudeCodeProcess Start(ProcessStartInfo info)
    {
        info.StandardInputEncoding = TextUtil.Utf8NoBom;
        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        var wrapper = new ClaudeCodeProcess(process);
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (wrapper._stderr)
                if (wrapper._stderr.Length < 64_000) wrapper._stderr.AppendLine(e.Data);
        };
        process.Exited += (_, _) => wrapper._exited.TrySetResult();
        process.Start();
        process.BeginErrorReadLine();
        if (wrapper.HasExited) wrapper._exited.TrySetResult();
        return wrapper;
    }

    public Task<string?> ReadLineAsync(CancellationToken cancellationToken) => _process.StandardOutput.ReadLineAsync(cancellationToken).AsTask();

    public async Task WriteLineAsync(string line, CancellationToken cancellationToken)
    {
        await _process.StandardInput.WriteAsync((line + "\n").AsMemory(), cancellationToken).ConfigureAwait(false);
        await _process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public void CloseInput()
    {
        if (_inputClosed) return;
        _inputClosed = true;
        try
        {
            _process.StandardInput.Close();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            // Already gone.
        }
    }

    public bool InputClosed => _inputClosed;

    public string Stderr
    {
        get
        {
            lock (_stderr) return _stderr.ToString();
        }
    }

    public bool HasExited
    {
        get
        {
            try
            {
                return _process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }
    }

    public int? ExitCode => HasExited ? SafeExitCode() : null;

    private int? SafeExitCode()
    {
        try
        {
            return _process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>The process itself has ended — not waiting for its stderr to close, which a program it started in the
    /// background may hold open.</summary>
    public Task WaitForExitAsync(CancellationToken cancellationToken) => _exited.Task.WaitAsync(cancellationToken);

    public void Kill()
    {
        _inputClosed = true;
        ClaudeCodeCli.KillTree(_process);
    }

    public void Dispose() => _process.Dispose();
}
