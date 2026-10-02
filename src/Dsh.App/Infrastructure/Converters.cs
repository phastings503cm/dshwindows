using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Dsh.App.Infrastructure;

/// <summary>true → Visible, false → Collapsed (Parameter "invert" flips it).</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var on = value is true;
        if (parameter is "invert") on = !on;
        return on ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is Visibility.Visible ^ (parameter is "invert");
}

/// <summary>null / empty string / empty collection → Collapsed (Parameter "invert" flips it).</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var present = value switch
        {
            null => false,
            string s => s.Length > 0,
            System.Collections.ICollection c => c.Count > 0,
            _ => true,
        };
        if (parameter is "invert") present = !present;
        return present ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}

/// <summary>Keep the end of a long string — the tail of a path or command is the part that
/// identifies it. Parameter: max characters (default 80).</summary>
public sealed class HeadTruncateConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var text = value as string ?? "";
        var max = parameter is string p && int.TryParse(p, out var n) ? n : 80;
        text = text.Replace("\r", " ").Replace('\n', ' ');
        return text.Length <= max ? text : "…" + Dsh.Core.TextUtil.Suffix(text, max - 1);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>A single line: newlines become spaces.</summary>
public sealed class OneLineConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        (value as string ?? "").Replace("\r", " ").Replace('\n', ' ');

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>non-null (and non-empty string) → true.</summary>
public sealed class NotNullConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string s ? s.Length > 0 : value is not null;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>A number → Visible when greater than zero (Parameter "invert" flips it).</summary>
public sealed class PositiveToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var positive = value switch
        {
            int i => i > 0,
            long l => l > 0,
            double d => d > 0,
            _ => false,
        };
        if (parameter is "invert") positive = !positive;
        return positive ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Formats a timestamp as a message's date and time on this PC's clock ("Fri, Oct 2, 2026 · 11:42:05 AM");
/// with the parameter "full", the long form with the UTC offset (for tooltips).</summary>
public sealed class StampConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        DateTimeOffset date when parameter is "full" => Formatting.FullStamp(date),
        DateTimeOffset date => Formatting.Stamp(date),
        _ => "",
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>A path → its last component ("C:\src\app" → "app").</summary>
public sealed class FileNameConverter : IValueConverter
{
    public static FileNameConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var path = value as string ?? "";
        var name = System.IO.Path.GetFileName(path.TrimEnd('\\', '/'));
        return name.Length > 0 ? name : path;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
