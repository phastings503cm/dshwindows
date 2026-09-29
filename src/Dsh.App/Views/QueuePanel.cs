using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Dsh.App.Infrastructure;
using Dsh.App.Model;
using Dsh.App.Views.Dialogs;
using Dsh.Core;

namespace Dsh.App.Views;

/// <summary>The task queue panel (right side, Ctrl+Shift+Q): an ordered, long-lived work list. Add,
/// edit, delete and reorder tasks, then Start to have the harness work them one at a time as
/// unattended goals — or open the timestamped log of what it did.</summary>
public sealed class QueuePanel : UserControl
{
    private readonly AppModel _model;
    private AgentHost Host => _model.Host;
    private TaskQueue Queue => Host.Queue;

    private readonly TextBlock _subtitle = Ui.Secondary("", 11.5);
    private readonly ProgressBar _spinner = new() { IsIndeterminate = true, Width = 16, Height = 16, Visibility = Visibility.Collapsed };
    private readonly Button _startStop = new() { Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(6, 0, 0, 0) };
    private readonly StackPanel _list = new() { Margin = new Thickness(6) };
    private readonly ScrollViewer _listScroll;
    private readonly ContentControl _detail = new() { Focusable = false };
    private readonly Border _detailHost;

    private string? _selectedId;
    private bool _adding;
    private bool _editing;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(30) };

    // The add card keeps what was typed across refreshes.
    private readonly TextBox _newTitle = Ui.Field(placeholder: "Title (short)");
    private readonly TextBox _newDetails = MultiLine("Everything the agent needs to know — its chat won't see this one.");
    private readonly CheckBox _newAtFront = new() { Content = "Add to the front of the queue", FontSize = 12 };

    /// <summary>Raised when the user closes the panel.</summary>
    public event Action? CloseRequested;

    public QueuePanel(AppModel model)
    {
        _model = model;
        SetResourceReference(BackgroundProperty, "LayerFillColorDefaultBrush");

        // Header
        var header = new DockPanel { Margin = new Thickness(12, 10, 8, 8) };
        var close = IconButton(Icons.Close, "Hide the queue panel (Ctrl+Shift+Q)", () => CloseRequested?.Invoke());
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);
        DockPanel.SetDock(_startStop, Dock.Right);
        _startStop.Click += (_, _) =>
        {
            if (Host.QueueRunning) Host.StopQueue();
            else Host.StartQueue();
        };
        header.Children.Add(_startStop);
        var log = IconButton(Icons.History, "View the queue log", ShowLog);
        DockPanel.SetDock(log, Dock.Right);
        header.Children.Add(log);
        var add = IconButton(Icons.Add, "Add a task", BeginAdd);
        DockPanel.SetDock(add, Dock.Right);
        header.Children.Add(add);
        _spinner.Margin = new Thickness(0, 0, 8, 0);
        DockPanel.SetDock(_spinner, Dock.Left);
        header.Children.Add(_spinner);
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(Ui.Text("Task Queue", 14, FontWeights.SemiBold, wrap: false));
        _subtitle.TextTrimming = TextTrimming.CharacterEllipsis;
        _subtitle.TextWrapping = TextWrapping.NoWrap;
        titles.Children.Add(_subtitle);
        header.Children.Add(titles);

        _listScroll = new ScrollViewer
        {
            Content = _list,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        _detailHost = new Border { Child = _detail, Padding = new Thickness(12, 10, 12, 12), MinHeight = 120 };

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(header, 0);
        var topRule = Ui.Divider();
        Grid.SetRow(topRule, 1);
        Grid.SetRow(_listScroll, 2);
        var bottomRule = Ui.Divider();
        Grid.SetRow(bottomRule, 3);
        Grid.SetRow(_detailHost, 4);
        grid.Children.Add(header);
        grid.Children.Add(topRule);
        grid.Children.Add(_listScroll);
        grid.Children.Add(bottomRule);
        grid.Children.Add(_detailHost);
        var edge = new Border { Child = grid, BorderThickness = new Thickness(1, 0, 0, 0) };
        edge.SetResourceReference(Border.BorderBrushProperty, "DividerStrokeColorDefaultBrush");
        Content = edge;

        _newTitle.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) AddTask();
        };

        Host.QueueChanged += Refresh;
        Host.PropertyChanged += OnHostChanged;
        _clock.Tick += (_, _) => Refresh();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                Refresh();
                _clock.Start();
            }
            else
            {
                _clock.Stop();
            }
        };
        Refresh();
    }

    private void OnHostChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AgentHost.QueueRunning) or nameof(AgentHost.QueueActiveTaskId) or nameof(AgentHost.QueueStopping))
            Refresh();
    }

    // MARK: - Rendering

    private void Refresh()
    {
        if (!IsVisible && IsLoaded) return;
        var tasks = Queue.Tasks;
        if (_selectedId is not null && tasks.All(t => t.Id != _selectedId))
        {
            _selectedId = null;
            _editing = false;
        }
        var stopping = Host.QueueStopping;
        _spinner.Visibility = Host.QueueRunning || stopping ? Visibility.Visible : Visibility.Collapsed;
        _subtitle.Text = stopping ? "Stopping — the current task is winding down…" : Subtitle();
        _subtitle.ToolTip = _subtitle.Text;
        _startStop.Content = StartStopContent(Host.QueueRunning);
        _startStop.ToolTip = Host.QueueRunning
            ? "Stop the queue; the current task goes back in line"
            : "Work the queued tasks one at a time, top to bottom";
        if (Host.QueueRunning) _startStop.ClearValue(StyleProperty);
        else _startStop.SetResourceReference(StyleProperty, "AccentButtonStyle");
        _startStop.IsEnabled = Host.QueueRunning || Queue.NextTask is not null || stopping;

        _list.Children.Clear();
        if (tasks.Count == 0 && !_adding)
        {
            _list.Children.Add(EmptyState());
        }
        else
        {
            foreach (var task in tasks) _list.Children.Add(Row(task));
            if (_adding) _list.Children.Add(AddCard());
        }
        _detail.Content = Detail();
        _detailHost.Visibility = tasks.Count == 0 && !_adding ? Visibility.Collapsed : Visibility.Visible;
    }

    private string Subtitle()
    {
        var s = Queue.Stats();
        if (Host.QueueRunning)
        {
            var active = (Host.QueueActiveTaskId is { } id ? Queue.Find(id) : null) ?? Queue.RunningTask;
            if (active is not null)
                return Host.QueueActiveTaskId is not null && s.Queued == 0
                    ? $"Working “{active.Title}”"
                    : $"Working “{active.Title}” · {s.Queued} waiting";
            return "Queue is running";
        }
        var parts = new List<string>();
        if (s.Queued > 0) parts.Add($"{s.Queued} waiting");
        if (s.Blocked > 0) parts.Add($"{s.Blocked} need{(s.Blocked == 1 ? "s" : "")} you");
        if (s.Failed > 0) parts.Add($"{s.Failed} failed");
        if (s.Completed > 0) parts.Add($"{s.Completed} done");
        if (s.Skipped > 0) parts.Add($"{s.Skipped} skipped");
        return parts.Count == 0 ? "Nothing queued" : string.Join(" · ", parts);
    }

    private static object StartStopContent(bool running)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        var glyph = Ui.Glyph(running ? Icons.Stop : Icons.Play, 11, running ? "SystemFillColorCriticalBrush" : "TextOnAccentFillColorPrimaryBrush");
        glyph.Margin = new Thickness(0, 0, 6, 0);
        panel.Children.Add(glyph);
        panel.Children.Add(new TextBlock { Text = running ? "Stop" : "Start", VerticalAlignment = VerticalAlignment.Center });
        return panel;
    }

    private UIElement EmptyState()
    {
        var panel = new StackPanel { Margin = new Thickness(18, 40, 18, 20), HorizontalAlignment = HorizontalAlignment.Center };
        var glyph = Ui.Glyph(Icons.Tray, 30, "TextFillColorTertiaryBrush");
        glyph.HorizontalAlignment = HorizontalAlignment.Center;
        panel.Children.Add(glyph);
        var title = Ui.Text("No tasks yet", 13, FontWeights.SemiBold, "TextFillColorSecondaryBrush");
        title.HorizontalAlignment = HorizontalAlignment.Center;
        title.Margin = new Thickness(0, 10, 0, 4);
        panel.Children.Add(title);
        var blurb = Ui.Text("Queue up work — one task per item. Press Start and the harness works them by itself, one after another, " +
                            "each in its own chat, all week if it has to.", 12, brushKey: "TextFillColorTertiaryBrush");
        blurb.TextAlignment = TextAlignment.Center;
        panel.Children.Add(blurb);
        var add = Ui.Button("Add the first task", BeginAdd);
        add.HorizontalAlignment = HorizontalAlignment.Center;
        add.Margin = new Thickness(0, 12, 0, 0);
        panel.Children.Add(add);
        return panel;
    }

    private UIElement Row(QueueTask task)
    {
        var selected = task.Id == _selectedId;
        var (glyph, brush) = Mark(task.Status);
        var icon = Ui.Glyph(glyph, 12, brush);
        icon.Width = 18;
        icon.Margin = new Thickness(0, 0, 8, 0);
        icon.VerticalAlignment = VerticalAlignment.Top;
        icon.Padding = new Thickness(0, 2, 0, 0);

        var lines = new StackPanel();
        lines.Children.Add(Ui.Text(task.Title, 12.5, selected ? FontWeights.SemiBold : FontWeights.Normal, wrap: false));
        var sub = Ui.Text(SubLine(task), 11, brushKey: "TextFillColorTertiaryBrush", wrap: false);
        lines.Children.Add(sub);

        var dock = new DockPanel();
        DockPanel.SetDock(icon, Dock.Left);
        dock.Children.Add(icon);
        dock.Children.Add(lines);

        var row = new Border
        {
            Child = dock,
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(0, 0, 0, 2),
            CornerRadius = new CornerRadius(6),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            ToolTip = task.Status == QueueTaskStatus.Queued ? "Drag onto another waiting task to reorder" : null,
            AllowDrop = task.Status == QueueTaskStatus.Queued,
        };
        if (selected) row.SetResourceReference(Border.BackgroundProperty, "SubtleFillColorSecondaryBrush");
        row.MouseEnter += (_, _) =>
        {
            if (!selected) row.SetResourceReference(Border.BackgroundProperty, "SubtleFillColorSecondaryBrush");
        };
        row.MouseLeave += (_, _) =>
        {
            if (!selected) row.Background = Brushes.Transparent;
        };

        // Click selects; dragging a waiting task onto another waiting one moves it there.
        Point? pressed = null;
        row.MouseLeftButtonDown += (_, e) =>
        {
            pressed = e.GetPosition(row);
            Select(task.Id);
            e.Handled = true;
        };
        row.MouseMove += (_, e) =>
        {
            if (pressed is not { } start || e.LeftButton != MouseButtonState.Pressed || task.Status != QueueTaskStatus.Queued) return;
            var delta = e.GetPosition(row) - start;
            if (Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            pressed = null;
            DragDrop.DoDragDrop(row, new DataObject("dsh-queue-task", task.Id), DragDropEffects.Move);
        };
        row.MouseLeftButtonUp += (_, _) => pressed = null;
        row.DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetData("dsh-queue-task") is string dragged && dragged != task.Id ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        };
        row.Drop += (_, e) =>
        {
            if (e.Data.GetData("dsh-queue-task") is not string dragged || dragged == task.Id) return;
            if (Queue.Find(dragged)?.Status != QueueTaskStatus.Queued) return;
            Host.QueueMoveOnto(dragged, task.Id);
            _selectedId = dragged;
        };
        return row;
    }

    private string SubLine(QueueTask task) => task.Status switch
    {
        QueueTaskStatus.Queued => (Queue.Position(task.Id) is { } p ? $"#{p} · " : "") + $"added {QueueLog.When(task.EnteredAt)}",
        QueueTaskStatus.Running => "running" + (task.Rounds > 0 ? $" · {task.Rounds} round{(task.Rounds == 1 ? "" : "s")}" : "")
                                   + (task.TotalTokens > 0 ? $" · {task.TotalTokens:N0} tok" : ""),
        QueueTaskStatus.Complete => $"done · {task.Rounds} round{(task.Rounds == 1 ? "" : "s")}"
                                    + (task.AvgTokensPerSecond() is { } rate ? $" · {Math.Round(rate):0} tok/s" : ""),
        QueueTaskStatus.Blocked => "waiting on you",
        QueueTaskStatus.Failed => "failed",
        _ => "skipped",
    };

    private static (string Glyph, string Brush) Mark(QueueTaskStatus status) => status switch
    {
        QueueTaskStatus.Running => (Icons.Play, "AccentTextFillColorPrimaryBrush"),
        QueueTaskStatus.Complete => (Icons.Completed, "SystemFillColorSuccessBrush"),
        QueueTaskStatus.Blocked => (Icons.Warning, "SystemFillColorCautionBrush"),
        QueueTaskStatus.Failed => (Icons.Error, "SystemFillColorCriticalBrush"),
        QueueTaskStatus.Skipped => (Icons.Next, "TextFillColorTertiaryBrush"),
        _ => (Icons.CircleRing, "TextFillColorSecondaryBrush"),
    };

    private UIElement AddCard()
    {
        var panel = new StackPanel();
        panel.Children.Add(_newTitle);
        _newDetails.Margin = new Thickness(0, 6, 0, 6);
        panel.Children.Add(_newDetails);
        panel.Children.Add(_newAtFront);
        var buttons = Ui.Buttons(Ui.Button("Cancel", CancelAdd), Ui.Button("Add Task", AddTask, accent: true));
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        buttons.Margin = new Thickness(0, 8, 0, 0);
        panel.Children.Add(buttons);
        var card = Ui.Card(panel, new Thickness(10));
        card.Margin = new Thickness(0, 6, 0, 0);
        return card;
    }

    private UIElement Detail()
    {
        if (_selectedId is null || Queue.Find(_selectedId) is not { } task)
        {
            var hint = Ui.Secondary(_adding ? "Give the task a short title and complete instructions." : "Select a task to edit it, reorder it or open its chat.", 12);
            hint.Margin = new Thickness(0, 6, 0, 0);
            return hint;
        }
        return _editing ? EditForm(task) : Summary(task);
    }

    private UIElement Summary(QueueTask task)
    {
        var panel = new StackPanel();
        var titleRow = new DockPanel();
        var (glyph, brush) = Mark(task.Status);
        var icon = Ui.Glyph(glyph, 13, brush);
        icon.Margin = new Thickness(0, 0, 8, 0);
        DockPanel.SetDock(icon, Dock.Left);
        titleRow.Children.Add(icon);
        titleRow.Children.Add(Ui.Text(task.Title, 13, FontWeights.SemiBold, wrap: false));
        panel.Children.Add(titleRow);
        var meta = Ui.Secondary(MetaLine(task), 11.5);
        meta.Margin = new Thickness(0, 3, 0, 0);
        panel.Children.Add(meta);
        if (task.Cwd is { } cwd)
        {
            var folder = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0), ToolTip = cwd };
            var folderGlyph = Ui.Glyph(Icons.Folder, 11, "TextFillColorTertiaryBrush");
            folderGlyph.Margin = new Thickness(0, 0, 5, 0);
            folder.Children.Add(folderGlyph);
            folder.Children.Add(Ui.Text(Path.GetFileName(cwd.TrimEnd('\\', '/')) is { Length: > 0 } name ? name : cwd, 11.5,
                brushKey: "TextFillColorTertiaryBrush", wrap: false));
            panel.Children.Add(folder);
        }
        if (task.Details.Length > 0)
        {
            var details = Ui.Secondary(task.Details, 12);
            details.MaxHeight = 54;
            details.TextTrimming = TextTrimming.CharacterEllipsis;
            details.Margin = new Thickness(0, 6, 0, 0);
            details.ToolTip = task.Details;
            panel.Children.Add(details);
        }

        var actions = new DockPanel { Margin = new Thickness(0, 10, 0, 0), LastChildFill = false };
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        if (task.SessionId is { } sessionId && Host.Session(sessionId) is not null)
            left.Children.Add(Small("Open chat", () => _model.OpenSession(sessionId)));
        if (task.Status is QueueTaskStatus.Blocked or QueueTaskStatus.Failed or QueueTaskStatus.Skipped)
        {
            var resume = Small("Resume", () => Host.ResumeTask(task.Id), accent: true,
                tooltip: "Work this task now in its own chat, picking up where it stopped (next in line if the queue is running)");
            left.Children.Add(resume);
            left.Children.Add(IconButton(Icons.Undo, "Put it back at the front of the queue without starting it", () => Host.QueueRequeue(task.Id)));
        }
        if (task.Status == QueueTaskStatus.Queued)
        {
            left.Children.Add(IconButton(Icons.Up, "Move up", () => Host.QueueMoveBy(task.Id, -1)));
            left.Children.Add(IconButton(Icons.Down, "Move down", () => Host.QueueMoveBy(task.Id, 1)));
        }
        DockPanel.SetDock(left, Dock.Left);
        actions.Children.Add(left);
        var delete = IconButton(Icons.Delete, "Delete this task", () => Delete(task), "SystemFillColorCriticalBrush");
        DockPanel.SetDock(delete, Dock.Right);
        actions.Children.Add(delete);
        if (task.Status != QueueTaskStatus.Running)
        {
            var edit = IconButton(Icons.Edit, "Edit this task", () =>
            {
                _editing = true;
                Refresh();
            });
            DockPanel.SetDock(edit, Dock.Right);
            actions.Children.Add(edit);
        }
        panel.Children.Add(actions);
        return panel;
    }

    private static string MetaLine(QueueTask task) => task.Status switch
    {
        QueueTaskStatus.Queued => $"in the queue · added {QueueLog.When(task.EnteredAt)}",
        QueueTaskStatus.Running => $"running · {task.Rounds} round{(task.Rounds == 1 ? "" : "s")}"
                                   + (task.TotalTokens > 0 ? $" · {task.TotalTokens:N0} tokens" : ""),
        QueueTaskStatus.Complete => $"done · {task.Rounds} round{(task.Rounds == 1 ? "" : "s")} · {task.TotalTokens:N0} tokens"
                                    + (task.AvgTokensPerSecond() is { } rate ? $" · {Math.Round(rate):0} tokens/s avg" : ""),
        QueueTaskStatus.Blocked => "waiting on you — see its chat",
        QueueTaskStatus.Failed => "failed — see its chat",
        _ => "skipped",
    };

    private UIElement EditForm(QueueTask task)
    {
        var title = Ui.Field(task.Title, "Title");
        var details = MultiLine("Instructions");
        details.Text = task.Details;
        details.Margin = new Thickness(0, 6, 0, 8);
        var panel = new StackPanel();
        panel.Children.Add(title);
        panel.Children.Add(details);
        panel.Children.Add(Ui.Buttons(
            Ui.Button("Save", () =>
            {
                if (title.Text.Trim().Length == 0) return;
                Host.QueueUpdate(task.Id, title.Text.Trim(), details.Text.Trim());
                _editing = false;
                Refresh();
            }, accent: true),
            Ui.Button("Cancel", () =>
            {
                _editing = false;
                Refresh();
            })));
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => title.Focus());
        return panel;
    }

    // MARK: - Actions

    private void Select(string id)
    {
        if (_selectedId == id && !_editing) return;
        _selectedId = id;
        _editing = false;
        Refresh();
    }

    public void BeginAdd()
    {
        _adding = true;
        _editing = false;
        _selectedId = null;
        Refresh();
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            _listScroll.ScrollToEnd();
            _newTitle.Focus();
        });
    }

    private void CancelAdd()
    {
        _adding = false;
        _newTitle.Text = "";
        _newDetails.Text = "";
        _newAtFront.IsChecked = false;
        Refresh();
    }

    private void AddTask()
    {
        var title = _newTitle.Text.Trim();
        if (title.Length == 0)
        {
            _newTitle.Focus();
            return;
        }
        var added = Host.QueueAdd(title, _newDetails.Text.Trim(), _newAtFront.IsChecked == true);
        _newTitle.Text = "";
        _newDetails.Text = "";
        _newAtFront.IsChecked = false;
        _adding = false;
        _selectedId = added.Id;
        Refresh();
    }

    private void Delete(QueueTask task)
    {
        var running = task.Status == QueueTaskStatus.Running;
        if (!Dialog.Confirm($"Delete “{task.Title}”?",
                running ? "The task is running: it stops now, and the queue moves on to the next one. Its chat stays in the sidebar."
                        : "It is removed from the queue. Its chat, if it has one, stays in the sidebar.",
                "Delete", destructive: true)) return;
        Host.QueueRemove(task.Id);
        _selectedId = null;
        Refresh();
    }

    private void ShowLog()
    {
        var window = new QueueLogWindow(Host) { Owner = Window.GetWindow(this) };
        window.Show();
    }

    // MARK: - Helpers

    private static TextBox MultiLine(string placeholder)
    {
        var box = Ui.Field(placeholder: placeholder);
        box.AcceptsReturn = true;
        box.TextWrapping = TextWrapping.Wrap;
        box.MinHeight = 70;
        box.MaxHeight = 160;
        box.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        box.VerticalContentAlignment = VerticalAlignment.Top;
        return box;
    }

    private static Button IconButton(string glyph, string tooltip, Action action, string? brushKey = null)
    {
        var button = new Button { Content = glyph, ToolTip = tooltip, Margin = new Thickness(2, 0, 0, 0) };
        button.SetResourceReference(StyleProperty, "IconButton");
        if (brushKey is not null) button.SetResourceReference(ForegroundProperty, brushKey);
        AutomationName(button, tooltip);
        button.Click += (_, _) => action();
        return button;
    }

    private static Button Small(string text, Action action, bool accent = false, string? tooltip = null)
    {
        var button = Ui.Button(text, action, accent, tooltip);
        button.MinWidth = 0;
        button.Padding = new Thickness(10, 3, 10, 3);
        button.Margin = new Thickness(0, 0, 6, 0);
        return button;
    }

    private static void AutomationName(DependencyObject element, string name) =>
        System.Windows.Automation.AutomationProperties.SetName(element, name);
}

