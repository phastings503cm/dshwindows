using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Vision.App.Model;

namespace Vision.App.Views;

/// <summary>What chat mode shows with no chat selected: what this is, and the two ways in — a chat, or a folder of code.</summary>
public sealed class EmptyStateView : UserControl
{
    public EmptyStateView(AppModel model)
    {
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 420 };
        stack.Children.Add(new Image
        {
            Source = new BitmapImage(new Uri("pack://application:,,,/Assets/logo-256.png")),
            Width = 72,
            Height = 72,
            Margin = new Thickness(0, 0, 0, 14),
        });
        stack.Children.Add(new TextBlock
        {
            Text = "DSH",
            FontSize = 26,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        var body = new TextBlock
        {
            Text = "Chat with your AI, or open a folder to work on code.",
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 18),
        };
        body.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        stack.Children.Add(body);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        var open = new Button { Content = "Open Folder…", Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 0, 8, 0) };
        open.Click += (_, _) => model.ChooseProject();
        var chat = new Button { Content = "New Chat", Padding = new Thickness(14, 6, 14, 6) };
        chat.SetResourceReference(StyleProperty, "AccentButtonStyle");
        // A chat started from here is a regular conversation unless a folder is open.
        chat.Click += (_, _) => model.NewChat();
        buttons.Children.Add(open);
        buttons.Children.Add(chat);
        stack.Children.Add(buttons);

        var hint = new TextBlock
        {
            Text = "Ctrl+N new chat · Ctrl+Shift+N chat without a project · Ctrl+O open folder · F1 shortcuts",
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 22, 0, 0),
            FontSize = 11.5,
        };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
        stack.Children.Add(hint);
        Content = stack;
    }
}
