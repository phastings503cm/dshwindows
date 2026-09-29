using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Dsh.Core;

namespace Dsh.Windows;

// MARK: - Process facts

/// <summary>What Task Manager's Details tab knows about a process, read without WMI (which takes
/// seconds to spin up): a Toolhelp snapshot for parents, the NT query for the command line.</summary>
internal static class ProcessFacts
{
    /// <summary>pid → (parent pid, executable file name), from one Toolhelp snapshot.</summary>
    public static IReadOnlyDictionary<int, (int Parent, string Executable)> Table()
    {
        var output = new Dictionary<int, (int, string)>();
        var snapshot = Native.CreateToolhelp32Snapshot(Native.TH32CS_SNAPPROCESS, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1)) return output;
        try
        {
            var entry = new Native.PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<Native.PROCESSENTRY32W>() };
            if (!Native.Process32First(snapshot, ref entry)) return output;
            do
            {
                output[(int)entry.th32ProcessID] = ((int)entry.th32ParentProcessID, entry.szExeFile);
            }
            while (Native.Process32Next(snapshot, ref entry));
        }
        finally
        {
            Native.CloseHandle(snapshot);
        }
        return output;
    }

    /// <summary>The command line (ProcessCommandLineInformation, Windows 8.1+), or null when the
    /// process can't be opened — protected system processes, or elevated ones on some builds.</summary>
    public static string? CommandLine(int pid)
    {
        if (pid <= 0) return null;
        using var handle = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle.IsInvalid) return null;
        Native.NtQueryInformationProcess(handle, Native.ProcessCommandLineInformation, IntPtr.Zero, 0, out var size);
        if (size <= 0 || size > 1 << 20) return null;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (Native.NtQueryInformationProcess(handle, Native.ProcessCommandLineInformation, buffer, size, out _) != 0) return null;
            // A UNICODE_STRING: USHORT Length (bytes), USHORT MaximumLength, then the pointer
            // (pointer-aligned), which points into this same buffer.
            var length = (ushort)Marshal.ReadInt16(buffer);
            var text = Marshal.ReadIntPtr(buffer, IntPtr.Size);
            return length == 0 || text == IntPtr.Zero ? null : Marshal.PtrToStringUni(text, length / 2).Trim();
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>pid → whether one of its visible windows is "Not Responding" (no message pumped for
    /// 5 s), for processes that have visible windows.</summary>
    public static IReadOnlyDictionary<int, bool> WindowResponsiveness()
    {
        var output = new Dictionary<int, bool>();
        foreach (var window in WindowServices.OnScreenWindows())
        {
            var hung = Native.IsHungAppWindow(window.Handle);
            output[window.ProcessId] = output.GetValueOrDefault(window.ProcessId) || hung;
        }
        return output;
    }

    /// <summary>ps-style elapsed time: mm:ss, hh:mm:ss, or d-hh:mm:ss.</summary>
    internal static string Elapsed(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        var hms = $"{span.Hours:00}:{span.Minutes:00}:{span.Seconds:00}";
        if (span.Days > 0) return $"{span.Days}-{hms}";
        return span.Hours > 0 ? hms : $"{span.Minutes:00}:{span.Seconds:00}";
    }

    /// <summary>CPU use between two samples as a percentage of one core, like ps: a busy render
    /// loop reads ~100, several busy threads more.</summary>
    internal static double CpuPercent(TimeSpan before, TimeSpan after, TimeSpan wall) =>
        wall <= TimeSpan.Zero ? 0 : Math.Max(0, (after - before).TotalMilliseconds / wall.TotalMilliseconds * 100);

    /// <summary>Sample every process's CPU time twice, <paramref name="window"/> apart.</summary>
    public static async Task<IReadOnlyDictionary<int, double?>> SampleCpu(IReadOnlyList<Process> processes, TimeSpan window,
                                                                          CancellationToken cancellationToken)
    {
        var first = processes.ToDictionary(p => p.Id, CpuTime);
        var clock = Stopwatch.StartNew();
        await Task.Delay(window, cancellationToken).ConfigureAwait(false);
        var wall = clock.Elapsed;
        return processes.ToDictionary(p => p.Id,
            p => first[p.Id] is { } before && CpuTime(p) is { } after ? CpuPercent(before, after, wall) : (double?)null);
    }

    private static TimeSpan? CpuTime(Process process) => Try(() => process.TotalProcessorTime);

    /// <summary>A property that throws for processes we may not open (or that just exited).</summary>
    public static T? Try<T>(Func<T> read) where T : struct
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    public static string? TryText(Func<string> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }
}

