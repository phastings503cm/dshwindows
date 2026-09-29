using System.Collections;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Dsh.Core;

/// <summary>A process attached to a Windows pseudo console (ConPTY): the shell behind an
/// integrated-terminal tab. Output arrives on a background thread as raw VT bytes; input is written
/// as the bytes a terminal would send.</summary>
[SupportedOSPlatform("windows")]
public sealed class PseudoConsoleSession : IDisposable
{
    private readonly object _gate = new();
    private IntPtr _console;
    private IntPtr _attributeList;
    private IntPtr _environment;
    private readonly IntPtr _process;
    private readonly IntPtr _thread;
    private readonly FileStream _input;
    private readonly FileStream _output;
    private readonly Thread _reader;
    private RegisteredWaitHandle? _exitWait;
    private ProcessWaitHandle? _exitEvent;
    private int _exitCode = -1;
    private bool _processExited;
    private bool _disposed;

    /// <summary>Raw output, on the reader thread. The buffer is only valid during the call.</summary>
    public event Action<byte[], int>? Output;
    /// <summary>The process exited and its output is drained. Raised once, on a background thread.</summary>
    public event Action<int>? Exited;

    public int ProcessId { get; }
    public bool HasExited => Volatile.Read(ref _processExited);
    public int ExitCode => _exitCode;

