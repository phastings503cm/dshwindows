namespace Vision.Core;

// MARK: - Choosing how to deliver one input
//
// A click or a keystroke can reach an app three ways. In order of preference:
//
//  1. UI Automation on the element itself (Invoke a button, Toggle a check box, Select a list item,
//     Expand a combo box, set a field's Value, Scroll a list; MSAA's default action for older
//     controls). Nothing moves: no cursor, no focus change, and it works on a window behind others.
//  2. Window messages posted to the control's own window (WM_LBUTTONDOWN/UP at client coordinates,
//     WM_CHAR, WM_KEYDOWN/UP, WM_SETTEXT, the edit commands). Also invisible to the person, but only
//     standard Win32 controls read their input from those messages faithfully: WPF, browsers, modern
//     (WinUI/UWP) apps and game engines read the real input state and ignore or misread posted input.
//  3. The real mouse and keyboard (SendInput), one chat at a time through the arbiter.
//
// "auto" (the default) uses the first route that is reliable for that element and window, and the
// real devices when none is. "background" never touches the real devices: no route is an error the
// model can act on. "real" is the old behaviour.
//
// The routes are only proposed here; the Windows side tries them in order and moves to the next one
// when a route doesn't apply after all (the element lost the pattern, UIPI refused the message).

/// <summary>What kind of window an input goes to, read from its window class.</summary>
public enum WindowKind
{
    /// <summary>A standard Win32 text box (Edit, RichEdit, Scintilla, a WinForms TextBox).</summary>
    Win32Edit,
    /// <summary>Another standard Win32 control or dialog (Button, ListBox, list/tree views, a WinForms control).</summary>
    Win32Control,
    /// <summary>A WPF window (one HWND for the whole UI).</summary>
    Wpf,
    /// <summary>A browser or Electron/Chromium/Firefox window.</summary>
    Browser,
    /// <summary>A WinUI, UWP or other XAML-island window.</summary>
    Modern,
    /// <summary>A game engine's window (Godot, Unity, Unreal, SDL, GLFW).</summary>
    Game,
    /// <summary>Anything else (Qt, Java, custom-drawn).</summary>
    Other,
}

/// <summary>How the person asked for the input to be delivered.</summary>
public enum InputMode
{
    Auto,
    Background,
    Real,
}

/// <summary>A mouse action.</summary>
public enum PointerAction
{
    Click,
    DoubleClick,
    RightClick,
    MiddleClick,
    Drag,
    Scroll,
}

/// <summary>One way of delivering an input.</summary>
public enum InputRoute
{
    UiaInvoke,
    UiaToggle,
    UiaSelect,
    UiaExpandCollapse,
    MsaaDefaultAction,
    UiaScroll,
    /// <summary>Replace a field's text through its Value pattern.</summary>
    UiaSetValue,
    /// <summary>Add to the end of a field's text through its Value pattern.</summary>
    UiaAppendValue,
    /// <summary>A click on a text field of a window that ignores posted clicks: remember the field, so
    /// the next keyboard text goes there, without clicking.</summary>
    TypingTarget,
    PostClick,
    PostDoubleClick,
    PostWheel,
    PostChars,
    PostSetText,
    PostKeys,
    /// <summary>ctrl+a/c/x/v/z on a Win32 text box as EM_SETSEL, WM_COPY, WM_CUT, WM_PASTE, WM_UNDO.</summary>
    PostEditCommand,
    /// <summary>alt+f4 as WM_CLOSE to the top-level window.</summary>
    PostClose,
    RealMouse,
    RealKeyboard,
}

/// <summary>What UI Automation says about the element an input goes to.</summary>
public sealed record ElementFacts
{
    /// <summary>The control type's short name: "Button", "CheckBox", "Edit", "ListItem"…</summary>
    public string ControlType { get; init; } = "";
    public string? Name { get; init; }
    public bool Enabled { get; init; } = true;
    public bool Invoke { get; init; }
    public bool Toggle { get; init; }
    public bool SelectionItem { get; init; }
    public bool ExpandCollapse { get; init; }
    public bool Scroll { get; init; }
    public bool Value { get; init; }
    public bool ValueReadOnly { get; init; }
    public bool IsPassword { get; init; }
    /// <summary>MSAA's default action ("Press", "Check", "Jump"…), when the element has one.</summary>
    public string? MsaaDefaultAction { get; init; }

