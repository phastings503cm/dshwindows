using System.Windows;
using System.Windows.Controls;
using Dsh.App.Infrastructure;
using Dsh.App.Model;
using Dsh.App.Views.Dialogs;
using Dsh.Core;

namespace Dsh.App.Views.Settings;

/// <summary>Appearance, permissions for new chats, the agent's shell, updates, and setup.</summary>
public sealed class GeneralPage : UserControl
{
    private readonly AppModel _model;

    public GeneralPage(AppModel model)
    {
        _model = model;
        var config = model.Config;
        var page = new StackPanel { MaxWidth = 760 };
        page.Children.Add(Ui.Title("General"));

        // Appearance
        page.Children.Add(Ui.Section("Appearance"));
        var theme = new ComboBox { MinWidth = 170 };
        foreach (var (value, label) in new[] { ("system", "Use Windows setting"), ("light", "Light"), ("dark", "Dark") })
            theme.Items.Add(new ComboBoxItem { Content = label, Tag = value, IsSelected = config.Theme == value });
        theme.SelectionChanged += (_, _) =>
        {
            if (theme.SelectedItem is ComboBoxItem { Tag: string value })
            {
                config.Theme = value;
                ThemeService.Instance.Apply(value);
            }
        };
        page.Children.Add(Ui.Row("Theme", "Light, dark, or follow Windows. The accent colour always follows Windows.", theme, Icons.Lightbulb));

        // Permissions
        page.Children.Add(Ui.Section("Permissions for new chats"));
        var presets = new StackPanel();
        foreach (var preset in PermissionPresets.All)
        {
            var radio = new RadioButton
            {
                GroupName = "preset",
                IsChecked = config.AsPreset == preset,
                Margin = new Thickness(0, 4, 0, 8),
                Content = Ui.Stack(Ui.Text(preset.Label(), 13.5, FontWeights.SemiBold), Ui.Secondary(preset.Detail())),
            };
            radio.Checked += (_, _) => Choose(preset, radio);
            presets.Children.Add(radio);
        }
        presets.Children.Add(Ui.Secondary("Each chat keeps the preset it was created with. /plan switches a chat into plan mode."));
        page.Children.Add(Ui.Card(presets));

        // Agent shell
        page.Children.Add(Ui.Section("Agent shell"));
        var shell = new ComboBox { MinWidth = 260 };
        var options = new (string Value, string Label)[]
        {
            (AgentShell.Auto, "Automatic (PowerShell 7 if installed)"),
            (AgentShell.PowerShellPreference, "PowerShell 7 (pwsh)"),
            (AgentShell.WindowsPowerShellPreference, "Windows PowerShell 5.1"),
            (AgentShell.CmdPreference, "Command Prompt (cmd.exe)"),
            (AgentShell.BashPreference, "Git Bash"),
        };
        var known = options.Any(o => string.Equals(o.Value, config.AgentShell, StringComparison.OrdinalIgnoreCase));
        foreach (var (value, label) in options)
            shell.Items.Add(new ComboBoxItem { Content = label, Tag = value, IsSelected = string.Equals(value, config.AgentShell, StringComparison.OrdinalIgnoreCase) });
        var custom = new ComboBoxItem { Content = "Custom executable\u2026", Tag = "custom", IsSelected = !known };
        shell.Items.Add(custom);
        var customPath = Ui.Field(known ? "" : config.AgentShell, @"C:\Program Files\PowerShell\7\pwsh.exe", mono: true);
        customPath.Margin = new Thickness(0, 8, 0, 0);
        customPath.Visibility = known ? Visibility.Collapsed : Visibility.Visible;
        var resolved = Ui.Secondary("");
        resolved.Margin = new Thickness(0, 6, 0, 0);
        void Describe()
        {
            var current = config.ResolvedAgentShell;
            resolved.Text = $"Commands run in {current.DisplayName} ({current.Executable}).";
        }
        shell.SelectionChanged += (_, _) =>
        {
            if (shell.SelectedItem is not ComboBoxItem { Tag: string value }) return;
            customPath.Visibility = value == "custom" ? Visibility.Visible : Visibility.Collapsed;
            if (value != "custom") config.AgentShell = value;
            else if (customPath.Text.Trim().Length > 0) config.AgentShell = customPath.Text.Trim();
            Describe();
        };
        customPath.LostFocus += (_, _) =>
        {
            if (customPath.Text.Trim().Length > 0) config.AgentShell = customPath.Text.Trim();
            Describe();
        };
        Describe();
        page.Children.Add(Ui.Card(Ui.Stack(
            DockRow(Ui.Stack(Ui.Text("Shell for the agent's commands", 13.5),
                             Ui.Secondary("What run_shell_command uses. The agent is told which shell it is so it writes commands for it.")), shell),
            customPath, resolved)));

        // Windows integration
        page.Children.Add(Ui.Section("Windows"));
        page.Children.Add(Ui.Row("\u201COpen with DSH\u201D in Explorer",
            "Adds DSH to the right-click menu of folders, so any folder opens as a project.",
            Ui.Toggle(ShellIntegration.IsFolderMenuInstalled, on =>
            {
                if (!ShellIntegration.SetFolderMenu(on)) Dialog.Info("Couldn't change the Explorer menu", "Windows refused the registry change.");
            }), Icons.Folder));
        page.Children.Add(Ui.Row("Check for updates", "Look for a newer release on startup. Nothing downloads without you clicking through.",
            Ui.Toggle(config.CheckForUpdates, on => config.CheckForUpdates = on), Icons.Download));

        // Setup
        page.Children.Add(Ui.Section("Setup"));
        page.Children.Add(Ui.Row("Configuration wizard", "Pick a model server, test it, and set permissions again.",
            Ui.Button("Run Again\u2026", model.ShowWizard), Icons.Play));
        page.Children.Add(Ui.Row("Project", model.Project ?? "No folder open",
            Ui.Button("Change\u2026", () =>
            {
                model.ChooseProject();
                Content = new GeneralPage(model).Content;
            }), Icons.OpenFolder));
        page.Children.Add(Ui.Row("Data folder", $"{AppPaths.Root} \u2014 settings, conversations, skills, plugins and logs.",
            Ui.Button("Open", () => ShellIntegration.OpenFolder(AppPaths.Root)), Icons.OpenInWindow));

        Content = Ui.Scroll(page);
    }

    private static DockPanel DockRow(UIElement left, FrameworkElement right)
    {
        var dock = new DockPanel();
        right.VerticalAlignment = VerticalAlignment.Center;
        right.Margin = new Thickness(16, 0, 0, 0);
        DockPanel.SetDock(right, Dock.Right);
        dock.Children.Add(right);
        dock.Children.Add(left);
        return dock;
    }

    private void Choose(PermissionPreset preset, RadioButton radio)
    {
        if (preset == _model.Config.AsPreset) return;
        if (preset == PermissionPreset.FullAccess && !Dialog.Confirm("Turn on full access?",
                "New chats will write files and run shell commands without asking, anywhere your user account can reach. " +
                "Existing chats keep the preset they were created with.", "Enable Full Access", destructive: true))
        {
            Dispatcher.BeginInvoke(() => Content = new GeneralPage(_model).Content);
            return;
        }
        _model.Config.Preset = preset.RawValue();
    }
}
