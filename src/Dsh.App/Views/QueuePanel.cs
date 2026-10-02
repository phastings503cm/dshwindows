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

/// <summary>The task list panel (right side, Ctrl+Shift+Q): the selected chat's own list of tasks. Add,
/// edit, delete and reorder them, then Start to have the chat work them one at a time, unattended — or
/// open the timestamped log of what it did. Each chat has its own list.
///
/// The add and edit forms are built once and only shown or hidden: a refresh (the queue changes, the
/// clock ticks) rebuilds the rows, never the form you're typing in.</summary>
public sealed class QueuePanel : UserControl
{
    private readonly AppModel _model;
    private AgentHost Host => _model.Host;
    private TaskQueue Queue => Host.Queue;
    /// <summary>The chat whose list is showing (the selected one).</summary>
    private string? ChatId => Host.SelectedId;

    private readonly TextBlock _subtitle = Ui.Secondary("", 11.5);
    private readonly TextBlock _chatName = Ui.Text("", 11.5, brushKey: "TextFillColorTertiaryBrush", wrap: false);
    private readonly TextBlock _elsewhere = Ui.Text("", 11, brushKey: "AccentTextFillColorPrimaryBrush", wrap: false);
    private readonly ProgressBar _spinner = new() { IsIndeterminate = true, Width = 16, Height = 16, Visibility = Visibility.Collapsed };
    private readonly Button _startStop = new() { Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(6, 0, 0, 0) };
    private readonly Button _addButton;
    private readonly Button _logButton;
    private readonly StackPanel _list = new() { Margin = new Thickness(6) };
    private readonly ScrollViewer _listScroll;
    private readonly ContentControl _detail = new() { Focusable = false };
    private readonly Border _detailHost;