    /// <summary>"Button “Save”", the way results name an element.</summary>
    public string Label => string.IsNullOrWhiteSpace(Name) ? (ControlType.Length == 0 ? "element" : ControlType) : $"{(ControlType.Length == 0 ? "element" : ControlType)} “{Name.Trim()}”";

    /// <summary>A text box (where a click means "type here").</summary>
    public bool IsTextField => ControlType is "Edit" || (ControlType is "Document" && Value && !ValueReadOnly);
}

public static class InputPlanner
{
    /// <summary>"auto", "background", "real" (any case; absent = auto); null for anything else.</summary>
    public static InputMode? ParseMode(string? text) => (text ?? "").Trim().ToLowerInvariant() switch
    {
        "" or "auto" => InputMode.Auto,
        "background" or "bg" => InputMode.Background,
        "real" or "foreground" => InputMode.Real,
        _ => null,
    };

    /// <summary>The kind of window behind a control's window class (<paramref name="className"/>) in a
    /// top-level window of class <paramref name="topClassName"/>.</summary>
    public static WindowKind Classify(string? className, string? topClassName)
    {
        var name = className ?? "";
        var top = topClassName ?? "";
        // WinForms controls are the Win32 ones under a longer name: "WindowsForms10.EDIT.app.0.141b42a_r6_ad1".
        var core = name.StartsWith("WindowsForms10.", StringComparison.OrdinalIgnoreCase) && name.Split('.') is { Length: > 1 } parts
            ? parts[1]
            : name;
        if (EditClasses.Contains(core) || core.StartsWith("RichEdit", StringComparison.OrdinalIgnoreCase)) return WindowKind.Win32Edit;
        foreach (var candidate in new[] { name, top })
        {
            if (candidate.StartsWith("HwndWrapper[", StringComparison.Ordinal)) return WindowKind.Wpf;
            if (candidate.StartsWith("Chrome_", StringComparison.Ordinal) || candidate is "MozillaWindowClass" or "Intermediate D3D Window")
                return WindowKind.Browser;
            if (ModernClasses.Contains(candidate) || candidate.StartsWith("Microsoft.UI.", StringComparison.Ordinal)
                || candidate.StartsWith("Windows.UI.", StringComparison.Ordinal))
            {
                return WindowKind.Modern;
            }
            if (GameClasses.Contains(candidate)) return WindowKind.Game;
        }
        if (ControlClasses.Contains(core)) return WindowKind.Win32Control;
        return WindowKind.Other;
    }

