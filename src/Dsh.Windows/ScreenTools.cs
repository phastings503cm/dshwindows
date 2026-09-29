using System.Globalization;
using System.IO;
using Dsh.Core;

namespace Dsh.Windows;

// MARK: - screenshot

public sealed class ScreenshotTool : IToolExecutor
{
    public const string ToolName = "screenshot";
    public string Name => ToolName;
    public ToolSpec Spec { get; } = new(ToolName,
        "Capture the screen (or one window, or one area) as a PNG and show it to the model. " +
        "`window` matches an app (process) or window-title substring (e.g. \"Godot\") — the usual way to watch a running game; " +
        "`area` is x,y,w,h in screen pixels. Big captures are downscaled to keep vision tokens sane. " +
        "Pair with process_start to debug GUI apps: start it, screenshot it, read its output, fix, repeat.",
        """{"type":"object","properties":{"window":{"type":"string","description":"Capture the window whose app or title contains this text"},"area":{"type":"string","description":"Region as x,y,width,height (screen pixels)"},"description":{"type":"string","description":"What you are looking for in the shot"}},"required":[]}""");

    public Task<ToolResult> ExecuteAsync(string arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var args = JsonArgs.Object(arguments);
        var window = JsonArgs.String(args, "window");
        var area = JsonArgs.String(args, "area");
        Capture? capture;
        string what;
        var fromScreen = false;
        if (!string.IsNullOrWhiteSpace(window))
        {
            if (WindowServices.Match(window) is not { } hit) return Task.FromResult<ToolResult>(NoWindow(window));
            what = $"{hit.Owner} — {(hit.Title.Length == 0 ? $"(window {hit.Id})" : hit.Title)}";
            if (hit.Minimized) return Task.FromResult<ToolResult>(Minimized(what, hit));
            capture = ScreenCapture.Window(hit, out fromScreen);
        }
        else if (!string.IsNullOrWhiteSpace(area))
        {
            if (!ToolArgs.TryParseArea(area, out var rect))
                return Task.FromResult<ToolResult>("Error: area must be x,y,width,height (e.g. \"100,200,800,600\").");
            var screen = ScreenCapture.VirtualScreen();
            if (rect.Intersect(screen).IsEmpty)
                return Task.FromResult<ToolResult>($"Error: area {rect} is off screen (the screen spans {screen}).");
            what = $"region {rect.X},{rect.Y},{rect.Width},{rect.Height}";
            capture = ScreenCapture.Screen(rect);
        }
        else
        {
            what = "main display";
            capture = ScreenCapture.Screen(ScreenCapture.PrimaryScreen());
        }
        if (capture is null) return Task.FromResult<ToolResult>(CaptureFailed);

        var name = $"screenshot-{FileSafe(what, 30)}.png";
        var image = ImagePipeline.Png(capture, ImagePipeline.LongEdge, name);
        var note = JsonArgs.String(args, "description");
        var output = $"Captured {what} ({image.Data.Length / 1024} KB; {Geometry(capture, image)})"
                     + (string.IsNullOrWhiteSpace(note) ? "" : $" — looking for: {note}")
                     + ". It is shown below; describe what you see before deciding the next step."
                     + (fromScreen ? " (The window would not draw itself off screen — typical for GPU-rendered games — so these are the pixels on screen: anything overlapping it shows too.)" : "");
        return Task.FromResult(new ToolResult(output) { Images = [new MessageAttachment(AttachmentKind.Image, image.Name, image.Data)] });
    }

    /// <summary>Where the pixels came from and how they were scaled, so coordinates read off the
    /// image can be turned back into screen pixels for the mouse tool.</summary>
    internal static string Geometry(Capture capture, PreparedImage image) =>
        image.ScaledFrom is null
            ? $"{capture.Area} on screen, shown 1:1"
            : string.Create(CultureInfo.InvariantCulture, // "× 1.667" on every locale, never "× 1,667"
                $"{capture.Area} on screen, shown at {image.Width}×{image.Height}: screen = {capture.Area.X},{capture.Area.Y} + image × {capture.Width / (double)image.Width:0.###}");

    /// <summary>"Godot — My Game (DEBUG)" → "Godot-My-Game-DEBUG": the attachment name may become a
    /// file name, so only letters, digits, '.', '_' and '-' survive.</summary>
    internal static string FileSafe(string text, int max)
    {
        var chars = text.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' ? c : '-').ToArray();
        var collapsed = string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
        var clipped = collapsed.Length > max ? collapsed[..max].TrimEnd('-', '.') : collapsed;
        return clipped.Length == 0 ? "capture" : clipped;
    }

