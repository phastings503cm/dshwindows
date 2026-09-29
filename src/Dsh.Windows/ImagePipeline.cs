using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Dsh.Windows;

/// <summary>An image ready for the model: encoded bytes, the attachment name (whose extension
/// sets the MIME type) and the pixel size sent.</summary>
public sealed record PreparedImage(byte[] Data, string Name, int Width, int Height)
{
    /// <summary>The source's size when it was downscaled, else null.</summary>
    public (int Width, int Height)? ScaledFrom { get; init; }
}

/// <summary>Cap what travels to the model: a 4K screenshot is several MB and thousands of vision
/// tokens; 1600 px long-edge PNGs read fine and stay near 200-400 KB. WPF imaging (WIC) does the
/// decoding, scaling and encoding, so there is no extra dependency. Everything here runs
/// synchronously on the calling thread and hands back bytes, never a bitmap object.</summary>
public static class ImagePipeline
{
    /// <summary>Long edge for screenshots and viewed files.</summary>
    public const int LongEdge = 1600;
    /// <summary>Long edge for screen_watch frames (several ride one message).</summary>
    public const int WatchLongEdge = 1280;

    /// <summary>The size that fits <paramref name="longEdge"/>, keeping the aspect ratio; images
    /// already small enough keep their size.</summary>
    public static (int Width, int Height) Fit(int width, int height, int longEdge)
    {
        var longest = Math.Max(width, height);
        if (longest <= longEdge || longest <= 0 || longEdge <= 0) return (width, height);
        var scale = longEdge / (double)longest;
        return (Math.Clamp((int)Math.Round(width * scale), 1, longEdge), Math.Clamp((int)Math.Round(height * scale), 1, longEdge));
    }

    /// <summary>A screen capture as a (downscaled) PNG.</summary>
    internal static PreparedImage Png(Capture capture, int longEdge, string name)
    {
        var source = BitmapSource.Create(capture.Width, capture.Height, 96, 96, PixelFormats.Bgr32, null, capture.Pixels,
                                         capture.Width * 4);
        var scaled = Scale(source, longEdge);
        return new PreparedImage(Encode(scaled, jpeg: false), name, scaled.PixelWidth, scaled.PixelHeight)
        {
            ScaledFrom = scaled == source ? null : (capture.Width, capture.Height),
        };
    }

    /// <summary>An image file's bytes, ready to show: kept as is when it is a PNG or JPEG that
    /// already fits, otherwise decoded, downscaled and re-encoded (JPEG stays JPEG; everything
    /// else becomes PNG so every vision model can read it). Null when Windows can't decode it.</summary>
    public static PreparedImage? PrepareFile(byte[] data, string fileName, int longEdge = LongEdge)
    {
        try
        {
            using var stream = new MemoryStream(data, writable: false);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0) return null;
            BitmapSource frame = decoder.Frames[0];
            var kind = Sniff(data);
            var (width, height) = Fit(frame.PixelWidth, frame.PixelHeight, longEdge);
            var fits = width == frame.PixelWidth && height == frame.PixelHeight;
            if (fits && kind is ImageKind.Png or ImageKind.Jpeg)
                return new PreparedImage(data, fileName, frame.PixelWidth, frame.PixelHeight);
            var scaled = Scale(frame, longEdge);
            var jpeg = kind == ImageKind.Jpeg;
            var name = Path.ChangeExtension(fileName, jpeg ? ".jpg" : ".png");
            return new PreparedImage(Encode(scaled, jpeg), name, scaled.PixelWidth, scaled.PixelHeight)
            {
                ScaledFrom = fits ? null : (frame.PixelWidth, frame.PixelHeight),
            };
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException or IOException
                                       or InvalidOperationException or COMException or OverflowException)
        {
            return null;
        }
    }

    /// <summary>Downscale to fit <paramref name="longEdge"/> (Fant interpolation, WIC's
    /// high-quality downscaler); the source itself when it already fits.</summary>
    internal static BitmapSource Scale(BitmapSource source, int longEdge)
    {
        var (width, height) = Fit(source.PixelWidth, source.PixelHeight, longEdge);
        if (width == source.PixelWidth && height == source.PixelHeight) return source;
        var scaled = new TransformedBitmap(source, new ScaleTransform(width / (double)source.PixelWidth,
                                                                      height / (double)source.PixelHeight));
        scaled.Freeze();
        return scaled;
    }

    internal static byte[] Encode(BitmapSource source, bool jpeg)
    {
        // Normalise the pixel format first: CMYK JPEGs, indexed GIFs and 32bpp BGRX captures are
        // not all accepted by every encoder.
        var format = jpeg ? PixelFormats.Bgr24 : source.Format == PixelFormats.Bgr32 ? PixelFormats.Bgr24 : PixelFormats.Bgra32;
        if (source.Format != format) source = new FormatConvertedBitmap(source, format, null, 0);
        BitmapEncoder encoder = jpeg ? new JpegBitmapEncoder { QualityLevel = 90 } : new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    internal enum ImageKind { Other, Png, Jpeg, Gif }

    /// <summary>The container format from the file's first bytes (extensions lie).</summary>
    internal static ImageKind Sniff(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 8 && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47) return ImageKind.Png;
        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF) return ImageKind.Jpeg;
        if (data.Length >= 6 && data[0] == 0x47 && data[1] == 0x49 && data[2] == 0x46) return ImageKind.Gif;
        return ImageKind.Other;
    }
}
