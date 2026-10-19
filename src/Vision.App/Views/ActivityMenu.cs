using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Vision.App.Infrastructure;
using Vision.App.Model;
using Vision.Core;

namespace Vision.App.Views;

/// <summary>The background-activity menu (the top bar's activity button, Ctrl+Shift+A): everything working right now, across
/// chats — each chat's main agent, its subagents and background agents, its task list — with the model and server each
/// really runs on, what it was assigned (the plan step, the task), its status, how long it has run and what it used; open
/// its log, or stop it. The model servers subagents share, and how busy each is, are at the bottom.</summary>
public sealed class ActivityMenu : UserControl
{
    private readonly AppModel _model;
    private AgentHost Host => _model.Host;
    private readonly StackPanel _list = new();
    private readonly StackPanel _servers = new() { Margin = new Thickness(14, 4, 14, 10) };
    private readonly TextBlock _summary = Ui.Text("", 11.5, brushKey: "TextFillColorSecondaryBrush", wrap: false);
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private string _drawn = "";

    /// <summary>Raised when an action means the menu should close (opening a chat).</summary>
    public event Action? Dismiss;

    public ActivityMenu(AppModel model)
    {
        _model = model;
        Width = 470;
        var header = new DockPanel { Margin = new Thickness(14, 10, 10, 6) };
        var stopAll = Ui.LinkButton("Stop all", () => Host.StopAll());
        stopAll.ToolTip = "Stop every agent and task list (Ctrl+Shift+.)";
        DockPanel.SetDock(stopAll, Dock.Right);
        header.Children.Add(stopAll);
        var titles = new StackPanel();
        titles.Children.Add(Ui.Text("Background activity", 14, FontWeights.SemiBold, wrap: false));
        titles.Children.Add(_summary);
        header.Children.Add(titles);
        var scroll = new ScrollViewer
        {
            Content = _list, MaxHeight = 520, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(8, 0, 8, 6),
        };
        var root = new StackPanel();
        root.Children.Add(header);
        root.Children.Add(Ui.Divider());
        root.Children.Add(scroll);
        root.Children.Add(Ui.Divider());
        root.Children.Add(_servers);
        var card = new Border { Child = root, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1) };
        card.SetResourceReference(Border.BackgroundProperty, "SolidBackgroundFillColorTertiaryBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "SurfaceStrokeColorFlyoutBrush");
        Content = card;
        _timer.Tick += (_, _) => Refresh();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                _drawn = "";
                Refresh();
                _timer.Start();
            }
            else _timer.Stop();
        };
        Refresh();
    }

    public void Refresh()
    {
        var items = Host.ActivityItems();
        var now = DateTimeOffset.Now;
        var count = RunActivity.Count(items);
        _summary.Text = count == 0 ? "Nothing is working right now." : $"{count} working" + (items.Count > count ? $" · {items.Count - count} waiting" : "");
        // Redraw only when what is listed changed; the clocks and counters are rewritten in place otherwise (a click must
        // not lose its button to a redraw).
        var shape = string.Join("|", items.Select(i => $"{i.Id}:{i.Status}:{i.Placement}:{i.Assignment}:{i.Running}"));
        if (shape != _drawn)
        {
            _drawn = shape;
            _list.Children.Clear();
            _clocks.Clear();
            if (items.Count == 0)
            {
                var empty = Ui.Text("When a chat works — a prompt, a /goal, a task list, a subagent — it shows here with the model it runs on and what it was given.", 12, brushKey: "TextFillColorTertiaryBrush");
                empty.Margin = new Thickness(8, 14, 8, 14);
                _list.Children.Add(empty);
            }
            foreach (var group in items.GroupBy(i => i.ChatId))
            {
                var title = Ui.Text(group.First().ChatTitle, 11.5, FontWeights.SemiBold, "TextFillColorSecondaryBrush", wrap: false);
                title.Margin = new Thickness(6, 10, 0, 4);
                title.TextTrimming = TextTrimming.CharacterEllipsis;
                _list.Children.Add(title);
                foreach (var item in group) _list.Children.Add(Row(item, now));
            }
        }
        else
        {
            foreach (var (label, item) in _clocks) label.Text = Meta(item, now);
        }
        DrawServers();
    }

    private readonly List<(TextBlock Label, ActivityItem Item)> _clocks = [];

    private UIElement Row(ActivityItem item, DateTimeOffset now)
    {
        var panel = new StackPanel();
        var head = new DockPanel();
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        actions.Children.Add(Icon(Icons.History, "Open its log", () => OpenLog(item)));
        if (item.ChatId is { } chatId) actions.Children.Add(Icon(Icons.Chat, "Open its chat", () =>
        {
            _model.OpenSession(chatId);
            Dismiss?.Invoke();
        }));
        if (item.Kind == ActivityKind.ChatTasks)
        {
            if (item.ChatId is { } id && Host.ChatTasksPaused(id) is not null)
                actions.Children.Add(Icon(Icons.Play, "Resume this chat's tasks", () => Host.ResumeChatTasks(id)));
        }
        else if (item.CanStop)
        {
            actions.Children.Add(Icon(Icons.Stop, item.Kind == ActivityKind.Subagent ? "Stop it (stops the turn it works for)" : "Stop it",
                () => Host.StopActivity(item), "SystemFillColorCriticalBrush"));
        }
        DockPanel.SetDock(actions, Dock.Right);
        head.Children.Add(actions);
        var (glyph, brush) = item.Kind switch
        {
            ActivityKind.MainAgent => (Icons.Robot, "SystemFillColorSuccessBrush"),
            ActivityKind.Subagent => (Icons.People, "AccentTextFillColorPrimaryBrush"),
            ActivityKind.BackgroundAgent => (Icons.Sync, "AccentTextFillColorPrimaryBrush"),
            ActivityKind.QueueRunner => (Icons.Queue, "AccentTextFillColorPrimaryBrush"),
            _ => (Icons.Tray, "TextFillColorSecondaryBrush"),
        };
        var icon = Ui.Glyph(glyph, 13, brush);
        icon.Margin = new Thickness(0, 1, 9, 0);
        icon.VerticalAlignment = VerticalAlignment.Top;
        icon.ToolTip = RunActivity.KindLabel(item.Kind);
        DockPanel.SetDock(icon, Dock.Left);
        head.Children.Add(icon);
        var lines = new StackPanel();
        var title = Ui.Text(item.Title, 12.5, FontWeights.SemiBold, wrap: false);
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        lines.Children.Add(title);
        if (item.Assignment is { } assignment) lines.Children.Add(Line(assignment, "TextFillColorPrimaryBrush"));
        // Waiting tasks run nowhere yet: no placement line for them.
        if (item.Kind != ActivityKind.ChatTasks)
            lines.Children.Add(Line(item.Placement is { } where ? "Runs on " + where : "Not on a model server yet", item.Placement is null ? "TextFillColorTertiaryBrush" : "AccentTextFillColorPrimaryBrush"));
        var meta = Line(Meta(item, now), "TextFillColorSecondaryBrush");
        _clocks.Add((meta, item));
        lines.Children.Add(meta);
        if (item.Activity is { Length: > 0 } activity) lines.Children.Add(Line(activity, "TextFillColorTertiaryBrush"));
        head.Children.Add(lines);
        panel.Children.Add(head);
        var row = new Border { Child = panel, Padding = new Thickness(8, 6, 6, 6), Margin = new Thickness(0, 0, 0, 2), CornerRadius = new CornerRadius(6), Background = Brushes.Transparent };
        row.ToolTip = string.Join("\n", new[] { item.Title, item.Assignment, item.Placement is null ? null : "Runs on " + item.Placement, item.Status, item.Activity }.Where(t => t is not null));
        row.MouseEnter += (_, _) => row.SetResourceReference(Border.BackgroundProperty, "SubtleFillColorSecondaryBrush");
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        return row;
    }

    /// <summary>"Execution · round 2 · 3m 12s · 14,203 tokens · 9 steps".</summary>
    private static string Meta(ActivityItem item, DateTimeOffset now)
    {
        var parts = new List<string> { item.Status };
        if (item.StartedAt is { } started && item.Running) parts.Add((now - started).FormattedDuration());
        if (item.Tokens is > 0 and var tokens) parts.Add($"{tokens:N0} tokens");
        if (item.Steps is > 0 and var steps) parts.Add($"{steps} step{(steps == 1 ? "" : "s")}");
        return string.Join(" · ", parts);
    }

    private void DrawServers()
    {
        _servers.Children.Clear();
        var fleet = Host.Fleet.Snapshot();
        if (fleet.Count == 0) return;
        _servers.Children.Add(Ui.Text("Model servers", 11.5, FontWeights.SemiBold, "TextFillColorSecondaryBrush", wrap: false));
        foreach (var server in fleet)
        {
            var text = $"{(server.Healthy ? "●" : "○")} {server.Label} — {(server.IsPrimary ? "main" : "subagent server")} · {server.InFlight}/{server.MaxParallel} busy · {server.Completed} done"
                       + (server.Failed > 0 ? $" · {server.Failed} failed" : "") + (server.Healthy ? "" : " · not answering, skipped for a minute");
            var line = Ui.Text(text, 11, brushKey: server.Healthy ? "TextFillColorSecondaryBrush" : "SystemFillColorCautionBrush", wrap: false);
            line.TextTrimming = TextTrimming.CharacterEllipsis;
            line.ToolTip = text;
            _servers.Children.Add(line);
        }
    }

    private void OpenLog(ActivityItem item)
    {
        var owner = Window.GetWindow(this) ?? Application.Current.MainWindow;
        if (item.Kind is ActivityKind.QueueRunner or ActivityKind.ChatTasks && item.ChatId is { } chatId)
        {
            new QueueLogWindow(Host, chatId) { Owner = owner }.Show();
            return;
        }
        // The item as it is now, each time the log redraws.
        var key = item.Id;
        new ActivityLogWindow($"{RunActivity.KindLabel(item.Kind)} — {item.Title}", () =>
        {
            var current = Host.ActivityItems().FirstOrDefault(i => i.Id == key) ?? item with { Status = "Finished" };
            return Host.ActivityLog(current);
        }) { Owner = owner }.Show();
    }

    private static TextBlock Line(string text, string brush)
    {
        var line = Ui.Text(text, 11, brushKey: brush, wrap: false);
        line.TextTrimming = TextTrimming.CharacterEllipsis;
        line.Margin = new Thickness(0, 1, 0, 0);
        return line;
    }

    private static Button Icon(string glyph, string tooltip, Action action, string? brush = null)
    {
        var button = new Button { Content = glyph, ToolTip = tooltip, Margin = new Thickness(2, 0, 0, 0) };
        button.SetResourceReference(StyleProperty, "IconButton");
        if (brush is not null) button.SetResourceReference(ForegroundProperty, brush);
        System.Windows.Automation.AutomationProperties.SetName(button, tooltip);
        button.Click += (_, _) => action();
        return button;
    }
}
