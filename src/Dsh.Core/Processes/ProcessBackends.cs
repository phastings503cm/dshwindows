using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Dsh.Core;

/// <summary>The backend for the machine the harness runs on.</summary>
public static class ProcessBackends
{
    public static IProcessBackend ForThisPlatform() =>
        OperatingSystem.IsWindows() ? new ConPtyProcessBackend() : new PipeProcessBackend();
}

// MARK: - Command lines

/// <summary>How a background command is launched in each agent shell: the same syntax as
/// run_shell_command, but on a console the program can prompt on.</summary>
internal static class BackgroundCommandLine
{
    /// <summary>A full CreateProcess command line running <paramref name="command"/> in <paramref name="shell"/>.</summary>
    public static string For(AgentShell shell, string command) => shell.Kind switch
    {
        ShellKind.PowerShell => string.Join(' ', [QuoteArgument(shell.Executable), .. PowerShellArguments(command).Select(QuoteArgument)]),
        // /s strips exactly the outer quotes, so the command reaches cmd verbatim; the code page switch
        // makes cmd's own output UTF-8 (the same wrapper run_shell_command uses).
        ShellKind.Cmd => $"{QuoteArgument(shell.Executable)} /d /s /c \"chcp 65001>nul & {command}\"",
        _ => $"{QuoteArgument(shell.Executable)} -c {QuoteArgument(command)}",
    };

    /// <summary>The command as -EncodedCommand (UTF-16LE base64), which sidesteps every quoting rule,
    /// inside the wrapper run_shell_command uses: UTF-8 output, no progress banner, and failures turned
    /// into an exit code. Unlike run_shell_command there is no -NonInteractive: prompting (Read-Host, a
    /// confirmation) is exactly what a background process may do.</summary>
    public static IEnumerable<string> PowerShellArguments(string command)
    {
        var script = new StringBuilder()
            .AppendLine("$ProgressPreference = 'SilentlyContinue'")
            .AppendLine("try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8; $OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }")
            .AppendLine("if ($PSStyle) { $PSStyle.OutputRendering = 'PlainText' }")
            .AppendLine(command)
            .AppendLine("$__dshOk = $?")
            .AppendLine("if ($LASTEXITCODE -is [int] -and $LASTEXITCODE -ne 0) { exit $LASTEXITCODE }")
            .AppendLine("if (-not $__dshOk) { exit 1 }")
            .AppendLine("exit 0")
            .ToString();
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        string[] common = ["-NoLogo", "-NoProfile", "-ExecutionPolicy", "Bypass"];
        // The Windows command line tops out at 32,767 characters; long scripts go through a file that
        // removes itself (PowerShell parses the whole file before running it).
        if (encoded.Length < 28_000) return [.. common, "-EncodedCommand", encoded];
        var file = Path.Combine(Path.GetTempPath(), $"dsh-bg-{Guid.NewGuid():N}.ps1");
        var selfDeleting = "Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue" + Environment.NewLine + script;
        File.WriteAllText(file, selfDeleting, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return [.. common, "-File", file];
    }

    /// <summary>Quote one argument the way CommandLineToArgvW (and the MSVC runtime) splits it back.</summary>
    public static string QuoteArgument(string argument)
    {
        if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0) return argument;
        var quoted = new StringBuilder(argument.Length + 2).Append('"');
        var backslashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }
            // Backslashes are literal unless they precede a quote.
            quoted.Append('\\', c == '"' ? backslashes * 2 + 1 : backslashes).Append(c);
            backslashes = 0;
        }
        return quoted.Append('\\', backslashes * 2).Append('"').ToString();
    }

    /// <summary>What every background process gets on top of the inherited environment (the same
    /// markers run_shell_command sets), then the model's own variables.</summary>
    public static Dictionary<string, string> EnvironmentFor(ProcessLaunch launch, IEnumerable<KeyValuePair<string, string>>? extra = null)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var environment = new Dictionary<string, string>(comparer)
        {
            ["PYTHONIOENCODING"] = "utf-8",
            ["DSH_AGENT"] = "1",
        };
        foreach (var (key, value) in extra ?? []) environment[key] = value;
        foreach (var (key, value) in launch.Environment) environment[key] = value;
        return environment;
    }
}

// MARK: - Windows: one pseudo console per process

/// <summary>Each process gets its own ConPTY, so it sees a real console: it can prompt, draw progress,
/// and take Ctrl+C typed as 0x03.</summary>
[SupportedOSPlatform("windows")]
internal sealed class ConPtyProcessBackend : IProcessBackend
{
    private static int _ctrlCRestored;

    public IProcessHandle Start(ProcessLaunch launch, IProcessSink sink)
    {
        AllowCtrlCInChildren();
        return ConPtyProcess.Start(launch, sink);
    }

