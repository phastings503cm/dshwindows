using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Dsh.App.Infrastructure;
using Dsh.App.Model;
using Dsh.Core;

namespace Dsh.App.Views.Skills;

/// <summary>The composer's Skills popup: pick, by hand, the skills this chat uses (their
/// instructions go into the prompt), and choose whether the model may also pick skills itself.</summary>
public sealed class SkillPickerView : UserControl
{
    private readonly AppModel _model;
    private readonly SessionVM _session;
    private readonly Action _close;
    private readonly TextBox _search = new() { Margin = new Thickness(10, 10, 10, 8), Padding = new Thickness(8, 5, 8, 5) };
    private readonly StackPanel _list = new();
    private readonly CheckBox _auto = new() { Margin = new Thickness(12, 4, 12, 8) };
    private readonly Button _drafts = new() { HorizontalContentAlignment = HorizontalAlignment.Left, Margin = new Thickness(6, 0, 6, 4) };
    private readonly Button _clear = new() { Content = "Clear selection", Margin = new Thickness(0, 0, 10, 0), Padding = new Thickness(6, 2, 6, 2) };
    private IReadOnlyList<Skill> _skills = [];

    public SkillPickerView(AppModel model, SessionVM session, Action close)
    {
        _model = model;
        _session = session;
        _close = close;
        Width = 440;
        Height = 500;

        Placeholder.SetText(_search, "Search skills");
        _search.TextChanged += (_, _) => Rebuild();
        _auto.Click += (_, _) => _model.Host.SetAutoSkills(_auto.IsChecked == true, _session);
        _drafts.SetResourceReference(StyleProperty, "SubtleButton");
        _drafts.Click += (_, _) => OpenSettings(null);
        _clear.SetResourceReference(StyleProperty, "SubtleButton");
        _clear.Click += (_, _) =>
        {
            _model.Host.ClearPinnedSkills(_session);
            Rebuild();
        };

        var root = new DockPanel();
        var top = new DockPanel();
        DockPanel.SetDock(_clear, Dock.Right);
        top.Children.Add(_clear);
        top.Children.Add(_search);
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);

        var autoPanel = new StackPanel();
        autoPanel.Children.Add(_auto);
        autoPanel.Children.Add(_drafts);
        autoPanel.Children.Add(Divider());
        DockPanel.SetDock(autoPanel, Dock.Top);
        root.Children.Add(autoPanel);

        var footer = new DockPanel { Margin = new Thickness(10, 8, 10, 10) };
        var manage = new Button { Content = "Manage…", Padding = new Thickness(12, 4, 12, 4) };
        manage.Click += (_, _) => OpenSettings(null);
        var generate = new Button { Content = "Write one with AI…", Padding = new Thickness(12, 4, 12, 4) };
        generate.SetResourceReference(StyleProperty, "AccentButtonStyle");
        generate.Click += (_, _) => OpenSettings(SkillsAction.Generate);
        DockPanel.SetDock(generate, Dock.Right);
        footer.Children.Add(generate);
        footer.Children.Add(manage);
        manage.HorizontalAlignment = HorizontalAlignment.Left;
        var footerHost = new StackPanel();
        footerHost.Children.Add(Divider());
        footerHost.Children.Add(footer);
        DockPanel.SetDock(footerHost, Dock.Bottom);
        root.Children.Add(footerHost);

