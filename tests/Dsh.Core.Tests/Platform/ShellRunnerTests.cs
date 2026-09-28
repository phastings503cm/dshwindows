using System.Diagnostics;

namespace Dsh.Core.Tests;

/// <summary>Commands written for the shell the runner uses on this OS: bash on Linux/macOS, PowerShell
/// on Windows (AgentShell.Default).</summary>
internal static class Sh
{
    public static string Pick(string bash, string powershell) => OperatingSystem.IsWindows() ? powershell : bash;
}

/// <summary>Ported from the runCommand tests in PTYTests.swift (the Mac ran commands on a pty; the
/// Windows port pipes them through <see cref="ShellRunner"/>), plus new ones for what the runner
/// promises: stdin is closed, output is cleaned, cancellation stops the command.</summary>
public sealed class ShellRunnerTests : IDisposable
{
    private readonly TempDirectory _cwd = new("dsh-shell");

    public void Dispose() => _cwd.Dispose();

    private Task<ShellResult> Run(string command, double timeoutSeconds = 10, int maxCapture = 200_000,
                                  Action<string>? onOutput = null, CancellationToken cancellationToken = default) =>
        ShellRunner.RunAsync(command, _cwd.Path, AgentShell.Default, TimeSpan.FromSeconds(timeoutSeconds), maxCapture,
            onOutput, cancellationToken);

    [Fact]
    public async Task RunCommandCapturesOutputAndExitStatus()
    {
        var result = await Run("echo hi-shell-run; exit 0");
        Assert.False(result.TimedOut);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("hi-shell-run", result.Output);
    }

    [Fact]
    public async Task RunCommandReportsNonZeroExit()
    {
        var result = await Run("exit 7");
        Assert.False(result.TimedOut);
        Assert.Equal(7, result.ExitCode);
    }

    /// <summary>A quiet-but-legitimately-slow command (no output) must NOT be killed on the idle limit —
    /// only on the full timeout. This is the "don't kill my slow build" guarantee.</summary>
    [Fact]
    public async Task RunCommandAllowsQuietSlowCommandUntilFullTimeout()
    {
        var result = await Run(Sh.Pick("sleep 2.5; echo finished-quietly",
            "Start-Sleep -Milliseconds 2500; Write-Output 'finished-quietly'"), timeoutSeconds: 6);
        Assert.False(result.TimedOut, "a quiet command that finishes in time must not be stopped");
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("finished-quietly", result.Output);
    }

    [Fact]
    public async Task StdinIsClosedSoReadsGetEndOfFile()
    {
        var result = await Run(Sh.Pick("read x; echo \"got:$x\"", "$x = [Console]::In.ReadLine(); \"got:$x\""));
        Assert.False(result.TimedOut, "a read must see end-of-file, not wait for input");
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("got:", result.Output.Trim());
    }

    [Fact]
    public async Task RunsInTheWorkingDirectory()
    {
        _cwd.Write("sentinel.txt", "hello");
        var result = await Run(Sh.Pick("ls", "Get-ChildItem -Name"));
        Assert.Contains("sentinel.txt", result.Output);
    }

    [Fact]
    public async Task StdoutAndStderrAreBothCaptured()
    {
        var result = await Run(Sh.Pick("echo to-out; echo to-err 1>&2", "Write-Output 'to-out'; [Console]::Error.WriteLine('to-err')"));
        Assert.Contains("to-out", result.Output);
        Assert.Contains("to-err", result.Output);
    }

    [Fact]
    public async Task AnsiEscapesAreStrippedFromTheCapture()
    {
        var result = await Run(Sh.Pick(@"printf 'a\033[31mred\033[0m\n'", "Write-Output \"a$([char]27)[31mred$([char]27)[0m\""));
        Assert.Equal("ared", result.Output.Trim());
    }

    [Fact]
    public async Task OutputIsStreamedAndTheCaptureKeepsTheTail()
    {
        var streamed = new LockedList();
        var result = await Run(Sh.Pick("head -c 5000 /dev/zero | tr '\\0' 'x'; echo; echo END", "'x' * 5000; 'END'"),
            maxCapture: 100, onOutput: streamed.Add);

        Assert.Contains("END", string.Concat(streamed.Items));
        Assert.True(result.Output.Length <= 100);
        Assert.EndsWith("END", result.Output.TrimEnd());
    }

    [Fact]
    public async Task AMissingWorkingDirectoryIsReportedNotThrown()
    {
        var result = await ShellRunner.RunAsync("echo hi", _cwd["does-not-exist"], AgentShell.Default, TimeSpan.FromSeconds(5));
        Assert.Equal(-1, result.ExitCode);
        Assert.False(result.TimedOut);
        Assert.StartsWith("Could not start", result.Output);
    }

