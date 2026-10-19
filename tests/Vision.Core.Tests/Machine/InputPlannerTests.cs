namespace Vision.Core.Tests;

/// <summary>Which route a click or a keystroke takes: the element's own pattern first, a posted message
/// for standard Win32 controls, the real devices last (auto) or never (background).</summary>
public sealed class InputPlannerTests
{
    private static readonly ElementFacts Button = new() { ControlType = "Button", Name = "Save", Invoke = true };

    [Theory]
    [InlineData("Edit", "#32770", WindowKind.Win32Edit)]
    [InlineData("RichEdit20W", "WordPadClass", WindowKind.Win32Edit)]
    [InlineData("RICHEDIT50W", "Notepad", WindowKind.Win32Edit)]
    [InlineData("RichEditD2DPT", "Notepad", WindowKind.Win32Edit)]
    [InlineData("Scintilla", "Notepad++", WindowKind.Win32Edit)]
    [InlineData("WindowsForms10.EDIT.app.0.141b42a_r6_ad1", "WindowsForms10.Window.8.app.0.141b42a_r6_ad1", WindowKind.Win32Edit)]
    [InlineData("Button", "#32770", WindowKind.Win32Control)]
    [InlineData("SysListView32", "CabinetWClass", WindowKind.Win32Control)]
    [InlineData("WindowsForms10.BUTTON.app.0.141b42a_r6_ad1", "WindowsForms10.Window.8.app.0.141b42a_r6_ad1", WindowKind.Win32Control)]
    [InlineData("HwndWrapper[Vision;;7c1a]", "HwndWrapper[Vision;;7c1a]", WindowKind.Wpf)]
    [InlineData("Chrome_RenderWidgetHostHWND", "Chrome_WidgetWin_1", WindowKind.Browser)]
    [InlineData("MozillaWindowClass", "MozillaWindowClass", WindowKind.Browser)]
    [InlineData("Windows.UI.Core.CoreWindow", "ApplicationFrameWindow", WindowKind.Modern)]
    [InlineData("Microsoft.UI.Content.DesktopChildSiteBridge", "WinUIDesktopWin32WindowClass", WindowKind.Modern)]
    [InlineData("Engine", "Engine", WindowKind.Game)]
    [InlineData("UnityWndClass", "UnityWndClass", WindowKind.Game)]
    [InlineData("SDL_app", "SDL_app", WindowKind.Game)]
    [InlineData("Qt5152QWindowIcon", "Qt5152QWindowIcon", WindowKind.Other)]
    [InlineData("", "", WindowKind.Other)]
    public void WindowClassesAreSortedByHowTheyReadInput(string className, string topClass, WindowKind kind) =>
        Assert.Equal(kind, InputPlanner.Classify(className, topClass));

    [Theory]
    [InlineData(null, InputMode.Auto)]
    [InlineData("", InputMode.Auto)]
    [InlineData("AUTO", InputMode.Auto)]
    [InlineData(" background ", InputMode.Background)]
    [InlineData("real", InputMode.Real)]
    public void Modes(string? text, InputMode mode) => Assert.Equal(mode, InputPlanner.ParseMode(text));

    [Fact]
    public void AnUnknownModeIsRejected() => Assert.Null(InputPlanner.ParseMode("sideways"));

    [Fact]
    public void AButtonIsInvokedInAnyKindOfWindow()
    {
        // (A Win32 text box is a text box whatever UI Automation says is under the point.)
        foreach (var kind in Enum.GetValues<WindowKind>().Where(k => k != WindowKind.Win32Edit))
        {
            var routes = InputPlanner.ForPointer(PointerAction.Click, InputMode.Auto, kind, Button);
            Assert.Equal(InputRoute.UiaInvoke, routes[0]);
            Assert.Equal(InputRoute.RealMouse, routes[^1]);
        }
        Assert.Equal([InputRoute.UiaInvoke, InputRoute.PostClick, InputRoute.RealMouse],
                     InputPlanner.ForPointer(PointerAction.Click, InputMode.Auto, WindowKind.Win32Control, Button));
        Assert.Equal([InputRoute.UiaInvoke], InputPlanner.ForPointer(PointerAction.Click, InputMode.Background, WindowKind.Wpf, Button));
    }

