using System.Text;

namespace Vision.Core.Tests;

/// <summary>What Vision reads out of each kind of attached file, and how it names it — the counts and wording pinned to
/// match Vision for macOS.</summary>
[Collection(nameof(OcrEngineCollection))]
public sealed class AttachmentReadingTests
{
    // MARK: Counting and formatting

    [Theory]
    [InlineData("", 0)]
    [InlineData("a", 1)]
    [InlineData("a\nb", 2)]
    [InlineData("a\nb\n", 2)]
    [InlineData("\n", 1)]
    public void LinesAreCountedLikeTheMac(string text, int lines) => Assert.Equal(lines, AttachmentFormat.LineCount(text));

    [Theory]
    [InlineData(1, "1 byte")]
    [InlineData(900, "900 bytes")]
    [InlineData(1023, "1,023 bytes")]
    [InlineData(421_888, "412 KB")]
    [InlineData(1_048_575, "1,024 KB")]
    [InlineData(2_202_009, "2.1 MB")]
    public void SizesReadTheSameOnBothPlatforms(long bytes, string expected) => Assert.Equal(expected, AttachmentFormat.Bytes(bytes));

    [Fact]
    public void NumbersAlwaysGroupWithCommas()
    {
        var saved = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Assert.Equal("10,214 lines", AttachmentFormat.Count(10_214, "line", "lines"));
            Assert.Equal("1 page", AttachmentFormat.Count(1, "page", "pages"));
            Assert.Equal("2.1 MB", AttachmentFormat.Bytes(2_202_009));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = saved;
        }
    }

    [Fact]
    public void SummariesNameTheFormatAndItsSize()
    {
        Assert.Equal("PDF · 42 pages", new AttachmentDigest { Format = DigestFormat.Pdf, Pages = 42, Lines = 900 }.Summary);
        Assert.Equal("PDF · 3 pages · scanned", new AttachmentDigest { Format = DigestFormat.Pdf, Pages = 3, Scanned = true }.Summary);
        Assert.Equal("Markdown · 1,204 lines", new AttachmentDigest { Format = DigestFormat.Markdown, Lines = 1204 }.Summary);
        Assert.Equal("Pasted text · 10,214 lines · 412 KB", new AttachmentDigest { Format = DigestFormat.Pasted, Lines = 10_214, Bytes = 421_888 }.Summary);
        Assert.Equal("PowerPoint · 9 slides", new AttachmentDigest { Format = DigestFormat.PowerPoint, Pages = 9 }.Summary);
        Assert.Equal("Excel · 1 sheet", new AttachmentDigest { Format = DigestFormat.Excel, Pages = 1 }.Summary);
        Assert.Equal("Image", new AttachmentDigest { Format = DigestFormat.Image }.Summary);
        Assert.Equal("File · 2.1 MB", new AttachmentDigest { Format = DigestFormat.Binary, Bytes = 2_202_009 }.Summary);
    }

    // MARK: Text files

    [Theory]
    [InlineData("notes.md", DigestFormat.Markdown)]
    [InlineData("data.CSV", DigestFormat.Csv)]
    [InlineData("rows.tsv", DigestFormat.Tsv)]
    [InlineData("config.json", DigestFormat.Json)]
    [InlineData("ci.yml", DigestFormat.Yaml)]
    [InlineData("App.xaml", DigestFormat.Xml)]
    [InlineData("index.html", DigestFormat.Html)]
    [InlineData("server.log", DigestFormat.Log)]
    [InlineData("Program.cs", DigestFormat.Code)]
    [InlineData("build.ps1", DigestFormat.Code)]
    [InlineData("README", DigestFormat.Text)]
    [InlineData("Makefile", DigestFormat.Text)]
    [InlineData("notes.txt", DigestFormat.Text)]
    [InlineData("unknown.xyz", DigestFormat.Text)]
    public void TextFilesAreReadAsTextWithTheirFormat(string name, DigestFormat format)
    {
        var digest = AttachmentReader.Read(name, Encoding.UTF8.GetBytes("line one\r\nline two\r\n"), AttachmentKind.File);
        Assert.Equal(format, digest.Format);
        Assert.Equal("line one\nline two\n", digest.Text);
        Assert.Equal(2, digest.Lines);
        Assert.True(digest.Readable);
        Assert.Null(digest.Problem);
    }

    [Fact]
    public void ByteOrderMarksAndOldWindowsTextAreDecoded()
    {
        var utf16 = new byte[] { 0xFF, 0xFE }.Concat(Encoding.Unicode.GetBytes("Grüße")).ToArray();
        Assert.Equal("Grüße", AttachmentReader.Read("a.txt", utf16, AttachmentKind.File).Text);
        var bom = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("# Title")).ToArray();
        Assert.Equal("# Title", AttachmentReader.Read("a.md", bom, AttachmentKind.File).Text);
        // Notepad's "ANSI": not UTF-8, but a file named as text is read as Windows-1252.
        var ansi = new byte[] { 0x93, 0x63, 0x61, 0x66, 0xE9, 0x94 };
        Assert.Equal("“café”", AttachmentReader.Read("a.txt", ansi, AttachmentKind.File).Text);
        // An unknown file is text only when it decodes cleanly.
        Assert.Equal(DigestFormat.Binary, AttachmentReader.Read("a.bin", ansi, AttachmentKind.File).Format);
    }

    [Fact]
    public void BinaryFilesSayOnlyTheirNameIsSent()
    {
        var digest = AttachmentReader.Read("blob.bin", [0, 159, 146, 150], AttachmentKind.File);
        Assert.Equal(DigestFormat.Binary, digest.Format);
        Assert.False(digest.Readable);
        Assert.Equal("Vision can't read this kind of file, so only its name is sent.", digest.Problem);
        Assert.Equal("File · 4 bytes", digest.Summary);
    }

    [Theory]
    [InlineData("old.doc", "Vision can't read the old Word format (.doc); save it as .docx and attach that.")]
    [InlineData("old.xls", "Vision can't read the old Excel format (.xls); save it as .xlsx and attach that.")]
    public void OldOfficeFormatsSayHowToSendThem(string name, string problem) =>
        Assert.Equal(problem, AttachmentReader.Read(name, [0xD0, 0xCF, 0x11, 0xE0], AttachmentKind.File).Problem);

    // MARK: PDF

    [Fact]
    public void APdfIsReadPageByPageWithMarkers()
    {
        var pdf = TestDocuments.Pdf(["Quarterly report", "North 120 units"], ["Page two heading", "South 80 units"], ["Totals", "200 units"]);
        var digest = AttachmentReader.Read("report.pdf", pdf, AttachmentKind.File);
        Assert.Equal(DigestFormat.Pdf, digest.Format);
        Assert.Equal(3, digest.Pages);
        Assert.Null(digest.Problem);
        Assert.False(digest.Scanned);
        Assert.Equal("PDF · 3 pages", digest.Summary);
        Assert.StartsWith("--- Page 1 of 3 ---\nQuarterly report", digest.Text);
        Assert.Contains("North 120 units", digest.Text);
        Assert.Contains("\n\n--- Page 2 of 3 ---\nPage two heading", digest.Text);
        Assert.Contains("--- Page 3 of 3 ---\nTotals", digest.Text);
        Assert.Empty(digest.PageImages);
    }

    [Fact]
    public void APdfIsRecognisedByItsBytesWhateverItsName()
    {
        var digest = AttachmentReader.Read("download", TestDocuments.Pdf(["Hello from a PDF"]), AttachmentKind.File);
        Assert.Equal(DigestFormat.Pdf, digest.Format);
        Assert.Contains("Hello from a PDF", digest.Text);
    }

    [Fact]
    public void AScanKeepsItsPagePicturesAndSaysItHasNoText()
    {
        AttachmentReader.Ocr = null;
        var digest = AttachmentReader.Read("scan.pdf", TestDocuments.ScanPdf(2), AttachmentKind.File);
        Assert.True(digest.Scanned);
        Assert.Equal(2, digest.Pages);
        Assert.Equal("PDF · 2 pages · scanned", digest.Summary);
        Assert.Equal("--- Page 1 of 2 ---\n(no text on this page)\n\n--- Page 2 of 2 ---\n(no text on this page)", digest.Text);
        Assert.Equal(2, digest.PageImages.Count);
        Assert.All(digest.PageImages, p => Assert.Equal("image/png", p.Mime));
        Assert.All(digest.PageImages, p => Assert.True(p.Data.AsSpan().StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47 })));
        Assert.False(digest.Ocr);
    }

    [Fact]
    public void AScanIsReadWithOcrWhenTheMachineHasIt()
    {
        var engine = new FakeOcr("INVOICE 4711\nTotal 99.00");
        AttachmentReader.Ocr = engine;
        try
        {
            var digest = AttachmentReader.Read("scan.pdf", TestDocuments.ScanPdf(1), AttachmentKind.File);
            Assert.True(digest.Ocr);
            Assert.True(digest.Scanned);
            Assert.Equal("--- Page 1 of 1 ---\nINVOICE 4711\nTotal 99.00", digest.Text);
            Assert.Equal(1, engine.Calls);
            Assert.True(engine.LastImage.AsSpan().StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47 }));
            Assert.Equal("[Attached file: scan.pdf — PDF · 1 page · scanned · text read by OCR]", AttachmentPrep.Header("scan.pdf", digest));
        }
        finally
        {
            AttachmentReader.Ocr = null;
        }
    }

    [Fact]
    public void ADamagedPdfSaysSo()
    {
        var digest = AttachmentReader.Read("broken.pdf", Encoding.ASCII.GetBytes("%PDF-1.7\nthis is not a pdf"), AttachmentKind.File);
        Assert.Equal(DigestFormat.Pdf, digest.Format);
        Assert.False(digest.Readable);
        Assert.NotNull(digest.Problem);
    }

    // MARK: Office

    [Fact]
    public void AWordFileKeepsHeadingsListsAndTables()
    {
        var digest = AttachmentReader.Read("spec.docx", TestDocuments.Docx(), AttachmentKind.File);
        Assert.Equal(DigestFormat.Word, digest.Format);
        Assert.Equal("# Stock service\n## Reservations\nHold for 15\tminutes\nthen release.\n- Never below zero\n\nSKU | Level\nA-1 | 12", digest.Text);
        Assert.Equal("Word · 8 lines", digest.Summary);
    }

    [Fact]
    public void APowerPointFileFollowsThePresentationOrder()
    {
        var digest = AttachmentReader.Read("deck.pptx", TestDocuments.Pptx(), AttachmentKind.File);
        Assert.Equal(DigestFormat.PowerPoint, digest.Format);
        Assert.Equal(2, digest.Pages);
        Assert.Equal("--- Slide 1 of 2 ---\nOpening slide\nAgenda\n\n--- Slide 2 of 2 ---\nSecond in the deck\nNumbers", digest.Text);
        Assert.Equal("PowerPoint · 2 slides", digest.Summary);
    }

    [Fact]
    public void AnExcelFileIsOneTabSeparatedSectionPerSheet()
    {
        var digest = AttachmentReader.Read("totals.xlsx", TestDocuments.Xlsx(), AttachmentKind.File);
        Assert.Equal(DigestFormat.Excel, digest.Format);
        Assert.Equal(2, digest.Pages);
        Assert.Equal("--- Sheet 1 of 2: Totals ---\nRegion\tRevenue\nNorth\t1250.5\t\tTRUE\n\n--- Sheet 2 of 2: Notes ---\nChecked by Pat", digest.Text);
    }

    [Fact]
    public void AnOpenDocumentTextIsReadToo()
    {
        var digest = AttachmentReader.Read("minutes.odt", TestDocuments.Odt(), AttachmentKind.File);
        Assert.Equal(DigestFormat.Word, digest.Format);
        Assert.Equal("## Minutes\nPresent:  Pat\nNext\tFriday", digest.Text);
    }

    [Fact]
    public void ADamagedOfficeFileSaysSo()
    {
        var digest = AttachmentReader.Read("spec.docx", TestDocuments.Zip(("something.xml", "<a/>")), AttachmentKind.File);
        Assert.Equal("Vision couldn't read this Word file (it may be damaged).", digest.Problem);
    }

    [Fact]
    public void RichTextKeepsItsTextAndDropsItsTables()
    {
        var rtf = Encoding.ASCII.GetBytes(
            "{\\rtf1\\ansi\\deff0{\\fonttbl{\\f0 Calibri;}}{\\colortbl;\\red0\\green0\\blue0;}{\\*\\generator Word;}" +
            "\\pard\\b Caf\\'e9 menu\\b0\\par Espresso\\tab 2.50\\line Latte \\u8212? 3.00\\par{\\*\\bkmkstart x}End\\par}");
        var digest = AttachmentReader.Read("menu.rtf", rtf, AttachmentKind.File);
        Assert.Equal(DigestFormat.Rtf, digest.Format);
        Assert.Equal("Café menu\nEspresso\t2.50\nLatte — 3.00\nEnd", digest.Text);
    }

    // MARK: Pictures

    [Fact]
    public void APictureCarriesItsOcrTextForModelsWithoutVision()
    {
        AttachmentReader.Ocr = new FakeOcr("Error 0x80070005\nAccess is denied");
        try
        {
            var image = AttachmentReader.Digested(new MessageAttachment(AttachmentKind.Image, "error.png", TestImages.Png(4, 4)));
            Assert.Equal(DigestFormat.Image, image.Digest!.Format);
            Assert.True(image.Digest.Ocr);
            Assert.Equal("[Image: error.png — this model can't take images; the text in it, read by OCR:]\nError 0x80070005\nAccess is denied", image.Text);
        }
        finally
        {
            AttachmentReader.Ocr = null;
        }
        var blank = AttachmentReader.Digested(new MessageAttachment(AttachmentKind.Image, "photo.png", TestImages.Png(4, 4)));
        Assert.Equal("[Image: photo.png — this model can't take images, and no text was found in it]", blank.Text);
    }

    [Fact]
    public void AnOcrEngineThatThrowsReadsAsNoText()
    {
        AttachmentReader.Ocr = new FakeOcr(null);
        try
        {
            var digest = AttachmentReader.Read("shot.png", TestImages.Png(2, 2), AttachmentKind.Image);
            Assert.Equal("", digest.Text);
            Assert.False(digest.Ocr);
        }
        finally
        {
            AttachmentReader.Ocr = null;
        }
    }
}

/// <summary>A stand-in for Windows.Media.Ocr: returns a fixed text (or throws when given null) and remembers what it saw.</summary>
internal sealed class FakeOcr(string? text) : IOcrEngine
{
    public int Calls { get; private set; }
    public byte[] LastImage { get; private set; } = [];

    public string Read(byte[] image)
    {
        Calls++;
        LastImage = image;
        return text ?? throw new InvalidOperationException("OCR failed");
    }
}

/// <summary>Tests that set <see cref="AttachmentReader.Ocr"/> (a static) run one at a time.</summary>
[CollectionDefinition(nameof(OcrEngineCollection), DisableParallelization = true)]
public sealed class OcrEngineCollection;