    [Fact]
    public async Task CancellationStopsTheCommand()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var clock = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Run(Sh.Pick("sleep 30", "Start-Sleep 30"), timeoutSeconds: 60, cancellationToken: cts.Token));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), $"cancellation took {clock.Elapsed}");
    }

    [Fact]
    public void IdleLimitIsNinetyPercentCappedAt45Seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(5.4), ShellRunner.IdleLimit(TimeSpan.FromSeconds(6)));
        Assert.Equal(TimeSpan.FromSeconds(9), ShellRunner.IdleLimit(TimeSpan.FromSeconds(10)));
        Assert.Equal(TimeSpan.FromSeconds(45), ShellRunner.IdleLimit(TimeSpan.FromSeconds(120)));
    }
}

/// <summary>Ported: the regression the idle limit targets — a command that prints a prompt and waits
/// for input used to block for the whole timeout. It runs on its own (Timing-sensitive collection) so
/// the rest of the suite can't skew the clock.</summary>
[Collection(TimingSensitive.Name)]
public sealed class ShellRunnerPromptTests : IDisposable
{
    private readonly TempDirectory _cwd = new("dsh-shell-prompt");

    public void Dispose() => _cwd.Dispose();

    [Fact]
    public async Task RunCommandStopsOnInteractivePrompt()
    {
        var timeout = TimeSpan.FromSeconds(6); // idle limit 0.9 × 6 = 5.4 s
        var clock = Stopwatch.StartNew();
        var result = await ShellRunner.RunAsync(
            Sh.Pick("printf 'Password: '; sleep 30", "Write-Host -NoNewline 'Password: '; Start-Sleep 30"),
            _cwd.Path, AgentShell.Default, timeout);
        clock.Stop();

        Assert.True(result.TimedOut, "a prompt-waiting command must be flagged as stopped");
        Assert.NotEqual(0, result.ExitCode); // killed, not exited cleanly
        Assert.Contains("Password:", result.Output);
        if (OperatingSystem.IsWindows())
        {
            // PowerShell's start-up delays the prompt, so the idle and total limits nearly coincide;
            // what matters is that the 30 s sleep was cut short.
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(15), $"took {clock.Elapsed}");
        }
        else
        {
            // bash prints at once, so the idle limit (5.4 s) fires before the 6 s timeout.
            Assert.True(clock.Elapsed < timeout, $"should stop on the idle limit, not the full timeout ({clock.Elapsed})");
        }
    }
}

/// <summary>New: run_shell_command's formatting of the runner's result.</summary>
public sealed class RunShellCommandToolTests : IDisposable
{
    private readonly TempDirectory _cwd = new("dsh-shell-tool");

    public void Dispose() => _cwd.Dispose();

    private async Task<string> Execute(string arguments)
    {
        var context = new ToolContext
        {
            Workspace = _cwd.Path,
            Policy = new PermissionPolicy(PermissionPreset.FullAccess, _cwd.Path),
            Client = new ScriptedClient(),
            Registry = new ToolRegistry([]),
        };
        return (await new RunShellCommandTool().ExecuteAsync(arguments, context, CancellationToken.None)).Output;
    }

    [Fact]
    public async Task OutputIsReturnedTrimmed()
    {
        Assert.Equal("hi", await Execute("""{"command":"echo hi"}"""));
    }

    [Fact]
    public async Task SilentSuccessSaysSo()
    {
        Assert.Equal("(no output)", await Execute("""{"command":"exit 0"}"""));
    }

    [Fact]
    public async Task NonZeroExitIsReported()
    {
        Assert.StartsWith("Command exited with code 3.", await Execute("""{"command":"exit 3","timeout":"20"}"""));
    }

    [Fact]
    public async Task CommandIsRequired()
    {
        Assert.Equal("Error: command is required.", await Execute("""{"command":"  "}"""));
    }

    [Fact]
    public void SpecNamesTheShellAndItsSyntax()
    {
        var cmd = RunShellCommandTool.SpecFor(new AgentShell(ShellKind.Cmd, @"C:\Windows\System32\cmd.exe", "Command Prompt (cmd.exe)"));
        Assert.Contains("Command Prompt (cmd.exe)", cmd.Description);
        Assert.Contains("cmd syntax", cmd.Description);
        Assert.Contains("stdin is closed", cmd.Description);
        Assert.Equal(RunShellCommandTool.ToolName, cmd.Name);
    }
}
