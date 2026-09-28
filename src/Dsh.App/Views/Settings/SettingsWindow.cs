using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Dsh.App.Infrastructure;
using Dsh.App.Model;
using Dsh.App.Views.Skills;

namespace Dsh.App.Views.Settings;

/// <summary>Settings: model routes, permissions, the Spark, skills, plugins, editor and terminal.
/// The wizard is the guided path; this is the direct one. Laid out like Windows 11 Settings: a
/// navigation list on the left, the page on the right.</summary>
public sealed class SettingsWindow : Window
{
    private readonly AppModel _model;
    private readonly ListBox _nav = new();
    private readonly ContentControl _page = new() { Focusable = false };
    private SkillsAction? _pendingAction;

    private sealed record NavItem(SettingsTab Tab, string Title, string Glyph);

    private static readonly NavItem[] Items =
    [
        new(SettingsTab.General, "General", Icons.Settings),
        new(SettingsTab.Models, "Models", Icons.Robot),
        new(SettingsTab.Spark, "DGX Spark", Icons.Bolt),
        new(SettingsTab.Skills, "Skills", Icons.Education),
        new(SettingsTab.Plugins, "Plugins", Icons.Puzzle),
        new(SettingsTab.Editor, "Editor & Terminal", Icons.Code),
    ];

    public SettingsWindow(AppModel model, SettingsTab tab, SkillsAction? action)
    {
        _model = model;
        Title = "Settings";
        Width = 980;
        Height = 700;
        MinWidth = 760;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "SolidBackgroundFillColorBaseBrush");

        _nav.SetResourceReference(ItemsControl.ItemContainerStyleProperty, "NavListItem");
        _nav.BorderThickness = new Thickness(0);
        _nav.Background = System.Windows.Media.Brushes.Transparent;
        foreach (var item in Items)
        {
            var row = Ui.Stack(Orientation.Horizontal, Ui.Glyph(item.Glyph, 15), Ui.Text(item.Title, 13.5, wrap: false));
            ((FrameworkElement)row.Children[0]).Margin = new Thickness(4, 0, 12, 0);
            _nav.Items.Add(new ListBoxItem { Content = row, Tag = item.Tab, Padding = new Thickness(8, 8, 8, 8) });
        }
        _nav.SelectionChanged += (_, _) =>
        {
            if (_nav.SelectedItem is ListBoxItem { Tag: SettingsTab selected }) Show(selected);
        };

        var navHost = new DockPanel { Width = 230 };
        navHost.SetResourceReference(Panel.BackgroundProperty, "LayerFillColorDefaultBrush");
        var heading = Ui.Title("Settings");
        heading.Margin = new Thickness(18, 18, 12, 14);
        DockPanel.SetDock(heading, Dock.Top);
        navHost.Children.Add(heading);
        var footer = Ui.Secondary($"DSH {Dsh.Core.AppInfo.Version}", 11);
        footer.Margin = new Thickness(18, 8, 12, 14);
        DockPanel.SetDock(footer, Dock.Bottom);
        navHost.Children.Add(footer);
        navHost.Children.Add(_nav);

        var root = new DockPanel();
        DockPanel.SetDock(navHost, Dock.Left);
        root.Children.Add(navHost);
        root.Children.Add(_page);
        Content = root;

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && Keyboard.FocusedElement is not TextBox)
            {
                Close();
                e.Handled = true;
            }
        };
        Navigate(tab, action);
    }

    public void Navigate(SettingsTab tab, SkillsAction? action)
    {
        _pendingAction = action;
        var index = Array.FindIndex(Items, i => i.Tab == tab);
        if (_nav.SelectedIndex == index) Show(tab);
        else _nav.SelectedIndex = Math.Max(0, index);
    }

    private void Show(SettingsTab tab)
    {
        _page.Content = tab switch
        {
            SettingsTab.Models => new ModelsPage(_model),
            SettingsTab.Spark => new SparkPage(_model),
            SettingsTab.Skills => new SkillsManagerPage(_model, TakeAction()),
            SettingsTab.Plugins => new PluginsPage(_model),
            SettingsTab.Editor => new EditorPage(_model),
            _ => new GeneralPage(_model),
        };
    }

    private SkillsAction? TakeAction()
    {
        var action = _pendingAction;
        _pendingAction = null;
        return action;
    }
}
