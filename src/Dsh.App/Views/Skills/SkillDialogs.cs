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
using ICSharpCode.AvalonEdit;
using Microsoft.Win32;

namespace Dsh.App.Views.Skills;

/// <summary>Shared chrome for the skill dialogs: a title row, a body, and a button row.</summary>
public abstract class SkillDialog : Window
{
    protected readonly TextBlock StatusLine = Ui.Secondary("");

    protected SkillDialog(string title, double width, double height)
    {
        Title = title;
        Width = width;
        Height = height;
        MinWidth = 520;
        MinHeight = 380;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        StatusLine.TextWrapping = TextWrapping.Wrap;
        StatusLine.MaxHeight = 60;
    }

    protected void Layout(UIElement header, UIElement body, UIElement left, UIElement right)
    {
        var footer = new DockPanel { Margin = new Thickness(16, 10, 16, 14) };
        DockPanel.SetDock(right, Dock.Right);
        footer.Children.Add(right);
        footer.Children.Add(left);
        var top = new Border { Child = header, Padding = new Thickness(16, 14, 16, 10) };
        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(footer);
        root.Children.Add(new Border { Child = body, Padding = new Thickness(16, 0, 16, 0) });
        Content = root;
    }

    protected void Fail(string message) => SetStatus(message, "SystemFillColorCriticalBrush");
    protected void Succeed(string message) => SetStatus(message, "SystemFillColorSuccessBrush");

    protected void SetStatus(string message, string brushKey = "TextFillColorSecondaryBrush")
    {
        StatusLine.Text = message;
        StatusLine.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
    }

    protected static TextEditor MarkdownEditor(string text, bool readOnly = false)
    {
        var editor = new TextEditor
        {
            Text = text,
            IsReadOnly = readOnly,
            ShowLineNumbers = true,
            WordWrap = true,
            FontSize = 13,
            Padding = new Thickness(6),
            BorderThickness = new Thickness(1),
        };
        editor.SetResourceReference(Control.FontFamilyProperty, "MonoFont");
        editor.SetResourceReference(Control.BackgroundProperty, "ControlFillColorDefaultBrush");
        editor.SetResourceReference(Control.ForegroundProperty, "TextFillColorPrimaryBrush");
        editor.SetResourceReference(Control.BorderBrushProperty, "ControlStrokeColorDefaultBrush");
        editor.SetResourceReference(TextEditor.LineNumbersForegroundProperty, "TextFillColorTertiaryBrush");
        editor.Options.ConvertTabsToSpaces = true;
        editor.Options.IndentationSize = 2;
        return editor;
    }

    /// <summary>Lint findings for a SKILL.md, as coloured lines.</summary>
    protected static StackPanel Issues(IEnumerable<SkillIssue> issues)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        foreach (var issue in issues)
        {
            var (glyph, key) = issue.Severity switch
            {
                SkillIssueSeverity.Error => (Icons.Error, "SystemFillColorCriticalBrush"),
                SkillIssueSeverity.Warning => (Icons.Warning, "SystemFillColorCautionBrush"),
                _ => (Icons.Info, "TextFillColorSecondaryBrush"),
            };
            var icon = Ui.Glyph(glyph, 12, key);
            icon.Margin = new Thickness(0, 1, 6, 0);
            icon.VerticalAlignment = VerticalAlignment.Top;
            var text = Ui.Text(issue.Message, 12, brushKey: key);
            var row = new DockPanel { Margin = new Thickness(0, 1, 0, 1) };
            DockPanel.SetDock(icon, Dock.Left);
            row.Children.Add(icon);
            row.Children.Add(text);
            panel.Children.Add(row);
        }
        return panel;
    }

    protected static ComboBox ScopePicker(AppModel model, SkillScope scope, string projectLabel = "This project", string userLabel = "Everywhere")
    {
        var picker = new ComboBox { MinWidth = 150 };
        picker.Items.Add(new ComboBoxItem { Content = projectLabel, Tag = SkillScope.Project, IsEnabled = model.Project is not null, IsSelected = scope == SkillScope.Project && model.Project is not null });
        picker.Items.Add(new ComboBoxItem { Content = userLabel, Tag = SkillScope.User, IsSelected = scope == SkillScope.User || model.Project is null });
        return picker;
    }

    protected static SkillScope ScopeOf(ComboBox picker) =>
        picker.SelectedItem is ComboBoxItem { Tag: SkillScope scope } ? scope : SkillScope.User;

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }
}

