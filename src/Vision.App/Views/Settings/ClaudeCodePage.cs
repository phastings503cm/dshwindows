using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using Vision.App.Infrastructure;
using Vision.App.Model;
using Vision.App.Views.Guide;
using Vision.Core;

namespace Vision.App.Views.Settings;

/// <summary>Settings › Claude Code: whether the official claude CLI is on this computer and signed in (with the install
/// and sign-in steps when it isn't), putting it in every chat's model menu (Claude Code · Opus, Sonnet, Haiku), how it
/// works with Vision's permissions, /goal and the task list, and — for the unusual setup — its path and WSL. The same
/// sections and words as Vision for macOS.</summary>
public sealed class ClaudeCodePage : UserControl
{
    private readonly AppModel _model;
    private ClaudeCodeService Service => _model.ClaudeCode;
    private readonly StackPanel _body = new();
    private readonly TextBox _path = Ui.Field(placeholder: "Found automatically", mono: true, width: 420);
    private bool _makePrimary;
    private bool _refreshQueued;

    public ClaudeCodePage(AppModel model)
    {
        _model = model;
        _path.Text = model.Config.ClaudeCodePath;
        _makePrimary = model.Config.ActiveProvider is null;
        var page = new StackPanel { MaxWidth = 760 };
        page.Children.Add(Ui.Title("Claude Code"));
        var intro = Ui.Secondary(ClaudeCodeRoutes.Explanation);
        intro.Margin = new Thickness(0, 6, 0, 4);
        page.Children.Add(intro);
        page.Children.Add(_body);
        Content = Ui.Scroll(page);

        PropertyChangedEventHandler handler = (_, _) => QueueRefresh();
        Service.PropertyChanged += handler;
        Unloaded += (_, _) => Service.PropertyChanged -= handler;
        Refresh();
        if (Service.Status is null && !Service.Busy) _ = Service.CheckAsync();
    }

