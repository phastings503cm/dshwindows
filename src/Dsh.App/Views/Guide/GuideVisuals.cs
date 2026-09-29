using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Dsh.App.Infrastructure;

namespace Dsh.App.Views.Guide;

/// <summary>The building blocks every guide page is made of: a picture, a heading, short plain
/// text and numbered steps, a "What's happening?" aside for the curious and a "Stuck?" tip. All
/// colours come from theme brushes so light and dark both work.</summary>
public static class GuideVisuals
{
    // MARK: - Pictures

    /// <summary>A guide illustration (Assets/Guide/NAME.png, or NAME-dark.png in the dark theme)
    /// that swaps with the theme and scales down with the window.</summary>
    public static FrameworkElement Picture(string name, string description, double maxHeight = 270)
    {
        var image = new Image
        {
            Stretch = Stretch.Uniform,
            MaxWidth = 600,
            MaxHeight = maxHeight,
            HorizontalAlignment = HorizontalAlignment.Left,
            SnapsToDevicePixels = true,
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        AutomationProperties.SetName(image, description);
        image.ToolTip = null;
        void Load() => image.Source = Bitmap(name, ThemeService.Instance.IsDark);
        Load();
        void OnTheme() => image.Dispatcher.BeginInvoke(Load);
        image.Loaded += (_, _) =>
        {
            Load();
            ThemeService.Instance.Changed += OnTheme;
        };
        image.Unloaded += (_, _) => ThemeService.Instance.Changed -= OnTheme;
        var frame = new Border { Child = image, Margin = new Thickness(0, 0, 0, 16), HorizontalAlignment = HorizontalAlignment.Left };
        return frame;
    }

    private static readonly Dictionary<string, BitmapImage> Cache = new(StringComparer.Ordinal);

    public static BitmapImage? Bitmap(string name, bool dark)
    {
        var key = dark ? $"{name}-dark" : name;
        if (Cache.TryGetValue(key, out var cached)) return cached;
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri($"pack://application:,,,/Assets/Guide/{key}.png");
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze();
            Cache[key] = bitmap;
            return bitmap;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // MARK: - Text

    public static TextBlock Heading(string text)
    {
        var block = Ui.Text(text, 21, FontWeights.SemiBold);
        block.Margin = new Thickness(0, 0, 0, 6);
        AutomationProperties.SetHeadingLevel(block, AutomationHeadingLevel.Level1);
        return block;
    }

    public static TextBlock Subheading(string text)
    {
        var block = Ui.Text(text, 14.5, FontWeights.SemiBold);
        block.Margin = new Thickness(0, 14, 0, 6);
        AutomationProperties.SetHeadingLevel(block, AutomationHeadingLevel.Level2);
        return block;
    }

    /// <summary>A paragraph of body text; **bold** spans are supported for the one word that matters.</summary>
    public static TextBlock Paragraph(string text, double size = 13.5, string brush = "TextFillColorPrimaryBrush")
    {
        var block = new TextBlock { FontSize = size, TextWrapping = TextWrapping.Wrap, LineHeight = size * 1.5, Margin = new Thickness(0, 0, 0, 10) };
        block.SetResourceReference(TextBlock.ForegroundProperty, brush);
        var parts = text.Split("**");
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length == 0) continue;
            block.Inlines.Add(i % 2 == 1 ? new Bold(new Run(parts[i])) : new Run(parts[i]));
        }
        return block;
    }

    public static TextBlock Lead(string text) => Paragraph(text, 13.5, "TextFillColorSecondaryBrush");

