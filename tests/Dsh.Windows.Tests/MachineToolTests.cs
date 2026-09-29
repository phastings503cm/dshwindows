using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Dsh.Core;

namespace Dsh.Windows.Tests;

// MARK: - Registry wiring

/// <summary>Ported from MachineRegistryWiringTests in MachineTests.swift.</summary>
public sealed class MachineRegistryTests
{
    [Fact]
    public void AllCoversTheGatedNames()
    {
        var names = MachineTools.All().Select(t => t.Name).ToList();
        Assert.Equal(
            new HashSet<string> { "screenshot", "list_windows", "screen_watch", "ui_tree", "inspect_process", "mouse", "keyboard", "focus_app", "view_image" },
            names.ToHashSet());
        Assert.Equal(names.Count, names.Distinct().Count());
        // Every machine tool is classified, so the engine gates it.
        foreach (var name in names) Assert.True(ComputerAccessInfo.ForTool(name) is not null, $"{name} must map to observe/control");
        Assert.Equal(ComputerAccess.Control, ComputerAccessInfo.ForTool("keyboard"));
        Assert.Equal(ComputerAccess.Observe, ComputerAccessInfo.ForTool("view_image"));
    }

    [Fact]
    public void EveryToolSpecParametersAreValidJson()
    {
        // Regression (upstream bcc8f01): some servers 400 when tools[i].function.parameters is not a
        // JSON-object schema, so every shipped spec must parse as one.
        foreach (var tool in MachineTools.All())
        {
            var spec = tool.Spec;
            Assert.Equal(tool.Name, spec.Name);
            Assert.False(string.IsNullOrWhiteSpace(spec.Description), $"{spec.Name}: description");
            using var doc = JsonDocument.Parse(spec.Parameters);
            var root = doc.RootElement;
            Assert.Equal(JsonValueKind.Object, root.ValueKind);
            Assert.Equal("object", root.GetProperty("type").GetString());
            if (!root.TryGetProperty("properties", out var properties)) continue;
            Assert.Equal(JsonValueKind.Object, properties.ValueKind);
            foreach (var property in properties.EnumerateObject())
            {
                Assert.Equal(JsonValueKind.Object, property.Value.ValueKind);
                Assert.True(property.Value.TryGetProperty("type", out _), $"{spec.Name}.{property.Name} has a type");
            }
            if (root.TryGetProperty("required", out var required))
            {
                foreach (var name in required.EnumerateArray())
                    Assert.True(properties.TryGetProperty(name.GetString()!, out _), $"{spec.Name}: required {name} is declared");
            }
        }
    }

    [Fact]
    public void TheRegistryAcceptsThemNextToTheStandardTools()
    {
        var registry = ToolRegistry.Standard().Adding(MachineTools.All());
        Assert.Equal(registry.Names.Count, registry.Names.Distinct().Count());
        Assert.NotNull(registry.Tool("screenshot"));
        Assert.NotNull(registry.Tool("read_file"));
    }
}

// MARK: - Argument validation (nothing is captured, clicked or typed)

public sealed class MachineArgumentTests
{
    [Fact]
    public async Task ScreenshotRejectsBadArea()
    {
        var output = await Tools.Output(Tools.Named("screenshot"), """{"area":"10,20,30"}""");
        Assert.StartsWith("Error:", output);
        Assert.Contains("x,y,width,height", output);
    }

    [Fact]
    public async Task ScreenshotRejectsAnOffScreenArea()
    {
        var output = await Tools.Output(Tools.Named("screenshot"), """{"area":"-900000,-900000,10,10"}""");
        Assert.StartsWith("Error:", output);
        Assert.Contains("off screen", output);
    }

    [Fact]
    public async Task ScreenshotNoWindowMatchListsAlternatives()
    {
        var output = await Tools.Output(Tools.Named("screenshot"), """{"window":"zzz-definitely-not-a-window"}""");
        Assert.Contains("no window matching", output);
        Assert.Contains("list_windows", output);
    }

    [Fact]
    public async Task ScreenWatchNeedsAWindow()
    {
        Assert.StartsWith("Error: no window matching \"?\"", await Tools.Output(Tools.Named("screen_watch"), "{}"));
        Assert.Contains("zzz-nope", await Tools.Output(Tools.Named("screen_watch"), """{"window":"zzz-nope"}"""));
    }

