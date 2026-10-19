using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Vision.App.Infrastructure;
using Vision.App.Model;
using Vision.App.Views.Dialogs;
using Vision.Core;

namespace Vision.App.Views;

// MARK: - Rewind (10-19 #20)
//
// /rewind (and "Changes…" on a turn's "Files changed" row) opens this window: the chat's recent turns that changed files on
// the left, the chosen turn's files and the selected file's diff on the right, and "Rewind this turn" / "Undo rewind".
// Rewinding asks first — and names every file that changed since the turn, which is overwritten only if the person says so.

/// <summary>Rewinding a turn, with its confirmation: shared by the transcript row and the Rewind window.</summary>
public static class RewindFlow
{
    /// <summary>Ask, then rewind <paramref name="turnId"/>'s files. Returns whether anything was rewound.</summary>
    public static bool Run(AppModel model, SessionVM vm, string turnId)
    {
        var host = model.Host;
        if (AgentHost.RewindBlocked(vm) is { } blocked)
        {
            Dialog.Info("Can't rewind right now", blocked);
            return false;
        }
        if (host.PlanRewind(vm, turnId) is not { } plan)
        {
            Dialog.Info("Can't rewind this turn", "Its snapshot is no longer kept (checkpoints are kept for 30 days, up to 1 GB).");
            return false;
        }
        string Name(RewindItem item) => plan.Turn.Workspace is { } root ? CheckpointRewind.Relative(root, item.File.Path) : System.IO.Path.GetFileName(item.File.Path);
        string List(IEnumerable<RewindItem> items)
        {
            var names = items.Select(Name).ToList();
            return string.Join("\n", names.Take(10).Select(n => "• " + n)) + (names.Count > 10 ? $"\n• and {names.Count - 10} more" : "");
        }
        if (plan.Actionable == 0)
        {
            var why = plan.Unavailable.Count > 0
                ? $"These files can't go back (too large to keep a copy of, or their snapshot is gone):\n{List(plan.Unavailable)}"
                : "The files are already as they were before this turn.";
            Dialog.Info("Nothing to rewind", why);
            return false;
        }
        var actionable = plan.Items.Where(i => i.Action is RewindAction.Restore or RewindAction.Delete).ToList();
        var message = $"These files go back to how they were before “{TextUtil.Prefix(plan.Turn.Prompt, 80)}”:\n{List(actionable)}";
        if (actionable.Any(i => i.Action == RewindAction.Delete))
            message += "\n\nFiles the turn created are deleted.";
        if (plan.Unavailable.Count > 0)
            message += $"\n\nThese can't go back (too large to keep a copy of, or their snapshot is gone):\n{List(plan.Unavailable)}";
        bool overwrite;
        if (plan.Conflicts.Count > 0)
        {
            message += $"\n\nThese changed since the turn ended — rewinding them loses those later changes:\n{List(plan.Conflicts)}";
            var all = plan.Conflicts.Count == actionable.Count;
            var choices = all
                ? new[] { new Dialog.Choice("Cancel", IsCancel: true), new Dialog.Choice("Rewind Anyway", IsDestructive: true) }
                : new[] { new Dialog.Choice("Cancel", IsCancel: true), new Dialog.Choice("Skip Changed Files", IsDefault: true), new Dialog.Choice("Rewind All", IsDestructive: true) };
            var answer = Dialog.Ask("Rewind this turn?", message, choices);
            if (answer <= 0) return false;
            overwrite = all || answer == 2;
        }
        else
        {
            if (!Dialog.Confirm("Rewind this turn?", message, "Rewind")) return false;
            overwrite = false;
        }
        try
        {
            var result = host.Rewind(vm, plan, overwrite);
            if (result.Failed.Count > 0) Dialog.Info("Some files couldn't be rewound", result.Summary);
            return result.Changed > 0;
        }
        catch (InvalidOperationException error)
        {
            Dialog.Info("Can't rewind right now", error.Message);
            return false;
        }
    }

    /// <summary>Ask, then put back what a rewind replaced.</summary>
    public static bool Undo(AppModel model, SessionVM vm, string turnId)
    {
        if (AgentHost.RewindBlocked(vm) is { } blocked)
        {
            Dialog.Info("Can't undo right now", blocked);
            return false;
        }
        if (!Dialog.Confirm("Undo the rewind?", "The files go back to how they were just before you rewound this turn.", "Undo Rewind")) return false;
        try
        {
            return model.Host.UndoRewind(vm, turnId).Changed > 0;
        }
        catch (InvalidOperationException error)
        {
            Dialog.Info("Can't undo right now", error.Message);
            return false;
        }
    }
}

/// <summary>The chat's recent turns that changed files, each file's diff, and Rewind.</summary>
public sealed class RewindWindow : Window
{
    private readonly AppModel _model;
    private readonly SessionVM _session;
    private readonly ListBox _turns = new();
    private readonly ListBox _files = new();
    private readonly FlowDocumentScrollViewer _diff = new();
    private readonly TextBlock _heading = Ui.Subtitle("");
    private readonly TextBlock _meta = Ui.Secondary("");
    private readonly Button _rewind;
    private readonly Button _undo;
    private IReadOnlyList<TurnCheckpoint> _list = [];
    private string? _selectFile;