    internal static string NoWindow(string needle)
    {
        var titles = WindowServices.OnScreenWindows().Where(w => !w.Minimized).Take(12).Select(w => w.Label);
        return $"Error: no window matching \"{needle}\". On screen now:\n{string.Join("\n", titles)}\n"
               + "(list_windows shows everything; process_list shows what you started).";
    }

    internal static string Minimized(string what, WindowInfo window) =>
        $"Error: {what} is minimized, so it has no pixels on screen. focus_app(app: \"{window.Owner}\") restores it; then capture again.";

    internal const string CaptureFailed =
        "Error: capture produced no image. Screen capture needs an unlocked, interactive desktop: it fails while the PC is locked, " +
        "while a UAC prompt is up, and from a service session. A window that just closed can't be captured either (list_windows).";
}

// MARK: - list_windows

public sealed class ListWindowsTool : IToolExecutor
{
    public const string ToolName = "list_windows";
    public string Name => ToolName;
    public ToolSpec Spec { get; } = new(ToolName,
        "List on-screen windows: id, app — title, size, pid. Target of screenshot/window= and the cheap way to see what is open (no screenshot tokens spent).",
        """{"type":"object","properties":{"app":{"type":"string","description":"Only windows of this app (substring)"}}}""");

    public Task<ToolResult> ExecuteAsync(string arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var needle = JsonArgs.String(arguments, "app");
        IEnumerable<WindowInfo> windows = WindowServices.OnScreenWindows();
        if (!string.IsNullOrWhiteSpace(needle))
        {
            windows = windows.Where(w => w.Owner.Contains(needle, StringComparison.OrdinalIgnoreCase)
                                         || w.Title.Contains(needle, StringComparison.OrdinalIgnoreCase));
        }
        return Task.FromResult<ToolResult>(Format(windows.ToList(), needle));
    }

    internal static string Format(IReadOnlyList<WindowInfo> windows, string? needle)
    {
        if (windows.Count == 0)
            return string.IsNullOrWhiteSpace(needle) ? "No visible windows." : $"No window matching \"{needle}\" is on screen.";
        var lines = windows.Take(40).Select(w =>
            $"• {w.Id}  {w.Label}  [{(w.Minimized ? "minimized" : w.Bounds.ToString())}]  pid {w.ProcessId}");
        return "Windows (topmost first):\n" + string.Join("\n", lines) + (windows.Count > 40 ? $"\n… {windows.Count - 40} more" : "");
    }
}

// MARK: - screen_watch

public sealed class ScreenWatchTool : IToolExecutor
{
    public const string ToolName = "screen_watch";
    public string Name => ToolName;
    public ToolSpec Spec { get; } = new(ToolName,
        "Watch a window: capture it every `interval` seconds for up to `duration`, and return only the frames that actually changed — " +
        "a freeze check for a hung game (\"every frame identical\" = wedged), or watching an animation settle. " +
        "Cheaper than repeated screenshots when most frames are identical.",
        """{"type":"object","properties":{"window":{"type":"string","description":"Window/app title substring"},"interval":{"type":"number","description":"Seconds between captures (default 1.5, min 0.5)"},"duration":{"type":"number","description":"Total seconds to watch (default 8, max 30)"}},"required":["window"]}""");

