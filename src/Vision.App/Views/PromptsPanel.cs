using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Vision.App.Infrastructure;
using Vision.App.Model;
using Vision.Core;

namespace Vision.App.Views;

// MARK: - The right column's two tabs: Tasks and Prompts
//
// The task list panel and the Prompts panel share the right-hand column, one at a time, switched by the tabs at its top
// (Ctrl+Shift+Q shows Tasks, Ctrl+Shift+H shows Prompts). Tasks: what the chat is doing now — the running prompt's or
// /goal's plan first — and what is queued after it. Prompts: every prompt the chat has sent, with how it ended.

public enum RunTab { Tasks, Prompts }

/// <summary>The column's frame: the Tasks | Prompts tabs and the close button, over whichever panel is showing.</summary>
public sealed class RunPanelHost : UserControl
{
    private readonly ContentControl _content = new() { Focusable = false };
    private readonly ToggleButton2 _tasks;
    private readonly ToggleButton2 _prompts;
    private readonly UIElement _tasksPanel;
    private readonly UIElement _promptsPanel;

    public RunTab Tab { get; private set; } = RunTab.Tasks;

    /// <summary>Raised when the user closes the column.</summary>
    public event Action? CloseRequested;
    /// <summary>Raised when the tab changes (the window keeps the shortcut's toggle right).</summary>
    public event Action? TabChanged;

    public RunPanelHost(UIElement tasks, UIElement prompts)
    {
        _tasksPanel = tasks;
        _promptsPanel = prompts;
        SetResourceReference(BackgroundProperty, "LayerFillColorDefaultBrush");
        _tasks = new ToggleButton2("Tasks", Icons.Queue, "Tasks (Ctrl+Shift+Q) — what this chat is doing now and what is queued after it", () => Show(RunTab.Tasks));
        _prompts = new ToggleButton2("Prompts", Icons.History, "Prompts (Ctrl+Shift+H) — every prompt this chat has sent, and how it ended", () => Show(RunTab.Prompts));
        var strip = new DockPanel { Margin = new Thickness(8, 6, 6, 4) };
        var close = new Button { Content = Icons.Close, ToolTip = "Hide this column", Margin = new Thickness(2, 0, 0, 0) };
        close.SetResourceReference(StyleProperty, "IconButton");
        System.Windows.Automation.AutomationProperties.SetName(close, "Hide this column");
        close.Click += (_, _) => CloseRequested?.Invoke();
        DockPanel.SetDock(close, Dock.Right);
        strip.Children.Add(close);
        var tabs = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(2), HorizontalAlignment = HorizontalAlignment.Left };
        tabs.SetResourceReference(Border.BackgroundProperty, "SubtleFillColorSecondaryBrush");
        tabs.Child = Ui.Stack(Orientation.Horizontal, _tasks, _prompts);
        strip.Children.Add(tabs);

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var rule = Ui.Divider();
        Grid.SetRow(strip, 0);
        Grid.SetRow(rule, 1);
        Grid.SetRow(_content, 2);
        grid.Children.Add(strip);
        grid.Children.Add(rule);
        grid.Children.Add(_content);
        var edge = new Border { Child = grid, BorderThickness = new Thickness(1, 0, 0, 0) };
        edge.SetResourceReference(Border.BorderBrushProperty, "DividerStrokeColorDefaultBrush");
        Content = edge;
        Show(RunTab.Tasks);
    }

    public void Show(RunTab tab)
    {
        Tab = tab;
        _content.Content = tab == RunTab.Tasks ? _tasksPanel : _promptsPanel;
        _tasks.IsOn = tab == RunTab.Tasks;
        _prompts.IsOn = tab == RunTab.Prompts;
        TabChanged?.Invoke();
    }

    /// <summary>A small segmented tab.</summary>
    private sealed class ToggleButton2 : Button
    {
        private readonly TextBlock _label;
        private readonly TextBlock _glyph;

        public ToggleButton2(string text, string glyph, string tooltip, Action click)
        {
            _glyph = Ui.Glyph(glyph, 12);
            _glyph.Margin = new Thickness(0, 0, 6, 0);
            _label = Ui.Text(text, 12.5, wrap: false);
            Content = Ui.Stack(Orientation.Horizontal, _glyph, _label);
            Padding = new Thickness(10, 3, 10, 3);
            ToolTip = tooltip;
            SetResourceReference(StyleProperty, "SubtleButton");
            System.Windows.Automation.AutomationProperties.SetName(this, text);
            Click += (_, _) => click();
        }

        public bool IsOn
        {
            set
            {
                if (value) SetResourceReference(BackgroundProperty, "ControlFillColorDefaultBrush");
                else Background = Brushes.Transparent;
                _label.FontWeight = value ? FontWeights.SemiBold : FontWeights.Normal;
                _glyph.SetResourceReference(TextBlock.ForegroundProperty, value ? "AccentTextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush");
            }
        }
    }
}