    [Fact]
    public async Task UiTreeNeedsARunningApp()
    {
        Assert.Equal("Error: app is required.", await Tools.Output(Tools.Named("ui_tree"), "{}"));
        var output = await Tools.Output(Tools.Named("ui_tree"), """{"app":"zzz-no-such-app-here"}""");
        Assert.StartsWith("Error: no running process named 'zzz-no-such-app-here'", output);
    }

    [Fact]
    public async Task FocusAppNeedsARunningApp()
    {
        Assert.Equal("Error: app is required.", await Tools.Output(Tools.Named("focus_app"), """{"app":""}"""));
        var output = await Tools.Output(Tools.Named("focus_app"), """{"app":"zzz-no-such-app-here"}""");
        Assert.StartsWith("Error: 'zzz-no-such-app-here' is not running or not focusable", output);
        Assert.Contains("Start-Process", output);
    }

    [Fact]
    public async Task MouseValidatesCoordinatesAndButton()
    {
        var mouse = Tools.Named("mouse");
        Assert.StartsWith("Error: x and y are required", await Tools.Output(mouse, """{"x":10}"""));
        Assert.StartsWith("Error: button must be", await Tools.Output(mouse, """{"x":10,"y":10,"button":"thumb"}"""));
        Assert.Contains("off screen", await Tools.Output(mouse, """{"x":-900000,"y":5}"""));
        Assert.Contains("off screen", await Tools.Output(mouse, """{"x":5,"y":5,"to_x":900000,"to_y":5}"""));
    }

    [Fact]
    public async Task KeyboardValidatesBeforeTouchingAnything()
    {
        var keyboard = Tools.Named("keyboard");
        Assert.Equal("Error: give text and/or keys.", await Tools.Output(keyboard, "{}"));
        var unknown = await Tools.Output(keyboard, """{"keys":["ctrl+s","hyper+q"]}""");
        Assert.StartsWith("Error: unknown key 'hyper+q'", unknown);
        Assert.Contains("f1–f24", unknown);
        Assert.StartsWith("Error: 'zzz-no-such-app-here' is not running",
                          await Tools.Output(keyboard, """{"app":"zzz-no-such-app-here","text":"hi"}"""));
    }

    [Fact]
    public async Task InspectProcessNeedsAMatchOrPid()
    {
        var inspect = Tools.Named("inspect_process");
        Assert.Equal("Error: give a match string or a pid.", await Tools.Output(inspect, "{}"));
        Assert.Equal("No process with pid 2147483000.", await Tools.Output(inspect, """{"pid":2147483000}"""));
        Assert.Equal("No process command-line matching \"zzz-no-such-process-qq\".",
                     await Tools.Output(inspect, """{"match":"zzz-no-such-process-qq"}"""));
    }

    [Fact]
    public async Task ViewImageRejectsWhatIsNotAnImage()
    {
        using var temp = new TempFolder();
        File.WriteAllText(temp.File("notes.txt"), "hello");
        File.WriteAllText(temp.File("fake.png"), "not really a png");
        var view = Tools.Named("view_image");
        Assert.Equal("Error: file_path is required.", await Tools.Output(view, "{}"));
        Assert.Equal("Error: notes.txt doesn't look like an image (txt).",
                     (await Tools.Run(view, """{"file_path":"notes.txt"}""", temp.Path)).Output);
        Assert.Equal("Error: README doesn't look like an image (no extension).",
                     (await Tools.Run(view, """{"file_path":"README"}""", temp.Path)).Output);
        Assert.StartsWith("Error: cannot read", (await Tools.Run(view, """{"file_path":"missing.png"}""", temp.Path)).Output);
        var fake = await Tools.Run(view, """{"file_path":"fake.png"}""", temp.Path);
        Assert.StartsWith("Error: cannot read", fake.Output);
        Assert.Empty(fake.Images);
    }
}

// MARK: - view_image (real decoding)