/// <summary>View or edit a skill, review a draft, or write a new one by hand.</summary>
public sealed class SkillEditorWindow : SkillDialog
{
    private enum Mode { Skill, Draft, New }

    private readonly AppModel _model;
    private readonly Mode _mode;
    private readonly Skill? _skill;
    private readonly SkillDraft? _draft;
    private readonly TextEditor _editor;
    private readonly StackPanel _issues = new();
    private readonly ComboBox? _scope;
    private readonly DispatcherTimer _lint;
    private string _original;
    private bool ReadOnly => _mode == Mode.Skill && _skill is { IsOwned: false };

    private SkillEditorWindow(AppModel model, Mode mode, Skill? skill, SkillDraft? draft, SkillScope scope, string text, string title)
        : base(title, 780, 640)
    {
        _model = model;
        _mode = mode;
        _skill = skill;
        _draft = draft;
        _original = text;
        _editor = MarkdownEditor(text, ReadOnly);
        _lint = new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, (_, _) =>
        {
            _lint!.Stop();
            UpdateIssues();
        }, Dispatcher);
        _lint.Stop();
        _editor.TextChanged += (_, _) =>
        {
            _lint.Stop();
            _lint.Start();
        };

        var header = new DockPanel();
        if (skill is not null)
        {
            var badges = SkillVisuals.Badges(skill);
            DockPanel.SetDock(badges, Dock.Right);
            header.Children.Add(badges);
        }
        else if (draft is not null)
        {
            var badge = SkillVisuals.Badge(draft.SourceLabel, Color.FromRgb(0xCA, 0x50, 0x10));
            DockPanel.SetDock(badge, Dock.Right);
            header.Children.Add(badge);
        }
        header.Children.Add(Ui.Subtitle(title));

        var body = new DockPanel();
        var bottom = Ui.Stack(new ScrollViewer { Content = _issues, MaxHeight = 90, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, StatusLine);
        DockPanel.SetDock(bottom, Dock.Bottom);
        body.Children.Add(bottom);
        body.Children.Add(_editor);

        var left = new StackPanel { Orientation = Orientation.Horizontal };
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        switch (mode)
        {
            case Mode.Skill when ReadOnly:
                left.Children.Add(Ui.Button("Copy to My Skills (this project)", () => Adopt(SkillScope.Project)));
                ((Button)left.Children[0]).IsEnabled = model.Project is not null;
                left.Children.Add(Spaced(Ui.Button("Copy to My Skills (everywhere)", () => Adopt(SkillScope.User))));
                right.Children.Add(Ui.Button("Close", Close));
                break;
            case Mode.Skill:
                left.Children.Add(Ui.Button("Improve with AI", () => _ = ImproveAsync()));
                right.Children.Add(Ui.Button("Cancel", Close));
                right.Children.Add(Spaced(Ui.Button("Save", Save, accent: true)));
                break;
            case Mode.Draft:
                _scope = ScopePicker(model, draft!.Scope);
                left.Children.Add(Ui.Text("Goes to  ", 12.5, wrap: false));
                ((FrameworkElement)left.Children[0]).VerticalAlignment = VerticalAlignment.Center;
                left.Children.Add(_scope);
                left.Children.Add(Spaced(Ui.Button("Reject", Reject)));
                right.Children.Add(Ui.Button("Cancel", Close));
                right.Children.Add(Spaced(Ui.Button("Save Draft", SaveDraft)));
                right.Children.Add(Spaced(Ui.Button("Approve & Activate", () => Activate(ConflictPolicy.Fail), accent: true)));
                break;
            case Mode.New:
                _scope = ScopePicker(model, scope);
                left.Children.Add(Ui.Text("Save to  ", 12.5, wrap: false));
                ((FrameworkElement)left.Children[0]).VerticalAlignment = VerticalAlignment.Center;
                left.Children.Add(_scope);
                right.Children.Add(Ui.Button("Cancel", Close));
                right.Children.Add(Spaced(Ui.Button("Create", () => Activate(ConflictPolicy.Fail), accent: true)));
                break;
        }
        Layout(header, body, left, right);
        UpdateIssues();
        Loaded += (_, _) => _editor.Focus();
    }

    private static FrameworkElement Spaced(FrameworkElement element)
    {
        element.Margin = new Thickness(8, 0, 0, 0);
        return element;
    }