// MARK: - Sockets

/// <summary>A process's TCP and UDP endpoints from the IP helper's owner-pid tables — the socket
/// half of what lsof shows (open files would need a system-wide handle scan).</summary>
internal static class SocketTable
{
    private const int AfInet = 2, AfInet6 = 23;
    private const int TcpTableOwnerPidAll = 5, UdpTableOwnerPid = 1;
    private const uint ErrorInsufficientBuffer = 122;

    public static IReadOnlyList<string> For(int pid)
    {
        var lines = new List<string>();
        if (Table(tcp: true, AfInet, TcpTableOwnerPidAll) is { } tcp4) lines.AddRange(Tcp(tcp4, pid, v6: false));
        if (Table(tcp: true, AfInet6, TcpTableOwnerPidAll) is { } tcp6) lines.AddRange(Tcp(tcp6, pid, v6: true));
        if (Table(tcp: false, AfInet, UdpTableOwnerPid) is { } udp4) lines.AddRange(Udp(udp4, pid, v6: false));
        if (Table(tcp: false, AfInet6, UdpTableOwnerPid) is { } udp6) lines.AddRange(Udp(udp6, pid, v6: true));
        return lines;
    }

    private static byte[]? Table(bool tcp, int family, int tableClass)
    {
        var size = 0;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var buffer = size == 0 ? IntPtr.Zero : Marshal.AllocHGlobal(size);
            try
            {
                var result = tcp
                    ? Native.GetExtendedTcpTable(buffer, ref size, true, family, tableClass, 0)
                    : Native.GetExtendedUdpTable(buffer, ref size, true, family, tableClass, 0);
                if (result == 0 && buffer != IntPtr.Zero)
                {
                    var bytes = new byte[size];
                    Marshal.Copy(buffer, bytes, 0, size);
                    return bytes;
                }
                // The table grows between the size query and the read; ask again.
                if (result != ErrorInsufficientBuffer && !(result == 0 && buffer == IntPtr.Zero)) return null;
                if (size <= 0) return null;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                return null;
            }
            finally
            {
                if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            }
        }
        return null;
    }

    /// <summary>Rows of MIB_TCPROW_OWNER_PID (24 bytes) or MIB_TCP6ROW_OWNER_PID (56 bytes) after a
    /// 4-byte count.</summary>
    internal static IEnumerable<string> Tcp(byte[] table, int pid, bool v6)
    {
        var rowSize = v6 ? 56 : 24;
        foreach (var offset in Rows(table, rowSize))
        {
            IPAddress local, remote;
            int localPort, remotePort;
            uint state, owner;
            if (v6)
            {
                local = new IPAddress(table.AsSpan(offset, 16));
                localPort = Port(table, offset + 20);
                remote = new IPAddress(table.AsSpan(offset + 24, 16));
                remotePort = Port(table, offset + 44);
                state = BitConverter.ToUInt32(table, offset + 48);
                owner = BitConverter.ToUInt32(table, offset + 52);
            }
            else
            {
                state = BitConverter.ToUInt32(table, offset);
                local = new IPAddress(table.AsSpan(offset + 4, 4));
                localPort = Port(table, offset + 8);
                remote = new IPAddress(table.AsSpan(offset + 12, 4));
                remotePort = Port(table, offset + 16);
                owner = BitConverter.ToUInt32(table, offset + 20);
            }
            if (owner != pid) continue;
            var line = $"TCP  {Endpoint(local, localPort)}";
            if (state != 2) line += $" → {Endpoint(remote, remotePort)}";
            yield return $"{line}  {TcpState(state)}";
        }
    }

    /// <summary>Rows of MIB_UDPROW_OWNER_PID (12 bytes) or MIB_UDP6ROW_OWNER_PID (28 bytes).</summary>
    internal static IEnumerable<string> Udp(byte[] table, int pid, bool v6)
    {
        var rowSize = v6 ? 28 : 12;
        foreach (var offset in Rows(table, rowSize))
        {
            var local = v6 ? new IPAddress(table.AsSpan(offset, 16)) : new IPAddress(table.AsSpan(offset, 4));
            var port = Port(table, offset + (v6 ? 20 : 4));
            var owner = BitConverter.ToUInt32(table, offset + (v6 ? 24 : 8));
            if (owner == pid) yield return $"UDP  {Endpoint(local, port)}";
        }
    }

    private static IEnumerable<int> Rows(byte[] table, int rowSize)
    {
        if (table.Length < 4) yield break;
        var count = BitConverter.ToUInt32(table, 0);
        for (long i = 0; i < count; i++)
        {
            var offset = 4 + i * rowSize;
            if (offset + rowSize > table.Length) yield break;
            yield return (int)offset;
        }
    }

    /// <summary>Ports sit in network byte order in the low word.</summary>
    private static int Port(byte[] table, int offset) => table[offset] << 8 | table[offset + 1];

    private static string Endpoint(IPAddress address, int port) =>
        address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{address}]:{port}" : $"{address}:{port}";

    internal static string TcpState(uint state) => state switch
    {
        1 => "closed",
        2 => "listening",
        3 => "syn-sent",
        4 => "syn-received",
        5 => "established",
        6 => "fin-wait-1",
        7 => "fin-wait-2",
        8 => "close-wait",
        9 => "closing",
        10 => "last-ack",
        11 => "time-wait",
        12 => "delete-tcb",
        _ => $"state {state}",
    };
}

