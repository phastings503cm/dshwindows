using System.IO.Compression;
using System.Text;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace Vision.Core.Tests;

/// <summary>Real documents made in the test: PDFs written with PdfPig (text pages and image-only scans) and Office files
/// zipped from their XML parts — the bytes a user's file has, not stand-ins.</summary>
internal static class TestDocuments
{
    /// <summary>A PDF whose pages carry the given lines as real text.</summary>
    public static byte[] Pdf(params string[][] pages)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        foreach (var lines in pages)
        {
            var page = builder.AddPage(PageSize.A4);
            for (var i = 0; i < lines.Length; i++) page.AddText(lines[i], 12, new PdfPoint(50, 780 - i * 18), font);
        }
        return builder.Build();
    }

    /// <summary>A PDF of <paramref name="pages"/> pages, each just a picture (a scan with no text layer).</summary>
    public static byte[] ScanPdf(int pages = 1)
    {
        var builder = new PdfDocumentBuilder();
        for (var i = 0; i < pages; i++)
        {
            var page = builder.AddPage(PageSize.A4);
            page.AddPng(TestImages.Png(40, 56, (byte)(10 * i), 20, 30), new PdfRectangle(0, 0, 595, 842));
        }
        return builder.Build();
    }

    public static byte[] Zip(params (string Name, string Content)[] parts)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in parts)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
                writer.Write(content);
            }
        }
        return stream.ToArray();
    }

    private const string W = "xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"";

    /// <summary>A .docx with a title, a heading, a paragraph with a tab and a break, a list item and a table.</summary>
    public static byte[] Docx() => Zip(("[Content_Types].xml", "<Types/>"), ("word/document.xml",
        $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:document {W}><w:body>" +
        "<w:p><w:pPr><w:pStyle w:val=\"Title\"/></w:pPr><w:r><w:t>Stock service</w:t></w:r></w:p>" +
        "<w:p><w:pPr><w:pStyle w:val=\"Heading2\"/><w:tabs><w:tab w:val=\"left\" w:pos=\"720\"/></w:tabs></w:pPr><w:r><w:t>Reservations</w:t></w:r></w:p>" +
        "<w:p><w:r><w:t xml:space=\"preserve\">Hold for </w:t></w:r><w:r><w:t>15</w:t><w:tab/><w:t>minutes</w:t><w:br/><w:t>then release.</w:t></w:r></w:p>" +
        "<w:p><w:pPr><w:numPr><w:ilvl w:val=\"0\"/><w:numId w:val=\"1\"/></w:numPr></w:pPr><w:r><w:t>Never below zero</w:t></w:r></w:p>" +
        "<w:p/><w:p/>" +
        "<w:tbl><w:tr><w:tc><w:p><w:r><w:t>SKU</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>Level</w:t></w:r></w:p></w:tc></w:tr>" +
        "<w:tr><w:tc><w:p><w:r><w:t>A-1</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>12</w:t></w:r></w:p></w:tc></w:tr></w:tbl>" +
        "</w:body></w:document>"));

    private const string A = "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\"";
    private const string R = "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"";

    private static string Slide(params string[] paragraphs) =>
        $"<p:sld {A}><p:cSld><p:spTree><p:sp><p:txBody>" +
        string.Concat(paragraphs.Select(t => $"<a:p><a:r><a:t>{t}</a:t></a:r></a:p>")) +
        "</p:txBody></p:sp></p:spTree></p:cSld></p:sld>";

    /// <summary>A .pptx whose presentation order (slide2 first) differs from the part numbers.</summary>
    public static byte[] Pptx() => Zip(
        ("ppt/presentation.xml", $"<p:presentation {A} {R}><p:sldIdLst><p:sldId id=\"256\" r:id=\"rId7\"/><p:sldId id=\"257\" r:id=\"rId3\"/></p:sldIdLst></p:presentation>"),
        ("ppt/_rels/presentation.xml.rels",
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId3\" Type=\"slide\" Target=\"slides/slide1.xml\"/><Relationship Id=\"rId7\" Type=\"slide\" Target=\"slides/slide2.xml\"/></Relationships>"),
        ("ppt/slides/slide1.xml", Slide("Second in the deck", "Numbers")),
        ("ppt/slides/slide2.xml", Slide("Opening slide", "Agenda")));

    private const string S = "xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"";

    /// <summary>A .xlsx with two named sheets: shared strings, numbers, a boolean, an inline string and a gap.</summary>
    public static byte[] Xlsx() => Zip(
        ("xl/workbook.xml", $"<workbook {S} {R}><sheets><sheet name=\"Totals\" sheetId=\"1\" r:id=\"rId1\"/><sheet name=\"Notes\" sheetId=\"2\" r:id=\"rId2\"/></sheets></workbook>"),
        ("xl/_rels/workbook.xml.rels",
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"worksheet\" Target=\"worksheets/sheet1.xml\"/><Relationship Id=\"rId2\" Type=\"worksheet\" Target=\"/xl/worksheets/sheet2.xml\"/></Relationships>"),
        ("xl/sharedStrings.xml", $"<sst {S}><si><t>Region</t></si><si><t>Revenue</t></si><si><r><t>No</t></r><r><t>rth</t></r></si></sst>"),
        ("xl/worksheets/sheet1.xml",
            $"<worksheet {S}><sheetData><row r=\"1\"><c r=\"A1\" t=\"s\"><v>0</v></c><c r=\"B1\" t=\"s\"><v>1</v></c></row>" +
            "<row r=\"2\"><c r=\"A2\" t=\"s\"><v>2</v></c><c r=\"B2\"><v>1250.5</v></c><c r=\"D2\" t=\"b\"><v>1</v></c></row></sheetData></worksheet>"),
        ("xl/worksheets/sheet2.xml", $"<worksheet {S}><sheetData><row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>Checked by Pat</t></is></c></row></sheetData></worksheet>"));

    /// <summary>An OpenDocument text file with a heading and two paragraphs.</summary>
    public static byte[] Odt() => Zip(("content.xml",
        "<office:document-content xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\" xmlns:text=\"urn:oasis:names:tc:opendocument:xmlns:text:1.0\">" +
        "<office:body><office:text><text:h text:outline-level=\"2\">Minutes</text:h><text:p>Present:<text:s text:c=\"2\"/>Pat</text:p>" +
        "<text:p>Next<text:tab/>Friday</text:p></office:text></office:body></office:document-content>"));
}