    public static SkillEditorWindow ForSkill(AppModel model, Skill skill) =>
        new(model, Mode.Skill, skill, null, skill.Scope, SkillManager.Read(skill),
            skill.IsOwned ? "Edit skill" : $"{skill.Origin.Label()} {skill.Kind.Label().ToLowerInvariant()} (read-only)");

    public static SkillEditorWindow ForDraft(AppModel model, SkillDraft draft) =>
        new(model, Mode.Draft, null, draft, draft.Scope, SkillFiles.ReadText(draft.SkillFile) ?? "", "Review draft");

    public static SkillEditorWindow ForNew(AppModel model, SkillScope scope) =>
        new(model, Mode.New, null, null, scope, SkillManager.Scaffold("new-skill", "Use when …"), "New skill");

    private void UpdateIssues()
    {
        _issues.Children.Clear();
        if (ReadOnly) return;
        _issues.Children.Add(Issues(SkillLint.Check(_editor.Text)));
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);
        if (ReadOnly || _editor.Text == _original || DialogResult == true) return;
        if (!Dialog.Confirm("Discard your changes?", "The skill hasn't been saved.", "Discard", destructive: true)) e.Cancel = true;
    }

    private void Finish()
    {
        _original = _editor.Text;
        _model.Host.RefreshDrafts();
        _model.Host.RefreshProjectContext();
        Close();
    }

    private void Save()
    {
        try
        {
            SkillManager.Write(_skill!, _editor.Text);
            Finish();
        }
        catch (Exception error) when (error is SkillException or IOException or UnauthorizedAccessException)
        {
            Fail(error.Message);
        }
    }

    private void SaveDraft()
    {
        try
        {
            SkillDrafts.Update(_draft!, _editor.Text);
            SkillDrafts.Retarget(_draft!, ScopeOf(_scope!), _model.Project);
            _original = _editor.Text;
            _model.Host.RefreshDrafts();
            Succeed("Draft saved.");
        }
        catch (Exception error) when (error is SkillException or IOException or UnauthorizedAccessException)
        {
            Fail(error.Message);
        }
    }

    private void Activate(ConflictPolicy policy)
    {
        var locations = _model.Host.SkillLocations;
        try
        {
            if (_mode == Mode.Draft)
            {
                SkillDrafts.Update(_draft!, _editor.Text);
                SkillDrafts.Retarget(_draft!, ScopeOf(_scope!), _model.Project);
                var fresh = SkillDrafts.List(locations).FirstOrDefault(d => d.Id == _draft!.Id) ?? _draft!;
                SkillDrafts.Approve(fresh, _model.Project, policy, locations);
            }
            else
            {
                SkillManager.Create(_editor.Text, ScopeOf(_scope!), _model.Project, policy, locations);
            }
            Finish();
        }
        catch (SkillException error) when (error.Kind == SkillErrorKind.Exists)
        {
            var choice = Dialog.Ask("A skill with that name already exists.", "Replace it, or keep both?",
                new Dialog.Choice("Replace It", IsDestructive: true), new Dialog.Choice("Keep Both", IsDefault: true), new Dialog.Choice("Cancel", IsCancel: true));
            if (choice == 0) Activate(ConflictPolicy.Replace);
            else if (choice == 1) Activate(ConflictPolicy.Rename);
        }
        catch (Exception error) when (error is SkillException or IOException or UnauthorizedAccessException)
        {
            Fail(error.Message);
        }
    }

    private void Reject()
    {
        if (!Dialog.Confirm($"Reject “{_draft!.Name}”?", "The draft is deleted.", "Reject", destructive: true)) return;
        try
        {
            SkillDrafts.Reject(_draft);
            _original = _editor.Text;
            Finish();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Fail(error.Message);
        }
    }

    private void Adopt(SkillScope scope)
    {
        try
        {
            SkillManager.Adopt(_skill!, scope, _model.Project, ConflictPolicy.Rename, _model.Host.SkillLocations);
            Finish();
        }
        catch (Exception error) when (error is SkillException or IOException or UnauthorizedAccessException)
        {
            Fail(error.Message);
        }
    }

    private async Task ImproveAsync()
    {
        SetStatus("Improving…");
        try
        {
            const string goal = "Improve this skill: tighten the description so it triggers at the right time, fix unclear or missing steps, and add verification. Keep its purpose and name.";
            var result = await _model.Host.GenerateSkillAsync(goal, null, _editor.Text);
            _editor.Document.Text = result.Text;
            SetStatus("Improved — review the changes, then save.");
        }
        catch (Exception error)
        {
            Fail("Couldn't improve it: " + AgentHost.Describe(error));
        }
    }
}