public sealed class ViewImageTests
{
    [Fact]
    public async Task DownscalesToTheLongEdge()
    {
        using var temp = new TempFolder();
        var big = TestImages.Png(3000, 1500);
        File.WriteAllBytes(temp.File("big.png"), big);
        var result = await Tools.Run(Tools.Named("view_image"), """{"file_path":"big.png"}""", temp.Path);
        Assert.StartsWith("Loaded big.png (", result.Output);
        Assert.Contains("1600×800, downscaled from 3000×1500", result.Output);
        var image = Assert.Single(result.Images);
        Assert.Equal(AttachmentKind.Image, image.Kind);
        Assert.Equal("image/png", image.Mime);
        var dims = ImageSize.Dimensions(image.Data);
        Assert.NotNull(dims);
        Assert.Equal(1600, Math.Max(dims.Value.Width, dims.Value.Height));
        // Fewer pixels, still compressed. (Not "fewer bytes than the original": a synthetic pattern
        // compresses so well that its resampled copy can come out larger on Windows' PNG encoder.)
        Assert.True(image.Data.Length < dims.Value.Width * dims.Value.Height * 4, "the result is a compressed image");
    }

    [Fact]
    public async Task SmallPngAndJpegTravelUntouched()
    {
        using var temp = new TempFolder();
        var png = TestImages.Png(320, 200);
        File.WriteAllBytes(temp.File("small.png"), png);
        var result = await Tools.Run(Tools.Named("view_image"), $$"""{"file_path":{{Tools.Q(temp.File("small.png"))}}}""");
        Assert.Equal(png, Assert.Single(result.Images).Data);
        Assert.Equal("small.png", result.Images[0].Name);

        var jpeg = TestImages.Jpeg(2400, 1200);
        File.WriteAllBytes(temp.File("photo.jpeg"), jpeg);
        var scaled = await Tools.Run(Tools.Named("view_image"), """{"file_path":"photo.jpeg"}""", temp.Path);
        var image = Assert.Single(scaled.Images);
        Assert.Equal("photo.jpg", image.Name); // still a JPEG, not a bloated PNG
        Assert.Equal(ImagePipeline.ImageKind.Jpeg, ImagePipeline.Sniff(image.Data));
        Assert.Equal((1600, 800), ImageSize.Dimensions(image.Data));
    }

    [Fact]
    public async Task OtherFormatsBecomePng()
    {
        using var temp = new TempFolder();
        File.WriteAllBytes(temp.File("shot.bmp"), TestImages.Bmp(64, 48));
        var result = await Tools.Run(Tools.Named("view_image"), """{"file_path":"shot.bmp"}""", temp.Path);
        var image = Assert.Single(result.Images);
        Assert.Equal("shot.png", image.Name);
        Assert.Equal((64, 48), ImageSize.Dimensions(image.Data));
    }
}

// MARK: - The real desktop

/// <summary>Ported from WindowServicesTests in MachineTests.swift, against real windows: a WPF test
/// window this process opens. On a machine without an interactive desktop the capture tests accept
/// an explanatory error instead of an image.</summary>
[Collection(DesktopCollection.Name)]
public sealed class DesktopTests
{
    [Fact]
    public void OnScreenWindowsSeesTheTestWindow()
    {
        using var window = new TestWindow();
        var all = WindowServices.OnScreenWindows(includeChrome: true);
        Assert.Contains(all, w => w.Id > 0);
        var mine = Assert.Single(WindowServices.OnScreenWindows(), w => w.Title == window.Title);
        Assert.Equal(window.Handle, mine.Handle);
        Assert.Equal(Environment.ProcessId, mine.ProcessId);
        Assert.Equal(Process.GetCurrentProcess().ProcessName, mine.Owner);
        Assert.False(mine.Chrome);
        Assert.True(mine.Bounds.Width > 100 && mine.Bounds.Height > 100, mine.Bounds.ToString());
        Assert.True(WindowServices.OnScreenWindows().All(w => !w.Chrome), "chrome filtered out by default");
        Assert.Equal(window.Handle, WindowServices.Match(window.Title)?.Handle);
        Assert.Equal(window.Handle, WindowServices.FindApp(window.Title)?.Handle);
    }

    [Fact]
    public async Task ListWindowsShowsTheTestWindow()
    {
        using var window = new TestWindow();
        var output = await Tools.Output(Tools.Named("list_windows"), $$"""{"app":{{Tools.Q(window.Title)}}}""");
        Assert.StartsWith("Windows (topmost first):", output);
        Assert.Contains($"— {window.Title}", output);
        Assert.Contains($"pid {Environment.ProcessId}", output);
        Assert.Equal("No window matching \"zzz-nope\" is on screen.", await Tools.Output(Tools.Named("list_windows"), """{"app":"zzz-nope"}"""));
    }