    private void QueueRefresh()
    {
        if (_refreshQueued) return;
        _refreshQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _refreshQueued = false;
            Refresh();
        });
    }

    private void Refresh()
    {
        if (_path.Parent is Panel holder) holder.Children.Remove(_path);
        _body.Children.Clear();
        _body.Children.Add(Ui.Section("On this computer"));
        _body.Children.Add(StatusCard());
        _body.Children.Add(Ui.Section("In the model menu"));
        _body.Children.Add(MenuCard());
        _body.Children.Add(Ui.Section("How it works with Vision"));
        _body.Children.Add(HowCard());
        _body.Children.Add(Ui.Section("Advanced"));
        _body.Children.Add(AdvancedCard());
    }

    // MARK: Status

    private UIElement StatusCard()
    {
        var panel = new StackPanel();
        var status = Service.Status;
        string title;
        string glyph;
        string brush;
        switch (status)
        {
            case null:
                (title, glyph, brush) = (Service.Busy ? "Looking for Claude Code…" : "Not checked yet", Icons.Search, "TextFillColorSecondaryBrush");
                break;
            case ClaudeCodeStatus.Ready ready:
                (title, glyph, brush) = ($"Claude Code {ready.Version} is ready", Icons.Completed, "SystemFillColorSuccessBrush");
                break;
            case ClaudeCodeStatus.SignedOut signedOut:
                (title, glyph, brush) = ($"Claude Code {signedOut.Version} is installed, but not signed in", Icons.Warning, "SystemFillColorCautionBrush");
                break;
            default:
                (title, glyph, brush) = (status is ClaudeCodeStatus.Missing ? "Claude Code isn't installed" : "Claude Code didn't start", Icons.Warning,
                    "SystemFillColorCautionBrush");
                break;
        }
        var icon = Ui.Glyph(glyph, 18, brush);
        icon.Margin = new Thickness(0, 0, 12, 0);
        icon.VerticalAlignment = VerticalAlignment.Top;
        var head = new DockPanel();
        var check = Ui.Button(Service.Busy ? "Checking…" : "Check again", () => _ = Service.CheckAsync());
        check.IsEnabled = !Service.Busy;
        DockPanel.SetDock(check, Dock.Right);
        check.VerticalAlignment = VerticalAlignment.Top;
        head.Children.Add(check);
        DockPanel.SetDock(icon, Dock.Left);
        head.Children.Add(icon);
        var words = Ui.Stack(Ui.Text(title, 14, FontWeights.SemiBold));
        if (status is not null) words.Children.Add(Ui.Secondary(status.Summary));
        var where = status switch
        {
            ClaudeCodeStatus.Ready r => r.Launch.Display,
            ClaudeCodeStatus.SignedOut s => s.Launch.Display,
            _ => null,
        };
        if (where is not null)
        {
            var line = Ui.Secondary($"Runs: {where}", 11.5);
            line.Margin = new Thickness(0, 4, 0, 0);
            words.Children.Add(line);
        }
        head.Children.Add(words);
        panel.Children.Add(head);

        switch (status)
        {
            case ClaudeCodeStatus.Missing or ClaudeCodeStatus.Broken:
                panel.Children.Add(Steps("Install it (a minute or two), then sign in once:", ClaudeCodeCli.InstallSteps));
                break;
            case ClaudeCodeStatus.SignedOut:
                panel.Children.Add(Steps("Sign in once, in a terminal (it opens your browser to sign in to your Claude account):",
                [
                    "claude auth login",
                    "Pick the Claude account with your Pro or Max plan, and finish in the browser.",
                    "Come back here and press Check again.",
                ]));
                break;
        }
        var note = Ui.Secondary("Vision checks with `claude --version` and `claude auth status`, which say whether Claude Code is signed in — never its " +
                                "sign-in itself. Vision doesn't read, copy or store Claude Code's credentials; Claude Code talks to Anthropic on its own.", 11.5);
        note.Margin = new Thickness(0, 10, 0, 0);
        panel.Children.Add(note);
        return Ui.Card(panel);
    }

    private static UIElement Steps(string lead, IReadOnlyList<string> steps)
    {
        var panel = new StackPanel { Margin = new Thickness(30, 12, 0, 0) };
        panel.Children.Add(Ui.Text(lead, 13));
        var n = 0;
        foreach (var step in steps)
        {
            n++;
            var text = new TextBox
            {
                Text = $"{n}.  {step}",
                IsReadOnly = true,
                BorderThickness = new Thickness(0),
                Background = System.Windows.Media.Brushes.Transparent,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0),
                FontSize = 12.5,
            };
            text.SetResourceReference(Control.FontFamilyProperty, "MonoFont");
            text.SetResourceReference(Control.ForegroundProperty, "TextFillColorPrimaryBrush");
            panel.Children.Add(text);
        }
        return panel;
    }

    // MARK: Model menu

    private UIElement MenuCard()
    {
        var panel = new StackPanel();
        if (Service.InMenu)
        {
            var primary = _model.Config.ActiveRoute;
            var head = new DockPanel();
            var remove = Ui.Button("Remove", () =>
            {
                if (!Dialogs.Dialog.Confirm("Take Claude Code out of the model menu?",
                        "Chats on it move to the primary model when they next send. Their Claude Code sessions stay on this computer, so adding it back carries on where they left off.",
                        "Remove", destructive: true)) return;
                Service.Remove();
            });
            DockPanel.SetDock(remove, Dock.Right);
            remove.VerticalAlignment = VerticalAlignment.Top;
            head.Children.Add(remove);
            head.Children.Add(Ui.Stack(Ui.Text("In every chat's model menu", 14, FontWeights.SemiBold),
                Ui.Secondary("Pick it for a chat from the model menu under the message box — that chat only. A chat keeps its Claude Code conversation from turn to turn.")));
            panel.Children.Add(head);
            foreach (var route in Service.Routes)
            {
                var info = ClaudeCodeRoutes.Models.FirstOrDefault(m => m.Alias == route.Model);
                var isPrimary = route.RouteId == primary;
                var row = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
                if (!isPrimary)
                {
                    var make = Ui.LinkButton("Make primary", () =>
                    {
                        _model.Config.MakePrimary(route.RouteId);
                        _model.Host.ResetRouteCache();
                        Refresh();
                    });
                    DockPanel.SetDock(make, Dock.Right);
                    row.Children.Add(make);
                }
                var glyph = Ui.Glyph(Icons.Code, 14);
                glyph.Margin = new Thickness(0, 0, 10, 0);
                DockPanel.SetDock(glyph, Dock.Left);
                row.Children.Add(glyph);
                row.Children.Add(Ui.Stack(Ui.Text($"Claude Code · {ClaudeCodeRoutes.ModelTitle(route.Model)}{(isPrimary ? "  (primary)" : "")}", 13),
                    Ui.Secondary(info?.Blurb is { } blurb ? char.ToUpperInvariant(blurb[0]) + blurb[1..] + "." : route.Model, 11.5)));
                panel.Children.Add(row);
            }
            return Ui.Card(panel);
        }
        var ready = Service.Status is ClaudeCodeStatus.Ready or ClaudeCodeStatus.SignedOut;
        var add = Ui.Button("Add Claude Code", () =>
        {
            Service.Use(_makePrimary ? RouteUse.Primary : RouteUse.JustAdd, ClaudeCodeRoutes.DefaultModel, Service.UsesWsl, Service.WslDistro);
            Refresh();
        }, accent: true);
        add.IsEnabled = ready;
        var addHead = new DockPanel();
        DockPanel.SetDock(add, Dock.Right);
        add.VerticalAlignment = VerticalAlignment.Top;
        addHead.Children.Add(add);
        addHead.Children.Add(Ui.Stack(Ui.Text("Not in the model menu yet", 14, FontWeights.SemiBold),
            Ui.Secondary(ready
                ? "Adds Claude Code · Opus, Sonnet and Haiku to every chat's model menu. Nothing changes for your other chats."
                : "Install Claude Code and sign in first (above).")));
        panel.Children.Add(addHead);
        var primaryBox = Ui.Check("Make Claude Code · Sonnet the primary (new projects start with it)", _makePrimary, on => _makePrimary = on);
        primaryBox.Margin = new Thickness(0, 10, 0, 0);
        primaryBox.IsEnabled = ready;
        panel.Children.Add(primaryBox);
        return Ui.Card(panel);
    }

    // MARK: How it works

    private static UIElement HowCard()
    {
        var panel = new StackPanel();
        void Point(string glyph, string title, string detail)
        {
            var icon = Ui.Glyph(glyph, 15, "AccentTextFillColorPrimaryBrush");
            icon.Margin = new Thickness(0, 2, 12, 0);
            icon.VerticalAlignment = VerticalAlignment.Top;
            var row = new DockPanel { Margin = new Thickness(0, 4, 0, 6) };
            DockPanel.SetDock(icon, Dock.Left);
            row.Children.Add(icon);
            row.Children.Add(Ui.Stack(Ui.Text(title, 13, FontWeights.SemiBold), Ui.Secondary(detail)));
            panel.Children.Add(row);
        }
        Point(Icons.Chat, "Each message is one Claude Code turn",
            "Vision runs claude in the chat's project folder and carries on the same Claude Code conversation every turn. Its replies, " +
            "thinking, tool calls, file changes and todo lists show in the chat as they happen, each with its time.");
        Point(Icons.Shield, "Your permission setting decides",
            "Workspace write: edits inside the project and commands that only read go ahead; anything else asks you here first. " +
            "Plan only: Claude Code researches and proposes a plan, and changes nothing. Full access: everything goes ahead.");
        Point(Icons.Play, "/goal, the task list and Stop work as with any model",
            "A /goal runs one Claude Code turn per round until it says the goal is done; tasks run one after another; Stop interrupts " +
            "Claude Code's turn. Run phases, time estimates and the Prompts timeline include its turns.");
        Point(Icons.Stopwatch, "What it used",
            "After each turn the chat shows its steps, tokens and Claude Code's own cost estimate. Your Claude plan covers it — unless " +
            "Claude Code is set up with an API key, which it then bills (the chat says so).");
        Point(Icons.Lock, "Not for Sensitive chats", ClaudeCodeRoutes.SensitiveReason + " A Sensitive chat (/sensitive on) can't use it — pick a private model there.");
        return Ui.Card(panel);
    }

    // MARK: Advanced

    private UIElement AdvancedCard()
    {
        var panel = new StackPanel();
        panel.Children.Add(Ui.Text("The claude program", 13.5));
        panel.Children.Add(Ui.Secondary("Leave empty to use the one on PATH (or where its installers put it). Set it when you have more than one."));
        var browse = Ui.Button("Browse…", () =>
        {
            var dialog = new OpenFileDialog
            {
                Title = "Choose the claude program",
                Filter = OperatingSystem.IsWindows() ? "claude (claude.exe, claude.cmd)|claude.exe;claude.cmd|All files|*.*" : "All files|*",
            };
            if (dialog.ShowDialog(Ui.Owner(this)) == true)
            {
                _path.Text = dialog.FileName;
                Service.SetPath(dialog.FileName);
            }
        });
        var use = Ui.Button("Use", () => Service.SetPath(_path.Text));
        var row = Ui.Buttons(_path, browse, use);
        row.Margin = new Thickness(0, 8, 0, 0);
        panel.Children.Add(row);

        if (OperatingSystem.IsWindows() || Service.WslDistros.Count > 0)
        {
            panel.Children.Add(Ui.Divider(12));
            var distros = new ComboBox { MinWidth = 200, IsEnabled = Service.UsesWsl && Service.WslDistros.Count > 0 };
            distros.Items.Add(new ComboBoxItem { Content = "Default distribution", Tag = null, IsSelected = Service.WslDistro is null });
            foreach (var d in Service.WslDistros) distros.Items.Add(new ComboBoxItem { Content = d, Tag = d, IsSelected = d == Service.WslDistro });
            distros.SelectionChanged += (_, _) =>
            {
                if (distros.SelectedItem is ComboBoxItem item && Service.UsesWsl) Service.SetWsl(true, item.Tag as string);
            };
            var toggle = Ui.Toggle(Service.UsesWsl, on => Service.SetWsl(on, on ? (distros.SelectedItem as ComboBoxItem)?.Tag as string : null));
            var wsl = new DockPanel();
            DockPanel.SetDock(toggle, Dock.Right);
            wsl.Children.Add(toggle);
            wsl.Children.Add(Ui.Stack(Ui.Text("Run Claude Code inside WSL", 13.5),
                Ui.Secondary("For a claude installed in Linux under WSL: Vision starts it there (wsl.exe) in the same project folder. " +
                             "Files it changes show here as Windows paths.")));
            panel.Children.Add(wsl);
            var pick = Ui.Buttons(Ui.Secondary("Distribution:"), distros);
            pick.Margin = new Thickness(0, 8, 0, 0);
            panel.Children.Add(pick);
        }
        return Ui.Card(panel);
    }

    // MARK: Self-test

    internal void ScrollTo(double fraction)
    {
        if (Content is not ScrollViewer scroll) return;
        scroll.UpdateLayout();
        scroll.ScrollToVerticalOffset(scroll.ScrollableHeight * fraction);
    }
}