/// <summary>Describe what a skill should do; the model writes it; you review it before anything is active.</summary>
public sealed class GenerateSkillWindow : SkillDialog
{
    private readonly AppModel _model;
    private readonly TextBox _goal = Ui.Field();
    private readonly CheckBox _fromChat;
    private readonly ComboBox _scope;
    private readonly TextEditor _result = MarkdownEditor("");
    private readonly StackPanel _issues = new();
    private readonly Grid _stages = new();
    private readonly StackPanel _describe;
    private readonly DockPanel _review = new() { Visibility = Visibility.Collapsed };
    private readonly Button _generate;
    private readonly StackPanel _reviewButtons;
    private readonly Spinner _spinner = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 8, 0) };
    private CancellationTokenSource? _work;

    public GenerateSkillWindow(AppModel model) : base("Write a Skill with AI", 700, 560)
    {
        _model = model;
        var chat = model.SelectedSession;
        _goal.AcceptsReturn = true;
        _goal.TextWrapping = TextWrapping.Wrap;
        _goal.Height = 120;
        _goal.VerticalContentAlignment = VerticalAlignment.Top;
        _goal.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        Placeholder.SetText(_goal, "What should it do, and when should it be used?");
        _fromChat = new CheckBox
        {
            Content = "Base it on this chat (“turn what we just did into a skill”)",
            IsEnabled = chat is { Entries.Count: > 0 },
            Margin = new Thickness(0, 10, 0, 10),
        };
        _scope = ScopePicker(model, model.Project is null ? SkillScope.User : SkillScope.Project, "This project", "All my projects");
        _spinner.SetResourceReference(Spinner.ForegroundProperty, "AccentTextFillColorPrimaryBrush");

        var example = Ui.Text("For example: “Debug a Godot 4 game: run it headless, read the errors, fix the GDScript, and confirm with a screenshot.”",
            12, brushKey: "TextFillColorTertiaryBrush");
        example.Margin = new Thickness(0, 6, 0, 0);
        _describe = Ui.Stack(
            Ui.Secondary("Describe what the skill should do and when it should be used. The model writes it; you review it before anything is active."),
            Spacer(8), _goal, example, _fromChat,
            Ui.Stack(Orientation.Horizontal, Ui.Text("Save for  ", 12.5, wrap: false), _scope));
        var reviewHint = Ui.Secondary("Review and edit it, then save it to the approval queue or activate it now.");
        reviewHint.Margin = new Thickness(0, 0, 0, 8);
        DockPanel.SetDock(reviewHint, Dock.Top);
        _review.Children.Add(reviewHint);
        var issuesHost = new ScrollViewer { Content = _issues, MaxHeight = 80, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        DockPanel.SetDock(issuesHost, Dock.Bottom);
        _review.Children.Add(issuesHost);
        _review.Children.Add(_result);
        _stages.Children.Add(_describe);
        _stages.Children.Add(_review);

        _generate = Ui.Button("Generate", Start, accent: true);
        _reviewButtons = Ui.Buttons(
            Ui.Button("Start Over", () => ShowStage(review: false)),
            Ui.Button("Save as Draft", () => Save(activate: false)),
            Ui.Button("Save & Activate", () => Save(activate: true), accent: true));
        _reviewButtons.Visibility = Visibility.Collapsed;
        var cancel = Ui.Button("Cancel", () =>
        {
            if (_work is not null) _work.Cancel();
            else Close();
        });
        var right = Ui.Buttons(cancel, _generate, _reviewButtons);
        var left = Ui.Stack(Orientation.Horizontal, _spinner, StatusLine);
        StatusLine.VerticalAlignment = VerticalAlignment.Center;
        StatusLine.MaxWidth = 300;
        Layout(Ui.Subtitle("Write a skill with AI"), _stages, left, right);
        Loaded += (_, _) => _goal.Focus();
    }

    private static Border Spacer(double height) => new() { Height = height };

    private void ShowStage(bool review)
    {
        _describe.Visibility = review ? Visibility.Collapsed : Visibility.Visible;
        _review.Visibility = review ? Visibility.Visible : Visibility.Collapsed;
        _generate.Visibility = review ? Visibility.Collapsed : Visibility.Visible;
        _reviewButtons.Visibility = review ? Visibility.Visible : Visibility.Collapsed;
        Height = review ? Math.Max(Height, 660) : Height;
    }

    private async void Start()
    {
        if (_goal.Text.Trim().Length < 8)
        {
            Fail("Say a little more about what the skill should do.");
            return;
        }
        _work = new CancellationTokenSource();
        _generate.IsEnabled = false;
        _spinner.Visibility = Visibility.Visible;
        SetStatus("Writing…");
        try
        {
            var chat = _fromChat.IsChecked == true ? _model.SelectedSession : null;
            var result = await _model.Host.GenerateSkillAsync(_goal.Text.Trim(), chat, ct: _work.Token);
            _result.Document.Text = result.Text;
            _issues.Children.Clear();
            _issues.Children.Add(Issues(SkillLint.Check(result.Text).Where(i => i.Severity >= SkillIssueSeverity.Warning)));
            SetStatus("");
            ShowStage(review: true);
        }
        catch (OperationCanceledException)
        {
            SetStatus("Stopped.");
        }
        catch (Exception error)
        {
            Fail(AgentHost.Describe(error));
        }
        finally
        {
            _work = null;
            _generate.IsEnabled = true;
            _spinner.Visibility = Visibility.Collapsed;
        }
    }

    private void Save(bool activate)
    {
        var locations = _model.Host.SkillLocations;
        try
        {
            if (activate)
                SkillManager.Create(_result.Text, ScopeOf(_scope), _model.Project, ConflictPolicy.Rename, locations);
            else
                SkillDrafts.Create(_result.Text, ScopeOf(_scope), _model.Project, _fromChat.IsChecked == true ? "chat" : "ai", locations: locations);
            _model.Host.RefreshDrafts();
            _model.Host.RefreshProjectContext();
            Close();
        }
        catch (Exception error) when (error is SkillException or IOException or UnauthorizedAccessException)
        {
            Fail(error.Message);
        }
    }
}