/// <summary>What the panels and windows do with a prompt that needs the main window: jump to it in its chat, or drop its
/// quote into the composer.</summary>
/// <param name="CopyText">What Copy puts on the clipboard for a prompt: as it was sent, with the text of the large pastes it
/// carried (10-19 #13); the record's own text when not given.</param>
public sealed record PromptActions(Action<PromptRecord> Jump, Action<PromptRecord> AddToComposer, Func<PromptRecord, string>? CopyText = null);

/// <summary>The Prompts tab: every prompt the open chat has sent, newest first under day headings, with its outcome, when,
/// how long against the estimate; select one for the whole of it, its evidence and the actions (copy, add to composer, jump
/// to it in the transcript, open its results).</summary>
public sealed class PromptsPanel : UserControl
{
    private readonly AppModel _model;
    private readonly PromptActions _actions;
    private AgentHost Host => _model.Host;
    private readonly TextBlock _subtitle = Ui.Text("", 11.5, brushKey: "TextFillColorTertiaryBrush", wrap: false);
    private readonly TextBox _search = Ui.Field(placeholder: "Search this chat's prompts");
    private readonly StackPanel _list = new() { Margin = new Thickness(6) };
    private readonly ContentControl _detail = new() { Focusable = false };
    private readonly Border _detailHost;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private SessionVM? _watched;
    private string? _selectedId;
    private bool _queued;

