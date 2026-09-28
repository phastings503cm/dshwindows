using System.Diagnostics;
using System.Text;

namespace Dsh.Core;

/// <summary>The result of one captured command.</summary>
public sealed record ShellResult(string Output, int ExitCode, bool TimedOut);

/// <summary>Runs one command in the agent's shell and captures what it prints.
///
/// Stdout and stderr are captured as they arrive (interleaved in arrival order) into a bounded tail.
/// Stdin is closed immediately, so a program that asks for input gets end-of-file instead of
/// hanging. Two limits are enforced: the total timeout, always; and an idle limit once output has
/// been seen — a command that printed a prompt and then went silent is almost certainly waiting for
/// input it will never get, so it is stopped early with the prompt in its output. A command that has
/// printed nothing (a slow, quiet build) runs to the full timeout.</summary>
public static class ShellRunner
{
    /// <summary>Idle limit: fail a command that goes silent this long after printing something.</summary>
    public static TimeSpan IdleLimit(TimeSpan total) =>
        TimeSpan.FromSeconds(Math.Min(45, total.TotalSeconds * 0.9));

    public static async Task<ShellResult> RunAsync(string command, string workingDirectory, AgentShell shell,
                                                   TimeSpan timeout, int maxCapture = 200_000,
                                                   Action<string>? onOutput = null,
                                                   CancellationToken cancellationToken = default)
    {
        Process process;
        try
        {
            process = new Process { StartInfo = shell.CreateStartInfo(command, workingDirectory) };
            if (!process.Start()) return new ShellResult("", -1, false);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return new ShellResult($"Could not start {shell.DisplayName}: {ex.Message}", -1, false);
        }

        using (process)
        {
            try { process.StandardInput.Close(); } catch (IOException) { }

            var tail = new ByteTail(maxCapture);
            var clock = Stopwatch.StartNew();
            long lastOutputTicks = 0;
            var sawOutput = false;

            void Record(byte[] buffer, int count)
            {
                tail.Append(buffer.AsSpan(0, count));
                Interlocked.Exchange(ref lastOutputTicks, clock.ElapsedTicks);
                Volatile.Write(ref sawOutput, true);
                onOutput?.Invoke(Encoding.UTF8.GetString(buffer, 0, count));
            }

            var readers = new[]
            {
                PumpAsync(process.StandardOutput.BaseStream, Record),
                PumpAsync(process.StandardError.BaseStream, Record),
            };

            var idleLimit = IdleLimit(timeout);
            var timedOut = false;
            try
            {
                while (true)
                {
                    if (clock.Elapsed > timeout)
                    {
                        timedOut = true;
                        break;
                    }
                    if (Volatile.Read(ref sawOutput))
                    {
                        var idleFor = TimeSpan.FromTicks((long)((clock.ElapsedTicks - Interlocked.Read(ref lastOutputTicks))
                                                                * (TimeSpan.TicksPerSecond / (double)Stopwatch.Frequency)));
                        if (idleFor > idleLimit)
                        {
                            timedOut = true;
                            break;
                        }
                    }
                    using var tick = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    tick.CancelAfter(200);
                    try
                    {
                        await process.WaitForExitAsync(tick.Token).ConfigureAwait(false);
                        break;
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        // Poll again.
                    }
                }
            }
            catch (OperationCanceledException)
            {
                KillTree(process);
                throw;
            }

            if (timedOut) KillTree(process);

            // Let the readers drain what the process printed last. A background child that inherited
            // the pipes can keep them open indefinitely, so this is bounded.
            await Task.WhenAny(Task.WhenAll(readers), Task.Delay(timedOut ? 1_000 : 400)).ConfigureAwait(false);

            int exit;
            try
            {
                exit = process.HasExited ? process.ExitCode : -1;
            }
            catch (InvalidOperationException)
            {
                exit = -1;
            }
            var text = Encoding.UTF8.GetString(tail.Take());
            return new ShellResult(TextUtil.CleanConsoleOutput(text), exit, timedOut);
        }
    }

    private static async Task PumpAsync(Stream stream, Action<byte[], int> sink)
    {
        var buffer = new byte[16_384];
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer).ConfigureAwait(false);
                if (read <= 0) return;
                sink(buffer, read);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // Pipe closed.
        }
    }

    private static void KillTree(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // Already gone.
        }
    }

    /// <summary>A byte tail both pipe readers append to.</summary>
    private sealed class ByteTail(int capacity)
    {
        private readonly Lock _lock = new();
        private byte[] _data = new byte[Math.Min(capacity, 64 * 1024)];
        private int _count;

        public void Append(ReadOnlySpan<byte> chunk)
        {
            lock (_lock)
            {
                if (chunk.Length >= capacity)
                {
                    _data = chunk[^capacity..].ToArray();
                    _count = capacity;
                    return;
                }
                if (_count + chunk.Length > _data.Length)
                {
                    var needed = Math.Min(capacity, Math.Max(_data.Length * 2, _count + chunk.Length));
                    if (needed > _data.Length) Array.Resize(ref _data, needed);
                }
                if (_count + chunk.Length > capacity)
                {
                    var drop = _count + chunk.Length - capacity;
                    Buffer.BlockCopy(_data, drop, _data, 0, _count - drop);
                    _count -= drop;
                }
                chunk.CopyTo(_data.AsSpan(_count));
                _count += chunk.Length;
            }
        }

        public byte[] Take()
        {
            lock (_lock) return _data.AsSpan(0, _count).ToArray();
        }
    }
}