/// <summary>Bring in skills, rules and commands from Claude Code, Cursor, Agent Skills, a folder, a
/// zip, or a link. Nothing is run; you see what was found and choose what to import.</summary>
public sealed class ImportSkillsWindow : SkillDialog
{
    private readonly AppModel _model;
    private readonly TextBox _link = Ui.Field(placeholder: "https://github.com/owner/repo/tree/main/skills");
    private readonly StackPanel _choose;
    private readonly DockPanel _review = new() { Visibility = Visibility.Collapsed };
    private readonly StackPanel _candidates = new();
    private readonly TextBlock _found = Ui.Secondary("");
    private readonly StackPanel _notes = new();
    private readonly ComboBox _scope;
    private readonly CheckBox _asDrafts = new() { Content = "Review each first", ToolTip = "Put them in the approval queue instead of activating right away." };
    private readonly Button _importButton;
    private readonly StackPanel _reviewButtons;
    private readonly Spinner _spinner = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 8, 0) };
    private readonly HashSet<string> _selected = [];
    private ImportPlan? _plan;

    public ImportSkillsWindow(AppModel model) : base("Import Skills", 720, 600)
    {
        _model = model;
        _scope = ScopePicker(model, model.Project is null ? SkillScope.User : SkillScope.Project, "This project", "All my projects");
        _spinner.SetResourceReference(Spinner.ForegroundProperty, "AccentTextFillColorPrimaryBrush");

        var places = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        foreach (var place in CommonPlaces())
        {
            var label = place.Replace(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "~");
            var button = Ui.Button(label, () => Scan(place));
            button.Margin = new Thickness(0, 0, 8, 8);
            places.Children.Add(button);
        }
        if (places.Children.Count == 0) places.Children.Add(Ui.Secondary("No .claude, .cursor or .agents folders found in your user folder or this project."));

        var fetch = Ui.Button("Fetch", () => _ = FetchAsync());
        var linkRow = new DockPanel();
        DockPanel.SetDock(fetch, Dock.Right);
        fetch.Margin = new Thickness(8, 0, 0, 0);
        linkRow.Children.Add(fetch);
        linkRow.Children.Add(_link);
        _link.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) _ = FetchAsync();
        };

        _choose = Ui.Stack(
            Ui.Secondary("Bring in skills, rules and commands from Claude Code, Cursor, Agent Skills, or a repository. You'll see what was found and choose what to import — nothing is run."),
            Ui.Section("From this computer"),
            Ui.Buttons(Ui.Button("Choose Folder…", () => Choose(folder: true)), Ui.Button("Choose File or Zip…", () => Choose(folder: false))),
            Ui.Section("Common places"),
            places,
            Ui.Section("From a link"),
            Ui.Secondary("A GitHub repository or folder, a .zip, or a .md / .mdc file (https only)."),
            new Border { Height = 6 },
            linkRow);

        _found.Margin = new Thickness(0, 0, 0, 8);
        DockPanel.SetDock(_found, Dock.Top);
        _review.Children.Add(_found);
        var options = Ui.Stack(Orientation.Horizontal, Ui.Text("Import to  ", 12.5, wrap: false), _scope, _asDrafts,
            Ui.LinkButton("Select all / none", ToggleAll));
        _asDrafts.Margin = new Thickness(16, 0, 16, 0);
        _asDrafts.VerticalAlignment = VerticalAlignment.Center;
        options.Margin = new Thickness(0, 8, 0, 0);
        var bottom = Ui.Stack(_notes, options);
        DockPanel.SetDock(bottom, Dock.Bottom);
        _review.Children.Add(bottom);
        _review.Children.Add(new ScrollViewer { Content = _candidates, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        var stages = new Grid();
        stages.Children.Add(_choose);
        stages.Children.Add(_review);

        _importButton = Ui.Button("Import", Perform, accent: true);
        _reviewButtons = Ui.Buttons(Ui.Button("Back", Back), _importButton);
        _reviewButtons.Visibility = Visibility.Collapsed;
        var right = Ui.Buttons(Ui.Button("Close", Close), _reviewButtons);
        StatusLine.VerticalAlignment = VerticalAlignment.Center;
        StatusLine.MaxWidth = 380;
        Layout(Ui.Subtitle("Import skills"), stages, Ui.Stack(Orientation.Horizontal, _spinner, StatusLine), right);
        Closed += (_, _) =>
        {
            if (_plan is not null) SkillImporter.Dispose(_plan);
        };
    }

    private IEnumerable<string> CommonPlaces()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var name in new[] { ".claude", ".cursor", ".agents" })
        {
            var path = Path.Combine(home, name);
            if (Directory.Exists(path)) yield return path;
        }
        if (_model.Project is { } project)
        {
            foreach (var name in new[] { ".claude", ".cursor", ".agents", ".qwen" })
            {
                var path = Path.Combine(project, name);
                if (Directory.Exists(path)) yield return path;
            }
        }
    }

    private void Choose(bool folder)
    {
        string? path;
        if (folder)
        {
            var dialog = new OpenFolderDialog { Title = "Choose a folder with skills, a .claude or .cursor folder, or a whole project" };
            path = dialog.ShowDialog(this) == true ? dialog.FolderName : null;
        }
        else
        {
            var dialog = new OpenFileDialog { Title = "Choose a .zip, .md or .mdc file", Filter = "Skills|*.zip;*.md;*.mdc|All files|*.*" };
            path = dialog.ShowDialog(this) == true ? dialog.FileName : null;
        }
        if (path is not null) Scan(path);
    }

    private async void Scan(string path)
    {
        Busy(true);
        try
        {
            Show(await Task.Run(() => SkillImporter.Scan(path)));
        }
        catch (Exception error) when (error is SkillException or IOException or UnauthorizedAccessException)
        {
            Fail(error.Message);
        }
        finally
        {
            Busy(false);
        }
    }

    private async Task FetchAsync()
    {
        var link = _link.Text.Trim();
        if (link.Length == 0) return;
        Busy(true);
        try
        {
            Show(await SkillImporter.ScanRemoteAsync(link));
        }
        catch (Exception error) when (error is SkillException or IOException or UnauthorizedAccessException or System.Net.Http.HttpRequestException or TaskCanceledException)
        {
            Fail(error.Message);
        }
        finally
        {
            Busy(false);
        }
    }

    private void Busy(bool busy)
    {
        _spinner.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        SetStatus(busy ? "Working…" : "");
    }

    private void Show(ImportPlan plan)
    {
        if (_plan is not null) SkillImporter.Dispose(_plan);
        _plan = plan;
        _selected.Clear();
        foreach (var candidate in plan.Candidates) _selected.Add(candidate.Id);
        var name = Path.GetFileName(plan.Source.TrimEnd('\\', '/'));
        _found.Text = $"Found in {(name.Length == 0 ? plan.Source : name)}:";
        _notes.Children.Clear();
        foreach (var note in plan.Notes) _notes.Children.Add(Ui.Secondary(note));
        RebuildCandidates();
        _choose.Visibility = Visibility.Collapsed;
        _review.Visibility = Visibility.Visible;
        _reviewButtons.Visibility = Visibility.Visible;
        if (plan.Candidates.Count == 0) SetStatus("Nothing to import here.");
    }

    private void RebuildCandidates()
    {
        _candidates.Children.Clear();
        if (_plan is null) return;
        foreach (var candidate in _plan.Candidates)
        {
            var check = new CheckBox { IsChecked = _selected.Contains(candidate.Id), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 10, 0) };
            check.Click += (_, _) =>
            {
                if (check.IsChecked == true) _selected.Add(candidate.Id); else _selected.Remove(candidate.Id);
                UpdateImportButton();
            };
            var title = new WrapPanel();
            var name = Ui.Text(candidate.Name, 13.5, FontWeights.SemiBold, wrap: false);
            name.Margin = new Thickness(0, 0, 8, 0);
            title.Children.Add(name);
            title.Children.Add(SkillVisuals.Badge(candidate.Origin.Label(), SkillVisuals.OriginColor(candidate.Origin)));
            title.Children.Add(SkillVisuals.Badge(candidate.IsInstructionFile ? "Project instructions" : candidate.Kind.Label()));
            if (candidate.HasScripts) title.Children.Add(SkillVisuals.Badge("Has scripts", Color.FromRgb(0xCA, 0x50, 0x10)));
            if (candidate.FileCount > 1) title.Children.Add(SkillVisuals.Badge($"{candidate.FileCount} files"));
            var body = Ui.Stack(title, Ui.Secondary(candidate.Description));
            foreach (var issue in candidate.Issues) body.Children.Add(Ui.Text("⚠ " + issue, 11.5, brushKey: "SystemFillColorCautionBrush"));
            var row = new DockPanel();
            DockPanel.SetDock(check, Dock.Left);
            row.Children.Add(check);
            row.Children.Add(body);
            _candidates.Children.Add(Ui.Card(row));
        }
        if (_plan.Candidates.Any(c => _selected.Contains(c.Id) && c.HasScripts))
            _candidates.Children.Add(Ui.Status("Some selected skills bundle scripts. They never run automatically, but read them before you let the agent use them.",
                "SystemFillColorCautionBrush"));
        UpdateImportButton();
    }

    private void UpdateImportButton()
    {
        _importButton.IsEnabled = _selected.Count > 0;
        _importButton.Content = _asDrafts.IsChecked == true ? $"Add {_selected.Count} for Review" : $"Import {_selected.Count}";
    }

    private void ToggleAll()
    {
        if (_plan is null) return;
        if (_selected.Count == _plan.Candidates.Count) _selected.Clear();
        else foreach (var candidate in _plan.Candidates) _selected.Add(candidate.Id);
        RebuildCandidates();
    }

    private void Back()
    {
        if (_plan is not null) SkillImporter.Dispose(_plan);
        _plan = null;
        _selected.Clear();
        _choose.Visibility = Visibility.Visible;
        _review.Visibility = Visibility.Collapsed;
        _reviewButtons.Visibility = Visibility.Collapsed;
        SetStatus("");
    }

    private void Perform()
    {
        if (_plan is null) return;
        try
        {
            var result = SkillImporter.Perform(_plan, _selected, ScopeOf(_scope), _model.Project, _asDrafts.IsChecked == true,
                ConflictPolicy.Rename, _model.Host.SkillLocations);
            _model.Host.RefreshDrafts();
            _model.Host.RefreshProjectContext();
            var parts = new List<string>();
            if (result.Imported.Count > 0) parts.Add($"Imported {result.Imported.Count}");
            if (result.Drafts.Count > 0) parts.Add($"{result.Drafts.Count} waiting for approval");
            if (result.Skipped.Count > 0)
                Fail(string.Join(" · ", parts.Append("skipped: " + string.Join("; ", result.Skipped.Select(s => $"{s.Key}: {s.Value}")))));
            else
                Succeed(string.Join(" · ", parts) + ".");
            _selected.Clear();
            RebuildCandidates();
        }
        catch (Exception error) when (error is SkillException or IOException or UnauthorizedAccessException)
        {
            Fail(error.Message);
        }
    }
}

