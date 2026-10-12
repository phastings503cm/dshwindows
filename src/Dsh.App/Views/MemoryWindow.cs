using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Dsh.App.Infrastructure;
using Dsh.App.Model;
using Dsh.App.Views.Dialogs;
using Dsh.App.Views.Import;
using Dsh.App.Views.Skills;
using Dsh.Core;
using ICSharpCode.AvalonEdit;

namespace Dsh.App.Views;

/// <summary>Project memory and skills. Both are just files in the project, so they diff and review
/// like anything else in the repo. This app assembles the system prompt itself, so what the System
/// prompt tab shows is literally what the model is told. In a chat without a project (or with no project
/// open) it shows what applies to every chat: your own instruction files and skills.</summary>
public sealed class MemoryWindow : Window
{
    private readonly AppModel _model;
    /// <summary>The project shown: the open chat's, else the open folder; null = your own, for every chat.</summary>
    private readonly string? _project;
    private readonly ContentControl _page = new() { Focusable = false };
    private readonly RadioButton _instructions = new() { Content = "Instructions", GroupName = "memory", IsChecked = true };
    private readonly RadioButton _skills = new() { Content = "Skills", GroupName = "memory" };
    private readonly RadioButton _prompt = new() { Content = "System prompt", GroupName = "memory" };

    public MemoryWindow(AppModel model)
    {
        _model = model;
        Title = "Memory & Skills";
        Width = 900;
        Height = 680;
        MinWidth = 640;
        MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "SolidBackgroundFillColorBaseBrush");

        _project = model.Host.Selected is { } chat ? chat.WorkspacePath : model.Project;
        var header = new DockPanel { Margin = new Thickness(20, 14, 20, 10) };
        var project = Ui.Secondary(_project is { } folder ? System.IO.Path.GetFileName(folder.TrimEnd('\\', '/')) : "Yours — for every chat");
        project.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(project, Dock.Right);
        header.Children.Add(project);
        header.Children.Add(Ui.Subtitle("Memory & Skills"));

