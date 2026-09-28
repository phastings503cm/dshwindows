using System.ComponentModel;
using System.Windows;
using System.Windows.Media;

namespace Dsh.App.Infrastructure;

/// <summary>Light/dark for the parts the app draws itself (terminal, editor colours). WPF's Fluent
/// theme (ThemeMode) restyles the standard controls; this watches the brush the theme swaps so
/// custom drawing follows it too, whether the change came from Settings or from Windows.</summary>
public sealed class ThemeService
{
    public static ThemeService Instance { get; } = new();

    private readonly FrameworkElement _probe = new();
    public event Action? Changed;

    private ThemeService()
    {
        _probe.SetResourceReference(FrameworkElement.TagProperty, "SolidBackgroundFillColorBaseBrush");
        DependencyPropertyDescriptor.FromProperty(FrameworkElement.TagProperty, typeof(FrameworkElement))
            .AddValueChanged(_probe, (_, _) => Changed?.Invoke());
    }

    /// <summary>"system", "light" or "dark" → the app's ThemeMode.</summary>
    public void Apply(string preference)
    {
        if (Application.Current is null) return;
#pragma warning disable WPF0001
        Application.Current.ThemeMode = preference switch
        {
            "light" => ThemeMode.Light,
            "dark" => ThemeMode.Dark,
            _ => ThemeMode.System,
        };
#pragma warning restore WPF0001
        // Resource lookups for the probe are relative to the application; force a re-read.
        _probe.SetResourceReference(FrameworkElement.TagProperty, "SolidBackgroundFillColorBaseBrush");
        Changed?.Invoke();
    }

    public bool IsDark
    {
        get
        {
            var brush = _probe.Tag as SolidColorBrush
                        ?? Application.Current?.TryFindResource("SolidBackgroundFillColorBaseBrush") as SolidColorBrush;
            if (brush is null) return SystemPrefersDark();
            var c = brush.Color;
            return (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0 < 0.5;
        }
    }

    private static bool SystemPrefersDark()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>A theme brush by Fluent key, with a fallback when the theme isn't loaded.</summary>
    public static Brush Brush(string key, Color fallback) =>
        Application.Current?.TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);
}
