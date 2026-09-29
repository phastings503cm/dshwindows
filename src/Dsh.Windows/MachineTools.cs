using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dsh.Core;

namespace Dsh.Windows;

// MARK: - Machine tools (see and steer what's on screen)
//
// The debugging loop a human uses for a GUI app — run it, look at the window, read the error,
// screenshot the wrong-looking pixel — needs the same tools here. Windows are enumerated through
// user32/DWM (EnumWindows, extended frame bounds, cloaking), captured with GDI (BitBlt from the
// screen, PrintWindow for one window), read as text through UI Automation, and driven with
// SendInput, so there is no dependency on a helper binary or a scripting bridge. Nothing needs a
// permission from Windows; the one wall is UIPI: an app running as administrator ignores input
// from a DSH that isn't, and hides its UI Automation tree from it.
//
// Images come back through ToolResult.Images, so the engine ships them to the model on an
// attached user message (and prunes old ones so a long debug session doesn't burn the window on
// stale frames). When the selected model has no vision, the engine already swaps in a "use the
// text tools" note.
//
// Every tool here maps to ComputerAccess.Observe/Control (see ComputerAccessInfo), so the first
// use in a chat asks once per chat and plan mode can't drive.

/// <summary>The machine tools, for the app to add to its tool registry.</summary>
public static class MachineTools
{
    /// <summary>screenshot, list_windows, screen_watch, ui_tree, inspect_process (observe);
    /// mouse, keyboard, focus_app (control); view_image.</summary>
    public static IReadOnlyList<IToolExecutor> All() =>
    [
        new ScreenshotTool(), new ListWindowsTool(), new ScreenWatchTool(), new UiTreeTool(), new InspectProcessTool(),
        new MouseTool(), new KeyboardTool(), new FocusAppTool(), new ViewImageTool(),
    ];
}

// MARK: - Argument helpers

/// <summary>Readers JsonArgs doesn't have: fractional numbers, string lists, screen areas.</summary>
internal static class ToolArgs
{
    /// <summary>A number (integral or not, or a numeric string), or null when absent or garbled.</summary>
    public static double? Number(JsonObject args, string key)
    {
        if (!args.TryGetPropertyValue(key, out var node) || node is not JsonValue value) return null;
        if (value.GetValueKind() == JsonValueKind.Number && value.TryGetValue<double>(out var number)) return Finite(number);
        if (value.TryGetValue<string>(out var text)
            && double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out number))
        {
            return Finite(number);
        }
        return null;

        static double? Finite(double d) => double.IsFinite(d) ? d : null;
    }

    /// <summary>A list of strings; a lone string counts as a one-item list (models send both).</summary>
    public static IReadOnlyList<string> Strings(JsonObject args, string key)
    {
        if (!args.TryGetPropertyValue(key, out var node) || node is null) return [];
        if (node is JsonValue single) return single.TryGetValue<string>(out var s) ? [s] : [single.ToJsonString()];
        if (node is not JsonArray array) return [];
        var output = new List<string>();
        foreach (var item in array)
        {
            if (item is JsonValue v) output.Add(v.TryGetValue<string>(out var text) ? text : v.ToJsonString());
        }
        return output;
    }

    /// <summary>"x,y,width,height" in screen pixels (spaces allowed, fractions rounded). False for
    /// anything else, including an empty area.</summary>
    public static bool TryParseArea(string text, out ScreenRect area)
    {
        area = default;
        var parts = text.Replace(" ", "", StringComparison.Ordinal).Split(',');
        if (parts.Length != 4) return false;
        var values = new int[4];
        for (var i = 0; i < 4; i++)
        {
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
                || !double.IsFinite(d) || Math.Abs(d) > 1_000_000)
            {
                return false;
            }
            values[i] = (int)Math.Round(d);
        }
        area = new ScreenRect(values[0], values[1], values[2], values[3]);
        return !area.IsEmpty;
    }
}