    /// <summary>Numbered steps, each a short sentence.</summary>
    public static StackPanel Steps(params string[] steps)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 2, 0, 8) };
        for (var i = 0; i < steps.Length; i++)
        {
            var number = new Border
            {
                Width = 24,
                Height = 24,
                CornerRadius = new CornerRadius(12),
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 0, 12, 0),
                Child = new TextBlock
                {
                    Text = (i + 1).ToString(),
                    FontSize = 12,
                    FontWeight = FontWeights.Bold,
                    Foreground = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
            number.Background = BrandBrush;
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
            DockPanel.SetDock(number, Dock.Left);
            row.Children.Add(number);
            var text = Paragraph(steps[i]);
            text.Margin = new Thickness(0, 2, 0, 0);
            row.Children.Add(text);
            panel.Children.Add(row);
        }
        return panel;
    }

    /// <summary>The app's gradient (#4338ca → #2563eb → #06b6d4), as in the logo.</summary>
    public static readonly LinearGradientBrush BrandBrush = Frozen(new LinearGradientBrush(
        [
            new GradientStop(Color.FromRgb(0x43, 0x38, 0xCA), 0),
            new GradientStop(Color.FromRgb(0x25, 0x63, 0xEB), 0.55),
            new GradientStop(Color.FromRgb(0x06, 0xB6, 0xD4), 1),
        ], new Point(0, 0), new Point(1, 1)));

    private static LinearGradientBrush Frozen(LinearGradientBrush brush)
    {
        brush.Freeze();
        return brush;
    }

    /// <summary>Bulleted lines with a glyph each.</summary>
    public static StackPanel Bullets(params (string Glyph, string Text)[] items)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        foreach (var (glyph, text) in items)
        {
            var icon = Ui.Glyph(glyph, 15, "AccentTextFillColorPrimaryBrush");
            icon.Margin = new Thickness(0, 2, 12, 0);
            icon.VerticalAlignment = VerticalAlignment.Top;
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            DockPanel.SetDock(icon, Dock.Left);
            row.Children.Add(icon);
            var body = Paragraph(text);
            body.Margin = new Thickness(0);
            row.Children.Add(body);
            panel.Children.Add(row);
        }
        return panel;
    }

    // MARK: - Asides

    /// <summary>"What's happening?" — the technical story for the curious, folded away.</summary>
    public static Expander Aside(string text, string title = "What's happening?")
    {
        var header = Ui.Stack(Orientation.Horizontal, Ui.Glyph(Icons.Info, 14, "AccentTextFillColorPrimaryBrush"), Ui.Text(title, 13, FontWeights.SemiBold));
        ((FrameworkElement)header.Children[0]).Margin = new Thickness(0, 0, 8, 0);
        var body = Paragraph(text, 12.5, "TextFillColorSecondaryBrush");
        body.Margin = new Thickness(0);
        return new Expander
        {
            Header = header,
            Content = new Border { Child = body, Padding = new Thickness(26, 6, 4, 4) },
            Margin = new Thickness(0, 12, 0, 0),
        };
    }

    /// <summary>"Stuck?" — the likely problem and its fix.</summary>
    public static Border Stuck(string text)
    {
        var icon = Ui.Glyph(Icons.Lightbulb, 16, "SystemFillColorCautionBrush");
        icon.Margin = new Thickness(0, 1, 12, 0);
        icon.VerticalAlignment = VerticalAlignment.Top;
        var body = Paragraph(text, 12.5, "TextFillColorSecondaryBrush");
        body.Margin = new Thickness(0, 2, 0, 0);
        var stack = Ui.Stack(Ui.Text("Stuck?", 13, FontWeights.SemiBold), body);
        var row = new DockPanel();
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);
        row.Children.Add(stack);
        var card = Ui.Card(row);
        card.Margin = new Thickness(0, 12, 0, 0);
        return card;
    }

    // MARK: - Status

    public enum Tone { Info, Success, Warning, Error }

    /// <summary>A coloured card with a glyph and text: success, warning, error or plain info.</summary>
    public static Border Note(Tone tone, string text, string? title = null, UIElement? extra = null)
    {
        var (glyph, brush, background) = tone switch
        {
            Tone.Success => (Icons.Completed, "SystemFillColorSuccessBrush", "SystemFillColorSuccessBackgroundBrush"),
            Tone.Warning => (Icons.Warning, "SystemFillColorCautionBrush", "SystemFillColorCautionBackgroundBrush"),
            Tone.Error => (Icons.Error, "SystemFillColorCriticalBrush", "SystemFillColorCriticalBackgroundBrush"),
            _ => (Icons.Info, "AccentTextFillColorPrimaryBrush", "SystemFillColorAttentionBackgroundBrush"),
        };
        var icon = Ui.Glyph(glyph, 16, brush);
        icon.Margin = new Thickness(0, 1, 12, 0);
        icon.VerticalAlignment = VerticalAlignment.Top;
        var stack = new StackPanel();
        if (title is not null) stack.Children.Add(Ui.Text(title, 13.5, FontWeights.SemiBold));
        if (text.Length > 0)
        {
            var body = Paragraph(text, 12.5, title is null ? "TextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush");
            body.Margin = new Thickness(0, title is null ? 1 : 2, 0, 0);
            stack.Children.Add(body);
        }
        if (extra is FrameworkElement element)
        {
            element.HorizontalAlignment = HorizontalAlignment.Left;
            stack.Children.Add(element);
        }
        var row = new DockPanel();
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);
        row.Children.Add(stack);
        var card = Ui.Card(row);
        card.SetResourceReference(Border.BackgroundProperty, background);
        card.Margin = new Thickness(0, 6, 0, 6);
        AutomationProperties.SetLiveSetting(card, AutomationLiveSetting.Polite);
        return card;
    }

    /// <summary>A spinner and a line of text: "Connecting…".</summary>
    public static StackPanel Busy(string text)
    {
        var spinner = new Spinner { Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        spinner.SetResourceReference(Spinner.ForegroundProperty, "AccentTextFillColorPrimaryBrush");
        var panel = Ui.Stack(Orientation.Horizontal, spinner, Ui.Text(text, 13, brushKey: "TextFillColorSecondaryBrush"));
        panel.Margin = new Thickness(0, 6, 0, 6);
        return panel;
    }

    /// <summary>A selectable monospace value (a fingerprint, a command) with a Copy button.</summary>
    public static DockPanel Copyable(string value, string? label = null)
    {
        var box = new TextBox { Text = value, TextWrapping = TextWrapping.Wrap, FontSize = 12.5 };
        box.SetResourceReference(FrameworkElement.StyleProperty, "SelectableText");
        box.SetResourceReference(Control.FontFamilyProperty, "MonoFont");
        box.VerticalAlignment = VerticalAlignment.Center;
        if (label is not null) AutomationProperties.SetName(box, label);
        var copy = Ui.Button("Copy", () =>
        {
            try { Clipboard.SetText(value); } catch (Exception) { }
        }, tooltip: "Copy to the clipboard");
        copy.MinWidth = 64;
        copy.Margin = new Thickness(12, 0, 0, 0);
        copy.VerticalAlignment = VerticalAlignment.Center;
        var row = new DockPanel();
        DockPanel.SetDock(copy, Dock.Right);
        row.Children.Add(copy);
        var frame = new Border { Child = box, Padding = new Thickness(10, 7, 10, 7), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1) };
        frame.SetResourceReference(Border.BackgroundProperty, "SubtleFillColorSecondaryBrush");
        frame.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        row.Children.Add(frame);
        row.Margin = new Thickness(0, 4, 0, 8);
        return row;
    }

    /// <summary>"AB:CD:EF:…" as four lines of eight bytes: easier to compare by eye.</summary>
    public static string FingerprintLines(string fingerprint)
    {
        var parts = fingerprint.Split(':');
        return parts.Length != 32 ? fingerprint : string.Join("\n", Enumerable.Range(0, 4).Select(i => string.Join(':', parts.Skip(i * 8).Take(8))));
    }

    /// <summary>A big selectable card: a glyph, a title and a line of detail; the chosen one is outlined.</summary>
    public static Button Choice(string glyph, string title, string detail, bool selected, Action choose)
    {
        var icon = Ui.Glyph(glyph, 20, selected ? "AccentTextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush");
        icon.Margin = new Thickness(0, 2, 16, 0);
        icon.VerticalAlignment = VerticalAlignment.Top;
        var check = Ui.Glyph(Icons.Completed, 18, "AccentTextFillColorPrimaryBrush");
        check.Visibility = selected ? Visibility.Visible : Visibility.Hidden;
        check.Margin = new Thickness(12, 0, 0, 0);
        var row = new DockPanel();
        DockPanel.SetDock(icon, Dock.Left);
        DockPanel.SetDock(check, Dock.Right);
        row.Children.Add(icon);
        row.Children.Add(check);
        var detailText = Ui.Secondary(detail, 12.5);
        detailText.Margin = new Thickness(0, 3, 0, 0);
        row.Children.Add(Ui.Stack(Ui.Text(title, 14.5, FontWeights.SemiBold), detailText));
        var card = new Border { Child = row, Padding = new Thickness(16, 14, 16, 14), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(selected ? 2 : 1) };
        card.SetResourceReference(Border.BackgroundProperty, selected ? "SubtleFillColorSecondaryBrush" : "CardBackgroundFillColorDefaultBrush");
        card.SetResourceReference(Border.BorderBrushProperty, selected ? "AccentFillColorDefaultBrush" : "CardStrokeColorDefaultBrush");
        var button = new Button
        {
            Content = card,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 0, 10),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        button.SetResourceReference(FrameworkElement.StyleProperty, "SubtleButton");
        AutomationProperties.SetName(button, title);
        button.Click += (_, _) => choose();
        return button;
    }

    /// <summary>A labelled form field.</summary>
    public static StackPanel Field(string label, FrameworkElement control, string? hint = null)
    {
        var title = Ui.Text(label, 12.5, FontWeights.SemiBold);
        title.Margin = new Thickness(0, 0, 0, 4);
        AutomationProperties.SetName(control, label);
        var panel = Ui.Stack(title, control);
        if (hint is not null)
        {
            var text = Ui.Secondary(hint);
            text.Margin = new Thickness(0, 4, 0, 0);
            panel.Children.Add(text);
        }
        panel.Margin = new Thickness(0, 0, 0, 12);
        return panel;
    }

    public static PasswordBox PasswordField() => new() { Padding = new Thickness(8, 5, 8, 5) };

    /// <summary>A button that looks like a link.</summary>
    public static Button Link(string text, Action action) => Ui.LinkButton(text, action);

    /// <summary>Take an element out of whatever panel or decorator holds it, so a page that is
    /// rebuilt can reuse its live controls (a log, a scanner) without losing their contents.</summary>
    public static T Detach<T>(T element) where T : FrameworkElement
    {
        switch (element.Parent)
        {
            case Panel panel:
                panel.Children.Remove(element);
                break;
            case Decorator decorator:
                decorator.Child = null;
                break;
            case ContentControl control:
                control.Content = null;
                break;
            case ItemsControl items:
                items.Items.Remove(element);
                break;
        }
        return element;
    }
}

