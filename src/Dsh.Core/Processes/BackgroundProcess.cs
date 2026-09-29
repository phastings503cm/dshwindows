using System.Text;

namespace Dsh.Core;

// MARK: - Background processes (interact with long-running programs)
//
// `run_shell_command` is one-shot: it captures output, waits, and stops anything that sits at a
// prompt. That is the right shape for builds and greps, but wrong for a game engine, a dev server, a
// REPL, or an installer that waits for a yes/no. These tools keep a process alive on its own pseudo
// console between tool calls: `process_start` launches it, `process_read` streams whatever it printed
// since the last read (with an `until` match so the model can wait for "Godot Engine v" or "SCRIPT
// ERROR" instead of polling blindly), `process_write` types into it, `process_stop` ends it.
//
// The output buffer is a bounded ring; each reader holds its own cursor, so a model turn that reads
// twice never sees the same bytes twice, and a crashed process still reports its exit code.

// MARK: - Backend abstraction

/// <summary>What a backend needs to launch one background command.</summary>
/// <param name="Command">The command line, in <paramref name="Shell"/>'s syntax.</param>
/// <param name="WorkingDirectory">An existing folder to start in.</param>
/// <param name="Environment">Variables added to (or replacing) the inherited environment.</param>
/// <param name="Cols">Terminal width; TUIs redraw to fit.</param>
/// <param name="Rows">Terminal height.</param>
/// <param name="Shell">The agent's shell, so the command is written like a run_shell_command one.</param>
public sealed record ProcessLaunch(string Command, string WorkingDirectory, IReadOnlyDictionary<string, string> Environment,
                                   int Cols, int Rows, AgentShell Shell);

/// <summary>Receives what a launched program does. A backend may call these from any thread, and
/// may call them before <see cref="IProcessBackend.Start"/> returns.</summary>
public interface IProcessSink
{
    /// <summary>Raw terminal bytes. The buffer is only valid during the call.</summary>
    void Output(ReadOnlySpan<byte> data);

    /// <summary>The program exited and its output has been delivered. Called once.</summary>
    void Exited(int exitCode);
}

/// <summary>A running program as a backend exposes it.</summary>
public interface IProcessHandle
{
    int ProcessId { get; }

    /// <summary>Bytes a terminal would send: typed text, "\r" for Enter, 0x03 for Ctrl+C.</summary>
    void Write(string text);

    void Resize(int cols, int rows);

    /// <summary>End the program and everything it started: politely first, or at once when
    /// <paramref name="kill"/> is set. Returns immediately; the exit arrives through the sink.</summary>
    void Stop(bool kill);
}

/// <summary>Launches programs for <see cref="ProcessManager"/>. The real ones are a pseudo console per
/// process on Windows and a pipe-connected process elsewhere; tests substitute a scripted one so the
/// manager's cursor, wait and ring-buffer logic is exercised deterministically.</summary>
public interface IProcessBackend
{
    /// <summary>Start the program. Throws when it cannot be started.</summary>
    IProcessHandle Start(ProcessLaunch launch, IProcessSink sink);
}

// MARK: - One process

/// <summary>One live (or recently exited) background process.</summary>
public sealed class BackgroundProcess : IProcessSink
{
    private readonly Lock _lock = new();
    /// <summary>Raw terminal bytes, trimmed to the most recent ring limit.</summary>
    private readonly ByteRing _buffer;
    /// <summary>Per-reader cursors: how far into the stream each reader has seen. Cursors are absolute
    /// offsets (bytes ever written), so trimming the ring never moves them.</summary>
    private readonly Dictionary<string, long> _cursors = new(StringComparer.Ordinal);
    private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IProcessHandle? _handle;
    private int? _exitCode;
    private DateTimeOffset? _exitedAt;

    public string Id { get; }
    public string Command { get; }
    /// <summary>What the model said the process is for (process_start's description).</summary>
    public string? Description { get; }
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.Now;

    public DateTimeOffset? ExitedAt
    {
        get
        {
            lock (_lock) return _exitedAt;
        }
    }

    internal BackgroundProcess(string id, string command, string? description, int ringLimit)
    {
        Id = id;
        Command = command;
        Description = description;
        _buffer = new ByteRing(ringLimit);
    }

    internal void Attach(IProcessHandle handle)
    {
        lock (_lock) _handle = handle;
    }

    /// <summary>The operating-system process id (the shell the command runs in), or -1.</summary>
    public int ProcessId
    {
        get
        {
            lock (_lock) return _handle?.ProcessId ?? -1;
        }
    }

    public bool IsRunning
    {
        get
        {
            lock (_lock) return _exitCode is null;
        }
    }

    /// <summary>The exit code, or null while running.</summary>
    public int? ExitCode
    {
        get
        {
            lock (_lock) return _exitCode;
        }
    }

