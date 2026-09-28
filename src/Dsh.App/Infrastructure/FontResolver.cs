using System.Windows;
using System.Windows.Media;

namespace Dsh.App.Infrastructure;

/// <summary>Points the app's font resources at families that are actually installed. Windows always
/// has Consolas and Segoe MDL2 Assets, but a missing family makes WPF fall back font by font (or, with
/// no fallback at all, fail outright), so the choice is made once, up front.</summary>
public static class FontResolver
{
    private static readonly string[] Mono = ["Cascadia Mono", "Cascadia Code", "Consolas", "Lucida Console", "Courier New"];
    private static readonly string[] IconFamilies = ["Segoe Fluent Icons", "Segoe MDL2 Assets"];

    public static void Apply(ResourceDictionary resources)
    {
        Set(resources, "MonoFont", FirstInstalled(Mono) ?? SystemFonts.MessageFontFamily);
        if (FirstInstalled(IconFamilies) is { } icons) Set(resources, "IconFont", icons);
    }

    public static FontFamily? FirstInstalled(IEnumerable<string> names)
    {
        foreach (var name in names)
        {
            var family = new FontFamily(name);
            var typeface = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            if (typeface.TryGetGlyphTypeface(out var glyphs) && glyphs.FamilyNames.Values.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
                return family;
        }
        return null;
    }

    /// <summary>Replace the value wherever the key is defined, so StaticResource lookups inside the
    /// theme dictionary see it too.</summary>
    private static void Set(ResourceDictionary dictionary, string key, object value)
    {
        if (dictionary.Contains(key)) dictionary[key] = value;
        foreach (var merged in dictionary.MergedDictionaries) Set(merged, key, value);
    }
}
