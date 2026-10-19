using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Vision.App.Infrastructure;

namespace Vision.App.Views.Chat;

/// <summary>Copy commands on transcript rows (10-19 #13: "I want to be able to copy existing prompts"). Executed with the row
/// (its entry) as the parameter; the chat view copies what that row holds — a prompt as it was sent, a reply, a tool's output.</summary>
public static class TranscriptCommands
{
    public static readonly RoutedUICommand CopyMessage = new("Copy", nameof(CopyMessage), typeof(TranscriptCommands));
}

/// <summary>The small "Copy" button on a message, shown while the pointer is over the row (or the row has the keyboard), that
/// says "Copied" for a moment after it copies.</summary>
public sealed class CopyButton : Button
{
    public static readonly DependencyProperty IsCopiedProperty = DependencyProperty.Register(
        nameof(IsCopied), typeof(bool), typeof(CopyButton), new PropertyMetadata(false));

    /// <summary>True for <see cref="CopiedFor"/> after a copy: the row keeps the button in view while it says so.</summary>
    public bool IsCopied
    {
        get => (bool)GetValue(IsCopiedProperty);
        private set => SetValue(IsCopiedProperty, value);
    }

    public static readonly TimeSpan CopiedFor = TimeSpan.FromSeconds(1.6);

    private readonly TextBlock _glyph;
    private readonly TextBlock _label;
    private readonly DispatcherTimer _timer;

    public CopyButton()
    {
        SetResourceReference(StyleProperty, "SubtleButton");
        Padding = new Thickness(6, 1, 6, 1);
        Focusable = false;
        _glyph = new TextBlock { Text = Icons.Copy, FontSize = 11, Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center };
        _glyph.SetResourceReference(StyleProperty, "Glyph");
        _glyph.FontSize = 11;
        _label = new TextBlock { Text = "Copy", FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center };
        var content = new StackPanel { Orientation = Orientation.Horizontal, Children = { _glyph, _label } };
        Content = content;
        SetResourceReference(ForegroundProperty, "TextFillColorSecondaryBrush");
        _timer = new DispatcherTimer(CopiedFor, DispatcherPriority.Background, (_, _) => Reset(), Dispatcher);
        _timer.Stop();
        // A recycled row starts fresh.
        DataContextChanged += (_, _) => Reset();
    }

    /// <summary>Copies the row this button sits on. (Not a bound Command: a button made before it joins the transcript would
    /// ask the command too early, find no handler and stay disabled until the next keystroke.)</summary>
    protected override void OnClick()
    {
        base.OnClick();
        TranscriptCommands.CopyMessage.Execute(DataContext, this);
    }

    /// <summary>"✓ Copied" for a moment (or "Couldn't copy" when the clipboard was busy).</summary>
    public void ShowCopied(bool ok = true)
    {
        _glyph.Text = ok ? Icons.CheckMark : Icons.Warning;
        _label.Text = ok ? "Copied" : "Couldn't copy";
        SetResourceReference(ForegroundProperty, ok ? "SystemFillColorSuccessBrush" : "SystemFillColorCautionBrush");
        IsCopied = true;
        _timer.Stop();
        _timer.Start();
    }

    private void Reset()
    {
        _timer.Stop();
        if (!IsCopied) return;
        _glyph.Text = Icons.Copy;
        _label.Text = "Copy";
        SetResourceReference(ForegroundProperty, "TextFillColorSecondaryBrush");
        IsCopied = false;
    }

    /// <summary>The copy button on the row that holds <paramref name="element"/>, if it has one.</summary>
    public static CopyButton? InRow(DependencyObject element)
    {
        DependencyObject? row = element;
        while (row is not null && row is not ContentPresenter { Content: Model.ChatEntryVM }) row = VisualTreeHelper.GetParent(row);
        return row is null ? null : Find(row);
    }

    private static CopyButton? Find(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is CopyButton button) return button;
            if (Find(child) is { } found) return found;
        }
        return null;
    }
}
