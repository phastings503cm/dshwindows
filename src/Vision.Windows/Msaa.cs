using System.Runtime.InteropServices;

namespace Vision.Windows;

/// <summary>MSAA's IAccessible (oleacc.idl), declared in vtable order so it binds through the dual
/// interface's vtable rather than by name. Only the default-action members are used: older Win32 and
/// owner-drawn controls expose "Press"/"Check"/"Jump" through MSAA even when UI Automation offers no
/// pattern for them (UI Automation's LegacyIAccessible pattern is the same call, but the managed client
/// doesn't wrap it).</summary>
[ComImport]
[Guid("618736E0-3C3D-11CF-810C-00AA00389B71")]
[InterfaceType(ComInterfaceType.InterfaceIsDual)]
internal interface IAccessible
{
    [PreserveSig] int get_accParent([MarshalAs(UnmanagedType.IDispatch)] out object? parent);
    [PreserveSig] int get_accChildCount(out int count);
    [PreserveSig] int get_accChild([MarshalAs(UnmanagedType.Struct)] object child, [MarshalAs(UnmanagedType.IDispatch)] out object? dispatch);
    [PreserveSig] int get_accName([MarshalAs(UnmanagedType.Struct)] object child, [MarshalAs(UnmanagedType.BStr)] out string? name);
    [PreserveSig] int get_accValue([MarshalAs(UnmanagedType.Struct)] object child, [MarshalAs(UnmanagedType.BStr)] out string? value);
    [PreserveSig] int get_accDescription([MarshalAs(UnmanagedType.Struct)] object child, [MarshalAs(UnmanagedType.BStr)] out string? description);
    [PreserveSig] int get_accRole([MarshalAs(UnmanagedType.Struct)] object child, [MarshalAs(UnmanagedType.Struct)] out object? role);
    [PreserveSig] int get_accState([MarshalAs(UnmanagedType.Struct)] object child, [MarshalAs(UnmanagedType.Struct)] out object? state);
    [PreserveSig] int get_accHelp([MarshalAs(UnmanagedType.Struct)] object child, [MarshalAs(UnmanagedType.BStr)] out string? help);
    [PreserveSig] int get_accHelpTopic([MarshalAs(UnmanagedType.BStr)] out string? helpFile, [MarshalAs(UnmanagedType.Struct)] object child, out int topic);
    [PreserveSig] int get_accKeyboardShortcut([MarshalAs(UnmanagedType.Struct)] object child, [MarshalAs(UnmanagedType.BStr)] out string? shortcut);
    [PreserveSig] int get_accFocus([MarshalAs(UnmanagedType.Struct)] out object? child);
    [PreserveSig] int get_accSelection([MarshalAs(UnmanagedType.Struct)] out object? children);
    [PreserveSig] int get_accDefaultAction([MarshalAs(UnmanagedType.Struct)] object child, [MarshalAs(UnmanagedType.BStr)] out string? action);
    [PreserveSig] int accSelect(int flags, [MarshalAs(UnmanagedType.Struct)] object child);
    [PreserveSig] int accLocation(out int left, out int top, out int width, out int height, [MarshalAs(UnmanagedType.Struct)] object child);
    [PreserveSig] int accNavigate(int direction, [MarshalAs(UnmanagedType.Struct)] object start, [MarshalAs(UnmanagedType.Struct)] out object? end);
    [PreserveSig] int accHitTest(int x, int y, [MarshalAs(UnmanagedType.Struct)] out object? child);
    [PreserveSig] int accDoDefaultAction([MarshalAs(UnmanagedType.Struct)] object child);
    [PreserveSig] int put_accName([MarshalAs(UnmanagedType.Struct)] object child, [MarshalAs(UnmanagedType.BStr)] string name);
    [PreserveSig] int put_accValue([MarshalAs(UnmanagedType.Struct)] object child, [MarshalAs(UnmanagedType.BStr)] string value);
}

/// <summary>The MSAA object under a screen point inside one window, and its default action.</summary>
internal static class Msaa
{
    private static readonly Guid AccessibleId = new("618736E0-3C3D-11CF-810C-00AA00389B71");

    /// <summary>Roles whose default action is not what a click on them means (the window, its client
    /// area, a pane, a grouping).</summary>
    private static readonly HashSet<int> NoClickRoles = [0x09 /* window */, 0x0A /* client */, 0x10 /* pane */, 0x14 /* grouping */];

    /// <summary>The default action ("Press", "Check"…) of the deepest object at screen point
    /// (<paramref name="x"/>, <paramref name="y"/>) in <paramref name="window"/>, and a way to do it;
    /// null when there is none.</summary>
    public static (string Action, Func<bool> Do)? DefaultActionAt(IntPtr window, int x, int y)
    {
        try
        {
            var iid = AccessibleId;
            if (Native.AccessibleObjectFromWindow(window, Native.OBJID_CLIENT, ref iid, out var root) != 0 || root is not IAccessible current)
                return null;
            object child = 0;
            // Hit-test down: a child object answers for its own children.
            for (var depth = 0; depth < 8; depth++)
            {
                if (current.accHitTest(x, y, out var hit) != 0 || hit is null) break;
                if (hit is int id)
                {
                    child = id;
                    break;
                }
                if (hit is IAccessible inner && !ReferenceEquals(inner, current))
                {
                    current = inner;
                    child = 0;
                    continue;
                }
                break;
            }
            if (current.get_accRole(child, out var role) == 0 && role is int r && NoClickRoles.Contains(r)) return null;
            if (current.get_accDefaultAction(child, out var action) != 0 || string.IsNullOrWhiteSpace(action)) return null;
            var target = current;
            var item = child;
            return (action.Trim(), () => target.accDoDefaultAction(item) == 0);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or InvalidComObjectException)
        {
            return null;
        }
    }
}
