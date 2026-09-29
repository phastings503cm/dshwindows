using System.Net;

namespace Dsh.Windows.Tests;

// MARK: - Geometry and images

public sealed class GeometryTests
{
    [Theory]
    [InlineData(3000, 1500, 1600, 1600, 800)]
    [InlineData(1500, 3000, 1600, 800, 1600)]
    [InlineData(1600, 900, 1600, 1600, 900)]   // already fits
    [InlineData(800, 600, 1600, 800, 600)]
    [InlineData(3840, 2160, 1280, 1280, 720)]
    [InlineData(5000, 3, 1600, 1600, 1)]       // never collapses to zero
    public void FitKeepsAspectWithinTheLongEdge(int w, int h, int edge, int ew, int eh)
    {
        Assert.Equal((ew, eh), ImagePipeline.Fit(w, h, edge));
    }

    [Fact]
    public void AreasParseAsXYWidthHeight()
    {
        Assert.True(ToolArgs.TryParseArea("100,200,800,600", out var a));
        Assert.Equal(new ScreenRect(100, 200, 800, 600), a);
        Assert.True(ToolArgs.TryParseArea(" -1920, 0 , 640.4, 480.6 ", out var b));
        Assert.Equal(new ScreenRect(-1920, 0, 640, 481), b); // other monitors are negative; fractions round
    }

    [Theory]
    [InlineData("10,20,30")]
    [InlineData("10,20,30,40,50")]
    [InlineData("a,b,c,d")]
    [InlineData("0,0,0,100")]      // empty
    [InlineData("0,0,100,-5")]
    [InlineData("")]
    [InlineData("0,0,1e9,100")]
    public void BadAreasAreRejected(string text)
    {
        Assert.False(ToolArgs.TryParseArea(text, out _));
    }

    [Fact]
    public void RectangleMath()
    {
        var screen = new ScreenRect(-1920, 0, 3840, 1080);
        Assert.True(screen.Contains(-1920, 0));
        Assert.True(screen.Contains(1919, 1079));
        Assert.False(screen.Contains(1920, 0));
        Assert.Equal(new ScreenRect(0, 0, 100, 50), new ScreenRect(-50, -50, 150, 100).Intersect(new ScreenRect(0, 0, 1920, 1080)));
        Assert.True(new ScreenRect(5000, 5000, 10, 10).Intersect(screen).IsEmpty);
        Assert.Equal("800×600 at 100,-200", new ScreenRect(100, -200, 800, 600).ToString());
    }

    [Fact]
    public void BlankDetectionIgnoresTheUnusedByte()
    {
        Assert.True(ScreenCapture.IsBlank(new byte[] { 0, 0, 0, 0xFF, 0, 0, 0, 0x00 }));
        Assert.False(ScreenCapture.IsBlank(new byte[] { 0, 0, 0, 0xFF, 0, 1, 0, 0xFF }));
    }

    [Fact]
    public void CropCutsTheVisibleFrameOutOfAWindowPrint()
    {
        // A 4×3 print at (10,10) whose pixel value is its own index; keep the middle 2×1 at (11,11).
        var pixels = new byte[4 * 3 * 4];
        for (var i = 0; i < 12; i++) pixels[i * 4] = (byte)i;
        var capture = new Capture(pixels, 4, 3, new ScreenRect(10, 10, 4, 3));
        var part = ScreenCapture.Crop(capture, new ScreenRect(11, 11, 2, 1));
        Assert.Equal((2, 1), (part.Width, part.Height));
        Assert.Equal(new ScreenRect(11, 11, 2, 1), part.Area);
        Assert.Equal(5, part.Pixels[0]);
        Assert.Equal(6, part.Pixels[4]);
        Assert.Same(capture, ScreenCapture.Crop(capture, new ScreenRect(0, 0, 100, 100)));
    }

    [Fact]
    public void FramesCompareByPixels()
    {
        var a = new Capture([1, 2, 3, 255], 1, 1, default);
        Assert.True(a.SamePixels(new Capture([1, 2, 3, 255], 1, 1, new ScreenRect(5, 5, 1, 1))));
        Assert.False(a.SamePixels(new Capture([1, 2, 4, 255], 1, 1, default)));
        Assert.False(a.SamePixels(new Capture([1, 2, 3, 255, 1, 2, 3, 255], 2, 1, default)));
    }

