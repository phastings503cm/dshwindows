using System.Diagnostics;

namespace Dsh.Core.Tests;

/// <summary>The BackgroundProcessTests scenarios from MachineTests.swift against real processes: through
/// a pseudo console per process on Windows (PowerShell and cmd commands), and through the pipe backend
/// with bash elsewhere. The exact cursor and wait semantics are pinned down deterministically in
/// <see cref="ProcessToolTests"/>; these prove the backends deliver output, input, Ctrl+C, exit codes
/// and stops for real. Timeouts are generous: a cold PowerShell on a CI runner can take seconds to
/// start.</summary>
public sealed class ProcessLiveTests : IDisposable
{
    private readonly ToolTestContext _tools = new();
    private readonly ProcessManager _manager = new();

    public void Dispose()
    {
        _manager.StopAll();
        _tools.Dispose();
    }

    private Task<string> Start(object args, ToolContext? context = null) => Run(new ProcessStartTool(_manager), args, context);
    private Task<string> Read(object args) => Run(new ProcessReadTool(_manager), args);
    private Task<string> Write(object args) => Run(new ProcessWriteTool(_manager), args);
    private Task<string> Stop(object args) => Run(new ProcessStopTool(_manager), args);
    private Task<string> List() => Run(new ProcessListTool(_manager), new { });

    private async Task<string> Run(IToolExecutor tool, object args, ToolContext? context = null) =>
        (await tool.ExecuteAsync(Args.Json(args), context ?? _tools.Context, CancellationToken.None)).Output;

    /// <summary>Read until the process has exited, collecting every read's output.</summary>
    private async Task<string> ReadToExit(string id, TimeSpan timeout, string seen = "")
    {
        var clock = Stopwatch.StartNew();
        var collected = seen;
        while (clock.Elapsed < timeout)
        {
            var read = await Read(new { id, until = "\u0000never\u0000", timeout = 2 });
            collected += "\n" + read;
            if (_manager.Get(id) is { IsRunning: false })
            {
                // One last plain read reports the exit code with whatever was left.
                return collected + "\n" + await Read(new { id });
            }
        }
        Assert.Fail($"{id} did not exit within {timeout.TotalSeconds}s. Output so far:\n{collected}");
        return collected;
    }

    /// <summary>Wait for <paramref name="marker"/> in the output, unless process_start's first-output
    /// peek already reported it (that peek consumes it, so an `until` read would never see it).</summary>
    private async Task WaitForMarker(string start, string marker, int timeoutSeconds)
    {
        var firstOutput = start.IndexOf("\nFirst output:\n", StringComparison.Ordinal);
        if (firstOutput >= 0 && start.IndexOf(marker, firstOutput, StringComparison.Ordinal) >= 0) return;
        var read = await Read(new { id = ProcessToolTests.FirstId(start), until = marker, timeout = timeoutSeconds });
        Assert.True(read.Contains("Matched", StringComparison.Ordinal), $"never saw {marker}:\n{read}");
    }

    private async Task WaitUntilExited(string id, TimeSpan timeout)
    {
        var process = _manager.Get(id);
        Assert.NotNull(process);
        Assert.True(await process.WaitForExitAsync(timeout), $"{id} is still running after {timeout.TotalSeconds}s");
    }