/// <summary>Write skills out for Claude Code, Cursor, Agent Skills or as plain folders — as a zip or
/// into a folder.</summary>
public sealed class ExportSkillsWindow : SkillDialog
{
    private readonly IReadOnlyList<Skill> _skills;
    private readonly HashSet<string> _selected;
    private readonly ComboBox _format = new() { MinWidth = 260 };
    private readonly TextBlock _detail = Ui.Secondary("");

    public ExportSkillsWindow(IReadOnlyList<Skill> skills, IReadOnlyCollection<string> preselected) : base("Export Skills", 600, 560)
    {
        _skills = skills;
        _selected = new HashSet<string>(preselected, StringComparer.OrdinalIgnoreCase);
        var list = new StackPanel();
        foreach (var skill in skills)
        {
            var check = new CheckBox { IsChecked = _selected.Contains(skill.Id), Margin = new Thickness(0, 3, 0, 3) };
            var label = Ui.Stack(Orientation.Horizontal, Ui.Text(skill.Name + "  ", 13, wrap: false),
                SkillVisuals.Badge(skill.Origin.Label(), SkillVisuals.OriginColor(skill.Origin)),
                skill.Kind == SkillKind.Skill ? null : SkillVisuals.Badge(skill.Kind.Label()));
            check.Content = label;
            check.Click += (_, _) =>
            {
                if (check.IsChecked == true) _selected.Add(skill.Id); else _selected.Remove(skill.Id);
            };
            list.Children.Add(check);
        }
        foreach (var format in ExportFormats.All)
            _format.Items.Add(new ComboBoxItem { Content = format.Label(), Tag = format, IsSelected = format == ExportFormat.Portable });
        _format.SelectionChanged += (_, _) => _detail.Text = Format.Detail();
        _detail.Text = Format.Detail();
        _detail.Margin = new Thickness(0, 6, 0, 0);

        var body = new DockPanel();
        var bottom = Ui.Stack(Ui.Section("Format"), _format, _detail);
        DockPanel.SetDock(bottom, Dock.Bottom);
        body.Children.Add(bottom);
        body.Children.Add(Ui.Card(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }));

