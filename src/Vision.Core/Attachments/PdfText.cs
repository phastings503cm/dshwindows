using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using UglyToad.PdfPig.Exceptions;
using UglyToad.PdfPig.Tokens;

namespace Vision.Core;

// MARK: - Text out of PDFs
//
// A PDF's own text layer, page by page ("--- Page N of M ---" before each), through PdfPig (Apache-2.0). A page without
// one (a scan, a photo of a page) is read with the machine's OCR, when it has one, from the picture the page is made of;
// when the whole file is a scan, its first pages' pictures are also kept so a model that takes images can look at them
// instead. Vision for macOS does the same with PDFKit and Apple's Vision framework, with the same numbers and markers.

public static class PdfText
{
    /// <summary>Pages of a scan kept as pictures for a vision model (Bedrock takes at most 20 images in a message).</summary>
    public const int MaxPageImages = 20;
    /// <summary>Pages of a scan read with OCR; the rest are listed as not read.</summary>
    public const int MaxOcrPages = 100;
    /// <summary>A page "has text" when its text layer holds at least this many non-blank characters.</summary>
    public const int MinPageCharacters = 8;

    public sealed record Result(string Text, int Pages, bool Ocr, bool Scanned, IReadOnlyList<PageImage> PageImages, string? Problem);

    public static Result Extract(byte[] data, bool ocr = true)
    {
        PdfDocument document;
        try
        {
            document = PdfDocument.Open(data, new ParsingOptions { UseLenientParsing = true, SkipMissingFonts = true });
        }
        catch (PdfDocumentEncryptedException)
        {
            return new Result("", 0, false, false, [], "This PDF is password-protected, so Vision can't read it.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new Result("", 0, false, false, [], "Vision couldn't open this PDF (it may be damaged).");
        }

        using (document)
        {
            int count;
            try
            {
                count = document.NumberOfPages;
            }
            catch (PdfDocumentEncryptedException)
            {
                return new Result("", 0, false, false, [], "This PDF is password-protected, so Vision can't read it.");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return new Result("", 0, false, false, [], "Vision couldn't open this PDF (it may be damaged).");
            }
            if (count == 0) return new Result("", 0, false, false, [], "Vision couldn't open this PDF (it may be damaged).");
            var texts = new string?[count];
            for (var index = 0; index < count; index++)
            {
                try
                {
                    var page = document.GetPage(index + 1);
                    var raw = AttachmentFormat.NormalizeNewlines(ContentOrderTextExtractor.GetText(page));
                    texts[index] = TextScan.Visible(raw) >= MinPageCharacters ? raw.Trim() : null;
                }
                catch (PdfDocumentEncryptedException)
                {
                    return new Result("", count, false, false, [], "This PDF is password-protected, so Vision can't read it.");
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    texts[index] = null; // a page PdfPig can't parse reads as one without text
                }
            }
            var scanned = count > 0 && texts.All(t => t is null);
            var engine = ocr ? AttachmentReader.Ocr : null;
            var usedOcr = false;
            var ocrBudget = MaxOcrPages;
            var pages = new List<string>(count);
            for (var index = 0; index < count; index++)
            {
                var body = texts[index];
                var skipped = false;
                if (body is null && engine is not null)
                {
                    if (ocrBudget > 0)
                    {
                        ocrBudget--;
                        if (PagePicture(document, index + 1) is { } picture && AttachmentReader.SafeOcr(engine, picture.Data) is { Length: > 0 } read)
                        {
                            body = AttachmentFormat.NormalizeNewlines(read);
                            usedOcr = true;
                        }
                    }
                    else
                    {
                        skipped = true;
                    }
                }
                var header = $"--- Page {index + 1} of {count} ---";
                pages.Add(header + "\n" + (body ?? (skipped ? $"(not read: past the first {MaxOcrPages} scanned pages)" : "(no text on this page)")));
            }
            var images = new List<PageImage>();
            if (scanned)
            {
                for (var index = 0; index < Math.Min(count, MaxPageImages); index++)
                {
                    if (PagePicture(document, index + 1) is { } picture) images.Add(picture);
                }
            }
            return new Result(string.Join("\n\n", pages), count, usedOcr, scanned, images, null);
        }
    }

    /// <summary>The picture a scanned page is made of: its largest image, as a PNG (or the JPEG it holds); null when it has
    /// none Vision can hand on (JBIG2 and CCITT faxes, say).</summary>
    internal static PageImage? PagePicture(PdfDocument document, int number)
    {
        try
        {
            var page = document.GetPage(number);
            var largest = page.GetImages()
                .Where(i => !i.IsImageMask && i.WidthInSamples > 0 && i.HeightInSamples > 0)
                .OrderByDescending(i => (long)i.WidthInSamples * i.HeightInSamples)
                .FirstOrDefault();
            if (largest is null) return null;
            if (IsJpeg(largest) && largest.RawMemory.Length > 2 && largest.RawMemory.Span[0] == 0xFF && largest.RawMemory.Span[1] == 0xD8)
                return new PageImage("image/jpeg", largest.RawMemory.ToArray());
            return largest.TryGetPng(out var png) && png is { Length: > 0 } ? new PageImage("image/png", png) : null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>Whether the image's only filter is DCTDecode: its raw bytes are then a JPEG file.</summary>
    private static bool IsJpeg(IPdfImage image)
    {
        if (!image.ImageDictionary.TryGet(NameToken.Filter, out var filter)) return false;
        return filter switch
        {
            NameToken name => name.Data is "DCTDecode" or "DCT",
            ArrayToken { Length: 1 } array => array.Data[0] is NameToken only && only.Data is "DCTDecode" or "DCT",
            _ => false,
        };
    }
}
