using System.Runtime.Versioning;
using System.Text;

namespace Dsh.Core.Tests.Terminal;

/// <summary>End to end through ConPTY: a real cmd.exe draws into the emulator.</summary>
[SupportedOSPlatform("windows")]
public class PseudoConsoleTests
{
    [WindowsFact]
    public async Task RunsACommandAndReportsItsExit()
    {
        var emulator = new TerminalEmulator(24, 100);
        var output = new StringBuilder();
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        PseudoConsoleSession? console = null;
        emulator.OnReply = reply => console?.Write(reply);
        // Handlers attached at Start: this command can finish before Start even returns.
        using var session = console = PseudoConsoleSession.Start($"\"{cmd}\" /d /c echo dsh-conpty-ok & exit /b 7",
            Path.GetTempPath(), 100, 24,
            output: (buffer, count) =>
            {
                lock (emulator)
                {
                    emulator.Feed(buffer.AsSpan(0, count));
                    output.Append(Encoding.UTF8.GetString(buffer, 0, count));
                }
            },
            exited: code => exited.TrySetResult(code));

        var finished = await Task.WhenAny(exited.Task, Task.Delay(TimeSpan.FromSeconds(30)));
        Assert.Same(exited.Task, finished);
        Assert.Equal(7, await exited.Task);
        lock (emulator) Assert.Contains("dsh-conpty-ok", emulator.Transcript);
    }

    [WindowsFact]
    public async Task InteractiveInputReachesTheShell()
    {
        var emulator = new TerminalEmulator(24, 100);
        var seen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        PseudoConsoleSession? console = null;
        emulator.OnReply = reply => console?.Write(reply);
        using var session = console = PseudoConsoleSession.Start($"\"{cmd}\" /d /q /k prompt $G", Path.GetTempPath(), 100, 24,
            output: (buffer, count) =>
            {
                lock (emulator)
                {
                    emulator.Feed(buffer.AsSpan(0, count));
                    if (emulator.Transcript.Contains("typed-through-conpty-42")) seen.TrySetResult();
                }
            });

        await Task.Delay(500);
        // One command line typed in pieces, with cursor keys in between, then Enter: cmd echoes it
        // and runs it, so the joined text shows up once the input has reached the shell.
        session.Write("echo typed-through-conpty-");
        session.Write(Encoding.ASCII.GetBytes("42"));
        session.Write(TerminalInput.Encode(TerminalKey.Home)!);
        session.Write(TerminalInput.Encode(TerminalKey.End)!);
        session.Write("\r");
        var finished = await Task.WhenAny(seen.Task, Task.Delay(TimeSpan.FromSeconds(30)));
        string transcript;
        lock (emulator) transcript = emulator.Transcript;
        Assert.True(seen.Task == finished, $"The typed command never showed up. Terminal:\n{transcript}");
        session.Resize(80, 20);
        session.Write("exit\r");
    }
}
