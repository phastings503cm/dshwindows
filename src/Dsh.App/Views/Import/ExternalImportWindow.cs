using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Dsh.App.Infrastructure;
using Dsh.App.Model;
using Dsh.App.Views.Skills;
using Dsh.Core;

namespace Dsh.App.Views.Import;

/// <summary>"Bring in Claude Code &amp; Cursor": looks in the folders those tools keep on this PC, lists
/// what DSH can use with the sensible boxes ticked, and copies what you choose into DSH's own data
/// folder. Three steps on screen — looking, choosing, done — and the middle one is a single button when
/// the suggestions are right. Nothing in the other tools' folders is changed, and nothing is run.</summary>
public sealed class ExternalImportWindow : Window
{
    private readonly AppModel _model;
    private readonly ExternalLocations _locations;
    private readonly HashSet<string> _selected = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CheckBox> _boxes = new(StringComparer.Ordinal);
    private readonly List<GroupView> _groups = [];

    private readonly TextBlock _status = Ui.Secondary("");
    private readonly Spinner _spinner = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 8, 0) };
    private readonly StackPanel _toolChips = new() { Orientation = Orientation.Horizontal };
    private readonly StackPanel _scanning;
    private readonly DockPanel _choose = new() { Visibility = Visibility.Collapsed };
    private readonly ScrollViewer _done = new() { Visibility = Visibility.Collapsed, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBlock _summary = Ui.Secondary("");
    private readonly StackPanel _list = new();
    private readonly StackPanel _notImported = new() { Visibility = Visibility.Collapsed };
    private readonly Button _notImportedToggle;
    private readonly StackPanel _selectLinks;
    private readonly CheckBox _reviewFirst = new()
    {
        Content = "Let me approve skills first",
        ToolTip = "Skills, commands, rules and subagents wait in Settings › Skills until you approve each one, instead of turning on right away.",
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(0, 0, 14, 0),
    };
    private readonly Button _import;
    private readonly StackPanel _chooseButtons;
    private readonly StackPanel _doneButtons;

    private readonly Button _doneButton;
    private ExternalInventory _inventory = ExternalInventory.Empty;
    private CancellationTokenSource? _work;
    private Stage _stage = Stage.Scanning;
    private bool _busy;
    /// <summary>Copying is under way: closing then would leave the result unseen, so Escape waits.</summary>
    private bool _importing;

    /// <param name="locations">Where to look; the real profile unless a test or the self-test says otherwise.</param>
    public ExternalImportWindow(AppModel model, ExternalLocations? locations = null)
    {
        _model = model;
        _locations = locations ?? ExternalLocations.Standard;
        Title = "Bring in Claude Code & Cursor";
        Width = 860;
        Height = 700;
        MinWidth = 640;
        MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        _spinner.SetResourceReference(Spinner.ForegroundProperty, "AccentTextFillColorPrimaryBrush");
        _status.TextWrapping = TextWrapping.Wrap;
        _status.VerticalAlignment = VerticalAlignment.Center;
        _status.MaxWidth = 380;

        var header = new StackPanel { Margin = new Thickness(24, 20, 24, 8) };
        header.Children.Add(Ui.Title("Bring in Claude Code & Cursor"));
        var intro = Ui.Secondary(
            "DSH looks in the folders those tools keep on this PC and lists what it can use: skills, commands, rules, subagents, your instructions and the notes Claude Code saved for each project. " +
            "You choose what to copy. Nothing in their folders is changed, and nothing is run.");
        intro.Margin = new Thickness(0, 6, 0, 0);
        header.Children.Add(intro);
        var elsewhere = Ui.LinkButton("Somewhere else…", ChooseFolder);
        elsewhere.ToolTip = "Import skills, rules and commands from a folder, a .zip or a link instead — for example Claude Code inside WSL.";
        var chips = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(elsewhere, Dock.Right);
        chips.Children.Add(elsewhere);
        chips.Children.Add(_toolChips);
        header.Children.Add(chips);

        var spinner = new Spinner { Width = 28, Height = 28 };
        spinner.SetResourceReference(Spinner.ForegroundProperty, "AccentTextFillColorPrimaryBrush");
        var looking = Ui.Text("Looking for Claude Code and Cursor on this PC…", 14);
        looking.Margin = new Thickness(0, 14, 0, 0);
        looking.HorizontalAlignment = HorizontalAlignment.Center;
        _scanning = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        _scanning.Children.Add(spinner);
        _scanning.Children.Add(looking);

        _notImportedToggle = Ui.LinkButton("", ToggleNotImported);
        _selectLinks = Ui.Stack(Orientation.Horizontal,
            Ui.LinkButton("Suggested", SelectSuggested), Ui.LinkButton("All", () => SelectAll(true)), Ui.LinkButton("None", () => SelectAll(false)),
            Ui.LinkButton("Scan again", () => _ = ScanAsync()));
        var top = new DockPanel { Margin = new Thickness(24, 4, 24, 4) };
        DockPanel.SetDock(_selectLinks, Dock.Right);
        top.Children.Add(_selectLinks);
        top.Children.Add(_summary);
        _summary.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(top, Dock.Top);
        _choose.Children.Add(top);
        var bottom = Ui.Stack(_notImportedToggle, _notImported);
        bottom.Margin = new Thickness(24, 4, 24, 4);
        DockPanel.SetDock(bottom, Dock.Bottom);
        _choose.Children.Add(bottom);
        _list.Margin = new Thickness(24, 0, 24, 0);
        _choose.Children.Add(new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        var stages = new Grid();
        stages.Children.Add(_scanning);
        stages.Children.Add(_choose);
        stages.Children.Add(_done);

        _import = Ui.Button("Import", () => _ = ImportAsync(), accent: true);
        _import.IsEnabled = false;
        _chooseButtons = Ui.Stack(Orientation.Horizontal, _reviewFirst, Ui.Buttons(Ui.Button("Close", Close), _import));
        _chooseButtons.Visibility = Visibility.Collapsed;
        _doneButton = Ui.Button("Done", Close, accent: true);
        _doneButtons = Ui.Buttons(Ui.Button("Open Skills", () => OpenSettings(), tooltip: "Settings › Skills"), _doneButton);
        _doneButtons.Visibility = Visibility.Collapsed;

        var footer = new DockPanel { Margin = new Thickness(24, 10, 24, 16) };
        var right = Ui.Stack(Orientation.Horizontal, _chooseButtons, _doneButtons);
        DockPanel.SetDock(right, Dock.Right);
        footer.Children.Add(right);
        footer.Children.Add(Ui.Stack(Orientation.Horizontal, _spinner, _status));

        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(header);
        root.Children.Add(footer);
        root.Children.Add(stages);
        Content = root;

        _reviewFirst.Click += (_, _) => RefreshChrome();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && !_importing)
            {
                Close();
                e.Handled = true;
            }
        };
        Loaded += (_, _) => _ = ScanAsync();
        Closed += (_, _) => _work?.Cancel();
    }

    // MARK: - Looking

    private async Task ScanAsync()
    {
        if (_busy) return;
        _work?.Cancel();
        _work = new CancellationTokenSource();
        var token = _work.Token;
        ShowStage(Stage.Scanning);
        Busy(true, "Looking…");
        var support = _model.Host.SkillLocations;
        string? failure = null;
        try
        {
            var inventory = await Task.Run(() => ExternalScanner.Scan(_locations, support, token), token);
            _inventory = inventory;
            _selected.Clear();
            foreach (var item in inventory.Items.Where(i => i.SelectedByDefault)) _selected.Add(item.Id);
            BuildChoose();
            ShowStage(Stage.Choose);
        }
        catch (OperationCanceledException)
        {
            // Closed while looking.
        }
        catch (Exception error)
        {
            // Whatever it was, end on a screen with the reason — never leave the spinner up.
            _inventory = ExternalInventory.Empty;
            BuildChoose();
            ShowStage(Stage.Choose);
            failure = "Couldn't read everything: " + error.Message;
        }
        finally
        {
            Busy(false, "");
        }
        if (failure is not null) Fail(failure);
    }

    private enum Stage { Scanning, Choose, Done }

    /// <summary>Your instructions first — short and worth the most — then the things that pile up.</summary>
    private static readonly ExternalKind[] SectionOrder =
    [
        ExternalKind.Instructions, ExternalKind.Skill, ExternalKind.Command, ExternalKind.Rule, ExternalKind.Subagent, ExternalKind.ProjectNotes,
    ];

    private void ShowStage(Stage stage)
    {
        _stage = stage;
        _import.IsDefault = stage == Stage.Choose; // a collapsed button must not keep answering Enter
        _doneButton.IsDefault = stage == Stage.Done;
        _scanning.Visibility = stage == Stage.Scanning ? Visibility.Visible : Visibility.Collapsed;
        _choose.Visibility = stage == Stage.Choose ? Visibility.Visible : Visibility.Collapsed;
        _done.Visibility = stage == Stage.Done ? Visibility.Visible : Visibility.Collapsed;
        _chooseButtons.Visibility = stage == Stage.Choose ? Visibility.Visible : Visibility.Collapsed;
        _doneButtons.Visibility = stage == Stage.Done ? Visibility.Visible : Visibility.Collapsed;
    }

    // MARK: - Choosing

    private void BuildChoose()
    {
        _list.Children.Clear();
        _boxes.Clear();
        _groups.Clear();

        _toolChips.Children.Clear();
        var home = _locations.Home;
        foreach (var tool in _inventory.Tools) _toolChips.Children.Add(Chip(tool, home));

        if (_inventory.Items.Count == 0)
        {
            _list.Children.Add(NothingFound());
        }
        else
        {
            foreach (var kind in SectionOrder)
            {
                var items = _inventory.Items.Where(i => i.Kind == kind).ToList();
                if (items.Count > 0) AddKind(kind, items);
            }
        }

        _notImported.Children.Clear();
        foreach (var note in _inventory.Left)
        {
            var line = Ui.Secondary($"{note.Tool.Label()}: {note.Text}");
            line.Margin = new Thickness(8, 2, 0, 2);
            _notImported.Children.Add(line);
        }
        _notImportedToggle.Visibility = _inventory.Left.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        _notImported.Visibility = Visibility.Collapsed;
        SetNotImportedLabel();

        _selectLinks.Visibility = _inventory.Items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        _summary.Text = _inventory.Items.Count == 0
            ? ""
            : $"Found {ExternalLabels.Summarize(_inventory.Items)}. " +
              (_inventory.Suggested > 0 ? "The ticked ones are new and safe to bring in." : "Nothing new is ticked.");
        RefreshChrome();
    }

    private static Border Chip(ExternalToolInfo tool, string home)
    {
        var color = SkillVisuals.OriginColor(tool.Tool == ExternalTool.ClaudeCode ? SkillOrigin.Claude : SkillOrigin.Cursor);
        var folder = tool.Folder.StartsWith(home, StringComparison.OrdinalIgnoreCase) ? "~" + tool.Folder[home.Length..] : tool.Folder;
        var label = Ui.Text(tool.Found ? $"{Icons.CheckMark}  {tool.Tool.Label()}  {folder}" : $"{tool.Tool.Label()}  not found", 12, FontWeights.SemiBold, wrap: false);
        label.Foreground = new SolidColorBrush(tool.Found ? color : Color.FromRgb(0x80, 0x80, 0x80));
        return new Border
        {
            Child = label,
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 3, 10, 3),
            Margin = new Thickness(0, 0, 8, 0),
            Background = new SolidColorBrush(Color.FromArgb(0x26, color.R, color.G, color.B)),
        };
    }

    private UIElement NothingFound()
    {
        var box = Ui.Stack();
        if (!_inventory.AnyToolFound)
        {
            box.Children.Add(Ui.Subtitle("Claude Code and Cursor weren't found"));
            var claude = _locations.ClaudeHome.Replace(_locations.Home, "~");
            var cursor = _locations.CursorHome.Replace(_locations.Home, "~");
            var text = Ui.Secondary($"DSH looked in {claude} and {cursor}. If you keep them somewhere else, or have skills in a project, choose the folder yourself.");
            text.Margin = new Thickness(0, 4, 0, 10);
            box.Children.Add(text);
            box.Children.Add(Ui.Button("Choose a Folder…", ChooseFolder, accent: true));
        }
        else
        {
            box.Children.Add(Ui.Subtitle("Nothing to bring in"));
            var text = Ui.Secondary("Both tools are here, but they have no skills, commands, rules, subagents, instructions or project notes that DSH can use. " +
                                    "Skills and instruction files inside a project folder are read in place whenever you open that folder.");
            text.Margin = new Thickness(0, 4, 0, 10);
            box.Children.Add(text);
            box.Children.Add(Ui.Button("Choose a Folder…", ChooseFolder));
        }
        return Ui.Card(box);
    }

    private void ChooseFolder()
    {
        var window = new ImportSkillsWindow(_model) { Owner = this };
        window.ShowDialog();
        _model.Host.RefreshDrafts();
        _model.Host.RefreshProjectContext();
    }

    private void AddKind(ExternalKind kind, List<ExternalItem> items)
    {
        _list.Children.Add(Ui.Section($"{kind.Plural()}  ·  {items.Count}"));
        var effect = Ui.Secondary(kind.Effect());
        effect.Margin = new Thickness(2, -2, 0, 8);
        _list.Children.Add(effect);

        var groups = items.GroupBy(i => i.Group).ToList();
        // A heading per group only earns its place when there is more than one, or one is long.
        var headings = groups.Count > 1 || items.Count > 8;
        foreach (var group in groups)
        {
            var members = group.ToList();
            if (!headings)
            {
                foreach (var item in members) _list.Children.Add(ItemRow(item));
                continue;
            }
            var body = new StackPanel();
            foreach (var item in members) body.Children.Add(ItemRow(item));
            _list.Children.Add(GroupHeader(group.Key, members, body));
            _list.Children.Add(body);
        }
    }

    private sealed class GroupView(IReadOnlyList<ExternalItem> items, CheckBox header, TextBlock count)
    {
        public IReadOnlyList<ExternalItem> Items { get; } = items;
        public CheckBox Header { get; } = header;
        public TextBlock Count { get; } = count;
    }

    private UIElement GroupHeader(string title, List<ExternalItem> members, StackPanel body)
    {
        // A long list from a plugin or Cursor's built-ins starts folded — the header still shows and sets
        // what's ticked; your own folders stay open unless they run very long.
        var yours = members.All(i => i.Group.StartsWith("Your", StringComparison.Ordinal));
        var folded = members.Count > 8 && (!yours || members.Count > 30);
        var chevron = Ui.Glyph(folded ? Icons.ChevronRight : Icons.ChevronDown, 11);
        chevron.Margin = new Thickness(0, 0, 8, 0);
        body.Visibility = folded ? Visibility.Collapsed : Visibility.Visible;

        var header = new CheckBox { IsThreeState = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        var count = Ui.Secondary("");
        count.VerticalAlignment = VerticalAlignment.Center;
        var group = new GroupView(members, header, count);
        _groups.Add(group);
        header.Click += (_, _) =>
        {
            var selectable = members.Where(Selectable).ToList();
            SetMany(selectable, !selectable.All(i => _selected.Contains(i.Id)));
        };

        var label = Ui.Text(title, 13, FontWeights.SemiBold, wrap: false);
        label.VerticalAlignment = VerticalAlignment.Center;
        label.Margin = new Thickness(0, 0, 10, 0);
        var toggle = new Button { Content = Ui.Stack(Orientation.Horizontal, chevron, label), Padding = new Thickness(2, 4, 6, 4), HorizontalContentAlignment = HorizontalAlignment.Left };
        toggle.SetResourceReference(StyleProperty, "SubtleButton");
        toggle.Click += (_, _) =>
        {
            var open = body.Visibility != Visibility.Visible;
            body.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            chevron.Text = open ? Icons.ChevronDown : Icons.ChevronRight;
        };
        var row = new DockPanel { Margin = new Thickness(0, 8, 0, 4) };
        DockPanel.SetDock(header, Dock.Left);
        DockPanel.SetDock(count, Dock.Right);
        row.Children.Add(header);
        row.Children.Add(count);
        row.Children.Add(toggle);
        return row;
    }

    /// <summary>Not already in DSH and not already waiting for approval.</summary>
    private static bool Selectable(ExternalItem item) => item.Status is ExternalStatus.New or ExternalStatus.Different;

    private UIElement ItemRow(ExternalItem item)
    {
        var already = item.Status is ExternalStatus.Imported or ExternalStatus.Waiting;
        var box = new CheckBox
        {
            IsChecked = _selected.Contains(item.Id),
            IsEnabled = !already,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 10, 0),
        };
        box.Click += (_, _) =>
        {
            Set(item.Id, box.IsChecked == true);
            RefreshChrome();
        };
        _boxes[item.Id] = box;

        var title = new WrapPanel();
        var name = Ui.Text(item.Kind == ExternalKind.Command ? "/" + item.Name : item.Name, 13.5, FontWeights.SemiBold, wrap: false);
        name.Margin = new Thickness(0, 0, 8, 0);
        title.Children.Add(name);
        var origin = item.Tool == ExternalTool.ClaudeCode ? SkillOrigin.Claude : SkillOrigin.Cursor;
        title.Children.Add(SkillVisuals.Badge(item.Tool.Label(), SkillVisuals.OriginColor(origin)));
        switch (item.Status)
        {
            case ExternalStatus.Imported:
                title.Children.Add(SkillVisuals.Badge("Already in DSH", Color.FromRgb(0x10, 0x7C, 0x10)));
                break;
            case ExternalStatus.Waiting:
                title.Children.Add(SkillVisuals.Badge("Waiting for your approval", Color.FromRgb(0xCA, 0x50, 0x10)));
                break;
            case ExternalStatus.Different when item.Kind == ExternalKind.ProjectNotes:
                title.Children.Add(SkillVisuals.Badge("Updated since", Color.FromRgb(0x00, 0x78, 0xD4)));
                break;
            case ExternalStatus.Different:
                title.Children.Add(SkillVisuals.Badge("Different one in DSH", Color.FromRgb(0xCA, 0x50, 0x10)));
                break;
        }
        if (item.HasScripts) title.Children.Add(SkillVisuals.Badge("Has scripts", Color.FromRgb(0xCA, 0x50, 0x10)));
        if (item.FileCount > 1 && item.Kind == ExternalKind.Skill) title.Children.Add(SkillVisuals.Badge($"{item.FileCount} files"));

        var body = Ui.Stack(title);
        if (item.Description.Length > 0)
        {
            var description = Ui.Secondary(item.Description);
            description.MaxHeight = 34;
            description.TextTrimming = TextTrimming.CharacterEllipsis;
            body.Children.Add(description);
        }
        foreach (var warning in item.Warnings) body.Children.Add(Ui.Text("⚠ " + warning, 11.5, brushKey: "SystemFillColorCautionBrush"));
        if (item.Reason is { } reason && !item.SelectedByDefault && !already)
            body.Children.Add(Ui.Text(reason, 11.5, brushKey: "TextFillColorTertiaryBrush"));

        var row = new DockPanel { Opacity = already ? 0.6 : 1 };
        DockPanel.SetDock(box, Dock.Left);
        row.Children.Add(box);
        row.Children.Add(body);
        var card = Ui.Card(row, new Thickness(12, 8, 12, 8));
        card.ToolTip = item.SourcePath;
        return card;
    }

    private void Set(string id, bool on)
    {
        if (on) _selected.Add(id); else _selected.Remove(id);
    }

    private void SetMany(IEnumerable<ExternalItem> items, bool on)
    {
        foreach (var item in items)
        {
            Set(item.Id, on);
            if (_boxes.TryGetValue(item.Id, out var box)) box.IsChecked = on;
        }
        RefreshChrome();
    }

    private void SelectAll(bool on) => SetMany(_inventory.Items.Where(Selectable), on);

    private void SelectSuggested()
    {
        foreach (var item in _inventory.Items.Where(Selectable)) Set(item.Id, item.SelectedByDefault);
        foreach (var (id, box) in _boxes) box.IsChecked = _selected.Contains(id);
        RefreshChrome();
    }

    private void RefreshChrome()
    {
        foreach (var group in _groups)
        {
            var selectable = group.Items.Where(Selectable).ToList();
            var picked = selectable.Count(i => _selected.Contains(i.Id));
            group.Header.IsChecked = picked == 0 ? false : picked == selectable.Count ? true : null;
            group.Header.IsEnabled = selectable.Count > 0;
            group.Count.Text = selectable.Count == 0 ? "all already handled" : $"{picked} of {selectable.Count}";
        }
        var chosen = _inventory.Items.Where(i => _selected.Contains(i.Id)).ToList();
        _import.IsEnabled = !_busy && _stage == Stage.Choose && chosen.Count > 0;
        _import.Content = chosen.Count == 0 ? "Import" : $"Import {chosen.Count}";
        if (_busy || _stage != Stage.Choose) return;
        if (chosen.Count == 0)
        {
            SetStatus(_inventory.Items.Count == 0 ? "" : "Nothing is ticked.");
            return;
        }
        var held = _reviewFirst.IsChecked == true && chosen.Any(i => i.Kind is not (ExternalKind.Instructions or ExternalKind.ProjectNotes));
        SetStatus(ExternalLabels.Summarize(chosen) + (held ? " — skills will wait for your approval." : "."));
    }

    private void ToggleNotImported()
    {
        _notImported.Visibility = _notImported.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        SetNotImportedLabel();
    }

    private void SetNotImportedLabel()
    {
        var open = _notImported.Visibility == Visibility.Visible;
        ((TextBlock)_notImportedToggle.Content).Text = (open ? "▾ " : "▸ ") + $"Found but not imported ({_inventory.Left.Count})";
    }

    // MARK: - Importing

    /// <summary>What the Import button does; internal so the UI self-test can press it.</summary>
    internal async Task ImportAsync()
    {
        if (_busy || _selected.Count == 0) return;
        var inventory = _inventory;
        var ids = _selected.ToList();
        var asDrafts = _reviewFirst.IsChecked == true;
        var support = _model.Host.SkillLocations;
        Busy(true, "Copying…");
        _importing = true;
        string? failure = null;
        try
        {
            var result = await Task.Run(() => ExternalImporter.Import(inventory, ids, support, asDrafts));
            _model.Host.RefreshDrafts();
            _model.Host.RefreshProjectContext();
            _model.Config.ExternalImportOffered = true;
            _selected.Clear(); // what was chosen has been done; Scan again starts from the suggestions
            BuildDone(result);
            ShowStage(Stage.Done);
        }
        catch (Exception error)
        {
            failure = "Couldn't finish: " + error.Message;
        }
        finally
        {
            _importing = false;
            Busy(false, "");
        }
        if (failure is not null) Fail(failure);
    }

    private void BuildDone(ExternalImportResult result)
    {
        var page = new StackPanel { Margin = new Thickness(24, 8, 24, 8) };
        var written = result.Imported.Where(i => !i.IsDraft).ToList();
        var drafted = result.Imported.Where(i => i.IsDraft).ToList();
        var skipped = result.Skipped.Where(s => !s.Reason.StartsWith("Already ", StringComparison.Ordinal)).ToList();
        var already = result.Skipped.Count - skipped.Count;

        var headline = Ui.Stack(Orientation.Horizontal, Ui.Glyph(Icons.CheckMark, 22, "SystemFillColorSuccessBrush"),
            Ui.Text(result.Imported.Count == 0 ? "Nothing new was added" : $"Brought in {ExternalLabels.Summarize(result.Imported.Select(i => i.Item))}", 17, FontWeights.SemiBold));
        ((FrameworkElement)headline.Children[0]).Margin = new Thickness(0, 0, 10, 0);
        page.Children.Add(headline);
        var safe = Ui.Secondary("Your Claude Code and Cursor files were not changed.");
        safe.Margin = new Thickness(0, 6, 0, 10);
        page.Children.Add(safe);

        foreach (var group in written.GroupBy(i => i.Item.Kind).OrderBy(g => g.Key))
        {
            var text = Ui.Stack(Ui.Text($"{ExternalLabels.Summarize(group.Select(i => i.Item))}", 13.5, FontWeights.SemiBold), Ui.Secondary(group.Key.Effect()));
            var renamed = group.Count(i => i.Renamed);
            if (renamed > 0)
                text.Children.Add(Ui.Text($"{renamed} kept next to a different one you already had, under a numbered name.", 11.5, brushKey: "TextFillColorTertiaryBrush"));
            page.Children.Add(Ui.Card(text));
        }

        if (drafted.Count > 0)
        {
            var text = Ui.Stack(
                Ui.Text($"{Formatting.Plural(drafted.Count, "skill")} waiting for your approval", 13.5, FontWeights.SemiBold),
                Ui.Secondary("Nothing is switched on until you approve it in Settings › Skills › To approve."),
                new Border { Height = 8 },
                Ui.Button("Review Them…", () => OpenSettings()));
            var card = Ui.Card(text);
            card.SetResourceReference(Border.BorderBrushProperty, "SystemFillColorCautionBrush");
            page.Children.Add(card);
        }

        if (already > 0)
        {
            var note = Ui.Secondary($"{Formatting.Plural(already, "item")} {(already == 1 ? "was" : "were")} already in DSH, or already waiting for your approval, and left alone.");
            note.Margin = new Thickness(2, 6, 0, 0);
            page.Children.Add(note);
        }

        if (skipped.Count > 0)
        {
            page.Children.Add(Ui.Section("Couldn't bring in"));
            foreach (var one in skipped)
            {
                var line = Ui.Text($"{one.Item.Name} — {one.Reason}", 12, brushKey: "SystemFillColorCautionBrush");
                line.Margin = new Thickness(2, 1, 0, 1);
                page.Children.Add(line);
            }
        }

        var where = Ui.Secondary("Find skills in Settings › Skills. Your instructions and project notes show up in Session › Memory & Skills when you open a project.");
        where.Margin = new Thickness(2, 14, 0, 0);
        page.Children.Add(where);
        _done.Content = page;
        SetStatus("");
    }

    private void OpenSettings()
    {
        Close();
        Application.Current.Dispatcher.BeginInvoke(() => _model.ShowSettings(SettingsTab.Skills));
    }

    // MARK: - Chrome

    private void Busy(bool busy, string message)
    {
        _busy = busy;
        _spinner.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (busy) SetStatus(message);
        if (busy) _import.IsEnabled = false; else RefreshChrome();
    }

    private void Fail(string message) => SetStatus(message, "SystemFillColorCriticalBrush");

    private void SetStatus(string message, string brushKey = "TextFillColorSecondaryBrush")
    {
        _status.Text = message;
        _status.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
    }
}