    /// <summary>Completes with the exit code once the process has exited and its output is in.</summary>
    public Task<int> Exit => _exit.Task;

    /// <summary>Wait up to <paramref name="timeout"/> for the exit; true when it has exited.</summary>
    public async Task<bool> WaitForExitAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (_exit.Task.IsCompleted) return true;
        try
        {
            await _exit.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    // MARK: Sink

    void IProcessSink.Output(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return;
        lock (_lock) _buffer.Append(data);
    }

    void IProcessSink.Exited(int exitCode)
    {
        lock (_lock)
        {
            if (_exitCode is not null) return;
            _exitCode = exitCode;
            _exitedAt = DateTimeOffset.Now;
        }
        _exit.TrySetResult(exitCode);
    }

    // MARK: Control

    public void Write(string text)
    {
        IProcessHandle? handle;
        lock (_lock) handle = _exitCode is null ? _handle : null;
        handle?.Write(text);
    }

    public void Resize(int cols, int rows)
    {
        IProcessHandle? handle;
        lock (_lock) handle = _exitCode is null ? _handle : null;
        handle?.Resize(cols, rows);
    }

    /// <summary>Ctrl+C / SIGTERM first, then the whole tree; or at once with <paramref name="kill"/>.</summary>
    public void Stop(bool kill)
    {
        IProcessHandle? handle;
        lock (_lock) handle = _exitCode is null ? _handle : null;
        handle?.Stop(kill);
    }

    // MARK: Reading

    /// <summary>Output a reader has not seen yet, advancing its cursor. <paramref name="reader"/> names a
    /// cursor ("tool", or "plan" for plan mode), so each read reports the stream exactly once. An unknown
    /// reader starts from "everything available now" if <paramref name="fromEnd"/>, else from the oldest
    /// retained byte.</summary>
    public (string Text, int? ExitCode, bool Running) Drain(string reader, bool fromEnd = false)
    {
        lock (_lock)
        {
            var end = ReadableEndLocked();
            long start;
            if (_cursors.TryGetValue(reader, out var cursor)) start = Math.Min(Math.Max(cursor, _buffer.Dropped), end);
            else start = fromEnd ? end : _buffer.Dropped;
            _cursors[reader] = Math.Max(start, end);
            return (DecodeLocked(start, end), _exitCode, _exitCode is null);
        }
    }

    /// <summary>What arrived since an absolute offset, and the offset it ends at (for `until` polling).
    /// Touches no cursor.</summary>
    public (string Text, long End, bool Running) TailSince(long offset)
    {
        lock (_lock)
        {
            var end = ReadableEndLocked();
            var start = Math.Min(Math.Max(offset, _buffer.Dropped), end);
            return (DecodeLocked(start, end), Math.Max(end, offset), _exitCode is null);
        }
    }

    /// <summary>Everything retained, without touching any cursor (`all: true`).</summary>
    public string PeekAll()
    {
        lock (_lock) return DecodeLocked(_buffer.Dropped, _buffer.Written);
    }

    /// <summary>Advance a reader's cursor without returning bytes (an `until` wait already reported
    /// what it saw; a later plain read must not repeat it).</summary>
    public void Consume(string reader, long offset)
    {
        lock (_lock)
        {
            var target = Math.Min(offset, _buffer.Written);
            _cursors[reader] = _cursors.TryGetValue(reader, out var cursor) ? Math.Max(cursor, target) : target;
        }
    }

    /// <summary>Absolute end offset right now (before a first read, use it to skip noise).</summary>
    public long EndOffset
    {
        get
        {
            lock (_lock) return ReadableEndLocked();
        }
    }

    /// <summary>Where a reader's cursor sits (absolute), if it has read before. The `until` poll starts
    /// here so bytes printed before the call are not skipped and then silently consumed.</summary>
    public long? Cursor(string reader)
    {
        lock (_lock) return _cursors.TryGetValue(reader, out var cursor) ? cursor : null;
    }

    /// <summary>Bytes currently held (bounded by the ring limit).</summary>
    public int RetainedBytes
    {
        get
        {
            lock (_lock) return _buffer.Count;
        }
    }

    public string SummaryLine()
    {
        var now = DateTimeOffset.Now;
        var command = TextUtil.Prefix(Command, 90);
        lock (_lock)
        {
            if (_exitCode is { } code)
            {
                var ago = (int)(now - (_exitedAt ?? StartedAt)).TotalSeconds;
                return $"{Id}  exited({code})  {ago}s ago  {command}";
            }
            var age = (int)(now - StartedAt).TotalSeconds;
            return $"{Id}  running(pid {_handle?.ProcessId ?? -1})  {age}s  {command}";
        }
    }

    /// <summary>The end a reader may advance to. While the process runs, a multi-byte UTF-8 character
    /// split across two chunks stays unread until its last byte arrives, so it is never reported as
    /// two replacement characters.</summary>
    private long ReadableEndLocked()
    {
        var end = _buffer.Written;
        if (_exitCode is not null) return end;
        return end - Utf8.IncompleteTail(_buffer, end);
    }

    private string DecodeLocked(long start, long end)
    {
        if (end <= start) return "";
        // A trimmed ring can start mid-character: skip the orphaned continuation bytes.
        if (start == _buffer.Dropped && start > 0)
        {
            var skipped = 0;
            while (start < end && skipped < 3 && Utf8.IsContinuation(_buffer.At(start)))
            {
                start++;
                skipped++;
            }
        }
        return Encoding.UTF8.GetString(_buffer.Copy(start, end));
    }

    private static class Utf8
    {
        public static bool IsContinuation(byte b) => (b & 0xC0) == 0x80;

        /// <summary>How many bytes at the end of the stream form an unfinished character.</summary>
        public static int IncompleteTail(ByteRing ring, long end)
        {
            var available = (int)Math.Min(3, end - ring.Dropped);
            for (var back = 1; back <= available; back++)
            {
                var b = ring.At(end - back);
                if (IsContinuation(b)) continue;
                var length = b switch
                {
                    >= 0xF0 and <= 0xF7 => 4,
                    >= 0xE0 => 3,
                    >= 0xC0 => 2,
                    _ => 1,
                };
                return length > back ? back : 0;
            }
            return 0;
        }
    }
}

// MARK: - Ring buffer

/// <summary>A byte buffer that keeps the most recent <c>capacity</c> bytes, addressed by absolute
/// offsets (bytes ever appended). Grows on demand up to the capacity, then overwrites the oldest bytes,
/// so a quiet process costs little and a chatty one never costs more than the cap. Not thread-safe; the
/// owner locks.</summary>
internal sealed class ByteRing
{
    private readonly int _capacity;
    private byte[] _data;
    /// <summary>Array index of the oldest retained byte.</summary>
    private int _head;