/// <summary>The queue's timestamped log, as text you can read and copy.</summary>
public sealed class QueueLogWindow : Window
{
    private readonly AgentHost _host;
    private readonly TextBox _text = new()
    {
        IsReadOnly = true,
        TextWrapping = TextWrapping.NoWrap,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        BorderThickness = new Thickness(0),
        Padding = new Thickness(14),
        FontSize = 12,
    };

    public QueueLogWindow(AgentHost host)
    {
        _host = host;
        Title = "Task Queue Log";
        Width = 760;
        Height = 520;
        MinWidth = 480;
        MinHeight = 300;
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
        var copy = Ui.Button("Copy", () => Clipboard.SetText(_text.Text));
        copy.Margin = new Thickness(0, 0, 8, 0);
        DockPanel.SetDock(copy, Dock.Right);
        header.Children.Add(copy);
        header.Children.Add(Ui.Subtitle("Task Queue Log"));

        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        root.Children.Add(_text);
        Content = root;

        Reload();
        host.QueueChanged += Reload;
        Closed += (_, _) => host.QueueChanged -= Reload;
    }

    private void Reload()
    {
        var report = QueueLog.Report(_host.Queue);
        _text.Text = report.Trim().Length == 0 ? "Nothing to log yet." : report;
    }
}