    [Fact]
    public async Task ScreenshotFullDisplayProducesImageOrExplains()
    {
        var result = await Tools.Run(Tools.Named("screenshot"), """{"description":"sanity"}""");
        if (result.Images.Count == 0)
        {
            Assert.StartsWith("Error", result.Output); // no image ⇒ must explain
            return;
        }
        Assert.StartsWith("Captured main display (", result.Output);
        Assert.Contains("looking for: sanity", result.Output);
        var image = result.Images[0];
        Assert.Equal(AttachmentKind.Image, image.Kind);
        Assert.Equal("image/png", image.Mime);
        Assert.True(image.Data.Length > 1024);
        var dims = ImageSize.Dimensions(image.Data)!.Value;
        Assert.True(Math.Max(dims.Width, dims.Height) <= ImagePipeline.LongEdge);
    }

    [Fact]
    public async Task ScreenshotOfAnAreaIsExactlyThatArea()
    {
        var result = await Tools.Run(Tools.Named("screenshot"), """{"area":"0, 0, 200, 100"}""");
        if (result.Images.Count == 0)
        {
            Assert.StartsWith("Error", result.Output);
            return;
        }
        Assert.StartsWith("Captured region 0,0,200,100 (", result.Output);
        Assert.Contains("shown 1:1", result.Output);
        Assert.Equal((200, 100), ImageSize.Dimensions(result.Images[0].Data));
    }

    [Fact]
    public async Task ScreenshotOfTheTestWindow()
    {
        using var window = new TestWindow();
        var result = await Tools.Run(Tools.Named("screenshot"), $$"""{"window":{{Tools.Q(window.Title)}}}""");
        var image = Assert.Single(result.Images);
        Assert.StartsWith("Captured ", result.Output);
        Assert.Contains(window.Title, result.Output);
        var bounds = WindowServices.Match(window.Title)!.Bounds;
        Assert.Equal((bounds.Width, bounds.Height), ImageSize.Dimensions(image.Data));
    }

    [Fact]
    public async Task ScreenWatchCallsAStillWindowUnchanged()
    {
        using var window = new TestWindow(withControls: false);
        var result = await Tools.Run(Tools.Named("screen_watch"),
                                     $$"""{"window":{{Tools.Q(window.Title)}},"interval":0.5,"duration":1.5}""");
        Assert.Contains("frames, essentially no pixel change", result.Output);
        Assert.Empty(result.Images);
    }

    [Fact]
    public async Task ScreenWatchReturnsTheFramesOfAnAnimation()
    {
        using var window = new TestWindow(withControls: false, animate: true);
        var result = await Tools.Run(Tools.Named("screen_watch"),
                                     $$"""{"window":{{Tools.Q(window.Title)}},"interval":0.5,"duration":3}""");
        Assert.Contains("frames changed", result.Output);
        Assert.InRange(result.Images.Count, 2, 5);
        Assert.Equal("frame-0.png", result.Images[0].Name);
        Assert.Equal(result.Images.Count, result.Images.Select(i => i.Name).Distinct().Count());
    }

    [Fact]
    public async Task UiTreeListsTheTestWindowControls()
    {
        using var window = new TestWindow();
        var output = await Tools.Output(Tools.Named("ui_tree"), $$"""{"app":{{Tools.Q(window.Title)}},"max_depth":4}""");
        Assert.True(output.StartsWith($"Accessibility tree of '{window.Title}' (front window", StringComparison.Ordinal), output);
        Assert.Contains("Button \"Press me\"", output);
        Assert.Contains("#pressMe", output);
        Assert.Contains("hello tree", output);
        Assert.DoesNotContain("TitleBar", output);
    }

    [Fact]
    public async Task InspectProcessFindsThisTestProcess()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var pid = Environment.ProcessId;
        var output = await Tools.Output(Tools.Named("inspect_process"), $$"""{"pid":{{pid}}}""");
        Assert.StartsWith($"pid {pid}  ", output);
        Assert.Contains("cpu ", output);
        Assert.Contains("threads", output);
        Assert.Contains("command: ", output);
        Assert.Contains("--- sockets ---", output);
        Assert.True(output.Contains($"TCP  127.0.0.1:{port}  listening", StringComparison.Ordinal), output);