    public ByteRing(int capacity)
    {
        _capacity = Math.Max(1, capacity);
        _data = new byte[Math.Min(_capacity, 16 * 1024)];
    }

    public int Capacity => _capacity;
    /// <summary>Bytes retained.</summary>
    public int Count { get; private set; }
    /// <summary>Total bytes ever appended: the absolute end offset.</summary>
    public long Written { get; private set; }
    /// <summary>Absolute offset of the oldest retained byte (bytes trimmed from the front).</summary>
    public long Dropped => Written - Count;

    public void Append(ReadOnlySpan<byte> chunk)
    {
        if (chunk.IsEmpty) return;
        Written += chunk.Length;
        if (chunk.Length >= _capacity)
        {
            // Only the tail of an oversized chunk survives.
            if (_data.Length < _capacity) _data = new byte[_capacity];
            chunk[^_capacity..].CopyTo(_data);
            _head = 0;
            Count = _capacity;
            return;
        }
        if (Count + chunk.Length > _data.Length && _data.Length < _capacity)
        {
            var size = Math.Min(_capacity, Math.Max(_data.Length * 2, Count + chunk.Length));
            var grown = new byte[size];
            CopyOut(0, Count, grown);
            _data = grown;
            _head = 0;
        }
        // Write at the tail, wrapping; when full, the bytes overwritten are exactly the oldest ones.
        var tail = (_head + Count) % _data.Length;
        var first = Math.Min(chunk.Length, _data.Length - tail);
        chunk[..first].CopyTo(_data.AsSpan(tail));
        chunk[first..].CopyTo(_data);
        var overflow = Math.Max(0, Count + chunk.Length - _data.Length);
        _head = (_head + overflow) % _data.Length;
        Count = Count + chunk.Length - overflow;
    }

    /// <summary>The byte at an absolute offset in [<see cref="Dropped"/>, <see cref="Written"/>).</summary>
    public byte At(long offset) => _data[(_head + (int)(offset - Dropped)) % _data.Length];

    /// <summary>Bytes in [start, end), clamped to what is retained.</summary>
    public byte[] Copy(long start, long end)
    {
        start = Math.Max(start, Dropped);
        end = Math.Min(end, Written);
        if (end <= start) return [];
        var result = new byte[end - start];
        CopyOut((int)(start - Dropped), result.Length, result);
        return result;
    }

    /// <summary>Copy <paramref name="length"/> bytes starting <paramref name="skip"/> bytes after the
    /// oldest into <paramref name="destination"/>.</summary>
    private void CopyOut(int skip, int length, byte[] destination)
    {
        if (length == 0) return;
        var from = (_head + skip) % _data.Length;
        var first = Math.Min(length, _data.Length - from);
        Array.Copy(_data, from, destination, 0, first);
        Array.Copy(_data, 0, destination, first, length - first);
    }
}
