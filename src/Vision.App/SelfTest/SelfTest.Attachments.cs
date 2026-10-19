using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Vision.App.Model;
using Vision.App.Views;
using Vision.App.Views.Chat;
using Vision.Core;
using MessageRole = Vision.App.Model.MessageRole;

namespace Vision.App;

// MARK: - Attachments: what the composer says it will send, and what a sent message carried

public sealed partial class SelfTest
{
    private async Task CaptureAttachments(MainWindow window, AppModel model)
    {
        try
        {
            var folder = Path.Combine(Output, "attachments-demo");
            Directory.CreateDirectory(folder);
            var files = new[]
            {
                Write(folder, "Quarterly report.pdf", DemoPdf(["Quarterly report", "Revenue by region"], 3)),
                Write(folder, "release-notes.md", Encoding.UTF8.GetBytes("# Release notes\n\n- Faster start\n- Attachments are read by Vision\n")),
                Write(folder, "Stock spec.docx", DemoDocx(["Stock service", "Reservations expire after 15 minutes."])),
                Write(folder, "scanned invoice.pdf", DemoScanPdf("INVOICE 4711")),
            };

            var chat = model.NewChat();
            chat.Title = "Attachments";
            model.Mode = WorkspaceMode.Chat;
            model.Select(chat.Id);
            await Settle();
            var view = window.SelfTestChatView;
            foreach (var file in files) view.SelfTestStage(file);
            view.AddPasted(ProbeText(ProbeLines));
            view.SelfTestComposer.Text = "Summarise the report and tell me what the log says about slow requests.";
            await Settle(2500);
            foreach (var label in view.SelfTestChipLabels) Note($"chip: {label}");
            Capture(window, "attachments-composer");

            await ShowAndCapture(TextViewerWindow.ForSelfTest("Pasted text", ProbeText(400), editable: true), "pasted-text-viewer");

            // As sent: the files prepared for the route (read, split, saved with the chat), and the badges under the message.
            var attachments = files.Select(f => new MessageAttachment(AttachmentKind.File, Path.GetFileName(f), File.ReadAllBytes(f)))
                .Append(PastedText.Attachment(ProbeText(ProbeLines))).ToList();
            var route = model.Host.AttachmentRouteFor();
            var prepared = await Task.Run(() => AttachmentPrep.Prepare(attachments, 262_144, model.Host.AttachmentStoreFor(chat.Id)));
            var badges = prepared.Badges.Zip(prepared.Attachments,
                (badge, attachment) => badge with { Label = badge.Label + " · " + AttachmentWire.Delivery(attachment, route).Label() }).ToList();
            foreach (var badge in badges) Note($"sent: {badge.Name} — {badge.Label}{(badge.Stored is null ? "" : $" (saved as {badge.Stored})")}");
            var wire = AttachmentWire.Parts(prepared.Attachments, route);
            Note($"wire on {model.Config.ActiveProvider?.DisplayName}: {string.Join(", ", wire.Select(p => p.GetType().Name))}");
            view.SelfTestComposer.Clear();
            chat.AppendMessage(MessageRole.User, "Summarise the report and tell me what the log says about slow requests.", attachments: badges);
            chat.AppendMessage(MessageRole.Assistant,
                "The report covers three regions; revenue grew in two. The log's slow requests all go through `/api/stock/reserve` — " +
                "I read parts 1 and 13 of the pasted text with `read_attachment`.");
            await Settle(800);
            Capture(window, "attachments-sent");
        }
        catch (Exception error)
        {
            Fail("attachments", error);
        }
    }

    private static string Write(string folder, string name, byte[] data)
    {
        var path = Path.Combine(folder, name);
        File.WriteAllBytes(path, data);
        return path;
    }

    /// <summary>A real PDF with a text layer: a title and a few lines on each page.</summary>
    internal static byte[] DemoPdf(IReadOnlyList<string> headings, int pages)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        for (var p = 0; p < pages; p++)
        {
            var page = builder.AddPage(PageSize.A4);
            page.AddText(headings[p % headings.Count], 18, new PdfPoint(50, 780), font);
            for (var line = 0; line < 12; line++)
                page.AddText($"Page {p + 1}, line {line + 1}: north {100 + line * 7} units, south {80 + line * 5} units.", 11, new PdfPoint(50, 740 - line * 18), font);
        }
        return builder.Build();
    }

    /// <summary>A PDF with no text layer: one picture of a page, as a scanner makes it.</summary>
    private static byte[] DemoScanPdf(string text)
    {
        var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(PageSize.A4);
        page.AddPng(RenderPng(text, 1240, 1754), new PdfRectangle(0, 0, 595, 842));
        return builder.Build();
    }

    /// <summary>Black text on white, as a PNG.</summary>
    private static byte[] RenderPng(string text, int width, int height)
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 96, Brushes.Black, 1.0);
            context.DrawText(formatted, new Point(120, 200));
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>A minimal real .docx: a heading and paragraphs.</summary>
    private static byte[] DemoDocx(IReadOnlyList<string> paragraphs)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Part(string name, string xml)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
                writer.Write(xml);
            }
            Part("[Content_Types].xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
                "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/></Types>");
            Part("_rels/.rels",
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>");
            var body = new StringBuilder();
            for (var i = 0; i < paragraphs.Count; i++)
            {
                var style = i == 0 ? "<w:pPr><w:pStyle w:val=\"Heading1\"/></w:pPr>" : "";
                body.Append($"<w:p>{style}<w:r><w:t xml:space=\"preserve\">{System.Security.SecurityElement.Escape(paragraphs[i])}</w:t></w:r></w:p>");
            }
            Part("word/document.xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?><w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>" +
                body + "</w:body></w:document>");
        }
        return stream.ToArray();
    }
}
