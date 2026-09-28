using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Dsh.App.Model;

namespace Dsh.App.Views;

/// <summary>A screenshot or image file, enlarged, with copy and zoom.</summary>
public sealed class ImageViewerWindow : Window
{
    private readonly BitmapSource _image;
    private readonly ScaleTransform _scale = new(1, 1);

    private ImageViewerWindow(BitmapSource image, string title)
    {
        _image = image;
        Title = title;
        Width = Math.Clamp(image.PixelWidth + 40, 480, SystemParameters.WorkArea.Width * 0.85);
        Height = Math.Clamp(image.PixelHeight + 110, 360, SystemParameters.WorkArea.Height * 0.85);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "SolidBackgroundFillColorBaseBrush");

        var picture = new Image { Source = image, Stretch = Stretch.Uniform, LayoutTransform = _scale };
        RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.HighQuality);
        var scroller = new ScrollViewer
        {
            Content = picture,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(12),
        };
        scroller.PreviewMouseWheel += (_, e) =>
        {
            if (Keyboard.Modifiers != ModifierKeys.Control) return;
            Zoom(e.Delta > 0 ? 1.15 : 1 / 1.15);
            e.Handled = true;
        };

        var copy = new Button { Content = "Copy", Padding = new Thickness(12, 4, 12, 4) };
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetImage(_image); } catch (Exception) { }
        };
        var save = new Button { Content = "Save As…", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(8, 0, 0, 0) };
        save.Click += (_, _) => SaveAs();
        var fit = new Button { Content = "Actual Size", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(8, 0, 0, 0) };
        fit.Click += (_, _) =>
        {
            var actual = picture.Stretch == Stretch.Uniform;
            picture.Stretch = actual ? Stretch.None : Stretch.Uniform;
            _scale.ScaleX = _scale.ScaleY = 1;
            fit.Content = actual ? "Fit" : "Actual Size";
        };
        var done = new Button { Content = "Done", Padding = new Thickness(14, 4, 14, 4), IsDefault = true, IsCancel = true };
        done.SetResourceReference(StyleProperty, "AccentButtonStyle");
        done.Click += (_, _) => Close();
        var info = new TextBlock { Text = $"{image.PixelWidth} × {image.PixelHeight}", VerticalAlignment = VerticalAlignment.Center };
        info.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");

        var bar = new DockPanel { Margin = new Thickness(12, 8, 12, 12) };
        DockPanel.SetDock(done, Dock.Right);
        bar.Children.Add(done);
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(copy);
        left.Children.Add(save);
        left.Children.Add(fit);
        left.Children.Add(new Border { Width = 12 });
        left.Children.Add(info);
        bar.Children.Add(left);

        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Bottom);
        root.Children.Add(bar);
        root.Children.Add(scroller);
        Content = root;
    }

    private void Zoom(double factor)
    {
        var next = Math.Clamp(_scale.ScaleX * factor, 0.1, 8);
        _scale.ScaleX = _scale.ScaleY = next;
    }

    private void SaveAs()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "PNG image|*.png", FileName = "image.png" };
        if (dialog.ShowDialog(this) != true) return;
        if (ImageTools.ToPng(_image) is { } png)
            try { File.WriteAllBytes(dialog.FileName, png); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public static void Show(Window? owner, BitmapSource image, string title = "Image") =>
        new ImageViewerWindow(image, title) { Owner = owner }.Show();

    public static void Show(Window? owner, string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            if (ImageTools.Load(bytes) is { } image) Show(owner, image, Path.GetFileName(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