    public PromptsPanel(AppModel model, PromptActions actions)
    {
        _model = model;
        _actions = actions;
        var header = new DockPanel { Margin = new Thickness(12, 8, 8, 6) };
        var all = Ui.LinkButton("All prompts…", () => AllPromptsWindow.Show(Window.GetWindow(this), _model, _actions));
        all.ToolTip = "Every prompt on this PC, across chats (Ctrl+Alt+H)";
        DockPanel.SetDock(all, Dock.Right);
        header.Children.Add(all);
        var titles = new StackPanel();
        titles.Children.Add(Ui.Text("Prompts", 14, FontWeights.SemiBold, wrap: false));
        _subtitle.TextTrimming = TextTrimming.CharacterEllipsis;
        titles.Children.Add(_subtitle);
        header.Children.Add(titles);
        _search.Margin = new Thickness(12, 0, 12, 8);
        _search.TextChanged += (_, _) => QueueRefresh();

        var scroll = new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        _detailHost = new Border { Child = new ScrollViewer { Content = _detail, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 360 }, Padding = new Thickness(12, 10, 12, 12) };

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var rule = Ui.Divider();
        Grid.SetRow(header, 0);
        Grid.SetRow(_search, 1);
        Grid.SetRow(scroll, 2);
        Grid.SetRow(rule, 3);
        Grid.SetRow(_detailHost, 4);
        foreach (var child in new UIElement[] { header, _search, scroll, rule, _detailHost }) grid.Children.Add(child);
        Content = grid;

        Host.PropertyChanged += OnHostChanged;
        Host.RunsChanged += QueueRefresh;
        _clock.Tick += (_, _) =>
        {
            // A running prompt's clock moves: redraw the rows (never under a click in progress).
            if (_watched?.CurrentPrompt is null || (IsMouseOver && Mouse.LeftButton == MouseButtonState.Pressed)) return;
            Refresh();
        };
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                Watch(Host.Selected);
                Refresh();
                _clock.Start();
            }
            else _clock.Stop();
        };
        Watch(Host.Selected);
    }

    private void OnHostChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(AgentHost.SelectedId) or nameof(AgentHost.Selected))) return;
        _selectedId = null;
        Watch(Host.Selected);
        QueueRefresh();
    }

    private void Watch(SessionVM? session)
    {
        if (ReferenceEquals(_watched, session)) return;
        if (_watched is not null) _watched.PromptsChanged -= QueueRefresh;
        _watched = session;
        if (_watched is not null) _watched.PromptsChanged += QueueRefresh;
    }

    private void QueueRefresh()
    {
        if (_queued) return;
        _queued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _queued = false;
            if (IsVisible || !IsLoaded) Refresh();
        });
    }

    /// <summary>Select a prompt (the self-test and "Open in its chat" use it).</summary>
    public void Select(string? recordId)
    {
        _selectedId = recordId;
        Refresh();
    }

    public void Refresh()
    {
        _list.Children.Clear();
        if (_watched is not { } vm)
        {
            _subtitle.Text = "No chat open";
            _list.Children.Add(PromptRows.Empty("Open a chat to see the prompts it has sent — or open All prompts (Ctrl+Alt+H)."));
            _detailHost.Visibility = Visibility.Collapsed;
            return;
        }
        var records = Host.PromptsOf(vm);
        var shown = PromptTimeline.Filter(records, _search.Text);
        _subtitle.Text = $"{records.Count} prompt{(records.Count == 1 ? "" : "s")} in “{vm.Title}”";
        _subtitle.ToolTip = vm.Title;
        if (records.Count == 0)
            _list.Children.Add(PromptRows.Empty("Nothing sent in this chat yet. Every prompt you send shows here, with how it ended."));
        else if (shown.Count == 0)
            _list.Children.Add(PromptRows.Empty($"No prompt here matches “{_search.Text.Trim()}”."));
        var now = DateTimeOffset.Now;
        foreach (var group in PromptTimeline.Grouped(shown, now))
        {
            _list.Children.Add(PromptRows.DayHeading(group.Title));
            foreach (var record in group.Records)
                _list.Children.Add(PromptRows.Row(record, now, record.Id == _selectedId, chatTitle: null,
                    select: () => Select(record.Id), open: () => _actions.Jump(record)));
        }
        var selected = _selectedId is { } id ? records.FirstOrDefault(r => r.Id == id) : null;
        _detailHost.Visibility = selected is null ? Visibility.Collapsed : Visibility.Visible;
        // The detail is rebuilt only when what it shows changed, so text selected in it stays selected.
        var stamp = selected is null ? "" : $"{selected.Id}|{selected.Outcome}|{selected.FinishedAt}|{selected.Evidence.Count}|{selected.Attempts}|{selected.Rounds}";
        if (stamp != _detailStamp)
        {
            _detailStamp = stamp;
            _detail.Content = selected is null ? null : PromptRows.Detail(selected, vm.Title, _actions, Window.GetWindow(this));
        }
    }

    private string _detailStamp = "";
}

/// <summary>How a prompt is drawn — the same in the Prompts panel and All prompts.</summary>
internal static class PromptRows
{
    public static (string Glyph, string Brush) Mark(PromptRecord record) => record.Outcome switch
    {
        PromptOutcome.Queued => (Icons.Recent, "TextFillColorSecondaryBrush"),
        PromptOutcome.Running => (Icons.Play, "AccentTextFillColorPrimaryBrush"),
        PromptOutcome.Completed => (Icons.Completed, "SystemFillColorSuccessBrush"),
        PromptOutcome.Issues => (Icons.Warning, "SystemFillColorCautionBrush"),
        PromptOutcome.NeedsYou => (Icons.Pause, "SystemFillColorCautionBrush"),
        PromptOutcome.Failed => (Icons.Error, "SystemFillColorCriticalBrush"),
        PromptOutcome.Stopped => (Icons.Stop, "TextFillColorTertiaryBrush"),
        _ => (Icons.Info, "TextFillColorTertiaryBrush"),
    };

    public static UIElement DayHeading(string title)
    {
        var text = Ui.Text(title, 11.5, FontWeights.SemiBold, "TextFillColorSecondaryBrush", wrap: false);
        text.Margin = new Thickness(4, 10, 0, 4);
        return text;
    }

    public static UIElement Empty(string message)
    {
        var text = Ui.Text(message, 12, brushKey: "TextFillColorTertiaryBrush");
        text.TextAlignment = TextAlignment.Center;
        text.Margin = new Thickness(18, 36, 18, 18);
        return text;
    }