    private static readonly HashSet<string> EditClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Edit", "RICHEDIT50W", "RichEdit20W", "RichEdit20A", "RichEditD2DPT", "Scintilla",
    };

    private static readonly HashSet<string> ControlClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Button", "ComboBox", "ComboBoxEx32", "ComboLBox", "ListBox", "SysListView32", "SysTreeView32", "SysTabControl32",
        "msctls_trackbar32", "msctls_updown32", "ToolbarWindow32", "Static", "SysLink", "ScrollBar", "#32770",
        "SysHeader32", "SysDateTimePick32", "SysMonthCal32", "msctls_hotkey32", "ReBarWindow32",
        // WinForms' own names for the same controls ("WindowsForms10.BUTTON…", "…Window.8…" is a panel).
        "BUTTON", "COMBOBOX", "LISTBOX", "STATIC", "Window",
    };

    private static readonly HashSet<string> ModernClasses = new(StringComparer.Ordinal)
    {
        "ApplicationFrameWindow", "ApplicationFrameInputSinkWindow", "WinUIDesktopWin32WindowClass", "XamlExplorerHostIslandWindow",
    };

    private static readonly HashSet<string> GameClasses = new(StringComparer.Ordinal)
    {
        "Engine", "UnityWndClass", "UnrealWindow", "SDL_app", "GLFW30", "LWJGL", "OgreD3D11Wnd", "SFML_Window", "raylibWindow",
    };

    private static bool PostsWork(WindowKind kind) => kind is WindowKind.Win32Edit or WindowKind.Win32Control;

    /// <summary>The routes for a mouse action, best first. Empty = it can't be done in this mode.</summary>
    public static IReadOnlyList<InputRoute> ForPointer(PointerAction action, InputMode mode, WindowKind kind, ElementFacts? element)
    {
        if (mode == InputMode.Real) return [InputRoute.RealMouse];
        var routes = new List<InputRoute>();
        switch (action)
        {
            case PointerAction.Click:
                ClickRoutes(routes, kind, element);
                break;
            case PointerAction.DoubleClick:
                if (element is { Enabled: true } item)
                {
                    if (item.Invoke && item.ControlType is "ListItem" or "DataItem" or "TreeItem" or "Button" or "Hyperlink" or "MenuItem")
                        routes.Add(InputRoute.UiaInvoke);
                    else if (item.ControlType == "TreeItem" && item.ExpandCollapse) routes.Add(InputRoute.UiaExpandCollapse);
                }
                if (PostsWork(kind)) routes.Add(InputRoute.PostDoubleClick);
                break;
            case PointerAction.Scroll:
                if (element is { Scroll: true }) routes.Add(InputRoute.UiaScroll);
                if (PostsWork(kind)) routes.Add(InputRoute.PostWheel);
                break;
            // A context menu, a middle-click paste and drag-and-drop all follow the real pointer.
            case PointerAction.RightClick or PointerAction.MiddleClick or PointerAction.Drag:
                break;
        }
        if (mode == InputMode.Auto) routes.Add(InputRoute.RealMouse);
        return routes.Distinct().ToList();
    }

    private static void ClickRoutes(List<InputRoute> routes, WindowKind kind, ElementFacts? element)
    {
        // A click into a text box puts the caret there. A Win32 one takes a posted click; anything else
        // gets the field remembered so the next keyboard text goes to it.
        if (kind == WindowKind.Win32Edit)
        {
            routes.Add(InputRoute.PostClick);
            return;
        }
        if (element is { Enabled: true, IsTextField: true })
        {
            routes.Add(InputRoute.TypingTarget);
            return;
        }
        if (element is { Enabled: true } e)
        {
            switch (e.ControlType)
            {
                case "CheckBox":
                    if (e.Toggle) routes.Add(InputRoute.UiaToggle);
                    if (e.Invoke) routes.Add(InputRoute.UiaInvoke);
                    break;
                case "RadioButton" or "TabItem" or "ListItem" or "DataItem" or "TreeItem":
                    // Invoke on an item opens it (a double-click); a click selects.
                    if (e.SelectionItem) routes.Add(InputRoute.UiaSelect);
                    else if (e.Toggle) routes.Add(InputRoute.UiaToggle);
                    break;
                case "ComboBox":
                    if (e.ExpandCollapse) routes.Add(InputRoute.UiaExpandCollapse);
                    break;
                case "MenuItem":
                    if (e.Invoke) routes.Add(InputRoute.UiaInvoke);
                    else if (e.ExpandCollapse) routes.Add(InputRoute.UiaExpandCollapse);
                    break;
                // Page backgrounds, panes and windows: a click there does nothing a pattern could stand for.
                case "Document" or "Pane" or "Window" or "Group" or "TitleBar" or "ScrollBar":
                    break;
                default:
                    if (e.Invoke) routes.Add(InputRoute.UiaInvoke);
                    else if (e.Toggle) routes.Add(InputRoute.UiaToggle);
                    else if (e.SelectionItem) routes.Add(InputRoute.UiaSelect);
                    break;
            }
            if (routes.Count == 0 && !string.IsNullOrWhiteSpace(e.MsaaDefaultAction) && e.ControlType is not ("Document" or "Pane" or "Window"))
                routes.Add(InputRoute.MsaaDefaultAction);
        }
        if (PostsWork(kind)) routes.Add(InputRoute.PostClick);
    }

    /// <summary>The routes for typing <paramref name="text"/>, best first. With
    /// <paramref name="replace"/>, the text replaces the field's instead of being typed at the caret.</summary>
    public static IReadOnlyList<InputRoute> ForText(string text, bool replace, InputMode mode, WindowKind kind, ElementFacts? element)
    {
        if (mode == InputMode.Real) return [InputRoute.RealKeyboard];
        var routes = new List<InputRoute>();
        if (kind == WindowKind.Win32Edit)
        {
            routes.Add(replace ? InputRoute.PostSetText : InputRoute.PostChars);
        }
        else if (element is { Enabled: true, Value: true, ValueReadOnly: false }
                 // Enter and Tab are keys that act (submit a form, move on), not text a Value can hold.
                 && text.IndexOfAny(['\r', '\n', '\t']) < 0)
        {
            routes.Add(replace || element.IsPassword ? InputRoute.UiaSetValue : InputRoute.UiaAppendValue);
        }
        if (mode == InputMode.Auto) routes.Add(InputRoute.RealKeyboard);
        return routes;
    }

    /// <summary>The routes for pressing one key combo, best first.</summary>
    public static IReadOnlyList<InputRoute> ForKey(string key, bool ctrl, bool alt, bool shift, bool win, InputMode mode, WindowKind kind,
                                                   ElementFacts? element)
    {
        if (mode == InputMode.Real) return [InputRoute.RealKeyboard];
        var routes = new List<InputRoute>();
        var plain = !ctrl && !alt && !shift && !win;
        key = key.ToLowerInvariant();
        if (alt && !ctrl && !shift && !win && key == "f4") routes.Add(InputRoute.PostClose);
        if (plain && element is { Enabled: true } e)
        {
            if (key is "enter" or "return" or "space" && e.Invoke && e.ControlType is "Button" or "SplitButton" or "Hyperlink" or "MenuItem")
                routes.Add(InputRoute.UiaInvoke);
            else if (key == "space" && e.Toggle) routes.Add(InputRoute.UiaToggle);
        }
        if (PostsWork(kind))
        {
            // Posted key messages carry no modifier state (apps ask GetKeyState, which only real input
            // changes), so only lone keys go that way — and the edit commands, which have messages of their own.
            if (plain && PlainKeys.Contains(key)) routes.Add(InputRoute.PostKeys);
            if (kind == WindowKind.Win32Edit && ctrl && !alt && !shift && !win && key is "a" or "c" or "x" or "v" or "z")
                routes.Add(InputRoute.PostEditCommand);
        }
        if (mode == InputMode.Auto) routes.Add(InputRoute.RealKeyboard);
        return routes;
    }

    /// <summary>Keys a Win32 control reads correctly from posted WM_KEYDOWN/WM_KEYUP.</summary>
    private static readonly HashSet<string> PlainKeys = new(StringComparer.Ordinal)
    {
        "enter", "return", "tab", "esc", "escape", "space", "backspace", "back", "delete", "del", "insert", "ins",
        "up", "down", "left", "right", "home", "end", "pgup", "pageup", "pgdn", "pagedown",
        "f1", "f2", "f3", "f4", "f5", "f6", "f7", "f8", "f9", "f10", "f11", "f12",
    };

    /// <summary>Whether <paramref name="route"/> leaves the person's mouse and keyboard alone.</summary>
    public static bool IsBackground(InputRoute route) => route is not (InputRoute.RealMouse or InputRoute.RealKeyboard);

    /// <summary>How a result names the route it used ("UI Automation Invoke", "a posted click"…).</summary>
    public static string Describe(InputRoute route) => route switch
    {
        InputRoute.UiaInvoke => "UI Automation Invoke",
        InputRoute.UiaToggle => "UI Automation Toggle",
        InputRoute.UiaSelect => "UI Automation Select",
        InputRoute.UiaExpandCollapse => "UI Automation Expand/Collapse",
        InputRoute.MsaaDefaultAction => "its accessibility default action",
        InputRoute.UiaScroll => "UI Automation Scroll",
        InputRoute.UiaSetValue => "UI Automation SetValue (replaced its text)",
        InputRoute.UiaAppendValue => "UI Automation SetValue (added at the end of its text)",
        InputRoute.TypingTarget => "remembered as the typing target",
        InputRoute.PostClick => "a click message to the control",
        InputRoute.PostDoubleClick => "a double-click message to the control",
        InputRoute.PostWheel => "a wheel message to the control",
        InputRoute.PostChars => "character messages to the control",
        InputRoute.PostSetText => "WM_SETTEXT (replaced its text)",
        InputRoute.PostKeys => "key messages to the control",
        InputRoute.PostEditCommand => "the text box's own edit command",
        InputRoute.PostClose => "WM_CLOSE to the window",
        InputRoute.RealMouse => "the real mouse",
        _ => "the real keyboard",
    };
}
