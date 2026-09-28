using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Dsh.App.Infrastructure;

/// <summary>Hint text shown in an empty TextBox (WPF has no PlaceholderText).</summary>
public static class Placeholder
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(Placeholder), new PropertyMetadata(null, OnTextChanged));

    public static string? GetText(DependencyObject d) => (string?)d.GetValue(TextProperty);
    public static void SetText(DependencyObject d, string? value) => d.SetValue(TextProperty, value);

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox box) return;
        box.Loaded -= Refresh;
        box.TextChanged -= Refresh;
        box.Loaded += Refresh;
        box.TextChanged += Refresh;
        box.IsVisibleChanged += (_, _) => Update(box);
        if (box.IsLoaded) Update(box);
    }

    private static void Refresh(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox box) Update(box);
    }

    private static void Update(TextBox box)
    {
        if (AdornerLayer.GetAdornerLayer(box) is not { } layer) return;
        var existing = layer.GetAdorners(box)?.OfType<PlaceholderAdorner>().FirstOrDefault();
        var show = box.Text.Length == 0 && !string.IsNullOrEmpty(GetText(box));
        if (show && existing is null) layer.Add(new PlaceholderAdorner(box));
        else if (!show && existing is not null) layer.Remove(existing);
        else existing?.InvalidateVisual();
    }

    private sealed class PlaceholderAdorner : Adorner
    {
        private readonly TextBox _box;

        public PlaceholderAdorner(TextBox box) : base(box)
        {
            _box = box;
            IsHitTestVisible = false;
        }

        protected override void OnRender(DrawingContext dc)
        {
            var text = GetText(_box);
            if (string.IsNullOrEmpty(text)) return;
            var brush = _box.TryFindResource("TextFillColorTertiaryBrush") as Brush ?? Brushes.Gray;
            var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, _box.FlowDirection,
                new Typeface(_box.FontFamily, _box.FontStyle, _box.FontWeight, _box.FontStretch), _box.FontSize, brush,
                VisualTreeHelper.GetDpi(this).PixelsPerDip)
            {
                MaxTextWidth = Math.Max(1, _box.ActualWidth - _box.Padding.Left - _box.Padding.Right - 8),
                MaxLineCount = 1,
                Trimming = TextTrimming.CharacterEllipsis,
            };
            // Line the hint up with where the caret sits: border + padding + the text view's own margin.
            var x = _box.BorderThickness.Left + _box.Padding.Left + 2;
            var y = _box.VerticalContentAlignment == VerticalAlignment.Center
                ? (_box.ActualHeight - formatted.Height) / 2
                : _box.BorderThickness.Top + _box.Padding.Top + 1;
            dc.DrawText(formatted, new Point(x, y));
        }
    }
}