    /// <summary>Lines of one diff shown at most (the rest is summarised).</summary>
    private const int MaxDiffLines = 4_000;

    public RewindWindow(AppModel model, SessionVM session, string? turnId = null, string? file = null)
    {
        _model = model;
        _session = session;
        _selectFile = file;
        Title = "Rewind — " + session.Title;
        Width = 1100;
        Height = 720;
        MinWidth = 760;
        MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "SolidBackgroundFillColorBaseBrush");

        _rewind = Ui.Button("Rewind this turn", () => Act(undo: false), accent: true,
            tooltip: "Put this turn's files back the way they were before it (asks first if any changed since)");
        _undo = Ui.Button("Undo rewind", () => Act(undo: true), tooltip: "Put back what the rewind replaced");

        // Left: the turns.
        _turns.BorderThickness = new Thickness(0);
        _turns.Background = Brushes.Transparent;
        _turns.SetResourceReference(ItemsControl.ItemContainerStyleProperty, "NavListItem");
        _turns.SelectionChanged += (_, _) => ShowTurn();
        var left = new DockPanel { Width = 330 };
        left.SetResourceReference(Panel.BackgroundProperty, "LayerFillColorDefaultBrush");
        var leftTitle = Ui.Subtitle("Recent turns");
        leftTitle.Margin = new Thickness(16, 16, 12, 2);
        DockPanel.SetDock(leftTitle, Dock.Top);
        left.Children.Add(leftTitle);
        var leftNote = Ui.Secondary("Turns of this chat that changed files, newest first. Kept for 30 days.", 11.5);
        leftNote.Margin = new Thickness(16, 0, 12, 10);
        DockPanel.SetDock(leftNote, Dock.Top);
        left.Children.Add(leftNote);
        left.Children.Add(_turns);

