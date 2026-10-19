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
    /// <summary>Anything else; the message says what the CLI said.</summary>
    Failed,
}

public sealed record ClaudeCodeFailure(ClaudeCodeFailureKind Kind, string Message, string? Detail = null);

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

    public static ClaudeCodeProcessStarter DefaultStarter { get; } = (launch, args, cwd) => ClaudeCodeProcess.Start(launch.StartInfo(args, cwd));

    /// <summary>Run one turn: start `claude -p`, send the prompt, turn every line into events, answer its permission
    /// questions with <paramref name="decide"/> (which may ask the person), and wait for it to finish — including any
    /// background work it waits for after its reply. A Stop (<paramref name="cancellationToken"/>) interrupts the turn
    /// the way Claude Code's own Esc does, then kills the process if it doesn't end; it surfaces as
    /// <see cref="OperationCanceledException"/>.</summary>
    public static async Task<ClaudeCodeTurnResult> RunAsync(ClaudeCodeLaunch launch, ClaudeCodeTurnOptions options, string workingDirectory,
        string userMessage, ClaudeCodeStream stream, Func<ClaudeCodePermissionRequest, CancellationToken, Task<ClaudeCodePermissionDecision>> decide,
        CancellationToken cancellationToken, ClaudeCodeProcessStarter? starter = null, Action<string?>? activity = null)
    {
        starter ??= DefaultStarter;
        using var process = starter(launch, ClaudeCodeCommand.Args(options), workingDirectory);
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

        using var stopping = new CancellationTokenSource(); // the person pressed Stop
        using var questions = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pending = new List<Task>();
        var interrupted = false;
        var results = 0;
        LlmUsage? usage = null;
        await using var onStop = cancellationToken.Register(() =>
        {
            interrupted = true;
            _ = Task.Run(async () =>
            {
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
            while (true)
            {
                string? line;
                try
                {
                    line = await process.ReadLineAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                    line = null;
                }
                if (line is null) break;
                var before = stream.Result;
                var question = stream.Feed(line, out var unhandled);
                if (question is not null)
                {
                    pending.Add(Task.Run(async () =>
                    {
                        ClaudeCodePermissionDecision decision;
                        try
                        {
                            decision = await decide(question, questions.Token).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (ex is OperationCanceledException)
                        {
                            decision = new ClaudeCodePermissionDecision.Deny(ClaudeCodePermissions.Declined);
                        }
                        await Write(decision switch
                        {
                            ClaudeCodePermissionDecision.Allow => ClaudeCodeCommand.Allow(question.RequestId, question.Input, question.ToolUseId),
                            ClaudeCodePermissionDecision.Deny deny => ClaudeCodeCommand.Deny(question.RequestId, deny.Message, question.ToolUseId),
                            _ => ClaudeCodeCommand.Deny(question.RequestId, ClaudeCodePermissions.Declined, question.ToolUseId),
                        }).ConfigureAwait(false);
                    }, CancellationToken.None));
                }
                else if (unhandled is { } request)
                {
                    await Write(ClaudeCodeCommand.Unhandled(request.RequestId, request.Subtype)).ConfigureAwait(false);
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
                    }
                    if (results == 1 && !interrupted) _ = NoteBackgroundWorkAsync();
                }
            }
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            stopping.Cancel();
            questions.Cancel();
            try
            {
                await Task.WhenAll(pending).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Answers to a process that is gone.
            }
            if (!process.HasExited) process.Kill();
        }

        stream.CloseDangling(interrupted ? "Stopped before it finished" : "Not finished — Claude Code ended the turn first");
        if (interrupted) throw new OperationCanceledException(cancellationToken);
        return new ClaudeCodeTurnResult(stream, Classify(stream, process.Stderr, process.ExitCode)) { Usage = usage };

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
        process.Start();
        process.BeginErrorReadLine();
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

    public async Task WaitForExitAsync(CancellationToken cancellationToken)
    {
        await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Kill()
    {
        _inputClosed = true;
        ClaudeCodeCli.KillTree(_process);
    }

    public void Dispose() => _process.Dispose();
}