    /// <summary>Whether a process ignores Ctrl+C is inherited by the processes it creates. If this
    /// process was itself started ignoring it (CREATE_NEW_PROCESS_GROUP does that implicitly, as some
    /// launchers and test hosts do), every background process would too, and a typed ctrl-c would echo
    /// but never interrupt anything. Restore normal handling once, before the first launch. It changes
    /// nothing for a GUI process without a console.</summary>
    private static void AllowCtrlCInChildren()
    {
        if (Interlocked.Exchange(ref _ctrlCRestored, 1) != 0) return;
        try
        {
            SetConsoleCtrlHandler(IntPtr.Zero, false);
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            // Not a desktop Windows.
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetConsoleCtrlHandler(IntPtr handlerRoutine, [MarshalAs(UnmanagedType.Bool)] bool add);
}

[SupportedOSPlatform("windows")]
internal sealed class ConPtyProcess : IProcessHandle
{
    /// <summary>How long a polite stop (Ctrl+C) gets before the process tree is ended.</summary>
    private static readonly TimeSpan GracePeriod = TimeSpan.FromMilliseconds(1500);

    private readonly PseudoConsoleSession _session;
    private readonly IProcessSink _sink;
    /// <summary>Only answers the console's own queries (cursor position, device attributes), exactly as
    /// the terminal panel's emulator does; the text for the model comes from the raw stream.</summary>
    private readonly TerminalEmulator _responder;
    private int _exited;

    public int ProcessId { get; }

    private ConPtyProcess(PseudoConsoleSession session, IProcessSink sink, TerminalEmulator responder)
    {
        _session = session;
        _sink = sink;
        _responder = responder;
        ProcessId = session.ProcessId;
    }

    public static ConPtyProcess Start(ProcessLaunch launch, IProcessSink sink)
    {
        var commandLine = BackgroundCommandLine.For(launch.Shell, launch.Command);
        var environment = BackgroundCommandLine.EnvironmentFor(launch);
        var responder = new TerminalEmulator(launch.Rows, launch.Cols, scrollbackLimit: 0);
        var session = PseudoConsoleSession.Start(commandLine, launch.WorkingDirectory, launch.Cols, launch.Rows, environment);
        var process = new ConPtyProcess(session, sink, responder);
        responder.OnReply = reply => session.Write(reply);
        session.Output += process.OnOutput;
        session.Exited += process.OnExited;
        return process;
    }

    /// <summary>The session knows as soon as the shell exits; the Exited event only comes once its
    /// output is drained. Either way it is over, and its id must not be signalled any more.</summary>
    private bool HasExited => Volatile.Read(ref _exited) != 0 || _session.HasExited;

    private void OnOutput(byte[] buffer, int count)
    {
        _sink.Output(buffer.AsSpan(0, count));
        lock (_responder) _responder.Feed(buffer.AsSpan(0, count));
    }

    private void OnExited(int code)
    {
        if (Interlocked.Exchange(ref _exited, 1) != 0) return;
        _sink.Exited(code);
        // The session raises Exited after its output is drained; release the console and handles.
        _session.Dispose();
    }

    public void Write(string text)
    {
        if (!HasExited) _session.Write(text);
    }

    public void Resize(int cols, int rows)
    {
        if (HasExited) return;
        _session.Resize(cols, rows);
        lock (_responder) _responder.Resize(rows, cols);
    }

    /// <summary>Windows has no SIGTERM for console programs; the polite request is the Ctrl+C a user
    /// would type, which dev servers and REPLs handle to shut down cleanly. Whatever is still running
    /// after the grace period (or at once with <paramref name="kill"/>) goes down as a tree, so GUI
    /// children that never attached to the console (a game window) end too.</summary>
    public void Stop(bool kill)
    {
        if (HasExited) return;
        if (kill)
        {
            KillTree();
            return;
        }
        _session.Write("\u0003");
        _ = Task.Delay(GracePeriod).ContinueWith(_ =>
        {
            if (!HasExited) KillTree();
        }, TaskScheduler.Default);
    }

    private void KillTree()
    {
        // Checked right before: once the shell has exited its id may be reused by another process.
        if (HasExited) return;
        try
        {
            using var process = Process.GetProcessById(ProcessId);
            process.Kill(entireProcessTree: true);
            return;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // Gone already, or not ours to kill; fall through to ending the shell with its console.
        }
        // Terminates the shell and closes the console, which ends everything attached to it; the exit
        // is still reported through the session's Exited event.
        if (!HasExited) _session.Dispose();
    }
}

// MARK: - Elsewhere: pipes (development and CI on Linux/macOS)

/// <summary>A plain pipe-connected <c>bash -c</c>: enough to exercise the manager against real
/// processes off Windows. There is no terminal, so the few terminal bytes that matter are translated:
/// Enter (CR) becomes a newline, Ctrl+C becomes SIGINT to the process group, Ctrl+D closes stdin.</summary>
internal sealed class PipeProcessBackend : IProcessBackend
{
    public IProcessHandle Start(ProcessLaunch launch, IProcessSink sink) => PipeProcess.Start(launch, sink);
}

internal sealed class PipeProcess : IProcessHandle
{
    private const int SIGINT = 2;
    private const int SIGQUIT = 3;
    private const int SIGKILL = 9;
    private const int SIGTERM = 15;
    private static readonly TimeSpan GracePeriod = TimeSpan.FromMilliseconds(1500);

    private readonly Process _process;
    private readonly IProcessSink _sink;
    private readonly Stream _stdin;
    private readonly Lock _writeLock = new();
    /// <summary>Started through setsid, so the process leads its own group and a signal to the group
    /// reaches everything it started without touching the test host.</summary>
    private readonly bool _ownGroup;
    private int _exited;
    private bool _stdinClosed;

    public int ProcessId { get; }

    private PipeProcess(Process process, IProcessSink sink, bool ownGroup)
    {
        _process = process;
        _sink = sink;
        _ownGroup = ownGroup;
        _stdin = process.StandardInput.BaseStream;
        ProcessId = process.Id;
    }

    public static PipeProcess Start(ProcessLaunch launch, IProcessSink sink)
    {
        var setsid = new[] { "/usr/bin/setsid", "/bin/setsid" }.FirstOrDefault(File.Exists);
        var psi = new ProcessStartInfo
        {
            FileName = setsid ?? launch.Shell.Executable,
            WorkingDirectory = launch.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (setsid is not null) psi.ArgumentList.Add(launch.Shell.Executable);
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(launch.Command);
        var environment = BackgroundCommandLine.EnvironmentFor(launch,
        [
            new("TERM", "dumb"),
            new("COLUMNS", launch.Cols.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new("LINES", launch.Rows.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            // Piped stdout is block-buffered; a background process's output is only useful live.
            new("PYTHONUNBUFFERED", "1"),
        ]);
        foreach (var (key, value) in environment) psi.Environment[key] = value;

        var process = new Process { StartInfo = psi };
        process.Start();
        var handle = new PipeProcess(process, sink, setsid is not null);
        handle.Begin();
        return handle;
    }

    private void Begin()
    {
        var pumps = new[]
        {
            PumpAsync(_process.StandardOutput.BaseStream),
            PumpAsync(_process.StandardError.BaseStream),
        };
        _ = WatchAsync(pumps);
    }

    private async Task PumpAsync(Stream stream)
    {
        var buffer = new byte[16_384];
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer).ConfigureAwait(false);
                if (read <= 0) return;
                _sink.Output(buffer.AsSpan(0, read));
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // Pipe closed.
        }
    }

    private async Task WatchAsync(Task[] pumps)
    {
        await _process.WaitForExitAsync().ConfigureAwait(false);
        // Let the pumps deliver what it printed last. A background child that inherited the pipes can
        // hold them open indefinitely, so this is bounded.
        await Task.WhenAny(Task.WhenAll(pumps), Task.Delay(500)).ConfigureAwait(false);
        int code;
        try
        {
            code = _process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            code = -1;
        }
        Volatile.Write(ref _exited, 1);
        _sink.Exited(code);
        CloseStdin();
        // The streams belong to the Process; release it once nothing reads them any more.
        _ = Task.WhenAll(pumps).ContinueWith(_ => _process.Dispose(), TaskScheduler.Default);
    }

    private bool HasExited => Volatile.Read(ref _exited) != 0;

    public void Write(string text)
    {
        var pending = new StringBuilder();
        foreach (var c in text)
        {
            switch (c)
            {
                case '\r':
                    pending.Append('\n');
                    break;
                case '\u0003':
                    Flush(pending);
                    Signal(SIGINT);
                    break;
                case '\u001C':
                    Flush(pending);
                    Signal(SIGQUIT);
                    break;
                case '\u0004':
                    Flush(pending);
                    CloseStdin();
                    break;
                case '\u001A':
                    // Ctrl+Z would stop the process with nothing to resume it: dropped.
                    break;
                default:
                    pending.Append(c);
                    break;
            }
        }
        Flush(pending);
    }

    private void Flush(StringBuilder pending)
    {
        if (pending.Length == 0) return;
        var bytes = Encoding.UTF8.GetBytes(pending.ToString());
        pending.Clear();
        lock (_writeLock)
        {
            if (_stdinClosed) return;
            try
            {
                _stdin.Write(bytes);
                _stdin.Flush();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // It stopped reading.
            }
        }
    }

    private void CloseStdin()
    {
        lock (_writeLock)
        {
            if (_stdinClosed) return;
            _stdinClosed = true;
            try { _stdin.Dispose(); } catch (IOException) { }
        }
    }

    public void Resize(int cols, int rows)
    {
        // No terminal to resize.
    }

    public void Stop(bool kill)
    {
        if (HasExited) return;
        if (kill)
        {
            Signal(SIGKILL);
            return;
        }
        Signal(SIGTERM);
        _ = Task.Delay(GracePeriod).ContinueWith(_ => Signal(SIGKILL), TaskScheduler.Default);
    }

    private void Signal(int signal)
    {
        // Checked right before: once exited (and reaped) the id may belong to someone else.
        if (HasExited) return;
        if (_ownGroup) _ = SysKill(-ProcessId, signal);
        _ = SysKill(ProcessId, signal);
    }

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int SysKill(int pid, int signal);
}