        var tabs = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var tab in new[] { _instructions, _skills, _prompt })
        {
            tab.SetResourceReference(StyleProperty, "Segment");
            tab.Checked += (_, _) => ShowTab();
            tabs.Children.Add(tab);
        }
        var tabHost = new Border { Child = tabs, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(20, 0, 20, 10) };
        tabHost.SetResourceReference(StyleProperty, "SegmentHost");

        var footer = new DockPanel { Margin = new Thickness(20, 10, 20, 14) };
        var done = Ui.Button("Done", Close, accent: true);
        done.IsDefault = true;
        DockPanel.SetDock(done, Dock.Right);
        footer.Children.Add(done);
        footer.Children.Add(Ui.Button("Reload", () =>
        {
            _model.Host.RefreshProjectContext();
            ShowTab();
        }));
        ((FrameworkElement)footer.Children[1]).HorizontalAlignment = HorizontalAlignment.Left;

        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(tabHost, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(header);
        root.Children.Add(tabHost);
        root.Children.Add(footer);
        root.Children.Add(_page);
        Content = root;

        ShowTab();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close();
                e.Handled = true;
            }
        };
    }

    private void ShowTab()
    {
        if (_skills.IsChecked == true) _page.Content = new SkillsManagerPage(_model, null);
        else if (_prompt.IsChecked == true) _page.Content = PromptPreview();
        else _page.Content = Instructions(null);
    }

    /// <summary>The instruction files in force here: the project's (with your own), or — no project — your own.</summary>
    private IReadOnlyList<InstructionFile> Files() =>
        _project is null
            ? ProjectContext.UserInstructionFiles(_model.Host.SkillLocations)
            : _model.Host.ProjectContext is { } context && string.Equals(context.Root, _project, StringComparison.OrdinalIgnoreCase)
                ? context.Instructions
                : ProjectContext.Load(_project, _model.Host.SkillLocations, _model.Config.SkillSources).Instructions;

    private UIElement Instructions(InstructionFile? editing)
    {
        var files = Files();
        if (editing is not null) return Editor(editing);
        var list = new StackPanel();
        if (files.Count == 0 && _project is null)
        {
            list.Children.Add(Ui.Card(Ui.Stack(
                Ui.Text("No instructions of your own yet", 14, FontWeights.SemiBold),
                Ui.Secondary("Your own instruction files (any .md in DSH's instructions folder) are loaded into every chat — with a project or without. Write down how you like things done, or bring your instructions over from Claude Code and Cursor."),
                new Border { Height = 10 },
                Ui.Buttons(Ui.Button("Write My Instructions", NewUserInstructions, accent: true), Ui.Button("From Claude Code & Cursor…", BringIn)))));
        }
        else if (files.Count == 0)
        {
            list.Children.Add(Ui.Card(Ui.Stack(
                Ui.Text("No instruction files", 14, FontWeights.SemiBold),
                Ui.Secondary("A file named AGENTS.md, QWEN.md, CLAUDE.md, DSH.md, or MEMORY.md in the project root is loaded into every prompt. Create the memory scaffold to get started, or bring your instructions and notes over from Claude Code and Cursor."),
                new Border { Height = 10 },
                Ui.Buttons(Ui.Button("Set Up Memory", SetUpMemory, accent: true), Ui.Button("From Claude Code & Cursor…", BringIn)))));
        }
        foreach (var file in files)
        {
            var open = Ui.Button("Edit", () => _page.Content = Instructions(file));
            var source = file.Scope switch
            {
                InstructionScope.User => "Your instructions, used in every project · ",
                InstructionScope.Notes => "Notes saved for this project (an index; the model reads the notes it lists) · ",
                _ => "",
            };
            var detail = source + $"{Formatting.Plural(file.LineCount, "line")}" + (file.LineCount > 100 ? " — long: this is loaded on every request; move detail into a skill" : "");
            var row = Ui.Row(file.Label, detail, open, file.LineCount > 100 ? Icons.Warning : Icons.Document);
            row.MouseLeftButtonDown += (_, e) =>
            {
                if (e.ClickCount == 2) _page.Content = Instructions(file);
            };
            list.Children.Add(row);
        }
        if (files.Count > 0 && _project is not null)
        {
            var scaffold = Ui.LinkButton("Set Up Memory Scaffold", SetUpMemory);
            scaffold.HorizontalAlignment = HorizontalAlignment.Left;
            list.Children.Add(scaffold);
            var bring = Ui.LinkButton("Bring in from Claude Code & Cursor…", BringIn);
            bring.HorizontalAlignment = HorizontalAlignment.Left;
            list.Children.Add(bring);
        }
        return Ui.Scroll(list, new Thickness(20, 4, 20, 8));
    }

    private UIElement Editor(InstructionFile file)
    {
        var editor = new TextEditor
        {
            Text = file.Text,
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
        var save = Ui.Button("Save", () =>
        {
            try
            {
                var format = FileText.Read(file.Path)?.Format ?? TextFileFormat.Default;
                FileText.Write(file.Path, editor.Text, format);
                _model.Host.RefreshProjectContext();
                _page.Content = Instructions(null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Dialog.Info("Couldn't save", ex.Message);
            }
        }, accent: true);
        var back = Ui.LinkButton("‹ All files", () => _page.Content = Instructions(null));
        var bar = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(back, Dock.Left);
        DockPanel.SetDock(save, Dock.Right);
        bar.Children.Add(back);
        bar.Children.Add(save);
        var label = Ui.Text(file.Label, 13, FontWeights.SemiBold, wrap: false);
        label.HorizontalAlignment = HorizontalAlignment.Center;
        label.VerticalAlignment = VerticalAlignment.Center;
        bar.Children.Add(label);
        var root = new DockPanel { Margin = new Thickness(20, 0, 20, 0) };
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);
        root.Children.Add(editor);
        Loaded += (_, _) => editor.Focus();
        return root;
    }

    /// <summary>Open the import over this window, then show what it added.</summary>
    private void BringIn()
    {
        new ExternalImportWindow(_model) { Owner = this }.ShowDialog();
        _model.Config.ExternalImportOffered = true;
        _model.Host.RefreshProjectContext();
        ShowTab();
    }

    /// <summary>Start your own instruction file (instructions.md in your instructions folder) and open it to edit.</summary>
    private void NewUserInstructions()
    {
        try
        {
            var folder = Directory.CreateDirectory(_model.Host.SkillLocations.UserInstructions).FullName;
            var path = Path.Combine(folder, "instructions.md");
            if (!File.Exists(path))
                File.WriteAllText(path, "# My instructions\n\nHow I like things done, in every chat:\n\n- \n", TextUtil.Utf8NoBom);
            _model.Host.RefreshProjectContext();
            var file = Files().FirstOrDefault(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase));
            _page.Content = Instructions(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Dialog.Info("Couldn't create the file", ex.Message);
        }
    }

    private void SetUpMemory()
    {
        if (_project is not { } project) return;
        try
        {
            ProjectContext.SetUpMemory(project);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Dialog.Info("Couldn't create the memory files", ex.Message);
        }
        _model.Host.RefreshProjectContext();
        _page.Content = Instructions(null);
    }

    private UIElement PromptPreview()
    {
        var text = Ui.Field(PromptText(), mono: true);
        text.IsReadOnly = true;
        text.TextWrapping = TextWrapping.Wrap;
        text.AcceptsReturn = true;
        text.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        text.VerticalContentAlignment = VerticalAlignment.Top;
        text.FontSize = 12;
        return new Border { Child = text, Padding = new Thickness(20, 0, 20, 0) };
    }

    private string PromptText()
    {
        var model = _model.Config.ActiveProvider?.Model ?? "model";
        if (_project is not { } project)
        {
            // A chat without a project: the regular-conversation prompt, your own instructions, the chat's scratch folder.
            var scratch = _model.Host.Selected is { IsPlain: true } chat ? chat.WorkingFolder : PlainChat.Folder("<chat id>");
            var plainEnvironment = ProjectContext.EnvironmentBlock(scratch, model, _model.Config.AsPreset, _model.Config.ResolvedAgentShell, scratch: true);
            return PlainChat.SystemPrompt("Windows", scratch) + "\n\n" +
                   ProjectContext.UserOnly(_model.Host.SkillLocations).PromptSupplement(plainEnvironment, includeSkills: false);
        }
        var environment = ProjectContext.EnvironmentBlock(project, model, _model.Config.AsPreset, _model.Config.ResolvedAgentShell);
        var context = _model.Host.ProjectContext is { } open && string.Equals(open.Root, project, StringComparison.OrdinalIgnoreCase)
            ? open
            : ProjectContext.Load(project, _model.Host.SkillLocations, _model.Config.SkillSources);
        return AgentHost.DefaultSystemPrompt + "\n\n" + context.PromptSupplement(environment);
    }
}
