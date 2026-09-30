using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Dsh.App.Infrastructure;
using Dsh.App.Model;
using Dsh.App.Views.Dialogs;
using Dsh.App.Views.Import;
using Dsh.Core;

namespace Dsh.App.Views.Skills;

/// <summary>Settings › Skills: every skill DSH can see (its own and those read from Claude Code,
/// Cursor and Agent Skills), drafts waiting for approval, and the ways to add more.</summary>
public sealed class SkillsManagerPage : UserControl
{
    private enum Filter { All, On, Off, Pending }

    private readonly AppModel _model;
    private readonly TextBox _search = Ui.Field(placeholder: "Search skills");
    private readonly StackPanel _filters = new() { Orientation = Orientation.Horizontal };
    private readonly StackPanel _list = new();
    /// <summary>"Claude Code and Cursor are on this PC": shown only when there is something new to bring in.</summary>
    private readonly Border _bringIn = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 10) };
    private Filter _filter = Filter.All;
    private IReadOnlyList<Skill> _skills = [];

    public SkillsManagerPage(AppModel model, SkillsAction? action)
    {
        _model = model;
        var page = new DockPanel { MaxWidth = 860 };

        var header = new StackPanel();
        header.Children.Add(Ui.Title("Skills"));
        var intro = Ui.Secondary(
            "A skill is a saved procedure the agent loads when a task matches: how you deploy, debug, review, or write in this codebase. " +
            "Skills from Claude Code, Cursor and Agent Skills are read in place.");
        intro.Margin = new Thickness(0, 6, 0, 12);
        header.Children.Add(intro);
        header.Children.Add(_bringIn);

        var newMenu = Ui.Button("New ▾", () => { });
        newMenu.Click += (_, _) =>
        {
            var menu = new ContextMenu();
            menu.Items.Add(Menus.Item("Write with AI…", Generate, Icons.Lightbulb));
            menu.Items.Add(Menus.Item("Write by hand…", NewManual, Icons.Edit));
            Menus.Open(menu, newMenu);
        };
        newMenu.SetResourceReference(StyleProperty, "AccentButtonStyle");
        var toolbar = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var actions = Ui.Buttons(newMenu, Ui.Button("Import…", Import), Ui.Button("Export…", () => Export(null)));
        DockPanel.SetDock(actions, Dock.Right);
        actions.Margin = new Thickness(12, 0, 0, 0);
        toolbar.Children.Add(actions);
        toolbar.Children.Add(_search);
        header.Children.Add(toolbar);
        _filters.Margin = new Thickness(0, 0, 0, 4);
        header.Children.Add(_filters);
        DockPanel.SetDock(header, Dock.Top);
        page.Children.Add(header);

        var footer = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        footer.Children.Add(Ui.Secondary("Also read:  "));
        var sources = model.Config.SkillSources;
        foreach (var (label, flags) in new[] { ("Claude Code", SkillSources.Claude), ("Cursor", SkillSources.Cursor), ("Agents / Qwen", SkillSources.Agents | SkillSources.Qwen) })
        {
            var box = Ui.Check(label, sources.HasFlag(flags), on =>
            {
                var current = model.Config.SkillSources;
                model.Config.SkillSources = on ? current | flags : current & ~flags;
                model.Host.RefreshProjectContext();
                model.Host.RefreshDrafts();
            });
            box.Margin = new Thickness(0, 0, 16, 0);
            box.ToolTip = $"Read {label} skills, commands and rules from where that tool keeps them.";
            footer.Children.Add(box);
        }
        footer.Children.Add(Ui.LinkButton("Reload", () =>
        {
            model.Host.RefreshDrafts();
            Reload();
        }));
        footer.Children.Add(Ui.LinkButton("Bring in from Claude Code & Cursor…", ImportExternal));
        foreach (FrameworkElement child in footer.Children) child.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(footer, Dock.Bottom);
        page.Children.Add(footer);

        page.Children.Add(new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = new Border { Child = page, Padding = new Thickness(28, 20, 28, 16) };

        _search.TextChanged += (_, _) => Rebuild();
        model.Host.PropertyChanged += OnHostChanged;
        Unloaded += (_, _) => model.Host.PropertyChanged -= OnHostChanged;
        Reload();
        _ = OfferBringInAsync();
        if (action is { } requested) Dispatcher.BeginInvoke(() => Run(requested));
    }

    /// <summary>Look for Claude Code and Cursor in the background and, if they have something DSH
    /// doesn't yet, put one button at the top of the page.</summary>
    private async Task OfferBringInAsync()
    {
        _bringIn.Visibility = Visibility.Collapsed;
        if (SelfTest.Current is not null) return; // the self-test renders a fixed page
        var support = _model.Host.SkillLocations;
        ExternalInventory inventory;
        try
        {
            inventory = await Task.Run(() => ExternalScanner.Scan(ExternalLocations.Standard, support));
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            return;
        }
        if (inventory.Suggested == 0) return;

        var ready = ExternalLabels.Summarize(inventory.Items.Where(i => i.SelectedByDefault));
        var text = Ui.Stack(
            Ui.Text("Claude Code and Cursor are on this PC", 13.5, FontWeights.SemiBold),
            Ui.Secondary($"DSH can bring in {ready}. It takes one click, and their files stay as they are."));
        var button = Ui.Button("Bring Them In…", ImportExternal, accent: true);
        button.VerticalAlignment = VerticalAlignment.Center;
        button.Margin = new Thickness(16, 0, 0, 0);
        var row = new DockPanel();
        DockPanel.SetDock(button, Dock.Right);
        row.Children.Add(button);
        row.Children.Add(text);
        var card = Ui.Card(row);
        card.SetResourceReference(Border.BorderBrushProperty, "AccentFillColorDefaultBrush");
        _bringIn.Child = card;
        _bringIn.Visibility = Visibility.Visible;
    }

    private void OnHostChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AgentHost.SkillsRevision)) Reload();
    }

    private void Run(SkillsAction action)
    {
        switch (action)
        {
            case SkillsAction.Generate: Generate(); break;
            case SkillsAction.Import: Import(); break;
            case SkillsAction.NewManual: NewManual(); break;
        }
    }

    private void Reload()
    {
        _skills = _model.Host.Skills(_model.SelectedSession);
        Rebuild();
    }

    private void Rebuild()
    {
        var disabled = _model.Config.DisabledSkills;
        var drafts = _model.Host.PendingDrafts;
        var query = _search.Text.Trim();

        _filters.Children.Clear();
        foreach (var filter in Enum.GetValues<Filter>())
        {
            var label = filter switch
            {
                Filter.On => "On",
                Filter.Off => "Off",
                Filter.Pending => drafts.Count > 0 ? $"To approve ({drafts.Count})" : "To approve",
                _ => "All",
            };
            var segment = new RadioButton { Content = label, GroupName = "skillfilter", IsChecked = filter == _filter };
            segment.SetResourceReference(StyleProperty, "Segment");
            var value = filter;
            segment.Checked += (_, _) =>
            {
                _filter = value;
                Rebuild();
            };
            _filters.Children.Add(segment);
        }

        bool Matches(string name, string description) =>
            query.Length == 0 || name.Contains(query, StringComparison.OrdinalIgnoreCase) || description.Contains(query, StringComparison.OrdinalIgnoreCase);

        _list.Children.Clear();
        var visibleDrafts = drafts.Where(d => Matches(d.Name, d.Description)).ToList();
        if (_skills.Count == 0 && drafts.Count == 0)
        {
            _list.Children.Add(Ui.Card(Ui.Stack(
                Ui.Subtitle("No skills yet"),
                Ui.Secondary("Write one, have the AI write one from what you want, or import from Claude Code or Cursor."),
                Ui.Buttons(Ui.Button("Write with AI…", Generate, accent: true), Ui.Button("From Claude Code & Cursor…", ImportExternal),
                    Ui.Button("Import…", Import)))));
            return;
        }

        if (visibleDrafts.Count > 0 && _filter is Filter.All or Filter.Pending)
        {
            var heading = Ui.Section("Awaiting your approval");
            heading.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorCautionBrush");
            _list.Children.Add(heading);
            foreach (var draft in visibleDrafts) _list.Children.Add(DraftRow(draft));
        }
        if (_filter == Filter.Pending)
        {
            if (visibleDrafts.Count == 0) _list.Children.Add(Ui.Card(Ui.Secondary("Nothing is waiting for approval.")));
            return;
        }

        var filtered = _skills.Where(s =>
        {
            var off = disabled.Contains(s.Id);
            if (_filter == Filter.On && (off || s.Shadowed)) return false;
            if (_filter == Filter.Off && !off) return false;
            return Matches(s.Name, s.Description);
        }).ToList();

        foreach (var group in filtered.GroupBy(GroupTitle))
        {
            _list.Children.Add(Ui.Section(group.Key));
            foreach (var skill in group) _list.Children.Add(SkillRow(skill, disabled.Contains(skill.Id)));
        }
        if (filtered.Count == 0 && visibleDrafts.Count == 0) _list.Children.Add(Ui.Card(Ui.Secondary("Nothing matches.")));
    }

    private static string GroupTitle(Skill s)
    {
        if (s.Origin == SkillOrigin.Builtin) return "Built in to DSH";
        if (s.Origin == SkillOrigin.Dsh) return s.Scope == SkillScope.Project ? "This project — your skills" : "All projects — your skills";
        return $"{s.Origin.Label()} — read from {(s.Scope == SkillScope.Project ? "this project" : "your user folder")}";
    }

    private UIElement SkillRow(Skill skill, bool off)
    {
        var toggle = new CheckBox { IsChecked = !off, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 12, 0), ToolTip = off ? "Off everywhere" : "On" };
        toggle.Click += (_, _) =>
        {
            _model.Config.SetSkillEnabled(skill.Id, toggle.IsChecked == true);
            _model.Host.RefreshProjectContext();
            Rebuild();
        };
        var body = new StackPanel();
        body.Children.Add(Ui.Text(skill.Name, 13.5, FontWeights.SemiBold));
        var badges = SkillVisuals.Badges(skill);
        badges.Margin = new Thickness(0, 3, 0, 3);
        body.Children.Add(badges);
        var description = Ui.Secondary(skill.Description);
        description.MaxHeight = 36;
        description.TextTrimming = TextTrimming.CharacterEllipsis;
        body.Children.Add(description);

        var more = new Button { Content = Icons.More, ToolTip = "More", VerticalAlignment = VerticalAlignment.Top };
        more.SetResourceReference(StyleProperty, "IconButton");
        more.Click += (_, _) =>
        {
            var menu = new ContextMenu();
            menu.Items.Add(Menus.Item(skill.IsOwned ? "Edit…" : "View…", () => Edit(SkillEditorWindow.ForSkill(_model, skill)), Icons.Edit));
            if (!skill.IsOwned)
            {
                menu.Items.Add(Menus.Item("Copy to My Skills (this project)", () => Adopt(skill, SkillScope.Project), enabled: _model.Project is not null));
                menu.Items.Add(Menus.Item("Copy to My Skills (everywhere)", () => Adopt(skill, SkillScope.User)));
            }
            menu.Items.Add(new Separator());
            menu.Items.Add(Menus.Item("Export…", () => Export([skill.Id]), Icons.Export));
            menu.Items.Add(Menus.Item("Reveal in Explorer", () => ShellIntegration.RevealInExplorer(skill.Path), Icons.OpenInWindow));
            if (skill.Origin != SkillOrigin.Builtin)
            {
                menu.Items.Add(new Separator());
                menu.Items.Add(Menus.Item("Move to Recycle Bin…", () => Trash(skill), Icons.Delete));
            }
            Menus.Open(menu, more);
        };

        var row = new DockPanel { Opacity = off || skill.Shadowed ? 0.55 : 1 };
        DockPanel.SetDock(toggle, Dock.Left);
        DockPanel.SetDock(more, Dock.Right);
        row.Children.Add(toggle);
        row.Children.Add(more);
        row.Children.Add(body);
        var card = Ui.Card(row);
        card.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2) Edit(SkillEditorWindow.ForSkill(_model, skill));
        };
        return card;
    }

    private UIElement DraftRow(SkillDraft draft)
    {
        var body = new StackPanel();
        var title = Ui.Stack(Orientation.Horizontal, Ui.Text(draft.Name, 13.5, FontWeights.SemiBold, wrap: false));
        var source = SkillVisuals.Badge(draft.SourceLabel, System.Windows.Media.Color.FromRgb(0xCA, 0x50, 0x10));
        source.Margin = new Thickness(8, 0, 4, 0);
        title.Children.Add(source);
        title.Children.Add(SkillVisuals.Badge(draft.Scope == SkillScope.Project ? "Project" : "Everywhere"));
        body.Children.Add(title);
        body.Children.Add(Ui.Secondary(draft.Description));
        if (draft.Note is { } note) body.Children.Add(Ui.Text(note, 11, brushKey: "TextFillColorTertiaryBrush"));

        var buttons = Ui.Buttons(
            Ui.Button("Review", () => Edit(SkillEditorWindow.ForDraft(_model, draft))),
            Ui.Button("Approve", () => Approve(draft, ConflictPolicy.Fail), accent: true));
        var reject = new Button { Content = Icons.Close, ToolTip = "Reject and delete this draft", Margin = new Thickness(6, 0, 0, 0) };
        reject.SetResourceReference(StyleProperty, "IconButton");
        reject.Click += (_, _) =>
        {
            if (!Dialog.Confirm($"Reject “{draft.Name}”?", "The draft is deleted.", "Reject", destructive: true)) return;
            try
            {
                SkillDrafts.Reject(draft);
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
            {
                Dialog.Info("Couldn't reject it", ex.Message);
            }
            _model.Host.RefreshDrafts();
        };
        buttons.Children.Add(reject);
        buttons.VerticalAlignment = VerticalAlignment.Top;

        var icon = Ui.Glyph(Icons.Lightbulb, 16, "SystemFillColorCautionBrush");
        icon.Margin = new Thickness(0, 2, 12, 0);
        icon.VerticalAlignment = VerticalAlignment.Top;
        var row = new DockPanel();
        DockPanel.SetDock(icon, Dock.Left);
        DockPanel.SetDock(buttons, Dock.Right);
        row.Children.Add(icon);
        row.Children.Add(buttons);
        row.Children.Add(body);
        var card = Ui.Card(row);
        card.SetResourceReference(Border.BorderBrushProperty, "SystemFillColorCautionBrush");
        return card;
    }

    private void Approve(SkillDraft draft, ConflictPolicy policy)
    {
        try
        {
            SkillDrafts.Approve(draft, _model.Project, policy, _model.Host.SkillLocations);
            _model.Host.RefreshDrafts();
            _model.Host.RefreshProjectContext();
        }
        catch (SkillException error) when (error.Kind == SkillErrorKind.Exists)
        {
            var choice = Dialog.Ask($"A skill named “{draft.Name}” already exists.", "Replace it, or keep both?",
                new Dialog.Choice("Replace It", IsDestructive: true), new Dialog.Choice("Keep Both", IsDefault: true), new Dialog.Choice("Cancel", IsCancel: true));
            if (choice == 0) Approve(draft, ConflictPolicy.Replace);
            else if (choice == 1) Approve(draft, ConflictPolicy.Rename);
        }
        catch (Exception error) when (error is SkillException or System.IO.IOException or UnauthorizedAccessException)
        {
            Dialog.Info("Couldn't approve it", error.Message);
        }
    }

    private void Adopt(Skill skill, SkillScope scope)
    {
        try
        {
            SkillManager.Adopt(skill, scope, _model.Project, ConflictPolicy.Rename, _model.Host.SkillLocations);
            _model.Host.RefreshDrafts();
            _model.Host.RefreshProjectContext();
            Dialog.Info("Copied", $"“{skill.Name}” is now one of your DSH skills. It overrides the original, and you can edit it.");
        }
        catch (Exception error) when (error is SkillException or System.IO.IOException or UnauthorizedAccessException)
        {
            Dialog.Info("Couldn't copy it", error.Message);
        }
    }

    private void Trash(Skill skill)
    {
        var message = skill.IsOwned
            ? "You can restore it from the Recycle Bin."
            : $"This deletes the file from {skill.Origin.Label()}'s folder; you can restore it from the Recycle Bin.";
        if (!Dialog.Confirm($"Move “{skill.Name}” to the Recycle Bin?", message, "Move to Recycle Bin", destructive: true)) return;
        try
        {
            SkillManager.Trash(skill);
        }
        catch (Exception error) when (error is SkillException or System.IO.IOException or UnauthorizedAccessException)
        {
            Dialog.Info("Couldn't remove it", error.Message);
        }
        _model.Host.RefreshDrafts();
        _model.Host.RefreshProjectContext();
    }

    private void Edit(Window window)
    {
        window.Owner = Ui.Owner(this);
        window.ShowDialog();
        _model.Host.RefreshDrafts();
        Reload();
    }

    private void NewManual() => Edit(SkillEditorWindow.ForNew(_model, _model.Project is null ? SkillScope.User : SkillScope.Project));
    private void Generate() => Edit(new GenerateSkillWindow(_model));
    private void Import() => Edit(new ImportSkillsWindow(_model));

    private void ImportExternal()
    {
        Edit(new ExternalImportWindow(_model));
        _model.Config.ExternalImportOffered = true;
        _ = OfferBringInAsync();
    }

    private void Export(IReadOnlyCollection<string>? preselected)
    {
        var skills = _skills.Where(s => !s.Shadowed).ToList();
        if (skills.Count == 0)
        {
            Dialog.Info("Nothing to export", "There are no skills yet.");
            return;
        }
        Edit(new ExportSkillsWindow(skills, preselected ?? skills.Where(s => s.IsOwned).Select(s => s.Id).ToList()));
    }
}
