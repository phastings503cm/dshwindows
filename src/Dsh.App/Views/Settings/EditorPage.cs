using System.Windows;
using System.Windows.Controls;
using Dsh.App.Infrastructure;
using Dsh.App.Model;
using Dsh.Core;

namespace Dsh.App.Views.Settings;

/// <summary>Editor and terminal preferences.</summary>
public sealed class EditorPage : UserControl
{
    public EditorPage(AppModel model)
    {
        var config = model.Config;
        var page = new StackPanel { MaxWidth = 760 };
        page.Children.Add(Ui.Title("Editor & Terminal"));

        page.Children.Add(Ui.Section("Editor"));
        page.Children.Add(Ui.Row("Font size", "Ctrl+mouse wheel in the editor changes it too.",
            SizeSlider(config.EditorFontSize, v => config.EditorFontSize = v), Icons.Edit));
        page.Children.Add(Ui.Row("Wrap long lines", null, Ui.Toggle(config.EditorWraps, on => config.EditorWraps = on)));
        page.Children.Add(Ui.Row("Show line numbers", null, Ui.Toggle(config.EditorLineNumbers, on => config.EditorLineNumbers = on)));
        page.Children.Add(Ui.Card(Ui.Secondary(
            "Files keep their line endings (CRLF or LF) and byte-order mark when saved, whether you or the agent edits them. " +
            "Ctrl+F finds, Ctrl+H replaces, Alt+drag selects a rectangle.")));

        page.Children.Add(Ui.Section("Terminal"));
        page.Children.Add(Ui.Row("Font size", null, SizeSlider(config.TerminalFontSize, v => config.TerminalFontSize = v), Icons.Terminal));

        var shell = new ComboBox { MinWidth = 240 };
        var options = new (string Value, string Label)[]
        {
            ("", "PowerShell (default)"),
            (AgentShell.PowerShellPreference, "PowerShell 7 (pwsh)"),
            (AgentShell.WindowsPowerShellPreference, "Windows PowerShell 5.1"),
            (AgentShell.CmdPreference, "Command Prompt"),
            (AgentShell.BashPreference, "Git Bash"),
        };
        var known = options.Any(o => string.Equals(o.Value, config.TerminalShell, StringComparison.OrdinalIgnoreCase));
        foreach (var (value, label) in options)
            shell.Items.Add(new ComboBoxItem { Content = label, Tag = value, IsSelected = string.Equals(value, config.TerminalShell, StringComparison.OrdinalIgnoreCase) });
        shell.Items.Add(new ComboBoxItem { Content = "Custom command line\u2026", Tag = "custom", IsSelected = !known });
        var custom = Ui.Field(known ? "" : config.TerminalShell, "wsl.exe ~   or   \"C:\\msys64\\usr\\bin\\bash.exe\" --login", mono: true);
        custom.Margin = new Thickness(0, 8, 0, 0);
        custom.Visibility = known ? Visibility.Collapsed : Visibility.Visible;
        var resolved = Ui.Secondary("");
        resolved.Margin = new Thickness(0, 6, 0, 0);
        void Describe() => resolved.Text = "New terminals run: " + AgentShell.DefaultTerminalCommandLine(config.TerminalShell);
        shell.SelectionChanged += (_, _) =>
        {
            if (shell.SelectedItem is not ComboBoxItem { Tag: string value }) return;
            custom.Visibility = value == "custom" ? Visibility.Visible : Visibility.Collapsed;
            if (value != "custom") config.TerminalShell = value;
            else if (custom.Text.Trim().Length > 0) config.TerminalShell = custom.Text.Trim();
            Describe();
        };
        custom.LostFocus += (_, _) =>
        {
            if (custom.Text.Trim().Length > 0) config.TerminalShell = custom.Text.Trim();
            Describe();
        };
        Describe();
        var shellRow = new DockPanel();
        DockPanel.SetDock(shell, Dock.Right);
        shell.Margin = new Thickness(16, 0, 0, 0);
        shell.VerticalAlignment = VerticalAlignment.Center;
        shellRow.Children.Add(shell);
        shellRow.Children.Add(Ui.Stack(Ui.Text("Shell", 13.5), Ui.Secondary("New terminals pick this up; open ones keep their shell.")));
        page.Children.Add(Ui.Card(Ui.Stack(shellRow, custom, resolved)));
        page.Children.Add(Ui.Card(Ui.Secondary(
            "Ctrl+Shift+C / Ctrl+Shift+V copy and paste (Ctrl+C copies when text is selected, otherwise it interrupts). " +
            "Shift+PageUp/PageDown scroll the history.")));

        Content = Ui.Scroll(page);
    }

    private static FrameworkElement SizeSlider(double value, Action<double> changed)
    {
        var label = Ui.Text($"{value:0} pt", 12, wrap: false);
        label.Width = 44;
        label.VerticalAlignment = VerticalAlignment.Center;
        var slider = new Slider
        {
            Minimum = 9,
            Maximum = 24,
            Value = value,
            Width = 180,
            IsSnapToTickEnabled = true,
            TickFrequency = 1,
            VerticalAlignment = VerticalAlignment.Center,
        };
        slider.ValueChanged += (_, e) =>
        {
            label.Text = $"{e.NewValue:0} pt";
            changed(e.NewValue);
        };
        return Ui.Stack(Orientation.Horizontal, slider, label);
    }
}