        root.Children.Add(new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        Content = new Border
        {
            Child = root,
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
        };
        ((Border)Content).SetResourceReference(Border.BackgroundProperty, "SolidBackgroundFillColorTertiaryBrush");
        ((Border)Content).SetResourceReference(Border.BorderBrushProperty, "SurfaceStrokeColorFlyoutBrush");

        Loaded += (_, _) =>
        {
            _skills = _model.Host.Skills(_session);
            Rebuild();
            _search.Focus();
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                _close();
                e.Handled = true;
            }
        };
    }

    private static Border Divider()
    {
        var line = new Border { Height = 1 };
        line.SetResourceReference(Border.BackgroundProperty, "DividerStrokeColorDefaultBrush");
        return line;
    }

    private void OpenSettings(SkillsAction? action)
    {
        _close();
        _model.ShowSettings(SettingsTab.Skills, action);
    }

    private void Rebuild()
    {
        var selection = _model.Host.Selection(_session.Id);
        _auto.IsChecked = selection.Auto;
        _auto.Content = new StackPanel
        {
            Children =
            {
                new TextBlock { Text = "Let the model choose skills" },
                Secondary(selection.Auto
                    ? "It sees each skill's description and loads the ones that fit."
                    : "Off — only the skills ticked below are used."),
            },
        };
        _clear.Visibility = selection.Pinned.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        var drafts = _model.Host.PendingDrafts.Count;
        _drafts.Visibility = drafts > 0 ? Visibility.Visible : Visibility.Collapsed;
        var draftsText = new TextBlock
        {
            Text = $"{drafts} skill{(drafts == 1 ? "" : "s")} awaiting your approval — review",
            FontSize = 12,
        };
        draftsText.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorCautionBrush");
        _drafts.Content = draftsText;

        var query = _search.Text.Trim();
        var visible = _skills.Where(s => !s.Shadowed && (query.Length == 0
            || s.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
            || s.Description.Contains(query, StringComparison.OrdinalIgnoreCase))).ToList();

        _list.Children.Clear();
        if (visible.Count == 0)
        {
            var empty = new StackPanel { Margin = new Thickness(24), HorizontalAlignment = HorizontalAlignment.Center };
            empty.Children.Add(new TextBlock
            {
                Text = Icons.Education,
                FontSize = 28,
                HorizontalAlignment = HorizontalAlignment.Center,
                FontFamily = (System.Windows.Media.FontFamily)FindResource("IconFont"),
            });
            empty.Children.Add(new TextBlock
            {
                Text = _skills.Count == 0 ? "No skills yet" : "Nothing matches",
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 8, 0, 4),
            });
            var hint = Secondary("Create one, or import from Claude or Cursor, in Settings › Skills.");
            hint.TextAlignment = TextAlignment.Center;
            empty.Children.Add(hint);
            _list.Children.Add(empty);
            return;
        }

        foreach (var skill in visible) _list.Children.Add(Row(skill, selection));
    }

    private UIElement Row(Skill skill, SkillSelection selection)
    {
        var pinned = selection.Pinned.Contains(skill.Id);
        var off = selection.Disabled.Contains(skill.Id);
        var check = new CheckBox { IsChecked = pinned, IsEnabled = !off, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 6, 0) };
        var body = new StackPanel();
        var title = new DockPanel();
        title.Children.Add(new TextBlock { Text = skill.Name, FontWeight = FontWeights.SemiBold });
        if (off)
        {
            var badge = SkillVisuals.Badge("Off in Settings", System.Windows.Media.Color.FromRgb(0xCA, 0x50, 0x10));
            badge.Margin = new Thickness(8, 0, 0, 0);
            title.Children.Add(badge);
        }
        body.Children.Add(title);
        var badges = SkillVisuals.Badges(skill);
        badges.Margin = new Thickness(0, 3, 0, 3);
        body.Children.Add(badges);
        var description = Secondary(skill.Description);
        description.MaxHeight = 34;
        description.TextTrimming = TextTrimming.CharacterEllipsis;
        body.Children.Add(description);

        var grid = new DockPanel { Margin = new Thickness(10, 7, 10, 7), Opacity = off ? 0.5 : 1 };
        DockPanel.SetDock(check, Dock.Left);
        grid.Children.Add(check);
        grid.Children.Add(body);

        var button = new Button
        {
            Content = grid,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(0),
            ToolTip = off ? "Turn it on in Settings › Skills to use it."
                : pinned ? "Selected — its instructions are in the prompt." : "Click to use it in this chat.",
        };
        button.SetResourceReference(StyleProperty, "SubtleButton");
        void Toggle()
        {
            if (off) return;
            _model.Host.SetPinned(skill, !_model.Host.IsPinned(skill, _session), _session);
            Rebuild();
        }
        button.Click += (_, _) => Toggle();
        check.Click += (_, e) =>
        {
            Toggle();
            e.Handled = true;
        };
        return button;
    }

    private static TextBlock Secondary(string text)
    {
        var block = new TextBlock { Text = text, FontSize = 11.5, TextWrapping = TextWrapping.Wrap };
        block.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        return block;
    }
}
