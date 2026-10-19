using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Vision.Core;

namespace Vision.App.Views.Chat;

/// <summary>A long text in a window of its own: a pasted block waiting in the composer (view and edit it), or a long
/// message in the transcript (read and copy it). The text box has a fixed viewport, so WPF lays out what is on screen
/// and scrolls the rest — unlike a row of the transcript, which is measured in full.</summary>
public sealed class TextViewerWindow : Window
{
    private readonly TextBox _text;
    private readonly TextBlock _count;

    /// <summary>The edited text when the user saved; null when they cancelled (or the window was read-only).</summary>
    public string? Result { get; private set; }

    private TextViewerWindow(string title, string text, bool editable)
    {
        Title = title;
        Width = Math.Min(980, SystemParameters.WorkArea.Width * 0.8);
        Height = Math.Min(760, SystemParameters.WorkArea.Height * 0.85);
        MinWidth = 420;
        MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "SolidBackgroundFillColorBaseBrush");

        _text = new TextBox
        {
            Text = text,
            IsReadOnly = !editable,
            AcceptsReturn = true,
            AcceptsTab = true,
            TextWrapping = TextWrapping.NoWrap,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontSize = 12.5,
            Padding = new Thickness(8),
            BorderThickness = new Thickness(1),
        };
        _text.SetResourceReference(FontFamilyProperty, "MonoFont");
        // A spell checker over hundreds of kilobytes of log lines only burns the UI thread.
        SpellCheck.SetIsEnabled(_text, false);
        var wrap = new CheckBox { Content = "Wrap lines", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) };
        wrap.Checked += (_, _) => _text.TextWrapping = TextWrapping.Wrap;
        wrap.Unchecked += (_, _) => _text.TextWrapping = TextWrapping.NoWrap;

        _count = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        _count.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        UpdateCount();
        if (editable) _text.TextChanged += (_, _) => UpdateCount();

        var copy = new Button { Content = "Copy All", Padding = new Thickness(12, 4, 12, 4) };
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetText(_text.Text); } catch (Exception) { }
        };

        var bar = new DockPanel { Margin = new Thickness(12, 8, 12, 12) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(buttons, Dock.Right);
        if (editable)
        {
            var cancel = new Button { Content = "Cancel", Padding = new Thickness(14, 4, 14, 4), IsCancel = true };
            cancel.Click += (_, _) => Close();
            var save = new Button { Content = "Save", Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(8, 0, 0, 0) };
            save.SetResourceReference(StyleProperty, "AccentButtonStyle");
            save.Click += (_, _) =>
            {
                Result = _text.Text;
                DialogResult = true;
            };
            buttons.Children.Add(cancel);
            buttons.Children.Add(save);
        }
        else
        {
            var done = new Button { Content = "Done", Padding = new Thickness(14, 4, 14, 4), IsDefault = true, IsCancel = true };
            done.SetResourceReference(StyleProperty, "AccentButtonStyle");
            done.Click += (_, _) => Close();
            buttons.Children.Add(done);
        }
        bar.Children.Add(buttons);
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(copy);
        left.Children.Add(new Border { Width = 12 });
        left.Children.Add(wrap);
        left.Children.Add(_count);
        bar.Children.Add(left);

        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Bottom);
        root.Children.Add(bar);
        var host = new Border { Margin = new Thickness(12, 12, 12, 0), Child = _text };
        root.Children.Add(host);
        Content = root;
        Loaded += (_, _) =>
        {
            _text.Focus();
            _text.CaretIndex = 0;
        };
    }

    private void UpdateCount()
    {
        var text = _text.Text;
        _count.Text = $"{AttachmentFormat.Count(AttachmentFormat.LineCount(AttachmentFormat.NormalizeNewlines(text)), "line", "lines")} · " +
                      AttachmentFormat.Bytes(TextUtil.Utf8NoBom.GetByteCount(text));
    }

    /// <summary>Show a long text to read and copy.</summary>
    public static void View(Window? owner, string title, string text)
    {
        var window = new TextViewerWindow(title, text, editable: false) { Owner = owner };
        window.Show();
    }

    /// <summary>Let the user edit a text; the new text, or null when they cancelled.</summary>
    public static string? Edit(Window? owner, string title, string text)
    {
        var window = new TextViewerWindow(title, text, editable: true) { Owner = owner };
        return window.ShowDialog() == true ? window.Result : null;
    }

    /// <summary>The self-test: the window without showing it modally.</summary>
    internal static TextViewerWindow ForSelfTest(string title, string text, bool editable) => new(title, text, editable);
}