    /// <summary>One prompt: the outcome glyph, its first line, its source and when, and the timing line.</summary>
    public static Border Row(PromptRecord record, DateTimeOffset now, bool selected, string? chatTitle, Action select, Action open)
    {
        var (glyph, brush) = Mark(record);
        var icon = Ui.Glyph(glyph, 12, brush);
        icon.Width = 18;
        icon.VerticalAlignment = VerticalAlignment.Top;
        icon.Margin = new Thickness(0, 2, 8, 0);
        icon.ToolTip = record.Headline;
        var lines = new StackPanel();
        var title = Ui.Text(record.FirstLine, 12.5, selected ? FontWeights.SemiBold : FontWeights.Normal, wrap: false);
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        lines.Children.Add(title);
        // Under a day heading the time is enough; the full date is in the details.
        var head = $"{record.Headline} · {record.Source.Label()} · {record.At.ToLocalTime().ToString("t", System.Globalization.CultureInfo.CurrentCulture)}"
                   + (chatTitle is null ? "" : $" · {chatTitle}");
        var meta = Ui.Text(head, 11, brushKey: brush == "TextFillColorTertiaryBrush" ? "TextFillColorTertiaryBrush" : brush, wrap: false);
        meta.TextTrimming = TextTrimming.CharacterEllipsis;
        lines.Children.Add(meta);
        var timing = PromptTimeline.Timing(record, now);
        if (timing.Length > 0)
        {
            var line = Ui.Text(timing, 11, brushKey: "TextFillColorTertiaryBrush", wrap: false);
            line.TextTrimming = TextTrimming.CharacterEllipsis;
            lines.Children.Add(line);
        }
        var dock = new DockPanel();
        DockPanel.SetDock(icon, Dock.Left);
        dock.Children.Add(icon);
        dock.Children.Add(lines);
        var row = new Border
        {
            Child = dock, Tag = record.Id, Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(0, 0, 0, 2),
            CornerRadius = new CornerRadius(6), Background = Brushes.Transparent, Cursor = Cursors.Hand,
            ToolTip = TextUtil.Prefix(record.Text, 600) + "\n\nClick for the details · double-click to jump to it in its chat",
        };
        if (selected) row.SetResourceReference(Border.BackgroundProperty, "SubtleFillColorSecondaryBrush");
        row.MouseEnter += (_, _) => row.SetResourceReference(Border.BackgroundProperty, "SubtleFillColorSecondaryBrush");
        row.MouseLeave += (_, _) =>
        {
            if (!selected) row.Background = Brushes.Transparent;
        };
        row.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            select();
        };
        row.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount != 2) return;
            e.Handled = true;
            open();
        };
        return row;
    }

    /// <summary>The whole of a prompt: its text, how it went, the evidence, and the actions.</summary>
    public static UIElement Detail(PromptRecord record, string chatTitle, PromptActions actions, Window? owner)
    {
        var panel = new StackPanel();
        var (glyph, brush) = Mark(record);
        var head = new DockPanel();
        var icon = Ui.Glyph(glyph, 13, brush);
        icon.Margin = new Thickness(0, 0, 8, 0);
        DockPanel.SetDock(icon, Dock.Left);
        head.Children.Add(icon);
        head.Children.Add(Ui.Text(record.Headline, 13, FontWeights.SemiBold, brush, wrap: false));
        panel.Children.Add(head);
        var meta = Ui.Text(MetaLine(record, chatTitle), 11.5, brushKey: "TextFillColorSecondaryBrush");
        meta.Margin = new Thickness(0, 3, 0, 6);
        panel.Children.Add(meta);
        var text = new TextBox
        {
            Text = record.Text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 120, BorderThickness = new Thickness(1),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(6, 4, 6, 4), FontSize = 12,
        };
        WheelBubbling.SetIsEnabled(text, false);
        panel.Children.Add(text);
        if (record.Reason is { Length: > 0 } reason && record.Outcome != PromptOutcome.Completed)
        {
            var why = Ui.Text(reason, 11.5, brushKey: brush);
            why.Margin = new Thickness(0, 6, 0, 0);
            panel.Children.Add(why);
        }
        if (record.Evidence.Count > 0)
        {
            var label = Ui.Text("Evidence", 11.5, FontWeights.SemiBold, "TextFillColorSecondaryBrush", wrap: false);
            label.Margin = new Thickness(0, 8, 0, 2);
            panel.Children.Add(label);
            foreach (var item in record.Evidence.Take(6))
            {
                var line = Ui.Text($"• {item.Title} — {TextUtil.Prefix(item.Detail.Replace('\n', ' '), 140)}", 11, brushKey: "TextFillColorSecondaryBrush");
                line.ToolTip = item.Detail;
                panel.Children.Add(line);
            }
        }
        if (record.Backfilled)
        {
            var note = Ui.Text("Sent before Vision kept a timeline; its ending was read from the chat.", 11, brushKey: "TextFillColorTertiaryBrush");
            note.Margin = new Thickness(0, 6, 0, 0);
            panel.Children.Add(note);
        }
        // (A wrap panel of its own, so the buttons wrap in a narrow column instead of being cut off.)
        var buttons = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        foreach (var button in new[]
                 {
                     CopyButton(() => actions.CopyText?.Invoke(record) ?? record.Text),
                     Small("Add to composer", () => actions.AddToComposer(record), "Quote it in the composer, with how it ended, to talk about it"),
                     Small("Jump to it", () => actions.Jump(record), "Show it in the transcript (double-click a row does the same)"),
                     Small("Results…", () => PromptResultsWindow.Show(owner, record, chatTitle), "Its results and completion notes"),
                 })
        {
            button.Margin = new Thickness(0, 0, 6, 6);
            buttons.Children.Add(button);
        }
        panel.Children.Add(buttons);
        return panel;
    }

    public static string MetaLine(PromptRecord record, string? chatTitle)
    {
        var parts = new List<string> { record.Source.Label(), "sent " + Formatting.FullStamp(record.At) };
        if (chatTitle is not null) parts.Add("in “" + chatTitle + "”");
        if (PromptTimeline.Timing(record, DateTimeOffset.Now) is { Length: > 0 } timing) parts.Add(timing);
        if (PromptTimeline.VersusEstimate(record) is { } versus) parts.Add(versus);
        if (RunActivity.Placement(record.Model, record.Route) is { } where) parts.Add(where);
        if (record.PromptTokens + record.CompletionTokens > 0) parts.Add($"{record.PromptTokens:N0} in · {record.CompletionTokens:N0} out");
        return string.Join(" · ", parts);
    }

    public static void Copy(string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch (Exception)
        {
            // Another app holds the clipboard.
        }
    }

    /// <summary>"Copy" that says "Copied" for a moment once the prompt is on the clipboard (10-19 #13).</summary>
    public static Button CopyButton(Func<string> text)
    {
        Button? button = null;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = Vision.App.Views.Chat.CopyButton.CopiedFor };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (button is not null) button.Content = "Copy";
        };
        button = Small("Copy", () =>
        {
            var ok = Vision.App.Views.Chat.ChatView.SetClipboard(text());
            button!.Content = ok ? "✓ Copied" : "Couldn't copy";
            timer.Stop();
            timer.Start();
        }, "Copy the prompt as it was sent (with any large paste it carried)");
        return button;
    }

    public static Button Small(string text, Action action, string? tooltip = null)
    {
        var button = Ui.Button(text, action, tooltip: tooltip);
        button.MinWidth = 0;
        button.Padding = new Thickness(10, 3, 10, 3);
        return button;
    }
}