    private string? _shownChatId;
    private string? _selectedId;
    private bool _adding;
    private bool _dragging;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(30) };

    // The add form: built once, shown or hidden.
    private readonly TextBox _newTitle = Ui.Field(placeholder: "Title (short)");
    private readonly TextBox _newDetails = MultiLine("Instructions — what to do and how to tell it's done");
    private readonly CheckBox _newAtFront = new() { Content = "Put it before the other waiting tasks", FontSize = 12 };
    private readonly TextBlock _addProblem = Ui.Text("", 11.5, brushKey: "SystemFillColorCriticalBrush");
    private readonly Border _addCard;

    // The edit form: built once, filled from the task being edited.
    private string? _editingId;
    private readonly TextBox _editTitle = Ui.Field(placeholder: "Title");
    private readonly TextBox _editDetails = MultiLine("Instructions");
    private readonly UIElement _editForm;

    /// <summary>Raised when the user closes the panel.</summary>
    public event Action? CloseRequested;

    public QueuePanel(AppModel model)
    {
        _model = model;
        SetResourceReference(BackgroundProperty, "LayerFillColorDefaultBrush");

        // Header
        var header = new DockPanel { Margin = new Thickness(12, 10, 8, 8) };
        var close = IconButton(Icons.Close, "Hide the task list (Ctrl+Shift+Q)", () => CloseRequested?.Invoke());
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);
        DockPanel.SetDock(_startStop, Dock.Right);
        _startStop.Click += (_, _) => StartOrStop();
        header.Children.Add(_startStop);
        _logButton = IconButton(Icons.History, "This chat's task log", ShowLog);
        DockPanel.SetDock(_logButton, Dock.Right);
        header.Children.Add(_logButton);
        _addButton = IconButton(Icons.Add, "Add a task to this chat's list", BeginAdd);
        DockPanel.SetDock(_addButton, Dock.Right);
        header.Children.Add(_addButton);
        _spinner.Margin = new Thickness(0, 0, 8, 0);
        DockPanel.SetDock(_spinner, Dock.Left);
        header.Children.Add(_spinner);
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(Ui.Text("Task List", 14, FontWeights.SemiBold, wrap: false));
        _chatName.TextTrimming = TextTrimming.CharacterEllipsis;
        titles.Children.Add(_chatName);
        _subtitle.TextTrimming = TextTrimming.CharacterEllipsis;
        _subtitle.TextWrapping = TextWrapping.NoWrap;
        titles.Children.Add(_subtitle);
        _elsewhere.TextTrimming = TextTrimming.CharacterEllipsis;
        _elsewhere.Visibility = Visibility.Collapsed;
        titles.Children.Add(_elsewhere);
        header.Children.Add(titles);

        _addCard = BuildAddCard();
        _editForm = BuildEditForm();

        var listArea = new StackPanel();
        listArea.Children.Add(_list);
        listArea.Children.Add(_addCard);
        _listScroll = new ScrollViewer
        {
            Content = listArea,
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
        if (e.PropertyName is nameof(AgentHost.SelectedId) or nameof(AgentHost.Selected) or nameof(AgentHost.QueueRunning))
            Refresh();
    }

    // MARK: - Rendering

    internal void Refresh()
    {
        if (!IsVisible && IsLoaded) return;
        // A drag in progress keeps its rows; the drop refreshes.
        if (_dragging) return;
        var chatId = ChatId;
        if (chatId != _shownChatId)
        {
            // Another chat, another list: what was selected or being edited belonged to the old one. A
            // half-typed new task stays, and goes on the list now showing.
            _shownChatId = chatId;
            _selectedId = null;
            _editingId = null;
        }
        var chat = chatId is null ? null : Host.Session(chatId);
        var tasks = chat is null ? [] : Queue.TasksFor(chatId);
        if (_selectedId is not null && tasks.All(t => t.Id != _selectedId))
        {
            _selectedId = null;
            _editingId = null;
        }

        var running = Host.IsQueueRunning(chatId);
        var stopping = Host.IsQueueStopping(chatId);
        _chatName.Text = chat is null ? "No chat open" : $"in “{chat.Title}”";
        _chatName.ToolTip = chat?.Title;
        _spinner.Visibility = running || stopping ? Visibility.Visible : Visibility.Collapsed;
        _subtitle.Text = chat is null ? "" : stopping ? "Stopping — the current task is winding down…" : Subtitle(chatId!, running);
        _subtitle.ToolTip = _subtitle.Text;
        var others = Host.QueueRunningCount - (running ? 1 : 0);
        _elsewhere.Visibility = others > 0 ? Visibility.Visible : Visibility.Collapsed;
        _elsewhere.Text = others == 1 ? "Another chat's list is running too" : $"{others} other chats' lists are running too";
        _startStop.Content = StartStopContent(running);
        _startStop.ToolTip = running
            ? "Stop this chat's list; the current task goes back in line"
            : "Work this chat's waiting tasks one at a time, top to bottom, in this chat";
        if (running) _startStop.ClearValue(StyleProperty);
        else _startStop.SetResourceReference(StyleProperty, "AccentButtonStyle");
        _startStop.IsEnabled = chat is not null && (running || stopping || Queue.NextTaskFor(chatId) is not null);
        _addButton.IsEnabled = chat is not null;
        _logButton.IsEnabled = chat is not null;

        _list.Children.Clear();
        if (chat is null) _list.Children.Add(NoChat());
        else if (tasks.Count == 0 && !_adding) _list.Children.Add(EmptyState());
        else
            foreach (var task in tasks)
                _list.Children.Add(Row(task));
        _addCard.Visibility = _adding && chat is not null ? Visibility.Visible : Visibility.Collapsed;
        RenderDetail(tasks.Count);
    }

    private void RenderDetail(int count)
    {
        if (_editingId is not null && Queue.Find(_editingId) is { Status: not QueueTaskStatus.Running })
        {
            // The form keeps what's typed in it; only (re)attach it.
            if (!ReferenceEquals(_detail.Content, _editForm)) _detail.Content = _editForm;
        }
        else
        {
            _editingId = null;
            _detail.Content = Detail();
        }
        _detailHost.Visibility = ChatId is null || (count == 0 && !_adding) ? Visibility.Collapsed : Visibility.Visible;
    }

    private string Subtitle(string chatId, bool running)
    {
        var s = Queue.Stats(chatId);
        if (running)
        {
            var active = (Host.QueueActiveTask(chatId) is { } id ? Queue.Find(id) : null) ?? Queue.RunningTaskFor(chatId);
            if (active is null) return s.Queued > 0 ? $"Starting once the chat is free · {s.Queued} waiting" : "Running";
            return s.Queued == 0 ? $"Working “{active.Title}”" : $"Working “{active.Title}” · {s.Queued} waiting";
        }
        var parts = new List<string>();
        if (s.Queued > 0) parts.Add($"{s.Queued} waiting");
        if (s.Blocked > 0) parts.Add($"{s.Blocked} need{(s.Blocked == 1 ? "s" : "")} you");
        if (s.Failed > 0) parts.Add($"{s.Failed} failed");
        if (s.Completed > 0) parts.Add($"{s.Completed} done");
        if (s.Skipped > 0) parts.Add($"{s.Skipped} skipped");
        return parts.Count == 0 ? "Nothing on this chat's list" : string.Join(" · ", parts);
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

    private static UIElement Centered(string glyphText, string title, string blurbText, Button? action)
    {
        var panel = new StackPanel { Margin = new Thickness(18, 40, 18, 20), HorizontalAlignment = HorizontalAlignment.Center };
        var glyph = Ui.Glyph(glyphText, 30, "TextFillColorTertiaryBrush");
        glyph.HorizontalAlignment = HorizontalAlignment.Center;
        panel.Children.Add(glyph);
        var heading = Ui.Text(title, 13, FontWeights.SemiBold, "TextFillColorSecondaryBrush");
        heading.HorizontalAlignment = HorizontalAlignment.Center;
        heading.TextAlignment = TextAlignment.Center;
        heading.Margin = new Thickness(0, 10, 0, 4);
        panel.Children.Add(heading);
        var blurb = Ui.Text(blurbText, 12, brushKey: "TextFillColorTertiaryBrush");
        blurb.TextAlignment = TextAlignment.Center;
        panel.Children.Add(blurb);
        if (action is not null)
        {
            action.HorizontalAlignment = HorizontalAlignment.Center;
            action.Margin = new Thickness(0, 12, 0, 0);
            panel.Children.Add(action);
        }
        return panel;
    }

    private UIElement EmptyState() => Centered(Icons.Tray, "No tasks in this chat yet",
        "Line up work for this chat, one task per item. Press Start and the chat works through them by itself, " +
        "one after another, each until it's done — all week if it has to.",
        Ui.Button("Add the first task", BeginAdd));

    private UIElement NoChat() => Centered(Icons.Chat, "Open a chat to see its task list",
        "Every chat has its own list of tasks, worked in that chat.", Ui.Button("New Chat", () => _model.NewChat()));

    private UIElement Row(QueueTask task)
    {
        var (glyph, brush) = Mark(task.Status);
        var icon = Ui.Glyph(glyph, 12, brush);
        icon.Width = 18;
        icon.Margin = new Thickness(0, 0, 8, 0);
        icon.VerticalAlignment = VerticalAlignment.Top;
        icon.Padding = new Thickness(0, 2, 0, 0);

        var lines = new StackPanel();
        var title = Ui.Text(task.Title, 12.5, wrap: false);
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        lines.Children.Add(title);
        lines.Children.Add(Ui.Text(SubLine(task), 11, brushKey: "TextFillColorTertiaryBrush", wrap: false));

        var dock = new DockPanel();
        DockPanel.SetDock(icon, Dock.Left);
        dock.Children.Add(icon);
        dock.Children.Add(lines);

        var row = new Border
        {
            Child = dock,
            Tag = task.Id,
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(0, 0, 0, 2),
            CornerRadius = new CornerRadius(6),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            ToolTip = task.Status == QueueTaskStatus.Queued ? "Drag onto another waiting task to reorder" : null,
            AllowDrop = task.Status == QueueTaskStatus.Queued,
        };
        Paint(row, title, task.Id == _selectedId);
        row.MouseEnter += (_, _) =>
        {
            if (!IsSelected(row)) row.SetResourceReference(Border.BackgroundProperty, "SubtleFillColorSecondaryBrush");
        };
        row.MouseLeave += (_, _) =>
        {
            if (!IsSelected(row)) row.Background = Brushes.Transparent;
        };

        // Click selects (without rebuilding the row, so a drag can start from it); dragging a waiting task
        // onto another waiting one moves it there.
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
            _dragging = true;
            try
            {
                DragDrop.DoDragDrop(row, new DataObject("dsh-queue-task", task.Id), DragDropEffects.Move);
            }
            finally
            {
                _dragging = false;
                Refresh();
            }
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

    private bool IsSelected(Border row) => row.Tag is string id && id == _selectedId;

    private static void Paint(Border row, TextBlock title, bool selected)
    {
        if (selected) row.SetResourceReference(Border.BackgroundProperty, "SubtleFillColorSecondaryBrush");
        else row.Background = Brushes.Transparent;
        title.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
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

    private Border BuildAddCard()
    {
        var panel = new StackPanel();
        panel.Children.Add(_newTitle);
        _newDetails.Margin = new Thickness(0, 6, 0, 6);
        panel.Children.Add(_newDetails);
        panel.Children.Add(_newAtFront);
        _addProblem.Margin = new Thickness(0, 6, 0, 0);
        _addProblem.Visibility = Visibility.Collapsed;
        panel.Children.Add(_addProblem);
        var buttons = Ui.Buttons(Ui.Button("Cancel", CancelAdd), Ui.Button("Add Task", AddTask, accent: true));
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        buttons.Margin = new Thickness(0, 8, 0, 0);
        panel.Children.Add(buttons);
        var card = Ui.Card(panel, new Thickness(10));
        card.Margin = new Thickness(6, 0, 6, 6);
        card.Visibility = Visibility.Collapsed;
        _newTitle.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                AddTask();
                e.Handled = true;
            }
        };
        card.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                CancelAdd();
                e.Handled = true;
            }
        };
        return card;
    }

    private UIElement BuildEditForm()
    {
        _editDetails.Margin = new Thickness(0, 6, 0, 8);
        var panel = new StackPanel();
        panel.Children.Add(_editTitle);
        panel.Children.Add(_editDetails);
        panel.Children.Add(Ui.Buttons(Ui.Button("Save", SaveEdit, accent: true), Ui.Button("Cancel", EndEdit)));
        panel.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                EndEdit();
                e.Handled = true;
            }
        };
        return panel;
    }

    private UIElement Detail()
    {
        if (_selectedId is null || Queue.Find(_selectedId) is not { } task)
        {
            var hint = Ui.Secondary(_adding
                ? "Give the task a short title and clear instructions. It runs in this chat, after the tasks above it."
                : "Select a task to edit it, reorder it or resume it.", 12);
            hint.Margin = new Thickness(0, 6, 0, 0);
            return hint;
        }
        return Summary(task);
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
        var heading = Ui.Text(task.Title, 13, FontWeights.SemiBold, wrap: false);
        heading.TextTrimming = TextTrimming.CharacterEllipsis;
        heading.ToolTip = task.Title;
        titleRow.Children.Add(heading);
        panel.Children.Add(titleRow);
        var meta = Ui.Secondary(MetaLine(task), 11.5);
        meta.Margin = new Thickness(0, 3, 0, 0);
        panel.Children.Add(meta);
        if (task.Cwd is { } cwd && Host.Session(task.SessionId ?? "") is { } chat
            && !string.Equals(Path.TrimEndingDirectorySeparator(cwd), Path.TrimEndingDirectorySeparator(chat.WorkspacePath ?? ""), StringComparison.OrdinalIgnoreCase))
        {
            var folder = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0), ToolTip = $"Added in {cwd}" };
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
        if (task.Status is QueueTaskStatus.Blocked or QueueTaskStatus.Failed or QueueTaskStatus.Skipped)
        {
            left.Children.Add(Small("Resume", () => Host.ResumeTask(task.Id), accent: true,
                tooltip: "Put it first on this chat's list and start the list, picking up where it stopped (next in line if the list is running)"));
            left.Children.Add(IconButton(Icons.Undo, "Put it back first in line without starting it", () => Host.QueueRequeue(task.Id)));
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
            var edit = IconButton(Icons.Edit, "Edit this task", () => BeginEdit(task));
            DockPanel.SetDock(edit, Dock.Right);
            actions.Children.Add(edit);
        }
        panel.Children.Add(actions);
        return panel;
    }

    private static string MetaLine(QueueTask task) => task.Status switch
    {
        QueueTaskStatus.Queued => $"waiting · added {QueueLog.When(task.EnteredAt)}",
        QueueTaskStatus.Running => $"running · {task.Rounds} round{(task.Rounds == 1 ? "" : "s")}"
                                   + (task.TotalTokens > 0 ? $" · {task.TotalTokens:N0} tokens" : ""),
        QueueTaskStatus.Complete => $"done · {task.Rounds} round{(task.Rounds == 1 ? "" : "s")} · {task.TotalTokens:N0} tokens"
                                    + (task.AvgTokensPerSecond() is { } rate ? $" · {Math.Round(rate):0} tokens/s avg" : ""),
        QueueTaskStatus.Blocked => "waiting on you — see the chat",
        QueueTaskStatus.Failed => "failed — see the chat",
        _ => "skipped",
    };

    // MARK: - Actions

    private void Select(string id)
    {
        if (_selectedId == id && _editingId is null) return;
        _selectedId = id;
        _editingId = null;
        // Repaint the rows in place: rebuilding them under the mouse would cancel a drag about to start.
        foreach (var child in _list.Children.OfType<Border>())
            if (child.Child is DockPanel { Children.Count: 2 } dock && dock.Children[1] is StackPanel { Children.Count: > 0 } lines
                && lines.Children[0] is TextBlock title)
                Paint(child, title, IsSelected(child));
        RenderDetail(_list.Children.Count);
    }

    private void StartOrStop()
    {
        if (ChatId is not { } chatId) return;
        if (Host.IsQueueRunning(chatId)) Host.StopQueue(chatId);
        else Host.StartQueue(chatId);
        Refresh();
    }

    public void BeginAdd()
    {
        if (ChatId is null) return;
        _adding = true;
        _editingId = null;
        _selectedId = null;
        _addProblem.Visibility = Visibility.Collapsed;
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
        _addProblem.Visibility = Visibility.Collapsed;
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
        if (ChatId is not { } chatId || Host.Session(chatId) is null)
        {
            _addProblem.Text = "Open a chat first — tasks go on a chat's list.";
            _addProblem.Visibility = Visibility.Visible;
            return;
        }
        QueueTask added;
        try
        {
            added = Host.QueueAdd(chatId, title, _newDetails.Text.Trim(), _newAtFront.IsChecked == true);
        }
        catch (Exception error)
        {
            // Never lose what was typed: say what went wrong and keep the form.
            _addProblem.Text = $"Couldn't add the task: {error.Message}";
            _addProblem.Visibility = Visibility.Visible;
            App.WriteCrashLog(error, "queue add");
            return;
        }
        _newTitle.Text = "";
        _newDetails.Text = "";
        _newAtFront.IsChecked = false;
        _addProblem.Visibility = Visibility.Collapsed;
        _adding = false;
        _selectedId = added.Id;
        Refresh();
    }

    private void BeginEdit(QueueTask task)
    {
        _editingId = task.Id;
        _editTitle.Text = task.Title;
        _editDetails.Text = task.Details;
        RenderDetail(_list.Children.Count);
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => _editTitle.Focus());
    }

    private void SaveEdit()
    {
        if (_editingId is not { } id) return;
        if (_editTitle.Text.Trim().Length == 0)
        {
            _editTitle.Focus();
            return;
        }
        Host.QueueUpdate(id, _editTitle.Text.Trim(), _editDetails.Text.Trim());
        EndEdit();
    }

    private void EndEdit()
    {
        _editingId = null;
        Refresh();
    }

    private void Delete(QueueTask task)
    {
        var running = task.Status == QueueTaskStatus.Running;
        if (!Dialog.Confirm($"Delete “{task.Title}”?",
                running ? "The task is running: it stops now, and the chat moves on to the next task on its list."
                        : "It is removed from this chat's task list. What it already did in the chat stays.",
                "Delete", destructive: true)) return;
        Host.QueueRemove(task.Id);
        _selectedId = null;
        Refresh();
    }

    private void ShowLog()
    {
        if (ChatId is not { } chatId) return;
        var window = new QueueLogWindow(Host, chatId) { Owner = Window.GetWindow(this) };
        window.Show();
    }

    /// <summary>The UI self-test's way in: open the add card (twice over, so a refresh happens while it is open), then fill it in
    /// and press its button, once for each title — the card has to survive being used and opened again.</summary>
    public void SelfTestAddTasks(params string[] titles)
    {
        BeginAdd();
        BeginAdd();
        foreach (var title in titles)
        {
            BeginAdd();
            _newTitle.Text = title;
            AddTask();
        }
    }

    // MARK: - For tests

    internal TextBox NewTitleBox => _newTitle;
    internal TextBox NewDetailsBox => _newDetails;
    internal TextBox EditDetailsBox => _editDetails;
    internal bool AddFormShowing => _addCard.Visibility == Visibility.Visible;
    internal bool EditFormShowing => ReferenceEquals(_detail.Content, _editForm);
    /// <summary>The task ids of the rows showing, in order.</summary>
    internal IReadOnlyList<string> RowIds => _list.Children.OfType<Border>().Select(b => b.Tag).OfType<string>().ToList();
    internal void SubmitAdd() => AddTask();
    internal void EditTask(string id)
    {
        if (Queue.Find(id) is { } task) BeginEdit(task);
    }
    internal void SubmitEdit() => SaveEdit();

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

/// <summary>One chat's task log, as text you can read and copy.</summary>
public sealed class QueueLogWindow : Window
{
    private readonly AgentHost _host;
    private readonly string _chatId;
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

    public QueueLogWindow(AgentHost host, string chatId)
    {
        _host = host;
        _chatId = chatId;
        var chatTitle = host.Session(chatId)?.Title ?? "chat";
        Title = $"Task Log — {chatTitle}";
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
        var subtitle = Ui.Subtitle($"Task Log · {chatTitle}");
        subtitle.TextTrimming = TextTrimming.CharacterEllipsis;
        header.Children.Add(subtitle);

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
        var tasks = _host.Queue.TasksFor(_chatId);
        _text.Text = tasks.Count == 0
            ? "This chat's task list is empty."
            : QueueLog.Report(tasks, _host.Queue.Now, _host.Queue.Clock.LocalTimeZone);
    }
}
