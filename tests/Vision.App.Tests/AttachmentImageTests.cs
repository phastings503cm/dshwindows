using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Vision.App.Model;

namespace Vision.App.Tests;

/// <summary>Attached pictures are made into PNGs every model server takes (the limits Vision for macOS uses).</summary>
public sealed class AttachmentImageTests
{
    private static BitmapSource Noise(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        new Random(7).NextBytes(pixels); // noise: PNG can't squeeze it, so the byte limit bites
        for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static (int Width, int Height) Size(byte[] png)
    {
        var frame = BitmapDecoder.Create(new MemoryStream(png), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        return (frame.PixelWidth, frame.PixelHeight);
    }

    [Fact]
    public void AWidePictureIsScaledToTwoThousandPixels()
    {
        var png = ImageTools.ToAttachmentPng(Noise(3000, 1000), maxBytes: int.MaxValue)!;
        Assert.Equal((2048, 683), Size(png));
    }

    [Fact]
    public void AHeavyPictureIsScaledUntilItFitsTheByteLimit()
    {
        var png = ImageTools.ToAttachmentPng(Noise(1600, 1600), maxBytes: 2_000_000)!;
        Assert.True(png.Length <= 2_000_000, $"{png.Length} bytes");
        Assert.True(Size(png).Width < 1600);
    }

    [Fact]
    public void ASmallPictureKeepsItsSize() => Assert.Equal((300, 200), Size(ImageTools.ToAttachmentPng(Noise(300, 200))!));
}
