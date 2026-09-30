using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Dsh.App.Infrastructure;
using Dsh.App.Model;
using Dsh.App.Views.Chat;
using Dsh.Core;

namespace Dsh.App.Views;

/// <summary>The plan panel (right side, Ctrl+Shift+P, or the checklist button top right): what the agent
/// is working through, laid out as the bullet-point plan it made — the step it is on, the ones behind it
/// and ahead — with the goal it is chasing, the subagents working beside it (and the servers they run
/// on), and a proposed plan when a planning run has one. It follows the open chat.</summary>
public sealed class PlanPanel : UserControl
{
    private readonly AppModel _model;
    private AgentHost Host => _model.Host;

    private readonly TextBlock _subtitle = Ui.Secondary("", 11.5);
    private readonly ProgressBar _progress = new() { Height = 4, Minimum = 0, Maximum = 1, Margin = new Thickness(12, 0, 12, 10) };
    private readonly StackPanel _body = new() { Margin = new Thickness(12, 6, 12, 12) };
    private readonly ScrollViewer _scroll;
    private readonly CheckBox _auto = new() { Content = "Show when the agent makes a plan", FontSize = 11.5, Margin = new Thickness(12, 8, 12, 10) };
    private readonly TextBlock _cache = Ui.Text("", 11, brushKey: "TextFillColorTertiaryBrush");
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };

    private SessionVM? _watched;
    private bool _refreshQueued;
    /// <summary>Reports the user opened, by chat and run (run ids restart at 1 in every chat).</summary>
    private readonly HashSet<string> _expandedAgents = [];
    /// <summary>The elapsed-time labels on screen, so a tick changes their text instead of redrawing the panel.</summary>
    private readonly List<(TextBlock Label, Func<string> Text)> _clocks = [];
    private readonly DispatcherTimer _later = new() { Interval = TimeSpan.FromMilliseconds(200) };

    /// <summary>Raised when the user closes the panel.</summary>
    public event Action? CloseRequested;

    public PlanPanel(AppModel model)
    {
        _model = model;
        SetResourceReference(BackgroundProperty, "LayerFillColorDefaultBrush");

        var header = new DockPanel { Margin = new Thickness(12, 10, 8, 8) };
        var close = IconButton(Icons.Close, "Hide the plan panel (Ctrl+Shift+P)", () => CloseRequested?.Invoke());
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);
        var glyph = Ui.Glyph(Icons.CheckList, 15, "AccentTextFillColorPrimaryBrush");
        glyph.Margin = new Thickness(0, 0, 10, 0);
        DockPanel.SetDock(glyph, Dock.Left);
        header.Children.Add(glyph);
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(Ui.Text("Plan", 14, FontWeights.SemiBold, wrap: false));
        _subtitle.TextTrimming = TextTrimming.CharacterEllipsis;
        _subtitle.TextWrapping = TextWrapping.NoWrap;
        titles.Children.Add(_subtitle);
        header.Children.Add(titles);

        _scroll = new ScrollViewer
        {
            Content = _body,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        _auto.IsChecked = Host.Config.PlanPanelAutoOpen;
        _auto.Click += (_, _) => Host.Config.PlanPanelAutoOpen = _auto.IsChecked == true;
        _cache.Margin = new Thickness(12, 0, 12, 10);

        // Three rows: the header and progress bar, the scrolling body, and the footer.
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var top = new StackPanel();
        top.Children.Add(header);
        top.Children.Add(_progress);
        top.Children.Add(Ui.Divider());
        var bottom = new StackPanel();
        bottom.Children.Add(Ui.Divider());
        bottom.Children.Add(_auto);
        bottom.Children.Add(_cache);
        Grid.SetRow(top, 0);
        Grid.SetRow(_scroll, 1);
        Grid.SetRow(bottom, 2);
        grid.Children.Add(top);
        grid.Children.Add(_scroll);
        grid.Children.Add(bottom);
        var edge = new Border { Child = grid, BorderThickness = new Thickness(1, 0, 0, 0) };
        edge.SetResourceReference(Border.BorderBrushProperty, "DividerStrokeColorDefaultBrush");
        Content = edge;

        Host.PropertyChanged += OnHostChanged;
        Host.Fleet.Changed += QueueRefresh;
        _clock.Tick += (_, _) => TickClock();
        _later.Tick += (_, _) =>
        {
            _later.Stop();
            QueueRefresh();
        };
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                Watch(Host.Selected);
                Refresh();
                _clock.Start();
            }
            else
            {
                _clock.Stop();
            }
        };
        Watch(Host.Selected);
        Refresh();
    }

    // MARK: - Following the open chat

    private void OnHostChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AgentHost.SelectedId) or nameof(AgentHost.Selected))
        {
            Watch(Host.Selected);
            QueueRefresh();
        }
    }

    private void Watch(SessionVM? session)
    {
        if (ReferenceEquals(_watched, session)) return;
        if (_watched is not null)
        {
            _watched.PropertyChanged -= OnSessionChanged;
            _watched.Agents.CollectionChanged -= OnAgentsChanged;
        }
        _watched = session;
        if (_watched is not null)
        {
            _watched.PropertyChanged += OnSessionChanged;
            _watched.Agents.CollectionChanged += OnAgentsChanged;
        }
    }

    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Streaming text changes constantly; the plan only cares about these.
        if (e.PropertyName is nameof(SessionVM.Todos) or nameof(SessionVM.Goal) or nameof(SessionVM.RunningTool) or nameof(SessionVM.Running)
            or nameof(SessionVM.Activity) or nameof(SessionVM.Outline) or nameof(SessionVM.ProposedPlan) or nameof(SessionVM.Retry))
            QueueRefresh();
    }

    private void OnAgentsChanged(object? sender, NotifyCollectionChangedEventArgs e) => QueueRefresh();

    /// <summary>Several changes in one moment (a step finishing, the next starting, a tool starting) redraw once.</summary>
    private void QueueRefresh()
    {
        if (_refreshQueued) return;
        _refreshQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _refreshQueued = false;
            // A click in progress (button down, not yet up) must not lose the button it is on to a redraw: try again in a moment.
            if (IsVisible && IsMouseOver && Mouse.LeftButton == MouseButtonState.Pressed)
            {
                _later.Start();
                return;
            }
            if (IsVisible || !IsLoaded) Refresh();
        });
    }

    /// <summary>Elapsed times tick without a change in the chat: only their text changes, the rest of the panel stays put.</summary>
    private void TickClock()
    {
        foreach (var (label, text) in _clocks) label.Text = text();
    }

    // MARK: - Drawing

    private sealed record Step(string Text, TodoStatus Status);

    private void Refresh()
    {
        _auto.IsChecked = Host.Config.PlanPanelAutoOpen;
        _body.Children.Clear();
        _clocks.Clear();
        var vm = _watched;
        if (vm is null)
        {
            SetSummary("No chat open", null);
            _cache.Text = "";
            _body.Children.Add(Empty("Open or start a chat, and the agent's plan appears here as it works."));
            return;
        }

        var steps = StepsOf(vm, out var isOutline);
        var running = vm.Agents.Where(a => a.IsRunning).ToList();
        var done = steps.Count(s => s.Status == TodoStatus.Completed);
        SetSummary(steps.Count > 0 ? $"{done} of {steps.Count} step{(steps.Count == 1 ? "" : "s")} done" + (isOutline ? " · outline" : "")
                                   : vm.ProposedPlan is not null ? "Plan proposed"
                                   : vm.Running ? "Working…" : "No plan yet",
            steps.Count > 0 && !isOutline ? done / (double)steps.Count : null);

        var anything = false;
        if (Now(vm, steps) is { } now)
        {
            _body.Children.Add(Section("Working on"));
            _body.Children.Add(now);
            anything = true;
        }
        if (vm.Goal is { } goal)
        {
            _body.Children.Add(Section("Goal"));
            _body.Children.Add(GoalCard(goal));
            anything = true;
        }
        if (steps.Count > 0)
        {
            _body.Children.Add(Section(isOutline ? "Outline" : "Steps"));
            for (var i = 0; i < steps.Count; i++) _body.Children.Add(StepRow(i + 1, steps[i], isOutline, vm.Running));
            anything = true;
        }
        if (vm.ProposedPlan is { Length: > 0 } proposed)
        {
            _body.Children.Add(Section("Proposed plan"));
            _body.Children.Add(ProposedPlanCard(proposed));
            anything = true;
        }
        if (vm.Agents.Count > 0)
        {
            _body.Children.Add(AgentsHeader(running.Count, vm));
            if (FleetLine() is { } fleet) _body.Children.Add(fleet);
            foreach (var run in vm.Agents.OrderByDescending(a => a.IsRunning).ThenByDescending(a => a.StartedAt).Take(14))
                _body.Children.Add(AgentRow(run));
            anything = true;
        }
        if (!anything)
            _body.Children.Add(Empty("No plan yet. When the agent takes on a job with several steps it lays the plan out here and ticks the steps off as it goes."));

        _cache.Text = Host.CacheSummary(vm.Id);
        _cache.Visibility = _cache.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static List<Step> StepsOf(SessionVM vm, out bool isOutline)
    {
        isOutline = false;
        if (vm.Outline.Count > 0)
        {
            isOutline = true;
            return vm.Outline.Select(t => new Step(t, TodoStatus.Pending)).ToList();
        }
        return vm.Todos.Select(t => new Step(t.Content, t.Status)).ToList();
    }

    private void SetSummary(string text, double? fraction)
    {
        _subtitle.Text = text;
        _subtitle.ToolTip = text;
        _progress.Visibility = fraction is null ? Visibility.Collapsed : Visibility.Visible;
        if (fraction is { } value) _progress.Value = value;
    }

    /// <summary>The step in progress and what the agent is doing right now.</summary>
    private UIElement? Now(SessionVM vm, List<Step> steps)
    {
        if (!vm.Running) return null;
        var current = steps.FirstOrDefault(s => s.Status == TodoStatus.InProgress);
        var doing = vm.Retry is { } retry ? $"The model isn't answering — {retry.Reason}"
            : vm.Activity is { Length: > 0 } activity ? activity
            : vm.RunningTool is { } tool ? $"{tool.Label}{(tool.Preview.Length > 0 ? ": " + TextUtil.Prefix(tool.Preview, 90) : "")}"
            : vm.Reasoning.Length > 0 ? "Thinking…"
            : "Working…";
        if (current is null && vm.Goal is null && doing == "Working…") return null;

        var panel = new StackPanel();
        var head = new DockPanel();
        var spinner = new Spinner { Width = 14, Height = 14, Margin = new Thickness(0, 1, 10, 0), VerticalAlignment = VerticalAlignment.Top };
        spinner.Foreground = ThemeService.Brush("AccentFillColorDefaultBrush", Colors.SteelBlue);
        DockPanel.SetDock(spinner, Dock.Left);
        head.Children.Add(spinner);
        head.Children.Add(Ui.Text(current?.Text ?? (vm.Goal is not null ? "Working on the goal" : "Working"), 13, FontWeights.SemiBold));
        panel.Children.Add(head);
        var line = Ui.Text(doing, 11.5, brushKey: "TextFillColorSecondaryBrush");
        line.Margin = new Thickness(24, 3, 0, 0);
        line.MaxHeight = 34;
        panel.Children.Add(line);
        return Card(panel, accent: true);
    }

    private UIElement StepRow(int number, Step step, bool outline, bool agentRunning)
    {
        var inProgress = step.Status == TodoStatus.InProgress;
        var completed = step.Status == TodoStatus.Completed;
        var dock = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };

        FrameworkElement marker;
        if (inProgress && agentRunning)
        {
            marker = new Spinner { Foreground = ThemeService.Brush("AccentFillColorDefaultBrush", Colors.SteelBlue), Width = 13, Height = 13 };
        }
        else
        {
            var glyph = completed ? Icons.Completed : inProgress ? Icons.CircleFill : Icons.CircleRing;
            var brush = completed ? "SystemFillColorSuccessBrush" : inProgress ? "AccentTextFillColorPrimaryBrush" : "TextFillColorTertiaryBrush";
            marker = Ui.Glyph(glyph, 13, brush);
        }
        marker.Margin = new Thickness(0, 2, 10, 0);
        marker.VerticalAlignment = VerticalAlignment.Top;
        marker.Width = 14;
        DockPanel.SetDock(marker, Dock.Left);
        dock.Children.Add(marker);

        var text = Ui.Text(step.Text, 12.5, inProgress ? FontWeights.SemiBold : FontWeights.Normal,
            completed ? "TextFillColorTertiaryBrush" : outline ? "TextFillColorSecondaryBrush" : null);
        if (completed) text.TextDecorations = TextDecorations.Strikethrough;
        dock.Children.Add(text);
        dock.ToolTip = step.Text;
        return dock;
    }

    private UIElement GoalCard(GoalState goal)
    {
        var panel = new StackPanel();
        var head = new DockPanel();
        string RoundText() => $"round {goal.Round} · {(DateTimeOffset.Now - goal.Started).FormattedDuration()}";
        var round = Ui.Text(RoundText(), 11, brushKey: "TextFillColorTertiaryBrush", wrap: false);
        _clocks.Add((round, RoundText));
        DockPanel.SetDock(round, Dock.Right);
        head.Children.Add(round);
        var glyph = Ui.Glyph(Icons.Flag, 12, "AccentTextFillColorPrimaryBrush");
        glyph.Margin = new Thickness(0, 0, 8, 0);
        head.Children.Add(glyph);
        head.Children.Add(Ui.Text("Working until done", 12, FontWeights.SemiBold, wrap: false));
        panel.Children.Add(head);
        var text = Ui.Text(goal.Text, 12, brushKey: "TextFillColorSecondaryBrush");
        text.MaxHeight = 64;
        text.Margin = new Thickness(0, 4, 0, 0);
        text.TextTrimming = TextTrimming.CharacterEllipsis;
        text.ToolTip = goal.Text;
        panel.Children.Add(text);
        return Card(panel);
    }

    private static UIElement ProposedPlanCard(string plan)
    {
        // (Focusable, so the plan can be selected and copied.)
        var markdown = new MarkdownPresenter { Markdown = plan, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0) };
        var panel = new StackPanel();
        panel.Children.Add(markdown);
        // (A chat keeps the permission preset it was created with, so the way to act on the plan is a new chat.)
        var note = Ui.Text("Proposed in planning mode, which changes nothing. To carry it out, choose another permission preset with the button in the top bar and start a new chat.", 11, brushKey: "TextFillColorTertiaryBrush");
        note.Margin = new Thickness(0, 6, 0, 0);
        panel.Children.Add(note);
        return Card(panel);
    }

    private UIElement AgentsHeader(int running, SessionVM vm)
    {
        var dock = new DockPanel { Margin = new Thickness(2, 16, 0, 6) };
        if (vm.Agents.Any(a => !a.IsRunning))
        {
            var clear = Ui.LinkButton("Clear finished", () =>
            {
                _watched?.ClearFinishedAgents();
                Refresh();
            });
            DockPanel.SetDock(clear, Dock.Right);
            dock.Children.Add(clear);
        }
        dock.Children.Add(Ui.Text(running > 0 ? $"Agents · {running} working" : "Agents", 12, FontWeights.SemiBold, "TextFillColorSecondaryBrush", wrap: false));
        return dock;
    }

    /// <summary>With more than one model server: how busy each is.</summary>
    private UIElement? FleetLine()
    {
        var servers = Host.Fleet.Snapshot();
        if (servers.Count < 2) return null;
        var parts = servers.Select(s => $"{(s.Healthy ? "●" : "○")} {s.Label} {s.InFlight}/{s.MaxParallel}");
        var line = Ui.Text(string.Join("   ", parts), 11, brushKey: "TextFillColorTertiaryBrush");
        line.Margin = new Thickness(2, 0, 0, 6);
        line.ToolTip = string.Join("\n", servers.Select(s =>
            $"{(s.IsPrimary ? "Main" : "Worker")} · {s.Label}: {s.InFlight} of {s.MaxParallel} busy, {s.Completed} done" +
            (s.Failed > 0 ? $", {s.Failed} failed" : "") + (s.Healthy ? "" : " — not answering")));
        return line;
    }

    private UIElement AgentRow(AgentRunInfo run)
    {
        var panel = new StackPanel();
        var head = new DockPanel();
        FrameworkElement marker;
        if (run.IsRunning)
        {
            marker = new Spinner { Foreground = ThemeService.Brush("AccentFillColorDefaultBrush", Colors.SteelBlue), Width = 12, Height = 12 };
        }
        else
        {
            var (glyph, brush) = run.Status switch
            {
                AgentRunStatus.Done => (Icons.Completed, "SystemFillColorSuccessBrush"),
                AgentRunStatus.Stopped => (Icons.Stop, "TextFillColorTertiaryBrush"),
                _ => (Icons.Error, "SystemFillColorCriticalBrush"),
            };
            marker = Ui.Glyph(glyph, 12, brush);
        }
        marker.Margin = new Thickness(0, 2, 9, 0);
        marker.VerticalAlignment = VerticalAlignment.Top;
        marker.Width = 14;
        DockPanel.SetDock(marker, Dock.Left);
        head.Children.Add(marker);
        var time = Ui.Text(run.Elapsed(DateTimeOffset.Now).FormattedDuration(), 11, brushKey: "TextFillColorTertiaryBrush", wrap: false);
        if (run.IsRunning) _clocks.Add((time, () => run.Elapsed(DateTimeOffset.Now).FormattedDuration()));
        time.Margin = new Thickness(8, 1, 0, 0);
        DockPanel.SetDock(time, Dock.Right);
        head.Children.Add(time);
        head.Children.Add(Ui.Text(run.Description, 12.5, FontWeights.SemiBold, wrap: false));
        panel.Children.Add(head);

        var where = string.Join(" · ", new[] { run.AgentType, run.Server, run.Background ? "background" : null }.Where(t => !string.IsNullOrEmpty(t)));
        var sub = Ui.Text(where, 11, brushKey: "TextFillColorTertiaryBrush", wrap: false);
        sub.Margin = new Thickness(23, 1, 0, 0);
        panel.Children.Add(sub);
        var activity = Ui.Text(run.IsRunning ? run.Activity + (run.Steps > 0 ? $"  ·  {run.Steps} step{(run.Steps == 1 ? "" : "s")}" : "") : ActivitySummary(run),
            11.5, brushKey: "TextFillColorSecondaryBrush", wrap: false);
        activity.Margin = new Thickness(23, 1, 0, 0);
        panel.Children.Add(activity);

        var key = $"{_watched?.Id}/{run.Id}";
        if (!run.IsRunning && !string.IsNullOrWhiteSpace(run.Report))
        {
            var expanded = _expandedAgents.Contains(key);
            var report = Ui.Text(expanded ? TextUtil.Prefix(run.Report, 1_500) : TextUtil.Prefix(run.Report.Replace('\n', ' '), 110),
                11.5, brushKey: "TextFillColorTertiaryBrush", wrap: expanded);
            report.Margin = new Thickness(23, 2, 0, 0);
            panel.Children.Add(report);
        }

        var row = new Border { Child = panel, Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(0, 0, 0, 2), CornerRadius = new CornerRadius(6), Background = Brushes.Transparent };
        // A title or an activity line cut short by the panel's width is in full here.
        row.ToolTip = run.Description + (run.IsRunning ? "\n" + run.Activity : "");
        if (!run.IsRunning && !string.IsNullOrWhiteSpace(run.Report))
        {
            row.Cursor = Cursors.Hand;
            row.ToolTip = run.Description + "\nClick to show or hide the report";
            row.MouseLeftButtonUp += (_, _) =>
            {
                if (!_expandedAgents.Remove(key)) _expandedAgents.Add(key);
                Refresh();
            };
            row.MouseEnter += (_, _) => row.SetResourceReference(Border.BackgroundProperty, "SubtleFillColorSecondaryBrush");
            row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        }
        return row;
    }

    private static string ActivitySummary(AgentRunInfo run) => run.Status switch
    {
        AgentRunStatus.Done => run.Steps > 0 ? $"finished · {run.Steps} step{(run.Steps == 1 ? "" : "s")}" : "finished",
        AgentRunStatus.Stopped => "stopped",
        _ => "failed",
    };

    // MARK: - Pieces

    private static UIElement Section(string title)
    {
        var text = Ui.Text(title, 12, FontWeights.SemiBold, "TextFillColorSecondaryBrush", wrap: false);
        text.Margin = new Thickness(2, 14, 0, 6);
        return text;
    }

    private static UIElement Empty(string message)
    {
        var panel = new StackPanel { Margin = new Thickness(10, 36, 10, 20), HorizontalAlignment = HorizontalAlignment.Center };
        var glyph = Ui.Glyph(Icons.CheckList, 30, "TextFillColorTertiaryBrush");
        glyph.HorizontalAlignment = HorizontalAlignment.Center;
        panel.Children.Add(glyph);
        var text = Ui.Text(message, 12, brushKey: "TextFillColorTertiaryBrush");
        text.TextAlignment = TextAlignment.Center;
        text.Margin = new Thickness(0, 10, 0, 0);
        panel.Children.Add(text);
        return panel;
    }

    private static Border Card(UIElement content, bool accent = false)
    {
        var card = new Border { Child = content, Padding = new Thickness(12, 10, 12, 10), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1) };
        card.SetResourceReference(Border.BackgroundProperty, accent ? "SubtleFillColorSecondaryBrush" : "LayerFillColorAltBrush");
        card.SetResourceReference(Border.BorderBrushProperty, accent ? "AccentFillColorDefaultBrush" : "CardStrokeColorDefaultBrush");
        return card;
    }

    private static Button IconButton(string glyph, string tooltip, Action action)
    {
        var button = new Button { Content = glyph, ToolTip = tooltip, Margin = new Thickness(2, 0, 0, 0) };
        button.SetResourceReference(StyleProperty, "IconButton");
        System.Windows.Automation.AutomationProperties.SetName(button, tooltip);
        button.Click += (_, _) => action();
        return button;
    }
}
