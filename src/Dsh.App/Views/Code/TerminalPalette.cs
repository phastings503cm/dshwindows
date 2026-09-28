using System.Windows.Media;
using Dsh.Core;

namespace Dsh.App.Views.Code;

/// <summary>The terminal's colours: 16 ANSI colours tuned per appearance, the 256-colour cube and
/// greyscale ramp, and truecolor.</summary>
public static class TerminalPalette
{
    private static readonly Color[] Light =
    [
        Rgb(0x33, 0x33, 0x38), Rgb(0xC5, 0x1E, 0x14), Rgb(0x1D, 0x7F, 0x2E), Rgb(0x9A, 0x6A, 0x00),
        Rgb(0x1E, 0x5A, 0xC8), Rgb(0x9B, 0x2F, 0xAE), Rgb(0x0E, 0x7C, 0x86), Rgb(0x8A, 0x8A, 0x90),
        Rgb(0x66, 0x66, 0x6C), Rgb(0xE0, 0x3E, 0x36), Rgb(0x2E, 0x9E, 0x44), Rgb(0xB8, 0x86, 0x00),
        Rgb(0x3B, 0x78, 0xE7), Rgb(0xB8, 0x4D, 0xCB), Rgb(0x16, 0x9C, 0xA8), Rgb(0x2B, 0x2B, 0x2E),
    ];

    private static readonly Color[] Dark =
    [
        Rgb(0x33, 0x33, 0x38), Rgb(0xF1, 0x4C, 0x4C), Rgb(0x4E, 0xC9, 0x6B), Rgb(0xE5, 0xC0, 0x4F),
        Rgb(0x4F, 0x9B, 0xF5), Rgb(0xD1, 0x7C, 0xE8), Rgb(0x3F, 0xD0, 0xD8), Rgb(0xCC, 0xCC, 0xCF),
        Rgb(0x76, 0x76, 0x7C), Rgb(0xF7, 0x6E, 0x6A), Rgb(0x6B, 0xE0, 0x86), Rgb(0xF2, 0xD2, 0x6C),
        Rgb(0x72, 0xB1, 0xFA), Rgb(0xDD, 0x96, 0xF0), Rgb(0x62, 0xE0, 0xE6), Rgb(0xF5, 0xF5, 0xF7),
    ];

    private static readonly double[] CubeSteps = [0, 0.373, 0.529, 0.686, 0.843, 1.0];

    private static Color Rgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);

    public static Color Indexed(int index, bool dark)
    {
        if (index < 16) return (dark ? Dark : Light)[index];
        if (index < 232)
        {
            var value = index - 16;
            return Color.FromRgb(Step(value / 36 % 6), Step(value / 6 % 6), Step(value % 6));
        }
        var level = (byte)Math.Round((0.03 + (index - 232) / 23.0 * 0.94) * 255);
        return Color.FromRgb(level, level, level);

        static byte Step(int i) => (byte)Math.Round(CubeSteps[i] * 255);
    }

    public static Color Resolve(TerminalColor colour, Color fallback, bool dark) => colour.Kind switch
    {
        TerminalColorKind.Indexed => Indexed(colour.Index, dark),
        TerminalColorKind.Rgb => Color.FromRgb(colour.R, colour.G, colour.B),
        _ => fallback,
    };
}