/// <summary>A live, read-only log in a monospace box that keeps itself scrolled to the newest line
/// (unless the reader has scrolled up to look at something).</summary>
public sealed class GuideLog : Border
{
    private readonly TextBox _box;
    private int _lines;
    private const int MaxLines = 3000;

    public GuideLog(double height = 190)
    {
        _box = new TextBox
        {
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            FontSize = 11.5,
            Height = height,
            Padding = new Thickness(10, 8, 10, 8),
        };
        _box.SetResourceReference(Control.FontFamilyProperty, "MonoFont");
        _box.SetResourceReference(Control.ForegroundProperty, "TextFillColorPrimaryBrush");
        AutomationProperties.SetName(_box, "Log");
        Child = _box;
        CornerRadius = new CornerRadius(8);
        BorderThickness = new Thickness(1);
        Margin = new Thickness(0, 6, 0, 6);
        SetResourceReference(BackgroundProperty, "SolidBackgroundFillColorSecondaryBrush");
        SetResourceReference(BorderBrushProperty, "CardStrokeColorDefaultBrush");
    }

    public bool IsEmpty => _lines == 0;

    public string Text => _box.Text;

    public void Append(string line)
    {
        var atEnd = _box.VerticalOffset + _box.ViewportHeight >= _box.ExtentHeight - 4;
        if (_lines > 0) _box.AppendText("\n");
        _box.AppendText(line);
        _lines++;
        if (_lines > MaxLines)
        {
            var cut = _box.Text.IndexOf('\n', _box.Text.Length / 4);
            if (cut > 0)
            {
                _box.Text = _box.Text[(cut + 1)..];
                _lines = _box.Text.Count(c => c == '\n') + 1;
            }
        }
        if (atEnd) _box.ScrollToEnd();
    }

    public void Clear()
    {
        _box.Clear();
        _lines = 0;
    }
}