    [Fact]
    public void RealModeIsTheOldBehaviour()
    {
        Assert.Equal([InputRoute.RealMouse], InputPlanner.ForPointer(PointerAction.Click, InputMode.Real, WindowKind.Win32Control, Button));
        Assert.Equal([InputRoute.RealKeyboard], InputPlanner.ForText("hi", false, InputMode.Real, WindowKind.Win32Edit, null));
        Assert.Equal([InputRoute.RealKeyboard], InputPlanner.ForKey("enter", false, false, false, false, InputMode.Real, WindowKind.Win32Edit, null));
    }

    [Theory]
    [InlineData("CheckBox", InputRoute.UiaToggle)]
    [InlineData("RadioButton", InputRoute.UiaSelect)]
    [InlineData("TabItem", InputRoute.UiaSelect)]
    [InlineData("ListItem", InputRoute.UiaSelect)]
    [InlineData("TreeItem", InputRoute.UiaSelect)]
    [InlineData("ComboBox", InputRoute.UiaExpandCollapse)]
    [InlineData("Hyperlink", InputRoute.UiaInvoke)]
    [InlineData("MenuItem", InputRoute.UiaInvoke)]
    public void EachControlGetsThePatternAClickMeans(string type, InputRoute expected)
    {
        // Every pattern is offered: the planner must pick the one a click stands for (a list item's
        // Invoke opens it, which is a double-click).
        var element = new ElementFacts { ControlType = type, Invoke = true, Toggle = true, SelectionItem = true, ExpandCollapse = true };
        if (type == "MenuItem") element = element with { ExpandCollapse = false };
        if (type == "ComboBox") element = element with { Invoke = false };
        Assert.Equal(expected, InputPlanner.ForPointer(PointerAction.Click, InputMode.Background, WindowKind.Wpf, element)[0]);
    }

    [Fact]
    public void AnItemDoubleClickedIsOpened()
    {
        var item = new ElementFacts { ControlType = "ListItem", Invoke = true, SelectionItem = true };
        Assert.Equal([InputRoute.UiaInvoke, InputRoute.PostDoubleClick, InputRoute.RealMouse],
                     InputPlanner.ForPointer(PointerAction.DoubleClick, InputMode.Auto, WindowKind.Win32Control, item));
        var tree = new ElementFacts { ControlType = "TreeItem", ExpandCollapse = true };
        Assert.Equal([InputRoute.UiaExpandCollapse], InputPlanner.ForPointer(PointerAction.DoubleClick, InputMode.Background, WindowKind.Wpf, tree));
    }

    [Fact]
    public void AClickIntoATextBoxPlacesTheCaretOrRemembersTheField()
    {
        var field = new ElementFacts { ControlType = "Edit", Name = "Search", Value = true };
        Assert.Equal([InputRoute.PostClick, InputRoute.RealMouse],
                     InputPlanner.ForPointer(PointerAction.Click, InputMode.Auto, WindowKind.Win32Edit, field));
        Assert.Equal([InputRoute.TypingTarget, InputRoute.RealMouse],
                     InputPlanner.ForPointer(PointerAction.Click, InputMode.Auto, WindowKind.Browser, field));
        // A code editor's document with a value counts as a field; a web page's body does not.
        Assert.Equal(InputRoute.TypingTarget,
                     InputPlanner.ForPointer(PointerAction.Click, InputMode.Auto, WindowKind.Wpf, field with { ControlType = "Document" })[0]);
        Assert.Equal([InputRoute.RealMouse],
                     InputPlanner.ForPointer(PointerAction.Click, InputMode.Auto, WindowKind.Browser, new ElementFacts { ControlType = "Document" }));
    }

    [Fact]
    public void WithoutAPatternOnlyWin32ControlsTakeAPostedClick()
    {
        var pane = new ElementFacts { ControlType = "Pane" };
        Assert.Equal([InputRoute.PostClick, InputRoute.RealMouse], InputPlanner.ForPointer(PointerAction.Click, InputMode.Auto, WindowKind.Win32Control, pane));
        foreach (var kind in new[] { WindowKind.Wpf, WindowKind.Browser, WindowKind.Modern, WindowKind.Game, WindowKind.Other })
        {
            Assert.Equal([InputRoute.RealMouse], InputPlanner.ForPointer(PointerAction.Click, InputMode.Auto, kind, pane));
            Assert.Empty(InputPlanner.ForPointer(PointerAction.Click, InputMode.Background, kind, null));
        }
    }

