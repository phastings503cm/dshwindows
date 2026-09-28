using System.Windows;
using System.Windows.Controls;
using Dsh.App.Infrastructure;
using Dsh.App.Model;
using Dsh.App.Views.Dialogs;
using Dsh.Core;

namespace Dsh.App.Views.Settings;

/// <summary>The plugin catalog: JSON manifests declaring tools backed by shell commands.</summary>
public sealed class PluginsPage : UserControl
{
    private readonly AppModel _model;
    private readonly StackPanel _list = new();

    public PluginsPage(AppModel model)
    {
        _model = model;
        var page = new StackPanel { MaxWidth = 760 };
        page.Children.Add(Ui.Title("Plugins"));
        var intro = Ui.Secondary(
            "A plugin is a JSON manifest declaring tools backed by shell commands. Put one in your plugins folder " +
            $"({PluginLoader.UserDirectory}) or in a project's .dsh\\plugins folder, then reload. A plugin runs shell commands, so its " +
            "tools ask before running unless its manifest says otherwise.");
        intro.Margin = new Thickness(0, 6, 0, 10);
        page.Children.Add(intro);
        page.Children.Add(Ui.Buttons(
            Ui.Button("Open Plugins Folder", () => ShellIntegration.OpenFolder(PluginLoader.UserDirectory)),
            Ui.Button("Add Example", () =>
            {
                try
                {
                    PluginLoader.InstallExample();
                    model.Host.RefreshProjectContext();
                    Rebuild();
                }
                catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
                {
                    Dialog.Info("Couldn't add the example", ex.Message);
                }
            }),
            Ui.Button("Reload", () =>
            {
                model.Host.RefreshProjectContext();
                Rebuild();
            })));
        page.Children.Add(_list);
        Content = Ui.Scroll(page);
        Rebuild();
    }

    private void Rebuild()
    {
        _list.Children.Clear();
        _list.Children.Add(Ui.Section("Loaded"));
        var plugins = _model.Host.Plugins;
        if (plugins.Count == 0) _list.Children.Add(Ui.Card(Ui.Secondary("No plugins loaded.")));
        foreach (var plugin in plugins)
        {
            var body = new StackPanel();
            var header = Ui.Stack(Orientation.Horizontal, Ui.Glyph(Icons.Puzzle, 14, "AccentTextFillColorPrimaryBrush"),
                Ui.Text(plugin.Name, 13.5, FontWeights.SemiBold, wrap: false),
                plugin.Version is { } version ? Ui.Secondary($"  v{version}") : null,
                Ui.Secondary($"  · {Formatting.Plural(plugin.Tools.Count, "tool")}"));
            ((FrameworkElement)header.Children[0]).Margin = new Thickness(0, 0, 8, 0);
            body.Children.Add(header);
            if (plugin.Description is { } description)
            {
                var text = Ui.Secondary(description);
                text.Margin = new Thickness(0, 4, 0, 4);
                body.Children.Add(text);
            }
            foreach (var tool in plugin.Tools)
            {
                var name = Ui.Text(tool.Name, 12, wrap: false);
                name.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
                var line = Ui.Stack(Orientation.Horizontal, name, Ui.Secondary("  " + tool.Description, 11.5));
                line.Margin = new Thickness(22, 2, 0, 0);
                body.Children.Add(line);
            }
            _list.Children.Add(Ui.Card(body));
        }
        if (_model.Host.PluginErrors.Count > 0)
        {
            _list.Children.Add(Ui.Section("Could not load"));
            foreach (var error in _model.Host.PluginErrors)
                _list.Children.Add(Ui.Card(Ui.Status(error, "SystemFillColorCriticalBrush")));
        }
    }
}
