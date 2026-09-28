using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Dsh.App.Infrastructure;

namespace Dsh.App.Views;

/// <summary>Small factories for the form-heavy windows (settings, wizard, skills), so they share one
/// look: Windows 11 settings cards — a title and a line of explanation on the left, the control on
/// the right.</summary>
public static class Ui
{
    public static TextBlock Text(string text, double size = 13, FontWeight? weight = null, string? brushKey = null, bool wrap = true)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = size,
            FontWeight = weight ?? FontWeights.Normal,
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
        };
        if (!wrap) block.TextTrimming = TextTrimming.CharacterEllipsis;
        block.SetResourceReference(TextBlock.ForegroundProperty, brushKey ?? "TextFillColorPrimaryBrush");
        return block;
    }

    public static TextBlock Secondary(string text, double size = 12) => Text(text, size, brushKey: "TextFillColorSecondaryBrush");

    public static TextBlock Title(string text) => Text(text, 22, FontWeights.SemiBold);

    public static TextBlock Subtitle(string text) => Text(text, 15, FontWeights.SemiBold);

    /// <summary>A small grey heading above a group of cards.</summary>
    public static TextBlock Section(string text)
    {
        var block = Text(text, 13, FontWeights.SemiBold);
        block.Margin = new Thickness(2, 18, 0, 6);
        return block;
    }

    public static TextBlock Glyph(string glyph, double size = 16, string brushKey = "TextFillColorSecondaryBrush")
    {
        var block = new TextBlock { Text = glyph, FontSize = size, VerticalAlignment = VerticalAlignment.Center };
        block.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        block.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        return block;
    }

    public static Border Card(UIElement content, Thickness? padding = null)
    {
        var card = new Border { Child = content, Margin = new Thickness(0, 0, 0, 4) };
        card.SetResourceReference(FrameworkElement.StyleProperty, "Card");
        if (padding is { } p) card.Padding = p;
        else card.Padding = new Thickness(16, 12, 16, 12);
        return card;
    }

    /// <summary>A settings card: glyph, title and description on the left, the control on the right.</summary>
    public static Border Row(string title, string? description, UIElement? control, string? glyph = null)
    {
        var dock = new DockPanel { LastChildFill = true };
        if (control is FrameworkElement element)
        {
            element.VerticalAlignment = VerticalAlignment.Center;
            element.Margin = new Thickness(16, 0, 0, 0);
            DockPanel.SetDock(element, Dock.Right);
            dock.Children.Add(element);
        }
        if (glyph is not null)
        {
            var icon = Glyph(glyph, 16);
            icon.Margin = new Thickness(0, 0, 14, 0);
            DockPanel.SetDock(icon, Dock.Left);
            dock.Children.Add(icon);
        }
        var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        labels.Children.Add(Text(title, 13.5));
        if (!string.IsNullOrEmpty(description))
        {
            var detail = Secondary(description);
            detail.Margin = new Thickness(0, 2, 0, 0);
            labels.Children.Add(detail);
        }
        dock.Children.Add(labels);
        return Card(dock);
    }

    public static Button Button(string text, Action action, bool accent = false, string? tooltip = null)
    {
        var button = new Button { Content = text, Padding = new Thickness(14, 5, 14, 5), MinWidth = 80, ToolTip = tooltip };
        if (accent) button.SetResourceReference(FrameworkElement.StyleProperty, "AccentButtonStyle");
        button.Click += (_, _) => action();
        return button;
    }

    public static Button LinkButton(string text, Action action)
    {
        var label = Text(text, 12.5, brushKey: "AccentTextFillColorPrimaryBrush", wrap: false);
        var button = new Button { Content = label, Padding = new Thickness(4, 2, 4, 2), Cursor = System.Windows.Input.Cursors.Hand };
        button.SetResourceReference(FrameworkElement.StyleProperty, "SubtleButton");
        button.Click += (_, _) => action();
        return button;
    }

    public static StackPanel Stack(params UIElement?[] children) => Stack(Orientation.Vertical, children);

    public static StackPanel Stack(Orientation orientation, params UIElement?[] children)
    {
        var panel = new StackPanel { Orientation = orientation };
        foreach (var child in children)
            if (child is not null) panel.Children.Add(child);
        return panel;
    }

    public static StackPanel Buttons(params UIElement?[] children)
    {
        var panel = Stack(Orientation.Horizontal, children);
        foreach (var child in panel.Children.OfType<FrameworkElement>().Skip(1)) child.Margin = new Thickness(8, 0, 0, 0);
        return panel;
    }

    public static ScrollViewer Scroll(UIElement content, Thickness? padding = null) => new()
    {
        Content = content,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        Padding = padding ?? new Thickness(28, 20, 28, 24),
    };

    public static TextBox Field(string text = "", string? placeholder = null, bool mono = false, double width = double.NaN)
    {
        var box = new TextBox { Text = text, Padding = new Thickness(8, 5, 8, 5), Width = width };
        if (placeholder is not null) Placeholder.SetText(box, placeholder);
        if (mono) box.SetResourceReference(Control.FontFamilyProperty, "MonoFont");
        return box;
    }

    public static CheckBox Check(string text, bool isChecked, Action<bool> changed)
    {
        var box = new CheckBox { Content = text, IsChecked = isChecked };
        box.Click += (_, _) => changed(box.IsChecked == true);
        return box;
    }

    /// <summary>An on/off switch styled like a Windows toggle, with its state as text.</summary>
    public static CheckBox Toggle(bool isOn, Action<bool> changed)
    {
        var box = new CheckBox { IsChecked = isOn, Content = isOn ? "On" : "Off", MinWidth = 60 };
        box.Click += (_, _) =>
        {
            box.Content = box.IsChecked == true ? "On" : "Off";
            changed(box.IsChecked == true);
        };
        return box;
    }

    public static Border Divider(double margin = 0)
    {
        var line = new Border { Height = 1, Margin = new Thickness(0, margin, 0, margin) };
        line.SetResourceReference(Border.BackgroundProperty, "DividerStrokeColorDefaultBrush");
        return line;
    }

    /// <summary>A coloured status line (error, success, notice).</summary>
    public static TextBlock Status(string text, string brushKey) => Text(text, 12, brushKey: brushKey);

    public static Window Owner(DependencyObject element) => Window.GetWindow(element) ?? Application.Current.MainWindow;
}