    [Fact]
    public void AnOldControlWithOnlyAnAccessibleDefaultAction()
    {
        var legacy = new ElementFacts { ControlType = "Custom", MsaaDefaultAction = "Press" };
        Assert.Equal([InputRoute.MsaaDefaultAction, InputRoute.RealMouse], InputPlanner.ForPointer(PointerAction.Click, InputMode.Auto, WindowKind.Other, legacy));
        // A pane's default action is not what a click on it means.
        Assert.Equal([InputRoute.RealMouse],
                     InputPlanner.ForPointer(PointerAction.Click, InputMode.Auto, WindowKind.Other, legacy with { ControlType = "Pane" }));
    }

    [Fact]
    public void ADisabledControlGetsNoPattern()
    {
        Assert.Equal([InputRoute.RealMouse], InputPlanner.ForPointer(PointerAction.Click, InputMode.Auto, WindowKind.Wpf, Button with { Enabled = false }));
    }

    [Fact]
    public void MenusMiddleClicksAndDragsNeedTheRealMouse()
    {
        foreach (var action in new[] { PointerAction.RightClick, PointerAction.MiddleClick, PointerAction.Drag })
        {
            Assert.Equal([InputRoute.RealMouse], InputPlanner.ForPointer(action, InputMode.Auto, WindowKind.Win32Control, Button));
            Assert.Empty(InputPlanner.ForPointer(action, InputMode.Background, WindowKind.Win32Control, Button));
        }
    }

    [Fact]
    public void Scrolling()
    {
        var list = new ElementFacts { ControlType = "List", Scroll = true };
        Assert.Equal([InputRoute.UiaScroll, InputRoute.PostWheel, InputRoute.RealMouse],
                     InputPlanner.ForPointer(PointerAction.Scroll, InputMode.Auto, WindowKind.Win32Control, list));
        Assert.Equal([InputRoute.UiaScroll], InputPlanner.ForPointer(PointerAction.Scroll, InputMode.Background, WindowKind.Browser, list));
        Assert.Equal([InputRoute.RealMouse], InputPlanner.ForPointer(PointerAction.Scroll, InputMode.Auto, WindowKind.Game, null));
    }

    [Fact]
    public void TextGoesToAWin32BoxAsCharactersOrAsItsWholeText()
    {
        Assert.Equal([InputRoute.PostChars, InputRoute.RealKeyboard], InputPlanner.ForText("hi\nthere", false, InputMode.Auto, WindowKind.Win32Edit, null));
        Assert.Equal([InputRoute.PostSetText], InputPlanner.ForText("hi", true, InputMode.Background, WindowKind.Win32Edit, null));
    }

    [Fact]
    public void TextGoesToOtherFieldsThroughTheirValue()
    {
        var field = new ElementFacts { ControlType = "Edit", Value = true };
        Assert.Equal([InputRoute.UiaAppendValue, InputRoute.RealKeyboard], InputPlanner.ForText("hi", false, InputMode.Auto, WindowKind.Browser, field));
        Assert.Equal([InputRoute.UiaSetValue], InputPlanner.ForText("hi", true, InputMode.Background, WindowKind.Wpf, field));
        // A password field doesn't show its text, so there is nothing to add to: it is set.
        Assert.Equal([InputRoute.UiaSetValue], InputPlanner.ForText("pw", false, InputMode.Background, WindowKind.Wpf, field with { IsPassword = true }));
        // Line breaks and tabs are keys that act; read-only and disabled fields take nothing.
        Assert.Empty(InputPlanner.ForText("a\nb", true, InputMode.Background, WindowKind.Wpf, field));
        Assert.Empty(InputPlanner.ForText("a\tb", true, InputMode.Background, WindowKind.Wpf, field));
        Assert.Empty(InputPlanner.ForText("x", true, InputMode.Background, WindowKind.Wpf, field with { ValueReadOnly = true }));
        Assert.Empty(InputPlanner.ForText("x", true, InputMode.Background, WindowKind.Wpf, field with { Enabled = false }));
        Assert.Equal([InputRoute.RealKeyboard], InputPlanner.ForText("x", false, InputMode.Auto, WindowKind.Game, null));
    }