    [Fact]
    public void ContainerSniffing()
    {
        Assert.Equal(ImagePipeline.ImageKind.Png, ImagePipeline.Sniff(TestImages.Png(4, 4)));
        Assert.Equal(ImagePipeline.ImageKind.Jpeg, ImagePipeline.Sniff(TestImages.Jpeg(4, 4)));
        Assert.Equal(ImagePipeline.ImageKind.Other, ImagePipeline.Sniff(TestImages.Bmp(4, 4)));
        Assert.Equal(ImagePipeline.ImageKind.Other, ImagePipeline.Sniff([1, 2]));
    }

    [Fact]
    public void AttachmentNamesAreFileSafe()
    {
        Assert.Equal("Godot_v4.3-stable_win64-My-Game", ScreenshotTool.FileSafe("Godot_v4.3-stable_win64 — My Game (DEBUG)", 31));
        Assert.Equal("main-display", ScreenshotTool.FileSafe("main display", 30));
        Assert.Equal("region-10-20-300-200", ScreenshotTool.FileSafe("region 10,20,300,200", 30));
        Assert.Equal("capture", ScreenshotTool.FileSafe("—", 30));
    }

    [Fact]
    public void ScreenshotGeometryTellsHowToMapBack()
    {
        var capture = new Capture(new byte[4], 3200, 1800, new ScreenRect(-3200, 0, 3200, 1800));
        var scaled = new PreparedImage([], "x.png", 1600, 900) { ScaledFrom = (3200, 1800) };
        Assert.Equal("3200×1800 at -3200,0 on screen, shown at 1600×900: screen = -3200,0 + image × 2",
                     ScreenshotTool.Geometry(capture, scaled));
        Assert.Equal("3200×1800 at -3200,0 on screen, shown 1:1",
                     ScreenshotTool.Geometry(capture, new PreparedImage([], "x.png", 3200, 1800)));
    }
}

// MARK: - Keys