// MARK: - inspect_process

public sealed class InspectProcessTool : IToolExecutor
{
    public const string ToolName = "inspect_process";
    public string Name => ToolName;
    public ToolSpec Spec { get; } = new(ToolName,
        "System-level process inspection (what Task Manager's Details tab shows): pid, CPU, memory, elapsed, state, command line — " +
        "filter by name/command-line substring or inspect one pid (adds window responsiveness, parent, children and TCP/UDP sockets). " +
        "Was Godot really started? Is it pegging CPU (render loop) or at 0% (wedged), or \"not responding\"? " +
        "For processes YOU started with process_start, prefer process_read — this one sees everything, including what the user launched.",
        """{"type":"object","properties":{"match":{"type":"string","description":"Substring to match against the process name or command line (e.g. 'Godot')"},"pid":{"type":"integer","description":"Inspect one pid instead (adds children and sockets)"}},"required":[]}""");

    private const int MaxRows = 20;
    private static readonly TimeSpan SampleWindow = TimeSpan.FromMilliseconds(500);

    public async Task<ToolResult> ExecuteAsync(string arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var args = JsonArgs.Object(arguments);
        var pid = JsonArgs.Int(args, "pid", 0);
        if (pid > 0) return await DescribeAsync(pid, cancellationToken).ConfigureAwait(false);
        var match = JsonArgs.String(args, "match")?.Trim();
        if (string.IsNullOrEmpty(match)) return "Error: give a match string or a pid.";
        return await ListAsync(match, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> ListAsync(string match, CancellationToken cancellationToken)
    {
        var all = Process.GetProcesses();
        try
        {
            var hits = new List<(Process Process, string Command)>();
            foreach (var process in all.OrderBy(p => p.Id))
            {
                if (process.Id == 0) continue; // the idle "process"
                var name = ProcessFacts.TryText(() => process.ProcessName) ?? "";
                var command = ProcessFacts.CommandLine(process.Id);
                if (name.Contains(match, StringComparison.OrdinalIgnoreCase) || (command?.Contains(match, StringComparison.OrdinalIgnoreCase) ?? false))
                    hits.Add((process, command ?? WindowServices.ImagePath(process.Id) ?? name));
            }
            if (hits.Count == 0) return $"No process command-line matching \"{match}\".";
            var shown = hits.Take(MaxRows).ToList();
            var cpu = await ProcessFacts.SampleCpu(shown.Select(h => h.Process).ToList(), SampleWindow, cancellationToken).ConfigureAwait(false);
            var windows = ProcessFacts.WindowResponsiveness();
            var lines = new List<string> { "PID       %CPU   MEM MB  ELAPSED      STATE    COMMAND" };
            foreach (var (process, command) in shown)
            {
                var memory = ProcessFacts.Try(() => process.WorkingSet64) is { } bytes ? (bytes / (1024 * 1024)).ToString(CultureInfo.InvariantCulture) : "?";
                var elapsed = ProcessFacts.Try(() => process.StartTime) is { } start ? ProcessFacts.Elapsed(DateTime.Now - start) : "?";
                var state = windows.TryGetValue(process.Id, out var hung) && hung ? "hung" : "running";
                var percent = cpu.GetValueOrDefault(process.Id) is { } p ? p.ToString("0.0", CultureInfo.InvariantCulture) : "?";
                lines.Add($"{process.Id,-8}  {percent,5}  {memory,7}  {elapsed,-11}  {state,-7}  {TextUtil.Prefix(command, 200)}");
            }
            if (hits.Count > MaxRows) lines.Add($"… {hits.Count - MaxRows} more (narrow the match)");
            lines.Add($"(%CPU is of one core; this machine has {Environment.ProcessorCount}. \"hung\" = a window of it is Not Responding.)");
            return string.Join("\n", lines);
        }
        finally
        {
            foreach (var process in all) process.Dispose();
        }
    }

    private static async Task<string> DescribeAsync(int pid, CancellationToken cancellationToken)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(pid);
        }
        catch (ArgumentException)
        {
            return $"No process with pid {pid}.";
        }
        using (process)
        {
            var cpu = await ProcessFacts.SampleCpu([process], SampleWindow, cancellationToken).ConfigureAwait(false);
            var table = ProcessFacts.Table();
            var name = ProcessFacts.TryText(() => process.ProcessName) ?? "?";
            var lines = new List<string>();
            var path = WindowServices.ImagePath(pid);
            lines.Add($"pid {pid}  {name}" + (path is null ? "" : $"  ({path})"));

            string Mb(long? bytes) => bytes is { } b ? (b / (1024 * 1024)).ToString(CultureInfo.InvariantCulture) : "?";
            var percent = cpu.GetValueOrDefault(pid) is { } p ? p.ToString("0.0", CultureInfo.InvariantCulture) + "%" : "?";
            lines.Add($"cpu {percent} of one core ({Environment.ProcessorCount} cores) · memory {Mb(ProcessFacts.Try(() => process.WorkingSet64))} MB working set, "
                      + $"{Mb(ProcessFacts.Try(() => process.PrivateMemorySize64))} MB private · "
                      + $"{ProcessFacts.Try(() => process.Threads.Count)?.ToString(CultureInfo.InvariantCulture) ?? "?"} threads · "
                      + $"{ProcessFacts.Try(() => process.HandleCount)?.ToString(CultureInfo.InvariantCulture) ?? "?"} handles");
            if (ProcessFacts.Try(() => process.StartTime) is { } start)
                lines.Add($"started {start.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} (elapsed {ProcessFacts.Elapsed(DateTime.Now - start)})");
            if (table.TryGetValue(pid, out var self) && self.Parent > 0)
            {
                var parent = table.TryGetValue(self.Parent, out var p2) ? p2.Executable : "exited";
                lines.Add($"parent {self.Parent} ({parent})");
            }

            var windows = WindowServices.OnScreenWindows().Where(w => w.ProcessId == pid).Take(5).ToList();
            if (windows.Count == 0) lines.Add("windows: none visible");
            foreach (var window in windows)
            {
                var responding = Native.IsHungAppWindow(window.Handle) ? "NOT RESPONDING" : "responding";
                lines.Add($"window \"{window.Title}\" [{(window.Minimized ? "minimized" : window.Bounds.ToString())}] — {responding}");
            }
            lines.Add($"command: {ProcessFacts.CommandLine(pid) ?? "(not readable — likely a protected system process)"}");

            var children = table.Where(e => e.Value.Parent == pid && e.Key != pid).OrderBy(e => e.Key).ToList();
            lines.Add(children.Count == 0
                ? "children: none"
                : "children: " + string.Join(", ", children.Take(20).Select(c => $"{c.Key} ({c.Value.Executable})"))
                  + (children.Count > 20 ? $", … {children.Count - 20} more" : ""));

            lines.Add("--- sockets ---");
            var sockets = SocketTable.For(pid);
            lines.AddRange(sockets.Count == 0 ? ["(none)"] : sockets.Take(25));
            if (sockets.Count > 25) lines.Add($"… {sockets.Count - 25} more");
            return string.Join("\n", lines);
        }
    }
}
