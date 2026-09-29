using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using Dsh.Core;

namespace Dsh.Windows;

// MARK: - Tree text

/// <summary>One element of an accessibility tree, as ui_tree prints it.</summary>
internal sealed record UiNode(string Role, string? Name = null, string? Value = null, string? AutomationId = null,
                              ScreenRect? Bounds = null);

/// <summary>The text form of an accessibility tree: one element per line, two spaces of indent per
/// level — <c>Button "Save" [x,y wxh] #saveButton</c>, <c>Edit "Search" = query text</c>.</summary>
internal static class UiTreeText
{
    public static string Describe(UiNode node, int level)
    {
        var line = new StringBuilder(new string(' ', level * 2));
        line.Append(node.Role);
        if (Clean(node.Name, 120) is { Length: > 0 } name) line.Append(" \"").Append(name).Append('"');
        if (Clean(node.Value, 80) is { Length: > 0 } value && node.Value != node.Name) line.Append(" = ").Append(value);
        if (node.Bounds is { IsEmpty: false } b) line.Append($" [{b.X},{b.Y} {b.Width}x{b.Height}]");
        if (Clean(node.AutomationId, 60) is { Length: > 0 } id) line.Append(" #").Append(id);
        return line.ToString();
    }

    /// <summary>Walk <paramref name="roots"/> depth-first: roots are level 0 and children are
    /// visited while the level is below <paramref name="maxDepth"/>. An element
    /// <paramref name="describe"/> maps to null is skipped with its subtree. Stops after
    /// <paramref name="maxNodes"/> lines.</summary>
    public static (string Text, int Count) Walk<T>(IEnumerable<T> roots, Func<T, IEnumerable<T>> children, Func<T, UiNode?> describe,
                                                   int maxDepth, int maxNodes)
    {
        var output = new StringBuilder();
        var count = 0;
        var capped = false;
        void Visit(IEnumerable<T> elements, int level)
        {
            foreach (var element in elements)
            {
                if (count >= maxNodes)
                {
                    capped = true;
                    return;
                }
                if (describe(element) is not { } node) continue;
                count++;
                output.Append(Describe(node, level)).Append('\n');
                if (level < maxDepth) Visit(children(element), level + 1);
            }
        }
        Visit(roots, 0);
        if (capped) output.Append($"… [stopped at {maxNodes} elements; use a smaller max_depth or screenshot]\n");
        return (output.ToString().TrimEnd('\n'), count);
    }

    /// <summary>One line, clipped.</summary>
    private static string? Clean(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var single = string.Join(' ', text.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return single.Length > max ? single[..max] + "…" : single;
    }
}

// MARK: - UI Automation walk

/// <summary>Reads a window's tree through UI Automation's control view, fetching each element's
/// properties in one cached round trip instead of one cross-process call per property.</summary>
internal static class UiAutomationTree
{
    public static (string Text, int Count) Read(IntPtr window, int maxDepth, int maxNodes, TimeSpan budget)
    {
        using var dpi = DpiScope.PerMonitor();
        var request = new CacheRequest { TreeFilter = Automation.ControlViewCondition };
        request.Add(AutomationElement.ControlTypeProperty);
        request.Add(AutomationElement.NameProperty);
        request.Add(AutomationElement.AutomationIdProperty);
        request.Add(AutomationElement.BoundingRectangleProperty);
        request.Add(ValuePattern.ValueProperty);
        using (request.Activate())
        {
            var root = AutomationElement.FromHandle(window);
            var walker = new TreeWalker(Automation.ControlViewCondition);
            var clock = Stopwatch.StartNew();

            IEnumerable<AutomationElement> Children(AutomationElement parent)
            {
                var child = Safe(() => walker.GetFirstChild(parent, request));
                // The budget bounds a huge tree (a browser, an IDE) the way maxNodes bounds the text.
                while (child is not null && clock.Elapsed < budget)
                {
                    yield return child;
                    var current = child;
                    child = Safe(() => walker.GetNextSibling(current, request));
                }
            }

            return UiTreeText.Walk(Children(root), Children, Node, maxDepth, maxNodes);
        }
    }

    private static AutomationElement? Safe(Func<AutomationElement?> step)
    {
        try
        {
            return step();
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or COMException or InvalidOperationException)
        {
            return null; // the element went away mid-walk
        }
    }

