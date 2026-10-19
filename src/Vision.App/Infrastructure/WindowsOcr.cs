using System.Runtime.Versioning;
using Vision.Core;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace Vision.App.Infrastructure;

/// <summary>Text in pictures and scanned pages, read with Windows' own OCR (Windows.Media.Ocr, in the languages of the
/// user's profile). Vision for macOS uses Apple's Vision framework for the same job. On a Windows without an OCR language
/// installed there is none, and pictures go to models without vision as "no text was found".</summary>
[SupportedOSPlatform("windows10.0.10240.0")]
public sealed class WindowsOcr : IOcrEngine
{
    private readonly OcrEngine _engine;

    private WindowsOcr(OcrEngine engine) => _engine = engine;

    /// <summary>The engine for the user's languages; null when Windows has no OCR language for them (or no OCR at all).</summary>
    public static WindowsOcr? TryCreate()
    {
        try
        {
            return OcrEngine.TryCreateFromUserProfileLanguages() is { } engine ? new WindowsOcr(engine) : null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>"en-US" — the language it reads.</summary>
    public string Language => _engine.RecognizerLanguage.LanguageTag;

    public string Read(byte[] image) => ReadAsync(image).GetAwaiter().GetResult();

    private async Task<string> ReadAsync(byte[] image)
    {
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(image);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
        }
        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        // The engine refuses pictures larger than MaxImageDimension on either side: scale those down.
        var transform = new BitmapTransform();
        var max = OcrEngine.MaxImageDimension;
        if (decoder.PixelWidth > max || decoder.PixelHeight > max)
        {
            var scale = (double)max / Math.Max(decoder.PixelWidth, decoder.PixelHeight);
            transform.ScaledWidth = (uint)Math.Max(1, decoder.PixelWidth * scale);
            transform.ScaledHeight = (uint)Math.Max(1, decoder.PixelHeight * scale);
        }
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
            ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage);
        var result = await _engine.RecognizeAsync(bitmap);
        return string.Join("\n", result.Lines.Select(line => line.Text));
    }
}
