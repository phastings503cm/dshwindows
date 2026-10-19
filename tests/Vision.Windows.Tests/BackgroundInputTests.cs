using System.Diagnostics;
using Vision.Core;
using Xunit.Abstractions;

namespace Vision.Windows.Tests;

/// <summary>Background input reaches real windows — standard Win32 controls by their own messages, WPF
/// controls by UI Automation — without moving the cursor or changing the foreground window; the real
/// mouse is shared one chat at a time and the cursor goes back where it was.</summary>
[Collection(DesktopCollection.Name)]
public sealed class BackgroundInputTests(ITestOutputHelper output)
{
    private static (int X, int Y) Cursor()
    {
        Native.GetCursorPos(out var point);
        return (point.X, point.Y);
    }

    private static string Id(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    private static IToolExecutor Tool(MachineChat chat, string name) => MachineTools.All(chat).Single(t => t.Name == name);

    [Fact]
    public async Task TextGoesIntoAWin32TextBoxWithoutTheKeyboardOrTheFocus()
    {
        using var edit = new Win32Control("EDIT", Win32Control.ES_MULTILINE | Win32Control.ES_WANTRETURN | Win32Control.ES_AUTOVSCROLL);
        var front = Native.GetForegroundWindow();
        var cursor = Cursor();
        var target = InputTargets.ForWindow(edit.Handle);
        Assert.Equal(WindowKind.Win32Edit, target.Kind);
        Assert.Equal(edit.Handle, target.Control);
        Assert.Equal([InputRoute.PostChars], InputPlanner.ForText("x", false, InputMode.Background, target.Kind, null));

        const string text = "héllo wörld 😀\nline two";
        Assert.Equal(RouteOutcome.Done, (await KeyRoutes.TextAsync(InputRoute.PostChars, target, text, CancellationToken.None)).Outcome);
        await Wait.Until(() => edit.Text == "héllo wörld 😀\r\nline two");
        Assert.Equal("héllo wörld 😀\r\nline two", edit.Text);

        // ctrl+a is the text box's own select-all.
        Assert.Equal(RouteOutcome.Done, (await KeyRoutes.KeyAsync(InputRoute.PostEditCommand, target, KeyNames.Parse("ctrl+a")!.Value, CancellationToken.None)).Outcome);
        Assert.Equal((0, edit.Text.Length), edit.Selection);

        // replace: the whole text at once.
        Assert.Equal(RouteOutcome.Done, (await KeyRoutes.TextAsync(InputRoute.PostSetText, target, "replaced", CancellationToken.None)).Outcome);
        Assert.Equal("replaced", edit.Text);

        // Lone keys as key messages: End, then Backspace.
        Assert.Equal(RouteOutcome.Done, (await KeyRoutes.KeyAsync(InputRoute.PostKeys, target, KeyNames.Parse("end")!.Value, CancellationToken.None)).Outcome);
        Assert.Equal(RouteOutcome.Done, (await KeyRoutes.KeyAsync(InputRoute.PostKeys, target, KeyNames.Parse("backspace")!.Value, CancellationToken.None)).Outcome);
        await Wait.Until(() => edit.Text == "replace");
        Assert.Equal("replace", edit.Text);

        Assert.Equal(front, Native.GetForegroundWindow());
        Assert.Equal(cursor, Cursor());
    }

    [Fact]
    public async Task APostedClickTicksAWin32CheckBox()
    {
        using var box = new Win32Control("BUTTON", Win32Control.BS_AUTOCHECKBOX, "Agree to the test");
        var cursor = Cursor();
        var (x, y) = box.Center();
        var target = InputTargets.AtPoint(x, y, box.Handle)!;
        Assert.Equal(WindowKind.Win32Control, target.Kind);
        Assert.Equal(box.Handle, target.Control);
        var chat = new MachineChat(Id("post"));
        var result = await PointerRoutes.TryAsync(InputRoute.PostClick, target, chat, PointerAction.Click, default, CancellationToken.None);
        Assert.Equal(RouteOutcome.Done, result.Outcome);
        Assert.Contains(PointerRoutes.Background, result.Message);
        await Wait.Until(() => box.Check == 1);
        Assert.Equal(1, box.Check);
        Assert.Equal(cursor, Cursor());
    }

    [Fact]
    public async Task TheMouseToolTicksAWin32CheckBoxThroughUiAutomation()
    {
        using var box = new Win32Control("BUTTON", Win32Control.BS_AUTOCHECKBOX, "Tick me please");
        var cursor = Cursor();
        var (x, y) = box.Center();
        var said = await Tools.Output(Tool(new MachineChat(Id("tick")), "mouse"), $$"""{"x":{{x}},"y":{{y}},"window":{{Tools.Q(box.Title)}}}""");
        output.WriteLine(said);
        Assert.Contains(PointerRoutes.Background, said);
        await Wait.Until(() => box.Check == 1);
        Assert.Equal(1, box.Check);
        Assert.Equal(cursor, Cursor());
    }

    [Fact]
    public async Task AClickPressesAButtonInAWindowBehindAnother()
    {
        using var back = new TestWindow();
        using var front = new TestWindow(); // same place, opened later: it covers the first
        var (x, y) = back.ButtonCenter();
        var cursor = Cursor();
        var said = await Tools.Output(Tool(new MachineChat(Id("behind")), "mouse"), $$"""{"x":{{x}},"y":{{y}},"window":{{Tools.Q(back.Title)}}}""");
        output.WriteLine(said);
        Assert.StartsWith("Pressed Button “Press me” in ", said);
        Assert.EndsWith($"with UI Automation Invoke ({PointerRoutes.Background}). Screenshot to verify.", said);
        await Wait.Until(() => back.Clicks == 1);
        Assert.Equal(1, back.Clicks);
        Assert.Equal(0, front.Clicks);
        Assert.Equal(cursor, Cursor());
    }

    [Fact]
    public async Task AnElementIsClickedByName()
    {
        using var window = new TestWindow();
        var said = await Tools.Output(Tool(new MachineChat(Id("named")), "mouse"), $$"""{"app":{{Tools.Q(window.Title)}},"element":"#agree"}""");
        Assert.StartsWith("Toggled (on now) CheckBox “I agree” in ", said);
        await Wait.Until(window.Checked);
        Assert.True(window.Checked());
        Assert.StartsWith("Error: no element named 'zzz-nothing'", await Tools.Output(Tool(new MachineChat(Id("named")), "mouse"),
                              $$"""{"app":{{Tools.Q(window.Title)}},"element":"zzz-nothing"}"""));
        Assert.StartsWith("Error: element needs app", await Tools.Output(Tool(new MachineChat(Id("named")), "mouse"), """{"element":"Save"}"""));
    }

    [Fact]
    public async Task TextGoesIntoAWpfFieldThroughItsValue()
    {
        using var window = new TestWindow();
        var (target, error) = await ControlArgs.Element(window.Handle, "#box", "test window", CancellationToken.None);
        Assert.Null(error);
        Assert.NotNull(target);
        Assert.Equal(WindowKind.Wpf, target.Kind);
        Assert.True(target.Facts!.Value);
        Assert.Equal([InputRoute.UiaAppendValue, InputRoute.RealKeyboard],
                     InputPlanner.ForText(" and more", false, InputMode.Auto, target.Kind, target.Facts));
        Assert.Equal(RouteOutcome.Done, (await KeyRoutes.TextAsync(InputRoute.UiaAppendValue, target, " and more", CancellationToken.None)).Outcome);
        await Wait.Until(() => window.BoxText() == "hello tree and more");
        Assert.Equal("hello tree and more", window.BoxText());
        Assert.Equal(RouteOutcome.Done, (await KeyRoutes.TextAsync(InputRoute.UiaSetValue, target, "fresh", CancellationToken.None)).Outcome);
        await Wait.Until(() => window.BoxText() == "fresh");
        Assert.Equal("fresh", window.BoxText());
    }

    [Fact]
    public async Task AClickIntoAWpfFieldPicksItForTyping()
    {
        using var window = new TestWindow();
        var chat = new MachineChat(Id("pick"));
        var (x, y) = window.BoxCenter();
        if (InputTargets.TopAt(x, y) != window.Handle) return; // something covers it
        var cursor = Cursor();
        var said = await Tools.Output(Tool(chat, "mouse"), $$"""{"x":{{x}},"y":{{y}}}""");
        Assert.StartsWith("Picked Edit", said);
        Assert.Contains("keyboard text without an app goes there next", said);
        Assert.NotNull(chat.TypingTarget);
        Assert.Equal(cursor, Cursor());
        // A click elsewhere moves the focus off it, as for a person.
        var (bx, by) = window.ButtonCenter();
        await Tools.Output(Tool(chat, "mouse"), $$"""{"x":{{bx}},"y":{{by}}}""");
        Assert.Null(chat.TypingTarget);
    }

    [Fact]
    public async Task InBackgroundModeWhatNeedsTheRealMouseIsRefused()
    {
        using var window = new TestWindow();
        var (x, y) = window.PadPoint(0.5, 0.5);
        var chat = new MachineChat(Id("bg"));
        var cursor = Cursor();
        var right = await Tools.Output(Tool(chat, "mouse"), $$"""{"x":{{x}},"y":{{y}},"button":"right","mode":"background"}""");
        Assert.StartsWith("Error: can't do that to ", right);
        Assert.Contains("a right-click opens a menu that follows the real pointer", right);
        Assert.Equal("Error: mode must be auto, background or real.", await Tools.Output(Tool(chat, "mouse"), """{"x":1,"y":1,"mode":"sideways"}"""));
        Assert.Equal(cursor, Cursor());
    }

    [Fact]
    public async Task RealClicksTakeTurnsAndGiveTheCursorBack()
    {
        using var window = new TestWindow();
        var (x, y) = window.ButtonCenter();
        if (InputTargets.TopAt(x, y) != window.Handle) return; // something covers the button
        PersonInput.Shared.ExpectCursor(7, 7);
        Native.SetCursorPos(7, 7);
        var start = Cursor();
        var holder = new MachineChat(Id("hold"), () => "Fix login");
        var waiter = new MachineChat(Id("wait"), () => "Build the game");
        var heard = new List<string?>();
        waiter.Status = line =>
        {
            lock (heard) heard.Add(line);
        };
        var lease = await holder.AcquireRealInputAsync(CancellationToken.None);
        var click = Tools.Output(Tool(waiter, "mouse"), $$"""{"x":{{x}},"y":{{y}},"mode":"real"}""");
        await Task.Delay(500);
        Assert.False(click.IsCompleted);
        Assert.Equal("Fix login", MachineControl.Arbiter.Snapshot().HolderTitle);
        Assert.Contains(MachineControl.Arbiter.Snapshot().Waiting, w => w.Title == "Build the game");
        lock (heard) Assert.Contains("Waiting for the mouse — “Fix login” is using it…", heard);
        lease.Dispose();
        var said = await click; // after the holder's linger
        Assert.Equal($"Clicked left at ({x},{y}). Screenshot to verify.", said);
        await Wait.Until(() => window.Clicks == 1);
        Assert.Equal(1, window.Clicks);
        Assert.Equal(start, Cursor());
        lock (heard) Assert.Null(heard[^1]);
    }

    [Fact]
    public async Task InjectedInputIsNotThePerson()
    {
        PersonInput.Shared.Start();
        try
        {
            Assert.True(PersonInput.Shared.Watching);
            await Task.Delay(200);
            var before = PersonInput.Shared.LastHuman;
            InputSender.MoveTo(40, 40);
            InputSender.Send([Native.INPUT.Mouse(Native.MOUSEEVENTF_MOVE, 5, 5)]);
            InputSender.Send([InputSender.Key(KeyNames.VkShift, up: false), InputSender.Key(KeyNames.VkShift, up: true)]);
            await Task.Delay(300);
            if (before is null) Assert.Null(PersonInput.Shared.LastHuman);

            // For the manual checklist: whether a bare SetCursorPos reaches the hooks as if from a person.
            Native.SetCursorPos(90, 90);
            await Task.Delay(300);
            output.WriteLine($"a bare SetCursorPos counted as the person: {PersonInput.Shared.LastHuman is not null && before is null}");
        }
        finally
        {
            PersonInput.Shared.Stop();
        }
        Assert.False(PersonInput.Shared.Watching);
    }

    [Fact]
    public async Task EveryMachineToolLightsItsChatsSession()
    {
        var chat = new MachineChat(Id("glow"), () => "Glow test");
        await Tools.Output(Tool(chat, "list_windows"), "{}");
        var session = Assert.Single(MachineControl.Activity.Sessions(), s => s.ChatId == chat.Id);
        Assert.Equal("Glow test", session.Title);
        MachineControl.Activity.EndRun(chat.Id);
    }

    [Fact]
    public async Task AChatKeptToItsOwnAppsLeavesOtherWindowsAlone()
    {
        using var window = new TestWindow();
        var chat = new MachineChat(Id("own"), () => "own", () => true);
        var (x, y) = window.ButtonCenter();
        var said = await Tools.Output(Tool(chat, "mouse"), $$"""{"x":{{x}},"y":{{y}}}""");
        Assert.StartsWith("Error: not clicking in ", said);
        Assert.Contains("Keep each chat to the apps it started", said);
        Assert.Equal(0, window.Clicks);
    }

    [Fact]
    public async Task AChatOwnsWhatItsCommandsStarted()
    {
        var chat = new MachineChat(Id("tree"));
        using var shell = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 30 127.0.0.1 >nul") { CreateNoWindow = true, UseShellExecute = false })!;
        try
        {
            ChatProcesses.Record(chat.Id, shell.Id, DateTimeOffset.UtcNow);
            Process? ping = null;
            var clock = Stopwatch.StartNew();
            while (ping is null && clock.Elapsed < TimeSpan.FromSeconds(10))
            {
                var lookup = ProcessLineage.Lookup();
                ping = Process.GetProcessesByName("PING").FirstOrDefault(p => lookup(p.Id)?.ParentId == shell.Id);
                if (ping is null) await Task.Delay(100);
            }
            Assert.NotNull(ping);
            Assert.True(chat.Owns(shell.Id));
            Assert.True(chat.Owns(ping.Id));
            Assert.False(new MachineChat(Id("stranger")).Owns(ping.Id));
            Assert.False(chat.Owns(Environment.ProcessId));
        }
        finally
        {
            try { shell.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            ChatProcesses.Forget(chat.Id);
        }
    }
}