/// <summary>All prompts: every prompt on this PC across chats, newest first under day headings, searchable. Double-click one to
/// open it in its chat.</summary>
public sealed class AllPromptsWindow : Window
{
    private static AllPromptsWindow? _open;
    private readonly AppModel _model;
    private readonly PromptActions _actions;
    private readonly TextBox _search = Ui.Field(placeholder: "Search every prompt — words from it, its outcome, its chat, its model");
    private readonly StackPanel _list = new() { Margin = new Thickness(12, 6, 12, 12) };
    private readonly TextBlock _count = Ui.Text("", 12, brushKey: "TextFillColorSecondaryBrush", wrap: false);
    private readonly ContentControl _detail = new() { Focusable = false };
    private IReadOnlyList<PromptRecord> _records = [];
    private string? _selectedId;

    public static void Show(Window? owner, AppModel model, PromptActions actions)
    {
        if (_open is { IsLoaded: true })
        {
            _open.Reload();
            _open.Activate();
            return;
        }
        _open = new AllPromptsWindow(model, actions) { Owner = owner };
        _open.Closed += (_, _) => _open = null;
        _open.Show();
    }

    public AllPromptsWindow(AppModel model, PromptActions actions)
    {
        _model = model;
        _actions = actions;
        Title = "All Prompts — Vision";
        Width = 980;
        Height = 680;
        MinWidth = 640;
        MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "SolidBackgroundFillColorBaseBrush");