        var name = Process.GetCurrentProcess().ProcessName;
        var listing = await Tools.Output(Tools.Named("inspect_process"), $$"""{"match":{{Tools.Q(name)}}}""");
        Assert.StartsWith("PID ", listing);
        Assert.Contains($"\n{pid} ", listing);
    }

    [Fact]
    public async Task KeyboardRefusesDshItself()
    {
        // The test window belongs to this process, which is what "DSH itself" means at run time.
        using var window = new TestWindow();
        var output = await Tools.Output(Tools.Named("keyboard"), $$"""{"app":{{Tools.Q(window.Title)}},"text":"never typed"}""");
        Assert.Equal(TerminalGuard.Refusal(window.Title), output);
        Assert.Equal("hello tree", window.BoxText()); // nothing was typed
    }

    [Fact]
    public async Task SynthesizedTypingReachesAWindow()
    {
        // The keyboard tool refuses this process's own windows (here they are "DSH itself"), so the
        // same input path is driven directly: ctrl+a, then Unicode text including a surrogate pair.
        using var window = new TestWindow();
        Assert.StartsWith("Focused", await Tools.Output(Tools.Named("focus_app"), $$"""{"app":{{Tools.Q(window.Title)}}}"""));
        window.FocusBox();
        if (!WindowFocus.IsForeground(window.Handle)) return; // something else holds the foreground
        Assert.True(InputSender.Send(KeyboardTool.ComboInputs(KeyNames.Parse("ctrl+a")!.Value)));
        const string text = "héllo wörld 😀 ok";
        var units = KeyNames.TextUnits(text);
        for (var start = 0; start < units.Count;)
        {
            var end = KeyboardTool.ChunkEnd(units, start);
            Assert.True(InputSender.Send(KeyboardTool.TextInputs(units, start, end)));
            start = end;
        }
        var clock = Stopwatch.StartNew();
        while (window.BoxText() != text && clock.Elapsed < TimeSpan.FromSeconds(3)) await Task.Delay(50);
        Assert.Equal(text, window.BoxText());
    }

    [Fact]
    public async Task MouseDragPressesAtTheStartAndReleasesAtTheEnd()
    {
        using var window = new TestWindow();
        await Tools.Output(Tools.Named("focus_app"), $$"""{"app":{{Tools.Q(window.Title)}}}""");
        var from = window.PadPoint(0.15, 0.5);
        var to = window.PadPoint(0.85, 0.5);
        if (WindowServices.OnScreenWindows(includeChrome: true).FirstOrDefault(w => w.Bounds.Contains(from.X, from.Y))?.Handle != window.Handle)
            return; // something else covers the pad
        var output = await Tools.Output(Tools.Named("mouse"), $$"""{"x":{{from.X}},"y":{{from.Y}},"to_x":{{to.X}},"to_y":{{to.Y}}}""");
        Assert.Equal($"Dragged ({from.X},{from.Y}) → ({to.X},{to.Y}).", output);
        var clock = Stopwatch.StartNew();
        while (window.PadEvents().Count < 2 && clock.Elapsed < TimeSpan.FromSeconds(3)) await Task.Delay(50);
        var events = window.PadEvents();
        Assert.Equal(["down", "up"], events.Select(e => e.Kind));
        Assert.InRange(events[0].X, from.X - 2, from.X + 2);
        Assert.InRange(events[1].X, to.X - 2, to.X + 2);
        Assert.InRange(events[1].Y, to.Y - 2, to.Y + 2);
    }

    [Fact]
    public async Task FocusAppAndMouseClickTheTestWindow()
    {
        using var window = new TestWindow();
        var focus = await Tools.Output(Tools.Named("focus_app"), $$"""{"app":{{Tools.Q(window.Title)}}}""");
        Assert.StartsWith($"Focused {window.Title} (", focus);

        var (x, y) = window.ButtonCenter();
        // Only click when the button is really what is under that point.
        var under = WindowServices.OnScreenWindows(includeChrome: true).FirstOrDefault(w => w.Bounds.Contains(x, y));
        if (under?.Handle != window.Handle) return;
        var click = await Tools.Output(Tools.Named("mouse"), $$"""{"x":{{x}},"y":{{y}}}""");
        Assert.Equal($"Clicked left at ({x},{y}). Screenshot to verify.", click);
        var clock = Stopwatch.StartNew();
        while (window.Clicks == 0 && clock.Elapsed < TimeSpan.FromSeconds(3)) await Task.Delay(50);
        Assert.Equal(1, window.Clicks);
    }
}