    [Fact]
    public void KeysWithoutModifiersArePostedToWin32Controls()
    {
        Assert.Equal([InputRoute.PostKeys, InputRoute.RealKeyboard],
                     InputPlanner.ForKey("Enter", false, false, false, false, InputMode.Auto, WindowKind.Win32Edit, null));
        Assert.Equal([InputRoute.PostKeys], InputPlanner.ForKey("f5", false, false, false, false, InputMode.Background, WindowKind.Win32Control, null));
        // Posted messages carry no modifier state.
        Assert.Equal([InputRoute.RealKeyboard], InputPlanner.ForKey("s", true, false, false, false, InputMode.Auto, WindowKind.Win32Edit, null));
        Assert.Empty(InputPlanner.ForKey("tab", false, false, true, false, InputMode.Background, WindowKind.Win32Control, null));
        Assert.Empty(InputPlanner.ForKey("enter", false, false, false, false, InputMode.Background, WindowKind.Browser, null));
    }

    [Theory]
    [InlineData("a")]
    [InlineData("c")]
    [InlineData("x")]
    [InlineData("v")]
    [InlineData("z")]
    public void EditShortcutsUseTheTextBoxsOwnCommands(string key)
    {
        Assert.Equal([InputRoute.PostEditCommand], InputPlanner.ForKey(key, true, false, false, false, InputMode.Background, WindowKind.Win32Edit, null));
        Assert.Empty(InputPlanner.ForKey(key, true, false, false, false, InputMode.Background, WindowKind.Win32Control, null));
    }

    [Fact]
    public void AltF4ClosesAnyWindowWithAMessage()
    {
        Assert.Equal([InputRoute.PostClose, InputRoute.RealKeyboard], InputPlanner.ForKey("F4", false, true, false, false, InputMode.Auto, WindowKind.Game, null));
    }

    [Fact]
    public void EnterAndSpaceOnAButtonPressIt()
    {
        Assert.Equal([InputRoute.UiaInvoke], InputPlanner.ForKey("enter", false, false, false, false, InputMode.Background, WindowKind.Wpf, Button));
        var box = new ElementFacts { ControlType = "CheckBox", Toggle = true };
        Assert.Equal([InputRoute.UiaToggle], InputPlanner.ForKey("space", false, false, false, false, InputMode.Background, WindowKind.Wpf, box));
    }

    [Fact]
    public void EnterInABrowserOrWpfFieldTriesItsSubmitBeforeTheRealKeyboard()
    {
        var field = new ElementFacts { ControlType = "Edit", Value = true };
        foreach (var kind in new[] { WindowKind.Browser, WindowKind.Wpf, WindowKind.Modern })
        {
            Assert.Equal([InputRoute.UiaSubmit, InputRoute.RealKeyboard], InputPlanner.ForKey("enter", false, false, false, false, InputMode.Auto, kind, field));
            Assert.Equal([InputRoute.UiaSubmit], InputPlanner.ForKey("return", false, false, false, false, InputMode.Background, kind, field));
        }
        // A Win32 text box takes a posted Enter; a modifier or another key is not a submit.
        Assert.Equal([InputRoute.PostKeys, InputRoute.RealKeyboard], InputPlanner.ForKey("enter", false, false, false, false, InputMode.Auto, WindowKind.Win32Edit, field));
        Assert.Equal([InputRoute.RealKeyboard], InputPlanner.ForKey("enter", false, false, true, false, InputMode.Auto, WindowKind.Browser, field));
        Assert.Equal([InputRoute.RealKeyboard], InputPlanner.ForKey("tab", false, false, false, false, InputMode.Auto, WindowKind.Browser, field));
        Assert.True(InputPlanner.IsBackground(InputRoute.UiaSubmit));
    }