        var header = new DockPanel { Margin = new Thickness(16, 12, 16, 8) };
        var done = Ui.Button("Done", Close, accent: true);
        done.IsCancel = true;
        DockPanel.SetDock(done, Dock.Right);
        header.Children.Add(done);
        var titles = new StackPanel();
        titles.Children.Add(Ui.Subtitle("All prompts"));
        titles.Children.Add(_count);
        header.Children.Add(titles);
        _search.Margin = new Thickness(16, 0, 16, 8);
        _search.TextChanged += (_, _) => Draw();

        var listScroll = new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var detailHost = new Border { Child = new ScrollViewer { Content = _detail, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, Padding = new Thickness(14), BorderThickness = new Thickness(1, 0, 0, 0) };
        detailHost.SetResourceReference(Border.BorderBrushProperty, "DividerStrokeColorDefaultBrush");
        detailHost.SetResourceReference(Border.BackgroundProperty, "LayerFillColorDefaultBrush");
        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.4, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 280 });
        Grid.SetColumn(detailHost, 1);
        body.Children.Add(listScroll);
        body.Children.Add(detailHost);

        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(_search, Dock.Top);
        var hint = Ui.Text("Double-click a prompt to open it in its chat.", 11.5, brushKey: "TextFillColorTertiaryBrush", wrap: false);
        hint.Margin = new Thickness(16, 4, 16, 10);
        DockPanel.SetDock(hint, Dock.Bottom);
        root.Children.Add(header);
        root.Children.Add(_search);
        root.Children.Add(hint);
        root.Children.Add(body);
        Content = root;
        // Prompts change often while chats work: read every chat's timeline at most twice a second.
        _later.Tick += (_, _) =>
        {
            _later.Stop();
            Reload();
        };
        void Soon()
        {
            if (!_later.IsEnabled) _later.Start();
        }
        _model.Host.RunsChanged += Soon;
        Closed += (_, _) =>
        {
            _model.Host.RunsChanged -= Soon;
            _later.Stop();
        };
        Reload();
    }

    private readonly DispatcherTimer _later = new() { Interval = TimeSpan.FromMilliseconds(500) };

    public void Reload()
    {
        _records = _model.Host.AllPromptRecords();
        Draw();
    }

    /// <summary>Select one (the self-test).</summary>
    public void Select(string id)
    {
        _selectedId = id;
        Draw();
    }

    private Dictionary<string, string> Titles() => _model.Host.Sessions.ToDictionary(s => s.Id, s => s.Title);

    private void Draw()
    {
        var titles = Titles();
        var shown = PromptTimeline.Filter(_records, _search.Text, titles);
        var chats = _records.Select(r => r.ChatId).Distinct().Count();
        _count.Text = $"{_records.Count} prompt{(_records.Count == 1 ? "" : "s")} across {chats} chat{(chats == 1 ? "" : "s")} on this PC"
                      + (shown.Count != _records.Count ? $" · {shown.Count} shown" : "");
        _list.Children.Clear();
        if (_records.Count == 0) _list.Children.Add(PromptRows.Empty("No prompts yet. Every prompt you send, in any chat, shows here."));
        else if (shown.Count == 0) _list.Children.Add(PromptRows.Empty($"Nothing matches “{_search.Text.Trim()}”."));
        var now = DateTimeOffset.Now;
        foreach (var group in PromptTimeline.Grouped(shown, now))
        {
            _list.Children.Add(PromptRows.DayHeading(group.Title));
            foreach (var record in group.Records)
            {
                var title = titles.GetValueOrDefault(record.ChatId) ?? "a deleted chat";
                _list.Children.Add(PromptRows.Row(record, now, record.Id == _selectedId, title, () => Select(record.Id), () => Open(record)));
            }
        }
        var selected = _selectedId is { } id ? _records.FirstOrDefault(r => r.Id == id) : null;
        _detail.Content = selected is null
            ? PromptRows.Empty("Select a prompt to see all of it, how it ended and its evidence.")
            : PromptRows.Detail(selected, titles.GetValueOrDefault(selected.ChatId) ?? "a deleted chat",
                _actions with { Jump = Open }, this);
    }

    private void Open(PromptRecord record)
    {
        _actions.Jump(record);
        Owner?.Activate();
    }
}

