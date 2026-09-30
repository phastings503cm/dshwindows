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

/// <summary>The remembered notes: what the agent (and you) have saved for later. Search them, add one,
/// edit or pin one, delete what's wrong. Notes are searched for each message and only the few that are
/// relevant ride along, so a long list costs nothing — this window is for keeping it tidy.</summary>
public sealed class MemoriesWindow : Window
{
    private readonly AppModel _model;
    private MemoryStore Store => _model.Host.Memory;

    private readonly TextBox _search = Ui.Field(placeholder: "Search what's remembered");
    private readonly StackPanel _list = new() { Margin = new Thickness(6) };
    private readonly TextBlock _stats = Ui.Secondary("", 11.5);
    private readonly TextBlock _storage = Ui.Status("", "SystemFillColorCautionBrush");
    private readonly TextBlock _detailHeading = Ui.Text("", 13, FontWeights.SemiBold);
    private readonly TextBox _title = Ui.Field(placeholder: "A short label");
    private readonly TextBox _body = Ui.Field(placeholder: "The fact itself — one or two sentences");
    private readonly TextBox _tags = Ui.Field(placeholder: "keywords, comma separated");
    private readonly ComboBox _kind = new() { MinWidth = 140 };
    private readonly ComboBox _scope = new() { MinWidth = 220 };
    private readonly CheckBox _pinned = new() { Content = "Always include in the system prompt (for a few standing facts)" };
    private readonly TextBlock _problem = Ui.Status("", "SystemFillColorCriticalBrush");
    private readonly TextBlock _meta = Ui.Text("", 11, brushKey: "TextFillColorTertiaryBrush");
    private readonly Button _save;
    private readonly Button _delete;
    private readonly DispatcherTimer _typing = new() { Interval = TimeSpan.FromMilliseconds(180) };

    private string? _selectedId;
    private bool _creating;

    public MemoriesWindow(AppModel model)
    {
        _model = model;
        Title = "Remembered Notes";
        Width = 980;
        Height = 660;
        MinWidth = 720;
        MinHeight = 440;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "SolidBackgroundFillColorBaseBrush");

        // Header: the switch, the counts, and the ways in.
        var header = new DockPanel { Margin = new Thickness(16, 14, 16, 10) };
        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var add = Ui.Button("Add a note", BeginNew, accent: true);
        var import = Ui.Button("Bring in from OpenClaw…", () => _model.ShowOpenClawImport());
        import.Margin = new Thickness(8, 0, 0, 0);
        right.Children.Add(add);
        right.Children.Add(import);
        DockPanel.SetDock(right, Dock.Right);
        header.Children.Add(right);
        var titles = new StackPanel();
        titles.Children.Add(Ui.Title("Remembered notes"));
        titles.Children.Add(_stats);
        _storage.TextWrapping = TextWrapping.Wrap;
        titles.Children.Add(_storage);
        header.Children.Add(titles);

        var switchRow = new DockPanel { Margin = new Thickness(16, 0, 16, 10) };
        var toggle = Ui.Check("Use memory in chats — relevant notes ride along with your messages, and the agent can save and look things up",
            _model.Config.MemoryEnabled, on => _model.Config.MemoryEnabled = on);
        switchRow.Children.Add(toggle);

        _search.Margin = new Thickness(10, 0, 10, 8);
        _typing.Tick += (_, _) =>
        {
            _typing.Stop();
            Reload();
        };
        _search.TextChanged += (_, _) =>
        {
            _typing.Stop();
            _typing.Start();
        };

        var listScroll = new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var listPane = new DockPanel();
        DockPanel.SetDock(_search, Dock.Top);
        listPane.Children.Add(_search);
        listPane.Children.Add(listScroll);
        var listBorder = new Border { Child = listPane, BorderThickness = new Thickness(0, 1, 1, 0) };
        listBorder.SetResourceReference(Border.BorderBrushProperty, "DividerStrokeColorDefaultBrush");
        listBorder.SetResourceReference(Border.BackgroundProperty, "LayerFillColorDefaultBrush");
        listBorder.Padding = new Thickness(0, 10, 0, 0);

