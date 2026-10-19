using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Vision.App.Model;

/// <summary>Small copies of what a tool captured, for the transcript. The model gets the
/// full-size image; the UI only needs enough to recognise it, and a long debugging session must not
/// hold hundreds of full screenshots.</summary>
public static class ImageTools
{
    public static BitmapSource? Thumbnail(byte[] data, int maxEdge = 720)
    {
        try
        {
            using var stream = new MemoryStream(data);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            BitmapSource frame = decoder.Frames[0];
            var scale = Math.Min(1.0, maxEdge / (double)Math.Max(frame.PixelWidth, frame.PixelHeight));
            if (scale < 1.0) frame = new TransformedBitmap(frame, new ScaleTransform(scale, scale));
            frame.Freeze();
            return frame;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static BitmapSource? Load(byte[] data)
    {
        try
        {
            using var stream = new MemoryStream(data);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Re-encode any image the user attaches as PNG so every vision model can read it.</summary>
    public static byte[]? ToPng(BitmapSource source)
    {
        try
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return stream.ToArray();
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static byte[]? ToPng(byte[] data) => Load(data) is { } image ? ToPng(image) : null;

    /// <summary>An attached picture as a PNG every model server takes: its longest side at most <paramref name="maxSide"/>
    /// pixels and the file under <paramref name="maxBytes"/> (Bedrock refuses images over 3.75 MB or 8,000 px) — the same
    /// limits as Vision for macOS. Null when it isn't a picture.</summary>
    public static byte[]? ToAttachmentPng(BitmapSource source, int maxSide = 2048, int maxBytes = 3_750_000)
    {
        var side = Math.Min(maxSide, Math.Max(source.PixelWidth, source.PixelHeight));
        while (side >= 256)
        {
            var scale = Math.Min(1.0, side / (double)Math.Max(source.PixelWidth, source.PixelHeight));
            BitmapSource frame = scale < 1.0 ? new TransformedBitmap(source, new ScaleTransform(scale, scale)) : source;
            if (ToPng(frame) is not { } png) return null;
            if (png.Length <= maxBytes) return png;
            side = side * 3 / 4;
        }
        return null;
    }

    public static byte[]? ToAttachmentPng(byte[] data) => Load(data) is { } image ? ToAttachmentPng(image) : null;
}