    private PseudoConsoleSession(IntPtr console, IntPtr attributeList, IntPtr environment, Native.PROCESS_INFORMATION info,
                                 SafeFileHandle input, SafeFileHandle output)
    {
        _console = console;
        _attributeList = attributeList;
        _environment = environment;
        _process = info.hProcess;
        _thread = info.hThread;
        ProcessId = info.dwProcessId;
        _input = new FileStream(input, FileAccess.Write, 1, isAsync: false);
        _output = new FileStream(output, FileAccess.Read, 1, isAsync: false);
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "ConPTY output" };
    }

    /// <summary>Launch <paramref name="commandLine"/> on a new pseudo console of the given size.
    /// <paramref name="output"/> and <paramref name="exited"/> are attached before the first byte is
    /// read: a quick command (<c>echo hi</c>) can print everything and exit before a caller that
    /// subscribes after Start returns gets the chance.</summary>
    public static PseudoConsoleSession Start(string commandLine, string workingDirectory, int cols, int rows,
                                             IReadOnlyDictionary<string, string>? environment = null,
                                             Action<byte[], int>? output = null, Action<int>? exited = null)
    {
        if (!Native.CreatePipe(out var inputRead, out var inputWrite, IntPtr.Zero, 0)) throw new Win32Exception();
        if (!Native.CreatePipe(out var outputRead, out var outputWrite, IntPtr.Zero, 0)) throw new Win32Exception();

        var size = new Native.COORD { X = (short)Math.Clamp(cols, 1, short.MaxValue), Y = (short)Math.Clamp(rows, 1, short.MaxValue) };
        var hr = Native.CreatePseudoConsole(size, inputRead, outputWrite, 0, out var console);
        // The pseudo console holds its own copies of its ends of the pipes.
        inputRead.Dispose();
        outputWrite.Dispose();
        if (hr != 0)
        {
            inputWrite.Dispose();
            outputRead.Dispose();
            throw new Win32Exception(hr, "Could not create a pseudo console. Windows 10 1809 or later is required.");
        }

        var attributeList = IntPtr.Zero;
        var environmentBlock = IntPtr.Zero;
        try
        {
            var listSize = IntPtr.Zero;
            Native.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref listSize);
            attributeList = Marshal.AllocHGlobal(listSize);
            if (!Native.InitializeProcThreadAttributeList(attributeList, 1, 0, ref listSize)) throw new Win32Exception();
            if (!Native.UpdateProcThreadAttribute(attributeList, 0, Native.PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, console,
                                                  IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception();

            var startup = new Native.STARTUPINFOEX();
            startup.StartupInfo.cb = Marshal.SizeOf<Native.STARTUPINFOEX>();
            // Null standard handles: the child must talk to the pseudo console, not inherit ours
            // (which may be redirected when the app itself was started from a pipe).
            startup.StartupInfo.dwFlags = Native.STARTF_USESTDHANDLES;
            startup.lpAttributeList = attributeList;

            environmentBlock = Marshal.StringToHGlobalUni(EnvironmentBlock(environment));
            var directory = Directory.Exists(workingDirectory)
                ? workingDirectory
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var mutableCommandLine = new StringBuilder(commandLine);
            if (!Native.CreateProcessW(null, mutableCommandLine, IntPtr.Zero, IntPtr.Zero, false,
                                       Native.EXTENDED_STARTUPINFO_PRESENT | Native.CREATE_UNICODE_ENVIRONMENT,
                                       environmentBlock, directory, ref startup, out var info))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not start {commandLine}");
            }

            var session = new PseudoConsoleSession(console, attributeList, environmentBlock, info, inputWrite, outputRead);
            if (output is not null) session.Output += output;
            if (exited is not null) session.Exited += exited;
            session.Begin();
            return session;
        }
        catch
        {
            Native.ClosePseudoConsole(console);
            if (attributeList != IntPtr.Zero)
            {
                Native.DeleteProcThreadAttributeList(attributeList);
                Marshal.FreeHGlobal(attributeList);
            }
            if (environmentBlock != IntPtr.Zero) Marshal.FreeHGlobal(environmentBlock);
            inputWrite.Dispose();
            outputRead.Dispose();
            throw;
        }
    }

    /// <summary>The inherited environment plus what a terminal advertises about itself, in the
    /// sorted, double-null-terminated form CreateProcess wants.</summary>
    private static string EnvironmentBlock(IReadOnlyDictionary<string, string>? overrides)
    {
        var variables = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && entry.Value is string value) variables[key] = value;
        }
        variables["TERM"] = "xterm-256color";
        variables["COLORTERM"] = "truecolor";
        variables["TERM_PROGRAM"] = "DSH";
        foreach (var (key, value) in overrides ?? new Dictionary<string, string>()) variables[key] = value;
        var block = new StringBuilder();
        foreach (var (key, value) in variables)
        {
            // Names starting with '=' are cmd's per-drive directories; keep them as they are.
            block.Append(key).Append('=').Append(value).Append('\0');
        }
        block.Append('\0');
        return block.ToString();
    }

    private void Begin()
    {
        _reader.Start();
        _exitEvent = new ProcessWaitHandle(_process);
        _exitWait = ThreadPool.RegisterWaitForSingleObject(_exitEvent, (_, _) => OnProcessExited(), null, Timeout.Infinite, true);
    }

    private void OnProcessExited()
    {
        if (Native.GetExitCodeProcess(_process, out var code)) _exitCode = unchecked((int)code);
        Volatile.Write(ref _processExited, true);
        // Closing the console ends the output pipe once everything written has been read, which
        // lets the reader finish and report the exit after the last output, not before it.
        ThreadPool.QueueUserWorkItem(_ => CloseConsole());
    }

    private void CloseConsole()
    {
        IntPtr console;
        lock (_gate)
        {
            console = _console;
            _console = IntPtr.Zero;
        }
        // Prior to Windows 11 24H2 this blocks until the output is drained, which the reader
        // thread keeps doing — so it must never run on that thread.
        if (console != IntPtr.Zero) Native.ClosePseudoConsole(console);
    }

    private void ReadLoop()
    {
        var buffer = new byte[16 * 1024];
        try
        {
            while (true)
            {
                var read = _output.Read(buffer, 0, buffer.Length);
                if (read <= 0) break;
                Output?.Invoke(buffer, read);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // The pipe closed.
        }
        if (!HasExited)
        {
            // The pipe broke first (console closed); wait briefly for the process to finish.
            _exitEvent?.WaitOne(TimeSpan.FromSeconds(2));
            if (Native.GetExitCodeProcess(_process, out var code) && code != Native.STILL_ACTIVE) _exitCode = unchecked((int)code);
        }
        Exited?.Invoke(_exitCode);
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return;
        lock (_gate)
        {
            if (_disposed || _console == IntPtr.Zero) return;
            try
            {
                _input.Write(data);
                _input.Flush();
            }
            catch (IOException)
            {
                // The shell went away.
            }
        }
    }

    public void Write(string text) => Write(Encoding.UTF8.GetBytes(text));

    public void Resize(int cols, int rows)
    {
        lock (_gate)
        {
            if (_disposed || _console == IntPtr.Zero) return;
            Native.ResizePseudoConsole(_console, new Native.COORD
            {
                X = (short)Math.Clamp(cols, 1, short.MaxValue),
                Y = (short)Math.Clamp(rows, 1, short.MaxValue),
            });
        }
    }

    /// <summary>End the shell and everything it started in this console.</summary>
    public void Kill()
    {
        // Closing the console sends CTRL_CLOSE_EVENT to every attached process, which is how a
        // terminal window closing ends a running dev server; the shell itself is then terminated
        // if it ignored that.
        ThreadPool.QueueUserWorkItem(_ =>
        {
            CloseConsole();
            if (!HasExited && _exitEvent?.WaitOne(TimeSpan.FromSeconds(1)) != true) Native.TerminateProcess(_process, 1);
        });
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        if (!HasExited) Native.TerminateProcess(_process, 1);
        CloseConsoleAsync();
        _exitWait?.Unregister(null);
        try { _input.Dispose(); } catch (IOException) { }
        if (_attributeList != IntPtr.Zero)
        {
            Native.DeleteProcThreadAttributeList(_attributeList);
            Marshal.FreeHGlobal(_attributeList);
            _attributeList = IntPtr.Zero;
        }
        if (_environment != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_environment);
            _environment = IntPtr.Zero;
        }
        Native.CloseHandle(_thread);
        // The process handle backs the exit wait; release it once the reader is done with it.
        ThreadPool.QueueUserWorkItem(_ =>
        {
            _reader.Join(TimeSpan.FromSeconds(5));
            try { _output.Dispose(); } catch (IOException) { }
            _exitEvent?.Dispose();
            Native.CloseHandle(_process);
        });
    }

    private void CloseConsoleAsync() => ThreadPool.QueueUserWorkItem(_ => CloseConsole());

    /// <summary>Waits on a process handle without owning it.</summary>
    private sealed class ProcessWaitHandle : WaitHandle
    {
        public ProcessWaitHandle(IntPtr process) => SafeWaitHandle = new SafeWaitHandle(process, ownsHandle: false);
    }

    private static class Native
    {
        public const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
        public const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
        public const int STARTF_USESTDHANDLES = 0x00000100;
        public const uint STILL_ACTIVE = 259;
        public static readonly IntPtr PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;

        [StructLayout(LayoutKind.Sequential)]
        public struct COORD
        {
            public short X;
            public short Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct STARTUPINFO
        {
            public int cb;
            public IntPtr lpReserved;
            public IntPtr lpDesktop;
            public IntPtr lpTitle;
            public int dwX;
            public int dwY;
            public int dwXSize;
            public int dwYSize;
            public int dwXCountChars;
            public int dwYCountChars;
            public int dwFillAttribute;
            public int dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct STARTUPINFOEX
        {
            public STARTUPINFO StartupInfo;
            public IntPtr lpAttributeList;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public int dwProcessId;
            public int dwThreadId;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern int CreatePseudoConsole(COORD size, SafeFileHandle hInput, SafeFileHandle hOutput, uint dwFlags, out IntPtr phPC);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern int ResizePseudoConsole(IntPtr hPC, COORD size);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern void ClosePseudoConsole(IntPtr hPC);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CreatePipe(out SafeFileHandle hReadPipe, out SafeFileHandle hWritePipe, IntPtr lpPipeAttributes, int nSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, IntPtr attribute, IntPtr lpValue,
                                                            IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateProcessW")]
        public static extern bool CreateProcessW(string? lpApplicationName, StringBuilder lpCommandLine, IntPtr lpProcessAttributes,
                                                 IntPtr lpThreadAttributes, bool bInheritHandles, uint dwCreationFlags,
                                                 IntPtr lpEnvironment, string? lpCurrentDirectory, ref STARTUPINFOEX lpStartupInfo,
                                                 out PROCESS_INFORMATION lpProcessInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr handle);
    }
}