        _save = Ui.Button("Save", Save, accent: true);
        _delete = Ui.Button("Delete", Delete);
        var editor = BuildEditor();

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(360) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(listBorder, 0);
        Grid.SetColumn(editor, 1);
        body.Children.Add(listBorder);
        body.Children.Add(editor);

        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(switchRow, Dock.Top);
        root.Children.Add(header);
        root.Children.Add(switchRow);
        root.Children.Add(body);
        Content = root;

        FillChoices();
        Store.Changed += OnStoreChanged;
        Closed += (_, _) => Store.Changed -= OnStoreChanged;
        Loaded += (_, _) => Reload();
        Reload();
        ShowNothing();
    }

    // MARK: - Editor

    private UIElement BuildEditor()
    {
        var panel = new StackPanel { Margin = new Thickness(20, 12, 20, 16) };
        panel.Children.Add(_detailHeading);
        _meta.Margin = new Thickness(0, 2, 0, 12);
        panel.Children.Add(_meta);
        panel.Children.Add(Label("Title"));
        panel.Children.Add(_title);
        panel.Children.Add(Label("What to remember"));
        _body.AcceptsReturn = true;
        _body.TextWrapping = TextWrapping.Wrap;
        _body.MinHeight = 130;
        _body.MaxHeight = 260;
        _body.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _body.VerticalContentAlignment = VerticalAlignment.Top;
        panel.Children.Add(_body);
        var row = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        var kindBox = new StackPanel { Margin = new Thickness(0, 0, 16, 0) };
        kindBox.Children.Add(Label("Kind"));
        kindBox.Children.Add(_kind);
        var scopeBox = new StackPanel();
        scopeBox.Children.Add(Label("Where it applies"));
        scopeBox.Children.Add(_scope);
        row.Children.Add(kindBox);
        row.Children.Add(scopeBox);
        panel.Children.Add(row);
        panel.Children.Add(Label("Tags"));
        panel.Children.Add(_tags);
        _pinned.Margin = new Thickness(0, 12, 0, 0);
        panel.Children.Add(_pinned);
        _problem.Margin = new Thickness(0, 10, 0, 0);
        _problem.TextWrapping = TextWrapping.Wrap;
        panel.Children.Add(_problem);
        var buttons = Ui.Buttons(_save, _delete);
        buttons.Margin = new Thickness(0, 14, 0, 0);
        panel.Children.Add(buttons);
        return new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private static TextBlock Label(string text)
    {
        var label = Ui.Text(text, 12, FontWeights.SemiBold, "TextFillColorSecondaryBrush");
        label.Margin = new Thickness(0, 10, 0, 4);
        return label;
    }

    private void FillChoices()
    {
        foreach (var kind in MemoryKinds.All) _kind.Items.Add(kind);
        _kind.SelectedItem = MemoryKinds.Note;
        RefillScopes(null);
    }

    /// <summary>"Everywhere" or "Only in <project>": the open project, and the note's own when it differs.</summary>
    private void RefillScopes(string? notesProject)
    {
        _scope.Items.Clear();
        _scope.Items.Add(new ScopeChoice("Everywhere (every chat)", null));
        var projects = new List<string>();
        if (_model.Project is { } open) projects.Add(open);
        if (notesProject is not null && !projects.Contains(notesProject, StringComparer.OrdinalIgnoreCase)) projects.Add(notesProject);
        foreach (var project in projects)
            _scope.Items.Add(new ScopeChoice($"Only in {Path.GetFileName(project.TrimEnd('\\', '/'))}", project));
        _scope.SelectedIndex = 0;
    }

    private sealed record ScopeChoice(string Label, string? Project)
    {
        public override string ToString() => Label;
    }

    // MARK: - List

    private void OnStoreChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
    {
        Reload();
        if (_selectedId is not null && Store.Get(_selectedId) is null && !_creating) ShowNothing();
    });

    private void Reload()
    {
        var stats = Store.Stats();
        var overflow = MemoryPrompt.Overflow(Store, _model.Project).Count;
        _stats.Text = stats.Count == 0
            ? "Nothing yet. Ask the agent to remember something, type /remember …, or add a note here."
            : $"{Formatting.Plural(stats.Count, "note")} · {stats.Pinned} pinned · {stats.Characters / 1000:N0}K characters" +
              (overflow > 0 ? $" · {overflow} pinned {(overflow == 1 ? "note doesn't" : "notes don't")} fit in the space the system prompt gives them (they are found by search instead)" : "");
        _storage.Text = Store.StorageProblem ?? "";
        _storage.Visibility = _storage.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        var query = _search.Text.Trim();
        // (Browsing: every note that mentions the words, however common they are — the pickier rule is for what rides along with a message.)
        IReadOnlyList<MemoryItem> items = query.Length == 0
            ? Store.All().OrderBy(i => i.Source.StartsWith(MemoryInstructions.SourcePrefix, StringComparison.Ordinal) ? 1 : 0).ToList() // copies of files after the notes someone wrote
            : Store.Search(query, new MemoryQueryOptions { AllProjects = true, IncludePinned = true, Limit = 60, Browse = true }).Select(h => h.Item).ToList();

        _list.Children.Clear();
        if (items.Count == 0)
        {
            var empty = Ui.Text(query.Length == 0 ? "No notes." : $"Nothing matches “{query}”.", 12, brushKey: "TextFillColorTertiaryBrush");
            empty.Margin = new Thickness(12, 16, 12, 0);
            _list.Children.Add(empty);
            return;
        }
        foreach (var item in items.Take(300)) _list.Children.Add(Row(item));
        if (items.Count > 300)
        {
            var more = Ui.Text($"…and {items.Count - 300} more. Search to find them.", 11.5, brushKey: "TextFillColorTertiaryBrush");
            more.Margin = new Thickness(12, 8, 12, 8);
            _list.Children.Add(more);
        }
    }

    private UIElement Row(MemoryItem item)
    {
        var selected = item.Id == _selectedId && !_creating;
        var lines = new StackPanel();
        var head = new DockPanel();
        if (item.Pinned)
        {
            var pin = Ui.Glyph(Icons.Pin, 11, "AccentTextFillColorPrimaryBrush");
            pin.Margin = new Thickness(6, 2, 0, 0);
            DockPanel.SetDock(pin, Dock.Right);
            head.Children.Add(pin);
        }
        head.Children.Add(Ui.Text(item.Title, 12.5, FontWeights.SemiBold, wrap: false));
        lines.Children.Add(head);
        if (item.Body.Length > 0 && !item.Body.StartsWith(item.Title.TrimEnd('…'), StringComparison.OrdinalIgnoreCase))
        {
            var preview = Ui.Text(TextUtil.Prefix(item.Body.Replace('\n', ' '), 110), 11.5, brushKey: "TextFillColorSecondaryBrush");
            preview.MaxHeight = 32;
            preview.TextTrimming = TextTrimming.CharacterEllipsis;
            lines.Children.Add(preview);
        }
        var scope = item.Project is null ? "everywhere" : Path.GetFileName(item.Project.TrimEnd('\\', '/'));
        var meta = Ui.Text($"{item.Kind} · {scope} · {SourceLabel(item.Source)} · {Formatting.Relative(item.UpdatedAt)}", 11, brushKey: "TextFillColorTertiaryBrush", wrap: false);
        lines.Children.Add(meta);

        var row = new Border
        {
            Child = lines,
            Padding = new Thickness(10, 7, 10, 7),
            Margin = new Thickness(0, 0, 0, 2),
            CornerRadius = new CornerRadius(6),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
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
        row.MouseLeftButtonUp += (_, _) => Select(item.Id);
        return row;
    }

    private static string SourceLabel(string source) =>
        source.StartsWith("import:", StringComparison.Ordinal) ? "imported"
        : source == "user" ? "you" : source == "agent" ? "the agent" : source;

    // MARK: - Selecting and editing

    private void Select(string id)
    {
        if (Store.Get(id) is not { } item) return;
        _selectedId = id;
        _creating = false;
        RefillScopes(item.Project);
        _detailHeading.Text = "Edit note";
        _title.Text = item.Title;
        _body.Text = item.Body;
        _tags.Text = string.Join(", ", item.Tags);
        _kind.SelectedItem = MemoryKinds.Normalize(item.Kind);
        _scope.SelectedIndex = item.Project is null ? 0 : Math.Max(0, _scope.Items.Cast<ScopeChoice>().ToList().FindIndex(c => string.Equals(c.Project, item.Project, StringComparison.OrdinalIgnoreCase)));
        _pinned.IsChecked = item.Pinned;
        _meta.Text = $"Saved {item.CreatedAt.LocalDateTime:d} by {SourceLabel(item.Source)}" +
                     (item.UseCount > 0 ? $" · used {Formatting.Plural(item.UseCount, "time")}" : " · not used yet") + $" · id {item.Id}";
        _problem.Text = "";
        // A note that mirrors a project's MEMORY.md is rebuilt from that file whenever it changes, so an edit
        // here would be lost: point at the file instead.
        var mirrored = item.Source.StartsWith(MemoryInstructions.SourcePrefix, StringComparison.Ordinal);
        SetEditorEnabled(!mirrored);
        _delete.Visibility = Visibility.Visible;
        if (mirrored)
            _meta.Text = $"This note is a piece of {item.Source[MemoryInstructions.SourcePrefix.Length..]} in the project folder, made searchable so the whole file needn't be sent every time. Edit that file to change it — or delete this piece to stop it coming up (the file itself is left alone).";
        Reload();
    }

    private void BeginNew()
    {
        _selectedId = null;
        _creating = true;
        RefillScopes(null);
        _detailHeading.Text = "New note";
        _title.Text = "";
        _body.Text = "";
        _tags.Text = "";
        _kind.SelectedItem = MemoryKinds.Note;
        if (_model.Project is not null) _scope.SelectedIndex = 1; // a note usually belongs to the folder you are working in
        _pinned.IsChecked = false;
        _meta.Text = "It will come up in chats when it's relevant to what you're asking.";
        _problem.Text = "";
        SetEditorEnabled(true);
        _delete.Visibility = Visibility.Collapsed;
        _title.Focus();
        Reload();
    }

    private void ShowNothing()
    {
        _selectedId = null;
        _creating = false;
        _detailHeading.Text = "Select a note";
        _meta.Text = "Pick one on the left to read or change it, or add a new one.";
        _title.Text = "";
        _body.Text = "";
        _tags.Text = "";
        _problem.Text = "";
        SetEditorEnabled(false);
        _delete.Visibility = Visibility.Collapsed;
    }

    private void SetEditorEnabled(bool on)
    {
        foreach (var control in new Control[] { _title, _body, _tags, _kind, _scope, _pinned, _save }) control.IsEnabled = on;
    }

    private void Save()
    {
        var title = _title.Text.Trim();
        var body = _body.Text.Trim();
        if (body.Length == 0 && title.Length == 0)
        {
            _problem.Text = "Write something to remember.";
            return;
        }
        var tags = _tags.Text.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (SecretGuard.LooksLikeSecret(title + ": " + body) || SecretGuard.LooksLikeSecret(string.Join('\n', tags)))
        {
            _problem.Text = "That looks like a key or password. Notes are stored as plain text — put it in the Credentials Vault (Ctrl+Shift+K) and write down only what it is for.";
            return;
        }
        var kind = _kind.SelectedItem as string ?? MemoryKinds.Note;
        var project = (_scope.SelectedItem as ScopeChoice)?.Project;
        var pinned = _pinned.IsChecked == true;
        if (_creating || _selectedId is null)
        {
            var saved = Store.Save(new MemoryDraft { Title = title, Body = body, Tags = tags, Kind = kind, Project = project, Pinned = pinned, Source = "user" });
            _creating = false;
            Select(saved.Item.Id);
        }
        else
        {
            // (Whoever edits a note owns it from then on: the agent can no longer forget it unasked.)
            var updated = Store.Update(_selectedId, n => n with { Title = title, Body = body, Tags = tags, Kind = kind, Project = project, Pinned = pinned, Source = n.Source is "agent" ? "user" : n.Source });
            if (updated is null)
            {
                _problem.Text = "That note no longer exists.";
                return;
            }
            Select(updated.Id);
        }
        _problem.Text = "";
    }

    private void Delete()
    {
        if (_selectedId is null || Store.Get(_selectedId) is not { } item) return;
        if (!Dialog.Confirm("Delete this note?", $"“{TextUtil.Prefix(item.Title, 80)}” will be forgotten.", "Delete", destructive: true)) return;
        Store.Delete(item.Id);
        ShowNothing();
        Reload();
    }
}
