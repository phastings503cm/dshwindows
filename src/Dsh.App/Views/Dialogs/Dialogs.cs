using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Dsh.App.Views.Dialogs;

/// <summary>Small modal dialogs in the app's theme: confirmations with custom buttons, a text
/// prompt, and plain messages.</summary>
public static class Dialog
{
    public sealed record Choice(string Label, bool IsDefault = false, bool IsCancel = false, bool IsDestructive = false);

    private static Window? Owner() =>
        Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current?.MainWindow;

    /// <summary>Ask a question; returns the index of the chosen button (the cancel index when the
    /// window is closed).</summary>
    public static int Ask(string title, string message, params Choice[] choices)
    {
        var window = Create(title);
        var cancelIndex = Array.FindIndex(choices, c => c.IsCancel);
        var result = cancelIndex;
        var panel = Body(title, message);
        panel.Children.Add(Buttons(choices, index =>
        {
            result = index;
            window.DialogResult = true;
        }));
        window.Content = panel;
        window.ShowDialog();
        return result;
    }

    /// <summary>OK/Cancel style confirmation.</summary>
    public static bool Confirm(string title, string message, string confirm = "OK", bool destructive = false) =>
        Ask(title, message, new Choice("Cancel", IsCancel: true), new Choice(confirm, IsDefault: true, IsDestructive: destructive)) == 1;

    public static void Info(string title, string message) =>
        Ask(title, message, new Choice("OK", IsDefault: true, IsCancel: true));

    /// <summary>Ask for a line of text; null when cancelled or empty.</summary>
    public static string? Prompt(string title, string message, string initial = "", string confirm = "OK")
    {
        var window = Create(title);
        var panel = Body(title, message);
        var box = new TextBox { Text = initial, Margin = new Thickness(0, 4, 0, 0), MinWidth = 320 };
        panel.Children.Insert(panel.Children.Count, box);
        string? result = null;
        panel.Children.Add(Buttons([new Choice("Cancel", IsCancel: true), new Choice(confirm, IsDefault: true)], index =>
        {
            if (index == 1) result = box.Text.Trim();
            window.DialogResult = true;
        }));
        window.Content = panel;
        window.Loaded += (_, _) =>
        {
            box.Focus();
            // Select the name without its extension, like Explorer's rename.
            var dot = box.Text.LastIndexOf('.');
            if (dot > 0) box.Select(0, dot); else box.SelectAll();
        };
        window.ShowDialog();
        return string.IsNullOrEmpty(result) ? null : result;
    }

    private static Window Create(string title)
    {
        var owner = Owner();
        var window = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            Owner = owner,
            MinWidth = 380,
            MaxWidth = 560,
        };
        window.SetResourceReference(Window.BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        return window;
    }

    private static StackPanel Body(string title, string message)
    {
        var panel = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
        var heading = new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        panel.Children.Add(heading);
        if (!string.IsNullOrEmpty(message))
        {
            var text = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8), LineHeight = 20 };
            text.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            panel.Children.Add(text);
        }
        return panel;
    }

    private static FrameworkElement Buttons(IReadOnlyList<Choice> choices, Action<int> pick)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        for (var i = 0; i < choices.Count; i++)
        {
            var index = i;
            var choice = choices[i];
            var button = new Button
            {
                Content = choice.Label,
                MinWidth = 96,
                Margin = new Thickness(8, 0, 0, 0),
                IsDefault = choice.IsDefault,
                IsCancel = choice.IsCancel,
            };
            if (choice.IsDefault) button.SetResourceReference(FrameworkElement.StyleProperty, "AccentButtonStyle");
            if (choice.IsDestructive)
            {
                button.SetResourceReference(Control.BackgroundProperty, "SystemFillColorCriticalBrush");
                button.Foreground = Brushes.White;
            }
            button.Click += (_, _) => pick(index);
            row.Children.Add(button);
        }
        return row;
    }
}

/// <summary>Esc closes any window that opts in (dialog-like tool windows).</summary>
public static class EscapeCloses
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(EscapeCloses), new PropertyMetadata(false, (d, e) =>
        {
            if (d is not Window window) return;
            if ((bool)e.NewValue) window.PreviewKeyDown += OnKey;
            else window.PreviewKeyDown -= OnKey;
        }));

    public static bool GetIsEnabled(DependencyObject d) => (bool)d.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject d, bool value) => d.SetValue(IsEnabledProperty, value);

    private static void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && sender is Window window && !e.Handled)
        {
            e.Handled = true;
            window.Close();
        }
    }
}
