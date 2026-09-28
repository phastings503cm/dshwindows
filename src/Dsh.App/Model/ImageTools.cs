using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Dsh.App.Model;

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
}