    private static int Occurrences(string text, string needle)
    {
        var count = 0;
        for (var at = text.IndexOf(needle, StringComparison.Ordinal); at >= 0; at = text.IndexOf(needle, at + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    // MARK: - Linux/macOS: bash through pipes

    [UnixFact]
    public async Task StartReadExitReportsCodeAndOutput()
    {
        // The quotes keep the marker itself out of the command text that "Started ..." repeats.
        var start = await Start(new { command = "echo hello-from-b\"\"g; exit 3" });
        var id = ProcessToolTests.FirstId(start);
        var all = await ReadToExit(id, TimeSpan.FromSeconds(30), start);
        Assert.Contains("exited with code 3", all);
        // Reported exactly once across the start peek and the reads: no double-send.
        Assert.Equal(1, Occurrences(all, "hello-from-bg"));
    }

    [UnixFact]
    public async Task UntilMatchesLinePrintedLate()
    {
        var id = ProcessToolTests.FirstId(await Start(new { command = "sleep 1.5; echo READY-LATER" }));
        var read = await Read(new { id, until = "READY-LATER", timeout = 10 });
        Assert.Contains("Matched \"READY-LATER\"", read);
        // Nothing new since: the cursor advanced through the matched bytes.
        await WaitUntilExited(id, TimeSpan.FromSeconds(10));
        var again = await Read(new { id });
        Assert.Contains("(no new output)", again);
        Assert.Contains("exited with code 0", again);
    }

    [UnixFact]
    public async Task UntilTimeoutStillReportsAndConsumes()
    {
        var id = ProcessToolTests.FirstId(await Start(new { command = "echo NEVER-COMING-OUT; sleep 30" }));
        var read = await Read(new { id, until = "NOPE", timeout = 1 });
        Assert.Contains("No line matching", read);
        Assert.Contains("still running", read);
        var again = await Read(new { id });
        Assert.Equal("(no new output)", again); // the timed-out wait already reported its bytes
        Assert.Equal("Stopped p1.", await Stop(new { id }));
    }

    [UnixFact]
    public async Task WriteDrivesAnInteractiveProcess()
    {
        var id = ProcessToolTests.FirstId(await Start(new { command = "exec cat" }));
        var write = await Write(new { id, input = "ping from the harness", enter = true, wait = 1 });
        Assert.StartsWith("Sent", write);
        var read = await Read(new { id, until = "ping from the harness", timeout = 10 });
        Assert.Contains("Matched", read);

        // ctrl-d is end of input: cat finishes normally.
        await Write(new { id, keys = new[] { "ctrl-d" }, wait = 0 });
        await WaitUntilExited(id, TimeSpan.FromSeconds(10));
        Assert.Equal(0, _manager.Get(id)!.ExitCode);
    }

    [UnixFact]
    public async Task CtrlCKeysStopTheForegroundProgram()
    {
        var start = await Start(new { command = "echo sleeping-now; sleep 120; echo not-interrupted" });
        var id = ProcessToolTests.FirstId(start);
        await WaitForMarker(start, "sleeping-now", 10);
        await Write(new { id, keys = new[] { "ctrl-c" }, wait = 1 });
        await WaitUntilExited(id, TimeSpan.FromSeconds(10));
        Assert.DoesNotContain("not-interrupted", await Read(new { id, all = true }));
    }

    [UnixFact]
    public async Task StopUnknownIdAndProcessList()
    {
        Assert.Contains("no such process", await Stop(new { id = "p99" }));
        Assert.Contains("No background processes", await List());

        var id = ProcessToolTests.FirstId(await Start(new { command = "sleep 20" }));
        var list = await List();
        Assert.Contains(id, list);
        Assert.Contains("running", list);

        Assert.Equal($"Stopped {id}.", await Stop(new { id }));
        await WaitUntilExited(id, TimeSpan.FromSeconds(10));
        Assert.Contains($"{id}  exited(", await List());
    }

    [UnixFact]
    public async Task ForceStopEndsTheWholeGroup()
    {
        // A child that ignores SIGTERM, in the background of the shell: SIGKILL to the group gets both.
        var start = await Start(new { command = "trap '' TERM; sleep 60 & echo child=$!; wait" });
        var id = ProcessToolTests.FirstId(start);
        await WaitForMarker(start, "child=", 10);
        var child = int.Parse(System.Text.RegularExpressions.Regex.Match(await Read(new { id, all = true }), @"child=(\d+)").Groups[1].Value);
        Assert.Equal($"Stopped {id}.", await Stop(new { id, force = true }));
        await WaitUntilExited(id, TimeSpan.FromSeconds(10));
        if (!Directory.Exists("/proc")) return;
        // The background child went down with the group (gone, or a zombie waiting to be reaped).
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(10) && IsAlive(child)) await Task.Delay(100);
        Assert.False(IsAlive(child), $"child {child} survived the group kill");
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            var stat = File.ReadAllText($"/proc/{pid}/stat");
            // State is the field after the parenthesised command name.
            var state = stat[(stat.LastIndexOf(')') + 2)..].Split(' ')[0];
            return state != "Z" && state != "X";
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    [UnixFact]
    public async Task RingBufferBoundsMemoryAndPeekAll()
    {
        // ~600 KB of output through a process — beyond the 512 KB ring.
        var id = ProcessToolTests.FirstId(await Start(new
        {
            command = "yes 012345678901234567890123456789012345678901234567890123456789012345678 | head -c 600000",
        }));
        await WaitUntilExited(id, TimeSpan.FromSeconds(30));
        var process = _manager.Get(id)!;
        // 600,000 bytes of stdout, plus `yes` complaining about the closed pipe on (merged) stderr.
        Assert.True(process.EndOffset >= 600_000, $"only {process.EndOffset} bytes arrived");
        Assert.Equal(ProcessManager.DefaultRingLimit, process.RetainedBytes);
        var read = await Read(new { id, all = true });
        Assert.Contains("0123456789", read);
    }

    [UnixFact]
    public async Task RunsInTheWorkingDirectoryWithTheExtraEnvironment()
    {
        _tools.Root.Write("sub/marker.txt", "x");
        var start = await Start(new
        {
            command = "echo \"var=$DSH_TEST_VAR agent=$DSH_AGENT\"; ls",
            cwd = "sub",
            env = new Dictionary<string, string> { ["DSH_TEST_VAR"] = "hello-env" },
        });
        var all = await ReadToExit(ProcessToolTests.FirstId(start), TimeSpan.FromSeconds(30), start);
        Assert.Contains("var=hello-env agent=1", all);
        Assert.Contains("marker.txt", all);
    }

    // MARK: - Windows: a pseudo console per process

    [WindowsFact]
    public async Task ConPtyStartReadExitReportsCodeAndOutput()
    {
        var start = await Start(new { command = "Write-Output ('hello-' + 'from-bg'); exit 3" });
        var id = ProcessToolTests.FirstId(start);
        var all = await ReadToExit(id, TimeSpan.FromSeconds(90), start);
        Assert.Contains("hello-from-bg", all);
        Assert.Contains("exited with code 3", all);
    }

    [WindowsFact]
    public async Task ConPtyCmdCommandsRunInCmd()
    {
        var cmd = _tools.Context with { Shell = AgentShell.Resolve(AgentShell.CmdPreference) };
        // The caret keeps the marker out of the command text that "Started ..." repeats.
        var start = await Start(new { command = "echo hello-from-c^md& exit /b 5" }, cmd);
        var id = ProcessToolTests.FirstId(start);
        var all = await ReadToExit(id, TimeSpan.FromSeconds(90), start);
        Assert.Contains("hello-from-cmd", all);
        Assert.Contains("exited with code 5", all);
    }

    [WindowsFact]
    public async Task ConPtyUntilMatchesLinePrintedLate()
    {
        var id = ProcessToolTests.FirstId(await Start(new { command = "Start-Sleep -Milliseconds 1500; Write-Output ('READY-' + 'LATER')" }));
        var read = await Read(new { id, until = "READY-LATER", timeout = 90 });
        Assert.Contains("Matched \"READY-LATER\"", read);
        Assert.Contains("READY-LATER", read);
    }

    [WindowsFact]
    public async Task ConPtyUntilTimeoutStillReports()
    {
        var start = await Start(new { command = "Write-Output ('first-' + 'line'); Start-Sleep -Seconds 60" });
        var id = ProcessToolTests.FirstId(start);
        await WaitForMarker(start, "first-line", 90);
        var read = await Read(new { id, until = "NOPE", timeout = 2 });
        Assert.Contains("No line matching \"NOPE\" within 2s", read);
        Assert.Contains("still running", read);
        await Stop(new { id, force = true });
        await WaitUntilExited(id, TimeSpan.FromSeconds(60));
    }

    [WindowsFact]
    public async Task ConPtyWriteAnswersAPrompt()
    {
        var start = await Start(new
        {
            command = "$line = Read-Host ('your ' + 'name'); Write-Output ('got:' + $line); Start-Sleep -Seconds 30",
        });
        var id = ProcessToolTests.FirstId(start);
        await WaitForMarker(start, "your name", 90);
        var write = await Write(new { id, input = "ping from the harness", enter = true, wait = 1 });
        Assert.StartsWith("Sent", write);
        var read = await Read(new { id, until = "got:ping from the harness", timeout = 60 });
        Assert.Contains("Matched", read);
    }

    [WindowsFact]
    public async Task ConPtyCtrlCStopsPowerShell()
    {
        var start = await Start(new { command = "Write-Output ('sleeping-' + 'now'); Start-Sleep -Seconds 120; Write-Output ('not-' + 'interrupted')" });
        var id = ProcessToolTests.FirstId(start);
        await WaitForMarker(start, "sleeping-now", 90);
        await Write(new { id, keys = new[] { "ctrl-c" }, wait = 1 });
        await WaitUntilExited(id, TimeSpan.FromSeconds(60));
        Assert.DoesNotContain("not-interrupted", await Read(new { id, all = true }));
    }

    [WindowsFact]
    public async Task ConPtyCtrlCStopsANativeProgramUnderCmd()
    {
        var cmd = _tools.Context with { Shell = AgentShell.Resolve(AgentShell.CmdPreference) };
        var start = await Start(new { command = "echo pinging-now& ping -n 120 127.0.0.1" }, cmd);
        var id = ProcessToolTests.FirstId(start);
        await WaitForMarker(start, "pinging-now", 90);
        // Wait for ping itself (its lines name the address in any language): a Ctrl+C that lands
        // while cmd is still between "echo" and "ping" is swallowed by cmd, and ping runs on.
        var afterMarker = start.IndexOf("\nFirst output:\n", StringComparison.Ordinal) is var at and >= 0 ? start[at..] : "";
        if (Occurrences(afterMarker, "127.0.0.1") == 0)
        {
            var read = await Read(new { id, until = "127.0.0.1", timeout = 60 });
            Assert.True(read.Contains("Matched", StringComparison.Ordinal), $"ping never started:\n{read}");
        }
        // Like a user, press it again if the first one went unanswered.
        var process = _manager.Get(id)!;
        for (var attempt = 0; attempt < 3 && process.IsRunning; attempt++)
        {
            await Write(new { id, keys = new[] { "ctrl-c" }, wait = 1 });
            await process.WaitForExitAsync(TimeSpan.FromSeconds(10));
        }
        await WaitUntilExited(id, TimeSpan.FromSeconds(30));
    }

    [WindowsFact]
    public async Task ConPtyListAndStop()
    {
        var id = ProcessToolTests.FirstId(await Start(new { command = "Start-Sleep -Seconds 120" }));
        var list = await List();
        Assert.Contains(id, list);
        Assert.Contains("running(pid", list);

        // Polite first (Ctrl+C), the tree after the grace period.
        Assert.Equal($"Stopped {id}.", await Stop(new { id }));
        await WaitUntilExited(id, TimeSpan.FromSeconds(60));
        Assert.Contains($"{id}  exited(", await List());

        var forced = ProcessToolTests.FirstId(await Start(new { command = "Start-Sleep -Seconds 120" }));
        Assert.Equal($"Stopped {forced}.", await Stop(new { id = forced, force = true }));
        await WaitUntilExited(forced, TimeSpan.FromSeconds(60));
    }

    [WindowsFact]
    public async Task ConPtyRingBufferBoundsMemory()
    {
        var manager = new ProcessManager { RingLimit = 20_000 };
        try
        {
            var start = await new ProcessStartTool(manager).ExecuteAsync(
                Args.Json(new { command = "1..1500 | ForEach-Object { '0123456789' * 7 }" }), _tools.Context, CancellationToken.None);
            var id = ProcessToolTests.FirstId(start.Output);
            var process = manager.Get(id)!;
            Assert.True(await process.WaitForExitAsync(TimeSpan.FromSeconds(120)), "the output loop did not finish");
            Assert.True(process.RetainedBytes <= 20_000);
            Assert.True(process.EndOffset > 20_000, $"only {process.EndOffset} bytes arrived");
            var all = await new ProcessReadTool(manager).ExecuteAsync(Args.Json(new { id, all = true }), _tools.Context, CancellationToken.None);
            Assert.Contains("0123456789", all.Output);
        }
        finally
        {
            manager.StopAll();
        }
    }

    [WindowsFact]
    public async Task ConPtyRunsInTheWorkingDirectoryWithTheExtraEnvironment()
    {
        _tools.Root.Write("sub/marker.txt", "x");
        var start = await Start(new
        {
            command = "Write-Output ('var=' + $env:DSH_TEST_VAR + ' agent=' + $env:DSH_AGENT); Get-ChildItem -Name",
            cwd = "sub",
            env = new Dictionary<string, string> { ["DSH_TEST_VAR"] = "hello-env" },
        });
        var id = ProcessToolTests.FirstId(start);
        var all = await ReadToExit(id, TimeSpan.FromSeconds(90), start);
        Assert.Contains("var=hello-env agent=1", all);
        Assert.Contains("marker.txt", all);
    }
}