    private static UiNode? Node(AutomationElement element)
    {
        try
        {
            var type = element.Cached.ControlType;
            // The title bar (system menu, minimize/maximize/close) is window chrome, not the app's UI.
            if (type == ControlType.TitleBar) return null;
            var role = type?.ProgrammaticName ?? "Element";
            if (role.StartsWith("ControlType.", StringComparison.Ordinal)) role = role["ControlType.".Length..];
            var rect = element.Cached.BoundingRectangle;
            ScreenRect? bounds = rect.IsEmpty || !double.IsFinite(rect.X) || !double.IsFinite(rect.Y)
                                 || !double.IsFinite(rect.Width) || !double.IsFinite(rect.Height)
                ? null
                : new ScreenRect((int)Math.Round(rect.X), (int)Math.Round(rect.Y), (int)Math.Round(rect.Width), (int)Math.Round(rect.Height));
            var value = element.GetCachedPropertyValue(ValuePattern.ValueProperty, true) as string;
            return new UiNode(role, element.Cached.Name, value, element.Cached.AutomationId, bounds);
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or COMException or InvalidOperationException)
        {
            return null;
        }
    }
}

// MARK: - ui_tree

public sealed class UiTreeTool : IToolExecutor
{
    public const string ToolName = "ui_tree";
    public string Name => ToolName;
    public ToolSpec Spec { get; } = new(ToolName,
        "Dump an app's accessibility tree (UI Automation: buttons, text, menus, values, positions) by app/process name or window title — " +
        "the cheap text alternative to screenshots for NATIVE apps (dialogs, editors, tool windows). " +
        "Godot's own window draws its whole UI with its renderer, so its tree is near-empty: use screenshot for the game/editor viewport, " +
        "and this for native panels (open-file dialogs, etc.).",
        """{"type":"object","properties":{"app":{"type":"string","description":"Application/process name or window title (e.g. 'Godot', 'notepad')"},"max_depth":{"type":"integer","description":"How deep to walk (default 3, max 6)"}},"required":["app"]}""");

    private const int MaxNodes = 800;
    private const int MaxChars = 12_000;
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(25);

    public async Task<ToolResult> ExecuteAsync(string arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var args = JsonArgs.Object(arguments);
        var app = JsonArgs.String(args, "app");
        if (string.IsNullOrWhiteSpace(app)) return "Error: app is required.";
        var depth = Math.Clamp(JsonArgs.Int(args, "max_depth", 3), 1, 6);
        if (WindowServices.FindApp(app) is not { } hit)
        {
            return WindowServices.ProcessExists(app)
                ? $"Error: '{app}' is running but has no windows to inspect."
                : $"Error: no running process named '{app}'. Check the name (list_windows / process_list / inspect_process).";
        }

        // A hung app blocks UI Automation calls for a long time, so the walk runs on its own
        // thread and is abandoned (not awaited) past the timeout.
        var work = OnOwnThread(() => UiAutomationTree.Read(hit.Handle, depth, MaxNodes, Budget));
        await Task.WhenAny(work, Task.Delay(Timeout, cancellationToken)).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (!work.IsCompleted)
            return $"Error: '{app}' did not answer UI Automation queries within {(int)Timeout.TotalSeconds} s — it may be hung (inspect_process shows whether it is responding).";
        if (work.IsFaulted)
        {
            var why = (work.Exception?.InnerException?.Message ?? "unknown error").Trim().TrimEnd('.');
            return $"Error: could not read the accessibility tree of '{app}': {TextUtil.Prefix(why, 200)}. An app running as administrator hides its tree from a DSH that isn't.";
        }
        var (text, _) = work.Result;
        if (text.Length == 0)
            return $"'{app}' exposes no UI elements — it almost certainly draws its own pixels (a game engine window). Use screenshot instead.";
        var capped = text.Length > MaxChars ? text[..MaxChars] + "\n… [truncated at 12k chars]" : text;
        return $"Accessibility tree of '{app}' (front window \"{hit.Title}\", depth {depth}):\n{capped}";
    }

    private static Task<T> OnOwnThread<T>(Func<T> work)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                completion.TrySetResult(work());
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "ui_tree walk",
        };
        // UI Automation clients belong in the MTA; an STA caller can deadlock against its own UI.
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        return completion.Task;
    }
}