        var right = Ui.Buttons(Ui.Button("Cancel", Close), Ui.Button("Write into a Folder…", WriteFolder), Ui.Button("Save Zip…", SaveZip, accent: true));
        StatusLine.VerticalAlignment = VerticalAlignment.Center;
        StatusLine.MaxWidth = 200;
        Layout(Ui.Subtitle("Export skills"), body, StatusLine, right);
    }

    private ExportFormat Format => _format.SelectedItem is ComboBoxItem { Tag: ExportFormat format } ? format : ExportFormat.Portable;
    private List<Skill> Chosen => _skills.Where(s => _selected.Contains(s.Id)).ToList();

    private void SaveZip()
    {
        if (Chosen.Count == 0)
        {
            Fail("Select at least one skill.");
            return;
        }
        var dialog = new SaveFileDialog { Filter = "Zip archive|*.zip", FileName = "dsh-skills.zip" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            SkillExporter.Zip(Chosen, Format, dialog.FileName);
            Succeed($"Saved {Formatting.Plural(Chosen.Count, "skill")}.");
            ShellIntegration.RevealInExplorer(dialog.FileName);
        }
        catch (Exception error) when (error is SkillException or IOException or UnauthorizedAccessException)
        {
            Fail(error.Message);
        }
    }

    private void WriteFolder()
    {
        if (Chosen.Count == 0)
        {
            Fail("Select at least one skill.");
            return;
        }
        var dialog = new OpenFolderDialog { Title = "Choose the folder to write into — a project root, or your user folder for user-wide skills" };
        if (dialog.ShowDialog(this) != true) return;
        Write(dialog.FolderName, ConflictPolicy.Fail);
    }

    private void Write(string folder, ConflictPolicy conflict)
    {
        try
        {
            var written = SkillExporter.Write(Chosen, Format, folder, conflict);
            Succeed($"Wrote {Formatting.Plural(written.Count, "item")}.");
            if (written.FirstOrDefault() is { } first) ShellIntegration.RevealInExplorer(first);
        }
        catch (SkillException error) when (error.Kind == SkillErrorKind.Exists)
        {
            var choice = Dialog.Ask("Some of these already exist in that folder.", "Replace them, or keep both?",
                new Dialog.Choice("Replace Them", IsDestructive: true), new Dialog.Choice("Keep Both", IsDefault: true), new Dialog.Choice("Cancel", IsCancel: true));
            if (choice == 0) Write(folder, ConflictPolicy.Replace);
            else if (choice == 1) Write(folder, ConflictPolicy.Rename);
        }
        catch (Exception error) when (error is SkillException or IOException or UnauthorizedAccessException)
        {
            Fail(error.Message);
        }
    }
}