public sealed class KeyNameTests
{
    [Fact]
    public void ComboParsing()
    {
        var c = KeyNames.Parse("cmd+shift+s")!.Value;
        Assert.True(c.Ctrl); // cmd is the macOS spelling of ctrl
        Assert.True(c.Shift);
        Assert.False(c.Alt);
        Assert.False(c.Win);
        Assert.Equal("s", c.Key);
        Assert.Equal(0x53, c.VirtualKey);

        Assert.Equal("return", KeyNames.Parse("return")!.Value.Key);
        Assert.Equal(0x74, KeyNames.Parse("F5")!.Value.VirtualKey);
        Assert.Equal(0x0D, KeyNames.Parse("enter")!.Value.VirtualKey);
        var comma = KeyNames.Parse("ctrl,s")!.Value; // the upstream description's spelling
        Assert.True(comma.Ctrl);
        Assert.Equal("s", comma.Key);
        var alt = KeyNames.Parse("Alt + F4")!.Value;
        Assert.True(alt.Alt);
        Assert.Equal(0x73, alt.VirtualKey);
        Assert.True(KeyNames.Parse("win+r")!.Value.Win);
        Assert.Equal(0xBB, KeyNames.Parse("ctrl++")!.Value.VirtualKey);
        Assert.Equal(0xBC, KeyNames.Parse("shift+,")!.Value.VirtualKey);
        Assert.Equal(0xBB, KeyNames.Parse("+")!.Value.VirtualKey);

        Assert.True(KeyNames.Codes.ContainsKey("return"));
        Assert.Equal((ushort)0x70, KeyNames.Codes["f1"]);
        Assert.Equal((ushort)0x7B, KeyNames.Codes["f12"]);
        Assert.Equal((ushort)'S', KeyNames.Codes["s"]);
        Assert.Equal((ushort)'0', KeyNames.Codes["0"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("hyper+s")]    // unknown modifier
    [InlineData("f25")]
    [InlineData("ctrl+banana")]
    public void UnknownKeysAreRejected(string combo)
    {
        Assert.Null(KeyNames.Parse(combo));
    }

    [Fact]
    public void ModifiersWrapTheKeyAndReleaseInReverse()
    {
        var sequence = KeyNames.Sequence(KeyNames.Parse("ctrl+shift+s")!.Value);
        Assert.Equal(
            [(KeyNames.VkControl, false), (KeyNames.VkShift, false), ((ushort)'S', false), ((ushort)'S', true),
             (KeyNames.VkShift, true), (KeyNames.VkControl, true)],
            sequence);
        Assert.Equal([((ushort)0x1B, false), ((ushort)0x1B, true)], KeyNames.Sequence(KeyNames.Parse("esc")!.Value));
    }

    [Fact]
    public void NavigationKeysAreExtended()
    {
        foreach (var name in new[] { "up", "down", "left", "right", "home", "end", "pgup", "pgdn", "insert", "delete" })
            Assert.True(KeyNames.IsExtended(KeyNames.Codes[name]), name);
        foreach (var name in new[] { "a", "enter", "f5", "esc", "space", "shift" })
            Assert.False(KeyNames.IsExtended(KeyNames.Codes[name]), name);
    }

    [Fact]
    public void TextTypesLineBreaksAndTabsAsKeys()
    {
        var expected = new (ushort, bool)[]
        {
            ('a', false), (KeyNames.VkReturn, true), ('b', false), (KeyNames.VkTab, true), ('c', false), (KeyNames.VkReturn, true),
            ('é', false),
        };
        Assert.Equal(expected, KeyNames.TextUnits("a\r\nb\tc\né")); // a CRLF pair is one Enter
        Assert.Equal(new (ushort, bool)[] { (KeyNames.VkReturn, true) }, KeyNames.TextUnits("\r"));
        // A surrogate pair stays two Unicode units.
        Assert.Equal(2, KeyNames.TextUnits("😀").Count);
    }

    [Fact]
    public void TypingBatchesNeverSplitASurrogatePair()
    {
        var units = KeyNames.TextUnits(new string('a', 19) + "😀b");
        Assert.Equal(22, units.Count);
        Assert.Equal(21, KeyboardTool.ChunkEnd(units, 0)); // 20 would cut the emoji in half
        Assert.Equal(22, KeyboardTool.ChunkEnd(units, 21));
        Assert.Equal(20, KeyboardTool.ChunkEnd(KeyNames.TextUnits(new string('a', 50)), 0));
    }

    [Fact]
    public void InputEventsForTextAndCombos()
    {
        var text = KeyboardTool.TextInputs(KeyNames.TextUnits("é\n"), 0, 2);
        Assert.Equal(4, text.Count);
        Assert.Equal(Native.INPUT_KEYBOARD, text[0].type);
        Assert.Equal((ushort)0, text[0].u.ki.wVk);
        Assert.Equal('é', (char)text[0].u.ki.wScan);
        Assert.Equal(Native.KEYEVENTF_UNICODE, text[0].u.ki.dwFlags);
        Assert.Equal(Native.KEYEVENTF_UNICODE | Native.KEYEVENTF_KEYUP, text[1].u.ki.dwFlags);
        Assert.Equal(KeyNames.VkReturn, text[2].u.ki.wVk); // the newline is the Enter key
        Assert.Equal(0u, text[2].u.ki.dwFlags & Native.KEYEVENTF_UNICODE);

        var combo = KeyboardTool.ComboInputs(KeyNames.Parse("ctrl+up")!.Value);
        Assert.Equal([KeyNames.VkControl, (ushort)0x26, (ushort)0x26, KeyNames.VkControl], combo.Select(i => i.u.ki.wVk));
        Assert.Equal(Native.KEYEVENTF_EXTENDEDKEY, combo[1].u.ki.dwFlags); // arrow keys are extended
        Assert.Equal(Native.KEYEVENTF_KEYUP, combo[3].u.ki.dwFlags);
    }

    [Theory]
    [InlineData(0, 0, 1920, 0)]
    [InlineData(1919, 0, 1920, 65535)]
    [InlineData(-1920, -1920, 3840, 0)]
    [InlineData(1919, -1920, 3840, 65535)]
    [InlineData(0, -1920, 3840, 32776)]
    public void AbsoluteMouseCoordinatesSpanTheVirtualScreen(int value, int origin, int extent, int expected)
    {
        Assert.Equal(expected, InputSender.Normalize(value, origin, extent));
    }
}

// MARK: - Terminal guard

public sealed class TerminalGuardTests
{
    [Theory]
    [InlineData(@"C:\Program Files\WindowsApps\Microsoft.WindowsTerminal_1.21\WindowsTerminal.exe")]
    [InlineData(@"C:\Windows\System32\cmd.exe")]
    [InlineData(@"C:\Windows\System32\conhost.exe")]
    [InlineData(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe")]
    [InlineData(@"C:\Program Files\PowerShell\7\pwsh.exe")]
    [InlineData(@"C:\Program Files\WezTerm\wezterm-gui.exe")]
    [InlineData(@"C:\Program Files\Alacritty\alacritty.exe")]
    [InlineData(@"C:\Program Files\Git\usr\bin\mintty.exe")]
    [InlineData(@"C:\Users\me\AppData\Local\Programs\DSH\DSH.exe")]
    [InlineData(@"C:\Windows\explorer.exe")]                                        // Run box, address bar
    [InlineData(@"C:\Windows\SystemApps\MicrosoftWindows.Client.CBS_cw5n1h2txyewy\SearchHost.exe")]
    [InlineData("PWSH.EXE")]
    public void TerminalsLaunchersAndDshAreBlocked(string path)
    {
        Assert.True(TerminalGuard.IsBlocked(path, "SomeWindowClass"));
    }

    [Theory]
    [InlineData(@"C:\Windows\System32\notepad.exe")]
    [InlineData(@"C:\Tools\Godot\Godot_v4.3-stable_win64.exe")]
    [InlineData(@"C:\Program Files\Microsoft VS Code\Code.exe")]
    [InlineData("")]
    [InlineData(null)]
    public void OrdinaryAppsAreAllowed(string? path)
    {
        Assert.False(TerminalGuard.IsBlocked(path, "Chrome_WidgetWin_1", processId: 4242_4242));
    }

    [Fact]
    public void ConsoleWindowsAreBlockedWhateverRunsInThem()
    {
        // A classic console window reports its client (python.exe) as the owner.
        Assert.True(TerminalGuard.IsBlocked(@"C:\Python312\python.exe", "ConsoleWindowClass"));
        Assert.True(TerminalGuard.IsBlocked(null, "CASCADIA_HOSTING_WINDOW_CLASS"));
    }

    [Fact]
    public void ThisProcessCountsAsDshItself()
    {
        Assert.True(TerminalGuard.IsBlocked(@"C:\x\testhost.exe", "HwndWrapper", Environment.ProcessId));
    }

    [Fact]
    public void PasteClicksOnlyCareAboutTerminals()
    {
        Assert.True(TerminalGuard.IsTerminal(@"C:\Windows\System32\cmd.exe", null));
        Assert.True(TerminalGuard.IsTerminal(null, "ConsoleWindowClass"));
        Assert.False(TerminalGuard.IsTerminal(@"C:\Windows\explorer.exe", "CabinetWClass"));
        Assert.False(TerminalGuard.IsTerminal(@"C:\x\DSH.exe", "HwndWrapper"));
    }

    [Fact]
    public void RefusalAdvice()
    {
        Assert.Contains("process_start", TerminalGuard.Refusal("Windows Terminal"));
        Assert.StartsWith("Error:", TerminalGuard.Refusal("pwsh"));
        Assert.StartsWith("Error:", TerminalGuard.ClickRefusal("cmd", "right"));
        Assert.Equal("cmd", TerminalGuard.ExecutableName(@"C:\Windows\System32\cmd.exe"));
    }
}

// MARK: - Windows (matching over a fixed list)

public sealed class WindowMatchingTests
{
    private static WindowInfo W(long id, string owner, string title, bool chrome = false, bool minimized = false) =>
        new(id, owner, title, (int)id, minimized ? default : new ScreenRect(0, 0, 800, 600)) { Chrome = chrome, Minimized = minimized };

    private static readonly IReadOnlyList<WindowInfo> Desktop =
    [
        W(1, "ShellExperienceHost", "Godot overlay", chrome: true),
        W(2, "Godot_v4.3-stable_win64", "My Game (DEBUG)"),
        W(3, "Godot_v4.3-stable_win64", "My Game - Godot Engine"),
        W(4, "notepad", "notes.txt - Notepad", minimized: true),
        W(5, "notepad", "todo.txt - Notepad"),
        W(6, "explorer", "", chrome: true),
    ];

    [Fact]
    public void MatchPrefersOrdinaryWindowsInZOrder()
    {
        Assert.Equal(2, WindowServices.Match(Desktop, "godot")!.Id); // topmost ordinary, not the chrome above it
        Assert.Equal(3, WindowServices.Match(Desktop, "Godot Engine")!.Id);
        Assert.Equal(5, WindowServices.Match(Desktop, "notepad")!.Id); // on screen beats minimized
        Assert.Equal(4, WindowServices.Match(Desktop, "notes.txt")!.Id);
        Assert.Equal(1, WindowServices.Match(Desktop, "overlay")!.Id);
        Assert.Null(WindowServices.Match(Desktop, "zzz-definitely-not-a-window"));
        Assert.Null(WindowServices.Match(Desktop, "  "));
    }

    [Fact]
    public void MatchAcceptsAWindowIdFromListWindows()
    {
        Assert.Equal(4, WindowServices.Match(Desktop, "4")!.Id);
        Assert.Equal(6, WindowServices.Match(Desktop, "0x6")!.Id);
    }

    [Fact]
    public void FindAppPrefersAnExactProcessName()
    {
        Assert.Equal(5, WindowServices.FindApp(Desktop, "notepad")!.Id);
        Assert.Equal(5, WindowServices.FindApp(Desktop, "NOTEPAD.EXE")!.Id);
        Assert.Equal(2, WindowServices.FindApp(Desktop, "Godot_v4.3-stable_win64.exe")!.Id);
        Assert.Equal(3, WindowServices.FindApp(Desktop, "Godot Engine")!.Id); // falls back to titles
        Assert.Null(WindowServices.FindApp(Desktop, "blender"));
    }

    [Fact]
    public void ChromeClassification()
    {
        Assert.True(WindowServices.IsChrome(0x80, "Palette", "Floating"));     // WS_EX_TOOLWINDOW
        Assert.True(WindowServices.IsChrome(0x08000000, "OSD", "Overlay"));  // WS_EX_NOACTIVATE
        Assert.True(WindowServices.IsChrome(0, "", "SomeHelper"));
        Assert.True(WindowServices.IsChrome(0, "Taskbar", "Shell_TrayWnd"));
        Assert.False(WindowServices.IsChrome(0x100, "My Game", "Engine"));
    }

    [Fact]
    public void ListWindowsFormatting()
    {
        Assert.Equal("No visible windows.", ListWindowsTool.Format([], null));
        Assert.Equal("No window matching \"godot\" is on screen.", ListWindowsTool.Format([], "godot"));
        var text = ListWindowsTool.Format(Desktop, null);
        Assert.StartsWith("Windows (topmost first):\n", text);
        Assert.Contains("• 2  Godot_v4.3-stable_win64 — My Game (DEBUG)  [800×600 at 0,0]  pid 2", text);
        Assert.Contains("• 4  notepad — notes.txt - Notepad  [minimized]  pid 4", text);
        Assert.Contains("explorer — (untitled)", text);

        var many = Enumerable.Range(1, 45).Select(i => W(i, "app", $"w{i}")).ToList();
        Assert.EndsWith("\n… 5 more", ListWindowsTool.Format(many, null));
    }
}

// MARK: - screen_watch schedule, ui_tree text, processes, sockets

public sealed class ToolLogicTests
{
    [Fact]
    public void WatchScheduleClampsLikeUpstream()
    {
        Assert.Equal((1.5, 8.0, 5), ScreenWatchTool.Schedule(null, null));
        Assert.Equal((0.5, 30.0, 60), ScreenWatchTool.Schedule(0.1, 99));
        Assert.Equal((10.0, 10.0, 2), ScreenWatchTool.Schedule(60, 1)); // at least one interval, at least two shots
        Assert.Equal((0.5, 1.0, 2), ScreenWatchTool.Schedule(0.5, 1));
    }

    [Fact]
    public void WatchShowsTheFirstAndLatestChangesWithoutRepeats()
    {
        Assert.Equal([0, 3], ScreenWatchTool.Pick([0, 3]));
        Assert.Equal([0, 5, 6, 7, 8], ScreenWatchTool.Pick([0, 1, 2, 5, 6, 7, 8]));
        Assert.Empty(ScreenWatchTool.Pick(Array.Empty<int>()));
    }

    [Fact]
    public void TreeLinesReadLikeUpstream()
    {
        Assert.Equal("  Button \"Save\" [10,20 80x24] #saveButton",
                     UiTreeText.Describe(new UiNode("Button", "Save", null, "saveButton", new ScreenRect(10, 20, 80, 24)), 1));
        Assert.Equal("Edit \"Search\" = hello world",
                     UiTreeText.Describe(new UiNode("Edit", "Search", "hello\nworld"), 0));
        Assert.Equal("Text \"Title\"", UiTreeText.Describe(new UiNode("Text", "Title", "Title"), 0)); // value = name isn't repeated
        var longValue = UiTreeText.Describe(new UiNode("Document", null, new string('x', 200)), 0);
        Assert.Equal("Document = " + new string('x', 80) + "…", longValue);
    }

    private sealed record Node(string Name, params Node[] Kids);

    [Fact]
    public void TreeWalkHonoursDepthCapAndSkips()
    {
        var tree = new Node("root-a", new Node("child", new Node("grandchild", new Node("deep"))), new Node("titlebar", new Node("close")));
        UiNode? Describe(Node n) => n.Name == "titlebar" ? null : new UiNode("Pane", n.Name);
        var (text, count) = UiTreeText.Walk([tree, new Node("root-b")], n => n.Kids, Describe, maxDepth: 2, maxNodes: 100);
        Assert.Equal("Pane \"root-a\"\n  Pane \"child\"\n    Pane \"grandchild\"\nPane \"root-b\"", text);
        Assert.Equal(4, count);

        var (capped, cappedCount) = UiTreeText.Walk([tree, new Node("root-b")], n => n.Kids, Describe, maxDepth: 6, maxNodes: 2);
        Assert.Equal(2, cappedCount);
        Assert.Contains("[stopped at 2 elements", capped);

        Assert.Equal(("", 0), UiTreeText.Walk(Array.Empty<Node>(), n => n.Kids, Describe, 3, 10));
    }

    [Fact]
    public void ElapsedAndCpuReadLikePs()
    {
        Assert.Equal("00:05", ProcessFacts.Elapsed(TimeSpan.FromSeconds(5)));
        Assert.Equal("01:02:03", ProcessFacts.Elapsed(new TimeSpan(1, 2, 3)));
        Assert.Equal("2-03:04:05", ProcessFacts.Elapsed(new TimeSpan(2, 3, 4, 5)));
        Assert.Equal("00:00", ProcessFacts.Elapsed(TimeSpan.FromSeconds(-3)));
        Assert.Equal(100, ProcessFacts.CpuPercent(TimeSpan.Zero, TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(500)), 3);
        Assert.Equal(250, ProcessFacts.CpuPercent(TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(2250), TimeSpan.FromMilliseconds(500)), 3);
        Assert.Equal(0, ProcessFacts.CpuPercent(TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.Zero));
    }

    [Fact]
    public void SocketRowsParseFromTheOwnerPidTables()
    {
        // MIB_TCPTABLE_OWNER_PID with two rows: pid 77 listening on 127.0.0.1:6007, pid 88 connected.
        var tcp = new List<byte>();
        tcp.AddRange(BitConverter.GetBytes(2u));
        tcp.AddRange(Row(2, [127, 0, 0, 1], 6007, [0, 0, 0, 0], 0, 77));
        tcp.AddRange(Row(5, [10, 0, 0, 2], 52011, [93, 184, 216, 34], 443, 88));
        Assert.Equal(["TCP  127.0.0.1:6007  listening"], SocketTable.Tcp([.. tcp], 77, v6: false).ToList());
        Assert.Equal(["TCP  10.0.0.2:52011 → 93.184.216.34:443  established"], SocketTable.Tcp([.. tcp], 88, v6: false).ToList());
        Assert.Empty(SocketTable.Tcp([.. tcp], 99, v6: false));

        var udp6 = new List<byte>();
        udp6.AddRange(BitConverter.GetBytes(1u));
        udp6.AddRange(IPAddress.IPv6Loopback.GetAddressBytes());
        udp6.AddRange(BitConverter.GetBytes(0u));
        udp6.AddRange([0x14, 0xE9, 0, 0]); // 5353
        udp6.AddRange(BitConverter.GetBytes(77u));
        Assert.Equal(["UDP  [::1]:5353"], SocketTable.Udp([.. udp6], 77, v6: true).ToList());

        // A truncated table yields what is complete, never an exception.
        Assert.Empty(SocketTable.Tcp([.. tcp.Take(10)], 77, v6: false));
        Assert.Equal("time-wait", SocketTable.TcpState(11));

        static IEnumerable<byte> Row(uint state, byte[] local, int localPort, byte[] remote, int remotePort, uint pid) =>
        [
            .. BitConverter.GetBytes(state), .. local, (byte)(localPort >> 8), (byte)localPort, 0, 0,
            .. remote, (byte)(remotePort >> 8), (byte)remotePort, 0, 0, .. BitConverter.GetBytes(pid),
        ];
    }
}