/// <summary>A prompt's results and completion notes, in full, to read and copy.</summary>
public sealed class PromptResultsWindow : Window
{
    public static void Show(Window? owner, PromptRecord record, string chatTitle) =>
        new PromptResultsWindow(record, chatTitle) { Owner = owner }.Show();

    public static string Text(PromptRecord record, string chatTitle)
    {
        var lines = new List<string>
        {
            record.Headline, PromptRows.MetaLine(record, chatTitle), "", "Prompt:", record.Text.Trim(),
        };
        if (record.Reason is { Length: > 0 } reason && record.Outcome != PromptOutcome.Completed) lines.AddRange(["", "Why: " + reason]);
        if (record.Evidence.Count > 0)
        {
            lines.AddRange(["", "Evidence:"]);
            foreach (var item in record.Evidence)
                lines.Add($"• {item.Kind.Label()} · {item.Title} ({Formatting.Stamp(item.At)})" + (item.Detail.Trim().Length > 0 ? "\n  " + item.Detail.Trim().Replace("\n", "\n  ") : ""));
        }
        if (record.CompletionNote is { Length: > 0 } note) lines.AddRange(["", "Completion notes:", note.Trim()]);
        if (record.Backfilled) lines.AddRange(["", "Sent before Vision kept a timeline; its ending was read from the chat."]);
        return string.Join(Environment.NewLine, lines);
    }

    public PromptResultsWindow(PromptRecord record, string chatTitle)
    {
        Title = $"Results — {TextUtil.Prefix(record.FirstLine, 60)}";
        Width = 720;
        Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        var text = new TextBox
        {
            Text = Text(record, chatTitle), IsReadOnly = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(16), FontSize = 12.5,
        };
        text.SetResourceReference(BackgroundProperty, "LayerFillColorDefaultBrush");
        var header = new DockPanel { Margin = new Thickness(16, 12, 16, 10) };
        var done = Ui.Button("Done", Close, accent: true);
        done.IsCancel = true;
        DockPanel.SetDock(done, Dock.Right);
        header.Children.Add(done);
        var copy = Ui.Button("Copy", () => PromptRows.Copy(text.Text));
        copy.Margin = new Thickness(0, 0, 8, 0);
        DockPanel.SetDock(copy, Dock.Right);
        header.Children.Add(copy);
        var (glyph, brush) = PromptRows.Mark(record);
        var icon = Ui.Glyph(glyph, 16, brush);
        icon.Margin = new Thickness(0, 0, 10, 0);
        DockPanel.SetDock(icon, Dock.Left);
        header.Children.Add(icon);
        header.Children.Add(Ui.Subtitle(record.Headline));
        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        root.Children.Add(text);
        Content = root;
    }
}

/// <summary>The stamped log behind one line of the activity menu, live: a subagent's own log, the main agent's steps.</summary>
public sealed class ActivityLogWindow : Window
{
    private readonly TextBox _text = new()
    {
        IsReadOnly = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0),
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(14), FontSize = 12,
    };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

    public ActivityLogWindow(string title, Func<string> text)
    {
        Title = title;
        Width = 760;
        Height = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        _text.SetResourceReference(FontFamilyProperty, "MonoFont");
        _text.SetResourceReference(BackgroundProperty, "LayerFillColorDefaultBrush");
        var header = new DockPanel { Margin = new Thickness(16, 12, 16, 10) };
        var done = Ui.Button("Done", Close, accent: true);
        done.IsCancel = true;
        DockPanel.SetDock(done, Dock.Right);
        header.Children.Add(done);
        var copy = Ui.Button("Copy", () => PromptRows.Copy(_text.Text));
        copy.Margin = new Thickness(0, 0, 8, 0);
        DockPanel.SetDock(copy, Dock.Right);
        header.Children.Add(copy);
        var subtitle = Ui.Subtitle(title);
        subtitle.TextTrimming = TextTrimming.CharacterEllipsis;
        header.Children.Add(subtitle);
        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        root.Children.Add(_text);
        Content = root;
        void Reload()
        {
            var next = text();
            if (next == _text.Text) return;
            var atEnd = _text.VerticalOffset >= _text.ExtentHeight - _text.ViewportHeight - 4;
            _text.Text = next;
            if (atEnd) _text.ScrollToEnd();
        }
        _timer.Tick += (_, _) => Reload();
        Loaded += (_, _) => _timer.Start();
        Closed += (_, _) => _timer.Stop();
        Reload();
    }
}
