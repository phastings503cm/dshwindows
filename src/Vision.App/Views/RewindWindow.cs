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
// the left, the chosen turn's files and the selected file's diff on the right, and "Rewind…". Rewinding asks first — naming
// every file that changed since the turn, which is rewound only on "Rewind All". A rewind is a turn of its own, so it can be
// rewound in turn. The words are Vision for macOS's (CheckpointViews.swift).

/// <summary>Rewinding a turn, with its confirmation: shared by the transcript row and the Rewind window.</summary>
public static class RewindFlow
{
    /// <summary>Ask, then rewind <paramref name="turnId"/>'s files. Returns whether anything was rewound.</summary>
    public static bool Run(AppModel model, SessionVM vm, string turnId)
    {
        var host = model.Host;
        if (AgentHost.RewindBlocked(vm) is { } blocked)
        {
            Dialog.Info("Can't rewind yet", blocked);
            return false;
        }
        if (host.PlanRewind(vm, turnId) is not { } plan)
        {
            Dialog.Info("Can't rewind this turn", "Its record is no longer kept (turns are kept for 30 days, up to 1 GB).");
            return false;
        }
        if (plan.IsEmpty)
        {
            Dialog.Info("Nothing to rewind", "Every file this turn changed already holds what it held before it (or can't be rewound).");
            return false;
        }
        var message = CheckpointRewind.Confirmation(plan, vm.WorkspacePath ?? plan.Turn.Workspace);
        var title = $"Rewind “{plan.Turn.Title}”?";
        bool includeConflicts;
        if (plan.Conflicts.Count == 0)
        {
            if (Dialog.Ask(title, message, new Dialog.Choice("Cancel", IsCancel: true), new Dialog.Choice("Rewind", IsDefault: true)) != 1) return false;
            includeConflicts = false;
        }
        else
        {
            var choices = new List<Dialog.Choice> { new("Cancel", IsCancel: true), new("Rewind All", IsDestructive: true) };
            if (plan.Safe.Count > 0) choices.Add(new Dialog.Choice("Rewind Only the Others", IsDefault: true));
            var answer = Dialog.Ask(title, message, [.. choices]);
            if (answer <= 0) return false;
            includeConflicts = answer == 1;
        }
        try
        {
            return host.Rewind(vm, plan, includeConflicts).ChangedCount > 0;
        }
        catch (InvalidOperationException error)
        {
            Dialog.Info("Can't rewind yet", error.Message);
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
    private IReadOnlyList<TurnCheckpoint> _list = [];
    private string? _selectFile;

    /// <summary>Lines of one diff shown at most (the rest is summarised).</summary>
    private const int MaxDiffLines = 4_000;

    /// <param name="ask">Open on <paramref name="turnId"/> and ask to rewind it (/rewind n).</param>
    public RewindWindow(AppModel model, SessionVM session, string? turnId = null, string? file = null, bool ask = false)
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

        _rewind = Ui.Button("Rewind…", Act, accent: true, tooltip: "Put this turn's files back to how they were before it (you confirm first)");

        // Left: the turns.
        _turns.BorderThickness = new Thickness(0);
        _turns.Background = Brushes.Transparent;
        _turns.SetResourceReference(ItemsControl.ItemContainerStyleProperty, "NavListItem");
        _turns.SelectionChanged += (_, _) => ShowTurn();
        var left = new DockPanel { Width = 340 };
        left.SetResourceReference(Panel.BackgroundProperty, "LayerFillColorDefaultBrush");
        var leftTitle = Ui.Subtitle("Rewind");
        leftTitle.Margin = new Thickness(16, 16, 12, 4);
        DockPanel.SetDock(leftTitle, Dock.Top);
        left.Children.Add(leftTitle);
        var leftNote = Ui.Secondary("Each turn below changed files. Rewinding one puts its files back to how they were before it — a file it " +
                                    "created is removed. You're asked first when a file changed again since. A rewind can itself be rewound.", 11.5);
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
        var buttons = Ui.Buttons(_rewind, Ui.Button("Done", Close));
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
        if (ask && turnId is not null) Loaded += (_, _) => Dispatcher.BeginInvoke(Act);
    }

    private void OnCheckpointsChanged(string chatId)
    {
        if (chatId == _session.Id) Reload((_turns.SelectedItem as ListBoxItem)?.Tag as string);
    }

    private void Reload(string? select)
    {
        _list = _model.Host.CheckpointsOf(_session);
        _turns.Items.Clear();
        foreach (var (turn, index) in _list.Select((t, i) => (t, i)))
        {
            var title = Ui.Text($"{index + 1}. {turn.Title}", 13, wrap: false);
            var names = string.Join(", ", turn.Changed.Take(4).Select(f => System.IO.Path.GetFileName(f.Path)));
            var line = Ui.Secondary($"{turn.StartedAt.ToLocalTime():MMM d, HH:mm} · {turn.Changed.Count} file{(turn.Changed.Count == 1 ? "" : "s")}: {names}" +
                                    (turn.IsRewound ? " · rewound" : ""), 11.5);
            line.TextTrimming = TextTrimming.CharacterEllipsis;
            line.TextWrapping = TextWrapping.NoWrap;
            _turns.Items.Add(new ListBoxItem { Content = Ui.Stack(title, line), Tag = turn.Id, Padding = new Thickness(12, 7, 12, 7) });
        }
        if (_list.Count == 0)
        {
            _heading.Text = "No turn in this chat has changed files yet.";
            _meta.Text = "";
            _files.Items.Clear();
            _diff.Document = new FlowDocument();
            _rewind.IsEnabled = false;
            return;
        }
        var index2 = select is null ? 0 : Math.Max(0, _list.ToList().FindIndex(t => t.Id == select));
        _turns.SelectedIndex = index2;
        ShowTurn();
    }

    private TurnCheckpoint? Selected => (_turns.SelectedItem as ListBoxItem)?.Tag is string id ? _list.FirstOrDefault(t => t.Id == id) : null;

    private void ShowTurn()
    {
        if (Selected is not { } turn) return;
        _heading.Text = turn.Title;
        var status = turn.IsRewound ? $" · rewound {turn.RewoundAt!.Value.ToLocalTime():MMM d, HH:mm}" : "";
        _meta.Text = $"{turn.StartedAt.ToLocalTime():dddd MMM d, HH:mm} · {turn.Heading} · {turn.LineSummary}{status}";
        _rewind.IsEnabled = AgentHost.RewindBlocked(_session) is null;
        _rewind.ToolTip = AgentHost.RewindBlocked(_session) ?? "Put this turn's files back to how they were before it (you confirm first)";
        _files.Items.Clear();
        var root = _session.WorkspacePath ?? turn.Workspace;
        foreach (var file in turn.Changed)
        {
            var vm = new CheckpointFileVM(file, root);
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
        _diff.Document = Render(CheckpointRewind.Diff(_model.Host.Checkpoints, file, _session.WorkspacePath ?? turn.Workspace));
    }

    /// <summary>A diff as a document: added lines green, removed lines red, hunk headers in the accent colour.</summary>
    public static FlowDocument Render(string diff)
    {
        var document = new FlowDocument { PagePadding = new Thickness(10), FontSize = 12 };
        document.SetResourceReference(FlowDocument.FontFamilyProperty, "MonoFont");
        document.SetResourceReference(FlowDocument.ForegroundProperty, "TextFillColorPrimaryBrush");
        var lines = diff.TrimEnd('\n').Split('\n');
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

    private void Act()
    {
        if (Selected is { } turn) RewindFlow.Run(_model, _session, turn.Id);
    }
}
