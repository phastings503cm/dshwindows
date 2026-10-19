using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Vision.App.Infrastructure;
using Vision.App.Model;
using Vision.Core;

namespace Vision.App.Views.Dialogs;

/// <summary>Guardian said no — shown as a dialog, never as a raw HTTP code (contract §Errors): the title, Guardian's own
/// sentence (it names who can grant what is missing), and what is missing or which limit was reached and when it resets.
/// A 401 offers to sign in again; a 403 or a limit offers Settings › Guardian.</summary>
public sealed class GuardianRefusalWindow : Window
{
    /// <summary>What the person chose.</summary>
    public bool OpenSettings { get; private set; }

    public GuardianRefusalWindow(GuardianError error)
    {
        var text = GuardianRefusalText.For(error);
        Title = "Guardian";
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        MinWidth = 420;
        MaxWidth = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "SolidBackgroundFillColorBaseBrush");

        var panel = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
        var glyph = Ui.Glyph(error.Kind == GuardianErrorKind.Limit ? Icons.Warning : Icons.Lock, 22,
            error.Kind == GuardianErrorKind.Limit ? "SystemFillColorCautionBrush" : "AccentTextFillColorPrimaryBrush");
        glyph.Margin = new Thickness(0, 0, 12, 0);
        glyph.VerticalAlignment = VerticalAlignment.Top;
        var heading = Ui.Text(text.Title, 18, FontWeights.SemiBold);
        var head = Ui.Stack(Orientation.Horizontal, glyph, heading);
        head.Margin = new Thickness(0, 0, 0, 10);
        panel.Children.Add(head);

        var message = Ui.Text(text.Message, 13.5);
        message.LineHeight = 20;
        panel.Children.Add(message);

        if (text.Details.Count > 0)
        {
            var details = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
            foreach (var line in text.Details)
            {
                var item = Ui.Secondary(line, 12.5);
                item.Margin = new Thickness(0, 2, 0, 2);
                details.Children.Add(item);
            }
            var box = new Border { Child = details, Padding = new Thickness(12, 8, 12, 8), CornerRadius = new CornerRadius(6) };
            box.SetResourceReference(Border.BackgroundProperty, "LayerFillColorDefaultBrush");
            panel.Children.Add(box);
        }
        if (error.Kind == GuardianErrorKind.Limit && error.Limit?.Fraction is { } fraction)
        {
            var bar = new ProgressBar { Minimum = 0, Maximum = 1, Value = fraction, Height = 4, Margin = new Thickness(0, 10, 0, 0) };
            panel.Children.Add(bar);
        }

        var close = new Button { Content = text.OffersSignIn ? "Not now" : "OK", MinWidth = 96, IsCancel = true, IsDefault = !text.OffersSignIn };
        if (!text.OffersSignIn) close.SetResourceReference(StyleProperty, "AccentButtonStyle");
        close.Click += (_, _) => Close();
        var settings = new Button
        {
            Content = text.OffersSignIn ? "Sign in again…" : error.Kind == GuardianErrorKind.Limit ? "See my limits…" : "Guardian settings…",
            MinWidth = 96,
            Margin = new Thickness(0, 0, 8, 0),
            IsDefault = text.OffersSignIn,
        };
        if (text.OffersSignIn) settings.SetResourceReference(StyleProperty, "AccentButtonStyle");
        settings.Click += (_, _) =>
        {
            OpenSettings = true;
            Close();
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        buttons.Children.Add(settings);
        buttons.Children.Add(close);
        panel.Children.Add(buttons);
        Content = panel;
    }

    /// <summary>Show <paramref name="error"/> over the app; Settings › Guardian opens if the person asks.</summary>
    public static void Show(AppModel model, GuardianError error)
    {
        if (SelfTest.Current is not null) return;
        var owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current?.MainWindow;
        var window = new GuardianRefusalWindow(error);
        if (owner is { IsVisible: true }) window.Owner = owner;
        else window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        window.ShowDialog();
        if (window.OpenSettings) model.ShowGuardian();
    }
}