    [Fact]
    public void OnlyOneButtonThatClearlySubmitsIsPressed()
    {
        Assert.Equal(0, SubmitButton.Pick([("Google Search", true), ("I'm Feeling Lucky", true)]));
        Assert.Equal(1, SubmitButton.Pick([("Forgot password?", true), ("Sign in", true), ("Create account", true)]));
        Assert.Equal(0, SubmitButton.Pick([("Send", true), ("Send", false)])); // a greyed-out one doesn't count
        Assert.Null(SubmitButton.Pick([("Search", true), ("Submit", true)])); // two candidates: not certain
        Assert.Null(SubmitButton.Pick([("Clear", true), ("Upload a file", true)])); // none
        Assert.Null(SubmitButton.Pick([]));
        Assert.Null(SubmitButton.Pick([("Gopher", true)])); // "go" only as a word
    }

    [Fact]
    public void ATrailingLineBreakIsTextThenEnter()
    {
        Assert.Equal(("weather in Portland", true), TrailingEnter.Split("weather in Portland\n"));
        Assert.Equal(("weather in Portland", true), TrailingEnter.Split("weather in Portland\r\n"));
        Assert.Equal(("two\nlines\n", false), TrailingEnter.Split("two\nlines\n")); // a real multi-line text stays as it is
        Assert.Equal(("\n", false), TrailingEnter.Split("\n"));
        Assert.Equal(("plain", false), TrailingEnter.Split("plain"));
        Assert.Equal(("a\tb\n", false), TrailingEnter.Split("a\tb\n"));
    }

    [Fact]
    public void AClickGoesToTheChatsOwnWindowNotWhateverCoversIt()
    {
        // On top there is the chat's window, or a menu/dialog of the same app: that.
        Assert.Equal(AimDecision.Top, InputAim.Decide(insideAimed: true, topIsAimed: true, topBelongsToAimed: false));
        Assert.Equal(AimDecision.Top, InputAim.Decide(insideAimed: false, topIsAimed: false, topBelongsToAimed: true)); // its menu past its edge
        Assert.Equal(AimDecision.Top, InputAim.Decide(insideAimed: true, topIsAimed: false, topBelongsToAimed: true)); // its own dialog over it
        // Covered there by the person's window, another chat's app or another window of the same app: the chat's window.
        Assert.Equal(AimDecision.Aimed, InputAim.Decide(insideAimed: true, topIsAimed: false, topBelongsToAimed: false));
        // Outside the chat's window, on another window: refused, with what to do instead.
        Assert.Equal(AimDecision.Refuse, InputAim.Decide(insideAimed: false, topIsAimed: false, topBelongsToAimed: false));
        Assert.StartsWith("Error: (5,6) is outside notepad — notes.txt, the window this chat last captured (it spans 0,0 800×600); chrome is there. Pass window:",
                          InputAim.OutsideAimed(5, 6, "notepad — notes.txt", "0,0 800×600", "chrome"));
        Assert.StartsWith("Error: say which app to type into (app:\"…\")", InputAim.NoTypingTarget("chrome"));
        Assert.Contains("the window in front (chrome) is not one this chat started or used", InputAim.NoTypingTarget("chrome"));
    }

    [Fact]
    public void ThePersonGetsTheirWindowBackOnlyWhenNothingElseHappened()
    {
        Assert.True(FocusReturn.ShouldRestore(hadPrevious: true, previousIsTarget: false, previousExists: true, frontIsTargetApp: true, personActive: false));
        // They were already in that app; they touched something since; their window closed; something else came up.
        Assert.False(FocusReturn.ShouldRestore(true, previousIsTarget: true, true, true, false));
        Assert.False(FocusReturn.ShouldRestore(true, false, true, true, personActive: true));
        Assert.False(FocusReturn.ShouldRestore(true, false, previousExists: false, true, false));
        Assert.False(FocusReturn.ShouldRestore(true, false, true, frontIsTargetApp: false, false));
        Assert.False(FocusReturn.ShouldRestore(hadPrevious: false, false, true, true, false));
    }

    [Fact]
    public void OnlyTheRealDevicesAreForeground()
    {
        foreach (var route in Enum.GetValues<InputRoute>())
            Assert.Equal(route is not (InputRoute.RealMouse or InputRoute.RealKeyboard), InputPlanner.IsBackground(route));
        Assert.Equal("Button “Save”", Button.Label);
        Assert.Equal("Pane", new ElementFacts { ControlType = "Pane" }.Label);
    }
}