    public async Task<ToolResult> ExecuteAsync(string arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var args = JsonArgs.Object(arguments);
        var needle = JsonArgs.String(args, "window");
        if (string.IsNullOrWhiteSpace(needle) || WindowServices.Match(needle) is not { } hit)
            return $"Error: no window matching \"{needle ?? "?"}\" right now.";
        if (hit.Minimized) return ScreenshotTool.Minimized(hit.Label, hit);
        var (interval, duration, shots) = Schedule(ToolArgs.Number(args, "interval"), ToolArgs.Number(args, "duration"));

        Capture? previous = null;
        var changed = new List<MessageAttachment>();
        var identical = 0;
        for (var index = 0; index < shots; index++)
        {
            var frame = ScreenCapture.Window(hit, out _);
            if (frame is null)
                return $"Error: capture failed at frame {index} for \"{hit.Owner}\" (closed or minimized? list_windows shows what is open).";
            if (previous is not null && frame.SamePixels(previous))
            {
                identical++;
            }
            else
            {
                var image = ImagePipeline.Png(frame, ImagePipeline.WatchLongEdge, $"frame-{index}.png");
                changed.Add(new MessageAttachment(AttachmentKind.Image, image.Name, image.Data));
            }
            previous = frame;
            if (index < shots - 1) await Task.Delay(TimeSpan.FromSeconds(interval), cancellationToken).ConfigureAwait(false);
        }
        var title = $"{hit.Owner} — {hit.Title}";
        if (changed.Count <= 1)
        {
            return $"Watched \"{title}\" for {(int)duration}s: {shots} frames, essentially no pixel change — the window is not redrawing (hung, or idle by design). "
                   + "Its process is up if process_list/inspect_process says so; that points at the game loop, not the window.";
        }
        // First frame (context) + the most recent changes, capped so tokens stay sane.
        var picked = Pick(changed);
        return new ToolResult($"Watched \"{title}\" for {(int)duration}s: {changed.Count} of {shots} frames changed ({identical} identical). Showing {picked.Count} (first + latest).")
        {
            Images = picked,
        };
    }

    /// <summary>Interval (0.5–10 s, default 1.5), duration (at least one interval, at most 30 s,
    /// default 8) and the number of captures (at least 2).</summary>
    internal static (double Interval, double Duration, int Shots) Schedule(double? interval, double? duration)
    {
        var every = Math.Clamp(interval ?? 1.5, 0.5, 10);
        var total = Math.Min(30, Math.Max(every, duration ?? 8));
        return (every, total, Math.Max(2, (int)(total / every)));
    }

    /// <summary>The first changed frame plus up to four of the latest, without repeating one.</summary>
    internal static IReadOnlyList<T> Pick<T>(IReadOnlyList<T> changed) =>
        changed.Count == 0 ? [] : [changed[0], .. changed.Skip(1).TakeLast(4)];
}

// MARK: - view_image

public sealed class ViewImageTool : IToolExecutor
{
    public const string ToolName = "view_image";
    public string Name => ToolName;
    public ToolSpec Spec { get; } = new(ToolName,
        "Show an image FILE to the model — a screenshot the user dropped in, a frame a process saved, an art asset under review. " +
        "Downscaled to ~1600 px long edge so vision tokens stay sane.",
        """{"type":"object","properties":{"file_path":{"type":"string","description":"Image path (png/jpg/gif/bmp/tiff/webp; may be relative to the project)"}},"required":["file_path"]}""");

    internal static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "png", "jpg", "jpeg", "gif", "bmp", "tif", "tiff", "webp", "ico", "heic", "jxr", "wdp",
    };

    /// <summary>Far beyond any real screenshot or texture; decoding more would only exhaust memory.</summary>
    private const long MaxBytes = 64L * 1024 * 1024;

    public Task<ToolResult> ExecuteAsync(string arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var raw = JsonArgs.String(arguments, "file_path");
        if (string.IsNullOrWhiteSpace(raw)) return Task.FromResult<ToolResult>("Error: file_path is required.");
        var (path, _) = context.Policy.Resolve(raw);
        return Task.FromResult(Load(path));
    }

    internal static ToolResult Load(string path)
    {
        var name = Path.GetFileName(path);
        var ext = Path.GetExtension(path).TrimStart('.');
        if (!Extensions.Contains(ext))
            return $"Error: {name} doesn't look like an image ({(ext.Length == 0 ? "no extension" : ext.ToLowerInvariant())}).";
        byte[] data;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return $"Error: cannot read {path}: no such file.";
            if (info.Length > MaxBytes) return $"Error: {name} is too large to show ({info.Length / (1024 * 1024)} MB).";
            data = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"Error: cannot read {path}: {ex.Message}";
        }
        if (ImagePipeline.PrepareFile(data, name) is not { } image)
            return $"Error: cannot read {path}: Windows has no decoder for it, or it is damaged.";
        var size = image.ScaledFrom is { } from
            ? $"{image.Width}×{image.Height}, downscaled from {from.Width}×{from.Height}"
            : $"{image.Width}×{image.Height}";
        return new ToolResult($"Loaded {name} ({image.Data.Length / 1024} KB, {size}).")
        {
            Images = [new MessageAttachment(AttachmentKind.Image, image.Name, image.Data)],
        };
    }
}