        // Right: the turn, its files, the diff.
        _files.MaxHeight = 170;
        _files.BorderThickness = new Thickness(0);
        _files.Background = Brushes.Transparent;
        _files.SelectionChanged += (_, _) => ShowDiff();
        _diff.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _diff.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        _diff.BorderThickness = new Thickness(1);
        _diff.SetResourceReference(Control.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        var header = Ui.Stack(_heading, _meta);
        _meta.Margin = new Thickness(0, 2, 0, 8);
        var buttons = Ui.Buttons(_rewind, _undo, Ui.Button("Close", Close));
        buttons.Margin = new Thickness(0, 10, 0, 0);
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        var right = new DockPanel { Margin = new Thickness(20, 16, 20, 16) };
        DockPanel.SetDock(header, Dock.Top);
        right.Children.Add(header);
        DockPanel.SetDock(buttons, Dock.Bottom);
        right.Children.Add(buttons);
        var filesCard = Ui.Card(_files, new Thickness(6));
        filesCard.Margin = new Thickness(0, 0, 0, 10);
        DockPanel.SetDock(filesCard, Dock.Top);
        right.Children.Add(filesCard);
        right.Children.Add(_diff);

        var root = new DockPanel();
        DockPanel.SetDock(left, Dock.Left);
        root.Children.Add(left);
        root.Children.Add(right);
        Content = root;

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            Close();
            e.Handled = true;
        };
        model.Host.CheckpointsChanged += OnCheckpointsChanged;
        Closed += (_, _) => model.Host.CheckpointsChanged -= OnCheckpointsChanged;
        Reload(turnId);
    }

    private void OnCheckpointsChanged(string chatId)
    {
        if (chatId == _session.Id) Reload((_turns.SelectedItem as ListBoxItem)?.Tag as string);
    }

    private void Reload(string? select)
    {
        _list = _model.Host.CheckpointsOf(_session).Take(50).ToList();
        _turns.Items.Clear();
        foreach (var turn in _list)
        {
            var title = Ui.Text(turn.Prompt.Length > 0 ? turn.Prompt : "(a turn the harness started)", 13, wrap: false);
            var line = Ui.Secondary($"{turn.At.ToLocalTime():MMM d, HH:mm} · {turn.Title}{(turn.IsRewound ? " · rewound" : "")}", 11.5);
            _turns.Items.Add(new ListBoxItem { Content = Ui.Stack(title, line), Tag = turn.Id, Padding = new Thickness(12, 7, 12, 7) });
        }
        if (_list.Count == 0)
        {
            _heading.Text = "Nothing to rewind yet";
            _meta.Text = "No turn in this chat has changed files.";
            _files.Items.Clear();
            _diff.Document = new FlowDocument();
            _rewind.IsEnabled = _undo.IsEnabled = false;
            return;
        }
        var index = select is null ? 0 : Math.Max(0, _list.ToList().FindIndex(t => t.Id == select));
        _turns.SelectedIndex = index;
        ShowTurn();
    }

    private TurnCheckpoint? Selected => (_turns.SelectedItem as ListBoxItem)?.Tag is string id ? _list.FirstOrDefault(t => t.Id == id) : null;

    private void ShowTurn()
    {
        if (Selected is not { } turn) return;
        _heading.Text = turn.Prompt.Length > 0 ? TextUtil.Prefix(turn.Prompt, 140) : "A turn the harness started";
        var status = turn.IsRewound ? $" · rewound {turn.RewoundAt!.Value.ToLocalTime():MMM d, HH:mm}" : "";
        _meta.Text = $"{turn.At.ToLocalTime():dddd MMM d, HH:mm} · {turn.Title} · {turn.LineSummary}{status}";
        _rewind.IsEnabled = !turn.IsRewound;
        _undo.Visibility = turn.RewoundFrom is { Count: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        _undo.IsEnabled = true;
        _files.Items.Clear();
        foreach (var file in turn.Changed)
        {
            var vm = new CheckpointFileVM(file, turn.Workspace);
            var (key, fallback) = vm.IsCreated ? ("SystemFillColorSuccessBrush", Colors.SeaGreen)
                : vm.IsDeleted ? ("SystemFillColorCriticalBrush", Colors.IndianRed) : ("SystemFillColorCautionBrush", Colors.DarkOrange);
            var dot = new System.Windows.Shapes.Ellipse { Width = 7, Height = 7, Margin = new Thickness(0, 0, 8, 0), Fill = ThemeService.Brush(key, fallback), VerticalAlignment = VerticalAlignment.Center };
            var name = Ui.Text(vm.RelativePath, 12, wrap: false);
            name.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
            var counts = Ui.Secondary($"   {vm.Counts} · {vm.Change}", 11.5);
            _files.Items.Add(new ListBoxItem { Content = Ui.Stack(Orientation.Horizontal, dot, name, counts), Tag = file, ToolTip = vm.Tooltip, Padding = new Thickness(6, 3, 6, 3) });
        }
        var at = _selectFile is null ? 0 : Math.Max(0, turn.Changed.ToList().FindIndex(f => string.Equals(f.Path, _selectFile, StringComparison.OrdinalIgnoreCase)));
        _selectFile = null;
        _files.SelectedIndex = _files.Items.Count == 0 ? -1 : at;
        ShowDiff();
    }

    private void ShowDiff()
    {
        if (Selected is not { } turn || (_files.SelectedItem as ListBoxItem)?.Tag is not CheckpointFile file)
        {
            _diff.Document = new FlowDocument();
            return;
        }
        _diff.Document = Render(CheckpointRewind.Diff(_model.Host.Checkpoints, file, turn.Workspace));
    }

    /// <summary>A diff as a document: added lines green, removed lines red, hunk headers in the accent colour.</summary>
    public static FlowDocument Render(string diff)
    {
        var document = new FlowDocument { PagePadding = new Thickness(10), FontSize = 12 };
        document.SetResourceReference(FlowDocument.FontFamilyProperty, "MonoFont");
        document.SetResourceReference(FlowDocument.ForegroundProperty, "TextFillColorPrimaryBrush");
        var lines = diff.Split('\n');
        var shown = 0;
        foreach (var line in lines)
        {
            if (++shown > MaxDiffLines)
            {
                document.Blocks.Add(new Paragraph(new Run($"… {lines.Length - MaxDiffLines} more lines")) { Margin = new Thickness(0, 6, 0, 0) });
                break;
            }
            var paragraph = new Paragraph(new Run(line.Length == 0 ? " " : line)) { Margin = new Thickness(0), Padding = new Thickness(4, 0, 4, 0) };
            if (line.StartsWith("+++", StringComparison.Ordinal) || line.StartsWith("---", StringComparison.Ordinal))
                paragraph.FontWeight = FontWeights.SemiBold;
            else if (line.StartsWith('+'))
                paragraph.Background = new SolidColorBrush(Color.FromArgb(0x38, 0x2E, 0xA0, 0x43));
            else if (line.StartsWith('-'))
                paragraph.Background = new SolidColorBrush(Color.FromArgb(0x38, 0xD1, 0x34, 0x38));
            else if (line.StartsWith("@@", StringComparison.Ordinal))
                paragraph.SetResourceReference(TextElement.ForegroundProperty, "AccentTextFillColorPrimaryBrush");
            document.Blocks.Add(paragraph);
        }
        return document;
    }

    private void Act(bool undo)
    {
        if (Selected is not { } turn) return;
        if (undo) RewindFlow.Undo(_model, _session, turn.Id);
        else RewindFlow.Run(_model, _session, turn.Id);
    }

    /// <summary>Self-test: choose a turn and a file without clicking.</summary>
    internal void SelfTestSelect(int turn, int file)
    {
        if (turn < _turns.Items.Count) _turns.SelectedIndex = turn;
        if (file < _files.Items.Count) _files.SelectedIndex = file;
    }
}
