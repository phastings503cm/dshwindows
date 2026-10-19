using System.Windows.Automation;
using Vision.Core;

namespace Vision.Windows;

// MARK: - The machine, shared by every chat

/// <summary>What all chats share: which of them are using the machine (the glow follows it), the one
/// real mouse (the arbiter) and whether the person is at the controls.</summary>
public static class MachineControl
{
    /// <summary>The chats using the machine now.</summary>
    public static MachineActivity Activity { get; } = new();

    /// <summary>The real mouse and keyboard, one chat at a time; nobody gets them while the person uses them.</summary>
    public static InputArbiter Arbiter { get; } = new(lastPersonInput: () => PersonInput.Shared.LastHuman);
}

/// <summary>A field a chat clicked into without clicking (a window that ignores posted clicks): the
/// next keyboard text without an app goes there.</summary>
internal sealed record TypingTarget(IntPtr Top, IntPtr Control, WindowKind Kind, int X, int Y, AutomationElement? Element,
                                    ElementFacts? Facts, DateTimeOffset At)
{
    /// <summary>How long a remembered field stays the default place to type.</summary>
    public static readonly TimeSpan Fresh = TimeSpan.FromMinutes(2);

    public bool IsFresh => DateTimeOffset.UtcNow - At < Fresh && Native.IsWindow(Top);
}

/// <summary>One chat as the machine tools see it: its name for the glow, whether it may only use the
/// apps it started, the field it last clicked into, and where it shows "waiting for the mouse".</summary>
public sealed class MachineChat
{
    private readonly Func<string> _title;
    private readonly Func<bool> _ownAppsOnly;

    public MachineChat(string id, Func<string>? title = null, Func<bool>? ownAppsOnly = null)
    {
        Id = id;
        _title = title ?? (static () => "a chat");
        _ownAppsOnly = ownAppsOnly ?? (static () => false);
    }

    /// <summary>The tools of a host that has no chats (tests, a bare registry).</summary>
    public static MachineChat Standalone() => new("", static () => "Vision");

    public string Id { get; }

    public string Title
    {
        get
        {
            try
            {
                var title = _title();
                return string.IsNullOrWhiteSpace(title) ? "a chat" : title;
            }
            catch (Exception)
            {
                return "a chat";
            }
        }
    }

    /// <summary>Settings › General › "Keep each chat to the apps it started".</summary>
    public bool OwnAppsOnly => _ownAppsOnly();

    /// <summary>Shows (text) or clears (null) the chat's "waiting for the mouse" line.</summary>
    public Action<string?>? Status { get; set; }

    internal TypingTarget? TypingTarget { get; set; }

    /// <summary>The real mouse and keyboard for one action (waits its turn, and for the person).</summary>
    public Task<InputLease> AcquireRealInputAsync(CancellationToken cancellationToken)
    {
        PersonInput.Shared.Start();
        return MachineControl.Arbiter.AcquireAsync(Id, Title, (reason, blocker) => Status?.Invoke(MachineText.Waiting(reason, blocker)),
                                                   cancellationToken);
    }

    /// <summary>Mid-action: wait for the person to let go. False after the longest wait.</summary>
    public Task<bool> WaitForPersonAsync(CancellationToken cancellationToken) =>
        MachineControl.Arbiter.WaitForPersonAsync((reason, blocker) => Status?.Invoke(MachineText.Waiting(reason, blocker)), cancellationToken);

    /// <summary>Whether the process behind a window descends from one this chat started.</summary>
    public bool Owns(int processId) =>
        processId != Environment.ProcessId && ProcessTree.Descends(processId, ChatProcesses.For(Id), ProcessLineage.Lookup());

    /// <summary>The refusal when "keep each chat to the apps it started" stops an input, or null.</summary>
    internal string? Refuse(IntPtr window, string verb)
    {
        if (!OwnAppsOnly || window == IntPtr.Zero) return null;
        var root = Native.GetAncestor(window, Native.GA_ROOT);
        var pid = WindowServices.ProcessIdOf(root == IntPtr.Zero ? window : root);
        if (Owns(pid)) return null;
        return $"Error: not {verb} {WindowServices.ProcessName(pid)}: this chat may only use the apps it started " +
               "(Settings › General › Keep each chat to the apps it started). Start the app with process_start or run_shell_command first, " +
               "or ask the person to turn the setting off.";
    }
}

/// <summary>Parent links and start times of running processes, for <see cref="ProcessTree"/>.</summary>
internal static class ProcessLineage
{
    /// <summary>A lookup over one snapshot of the process table; start times are read on demand.</summary>
    public static Func<int, ProcessEntry?> Lookup()
    {
        var parents = Parents();
        var cache = new Dictionary<int, ProcessEntry?>();
        return pid =>
        {
            if (cache.TryGetValue(pid, out var known)) return known;
            var entry = parents.TryGetValue(pid, out var parent) ? new ProcessEntry(pid, parent, Started(pid)) : null;
            cache[pid] = entry;
            return entry;
        };
    }

    private static Dictionary<int, int> Parents()
    {
        var output = new Dictionary<int, int>();
        var snapshot = Native.CreateToolhelp32Snapshot(Native.TH32CS_SNAPPROCESS, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1)) return output;
        try
        {
            var entry = new Native.PROCESSENTRY32W { dwSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.PROCESSENTRY32W>() };
            if (!Native.Process32First(snapshot, ref entry)) return output;
            do output[(int)entry.th32ProcessID] = (int)entry.th32ParentProcessID;
            while (Native.Process32Next(snapshot, ref entry));
        }
        finally
        {
            Native.CloseHandle(snapshot);
        }
        return output;
    }

    private static DateTimeOffset? Started(int pid)
    {
        using var handle = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle.IsInvalid) return null;
        return Native.GetProcessTimes(handle, out var creation, out _, out _, out _) && creation > 0
            ? DateTimeOffset.FromFileTime(creation)
            : null;
    }
}

/// <summary>Every machine tool runs inside its chat's machine session: the glow lights for it.</summary>
internal sealed class MachineScoped(IToolExecutor inner, MachineChat chat) : IToolExecutor
{
    public string Name => inner.Name;
    public ToolSpec Spec => inner.Spec;

    public async Task<ToolResult> ExecuteAsync(string arguments, ToolContext context, CancellationToken cancellationToken)
    {
        using var use = MachineControl.Activity.Begin(chat.Id, chat.Title);
        return await inner.ExecuteAsync(arguments, context, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Runs UI Automation and MSAA calls on a thread of their own (they belong in the MTA, and a hung
/// app can block one for a long time): the caller waits up to a timeout and then moves on.</summary>
internal static class MtaThread
{
    public static async Task<(bool Done, T? Value)> Run<T>(Func<T> work, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                using var dpi = DpiScope.PerMonitor();
                completion.TrySetResult(work());
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "ui automation call",
        };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        await Task.WhenAny(completion.Task, Task.Delay(timeout, cancellationToken)).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (!completion.Task.IsCompleted) return (false, default);
        return (true, await completion.Task.ConfigureAwait(false));
    }
}
