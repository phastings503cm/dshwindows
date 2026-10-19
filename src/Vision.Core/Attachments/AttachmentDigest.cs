using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;

namespace Vision.Core;

// MARK: - What Vision read out of an attached file
//
// A model server only takes the content types it was built for: an OpenAI-compatible server (vLLM, SGLang, llama.cpp,
// Ollama, a Spark Swapper) takes text and — on a vision model — `image_url`; none of them takes the `file` part Vision
// used to send for a PDF or a Markdown file (vLLM: "Unknown part type: file" / "Unsupported chat content part type";
// llama.cpp: "unsupported content[].type"). So the harness reads every attachment itself when it is attached: text files
// as they are, PDFs page by page, Office files from their XML, pictures and scans with OCR where the machine has it. What
// goes on the wire is then text, or an image on a route that takes images, or a native document block where the provider
// has one (Bedrock). See AttachmentWire for the per-route choice and AttachmentPrep for long files. Vision for macOS
// (VisionHarnessCore/Attachments) does the same with the same numbers and wording.

/// <summary>The kind of file an attachment is, as the composer chip and the model's header name it.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DigestFormat>))]
public enum DigestFormat
{
    [JsonStringEnumMemberName("text")] Text,
    [JsonStringEnumMemberName("markdown")] Markdown,
    [JsonStringEnumMemberName("csv")] Csv,
    [JsonStringEnumMemberName("tsv")] Tsv,
    [JsonStringEnumMemberName("json")] Json,
    [JsonStringEnumMemberName("yaml")] Yaml,
    [JsonStringEnumMemberName("xml")] Xml,
    [JsonStringEnumMemberName("html")] Html,
    [JsonStringEnumMemberName("log")] Log,
    [JsonStringEnumMemberName("code")] Code,
    [JsonStringEnumMemberName("pdf")] Pdf,
    [JsonStringEnumMemberName("word")] Word,
    [JsonStringEnumMemberName("powerpoint")] PowerPoint,
    [JsonStringEnumMemberName("excel")] Excel,
    [JsonStringEnumMemberName("rtf")] Rtf,
    [JsonStringEnumMemberName("image")] Image,
    [JsonStringEnumMemberName("pasted")] Pasted,
    [JsonStringEnumMemberName("binary")] Binary,
}

public static class DigestFormats
{
    /// <summary>The word the composer chip and the model's header use (the same on every platform).</summary>
    public static string Label(this DigestFormat format) => format switch
    {
        DigestFormat.Text => "Text",
        DigestFormat.Markdown => "Markdown",
        DigestFormat.Csv => "CSV",
        DigestFormat.Tsv => "TSV",
        DigestFormat.Json => "JSON",
        DigestFormat.Yaml => "YAML",
        DigestFormat.Xml => "XML",
        DigestFormat.Html => "HTML",
        DigestFormat.Log => "Log",
        DigestFormat.Code => "Code",
        DigestFormat.Pdf => "PDF",
        DigestFormat.Word => "Word",
        DigestFormat.PowerPoint => "PowerPoint",
        DigestFormat.Excel => "Excel",
        DigestFormat.Rtf => "Rich text",
        DigestFormat.Image => "Image",
        DigestFormat.Pasted => "Pasted text",
        _ => "File",
    };

    /// <summary>What <see cref="AttachmentDigest.Pages"/> counts for this format: pages, slides or sheets; null for the rest.</summary>
    public static (string One, string Many)? Unit(this DigestFormat format) => format switch
    {
        DigestFormat.Pdf => ("page", "pages"),
        DigestFormat.PowerPoint => ("slide", "slides"),
        DigestFormat.Excel => ("sheet", "sheets"),
        _ => null,
    };
}

/// <summary>What Vision read out of an attached file, worked out once when the file is attached.</summary>
public sealed record AttachmentDigest
{
    public DigestFormat Format { get; init; }
    /// <summary>Pages (PDF), slides (PowerPoint) or sheets (Excel); null for the rest.</summary>
    public int? Pages { get; init; }
    /// <summary>Lines in <see cref="Text"/> (see <see cref="AttachmentFormat.LineCount"/>).</summary>
    public int Lines { get; init; }
    /// <summary>The file's own size in bytes.</summary>
    public long Bytes { get; init; }
    /// <summary>Everything readable in the file, as text, with page / slide / sheet markers. Empty for a picture with no
    /// text in it or a file Vision can't read. Emptied once the message is prepared (the full text then lives in the
    /// chat's attachment store and the attachment carries <see cref="MessageAttachment.Text"/>).</summary>
    public string Text
    {
        get => _text;
        init
        {
            _text = value ?? "";
            TextLength = _text.Length;
        }
    }
    private readonly string _text = "";
    /// <summary><see cref="Text"/>'s length in UTF-16 units, kept when the text itself is emptied after the message is
    /// prepared. (Setting <see cref="Text"/> sets it; set this after it to keep a length.)</summary>
    public int TextLength { get; init; }
    /// <summary>Some or all of the text was read off pictures (OCR).</summary>
    public bool Ocr { get; init; }
    /// <summary>A PDF with no text layer (a scan).</summary>
    public bool Scanned { get; init; }
    /// <summary>A scanned PDF's first pages as pictures (PNG or JPEG), for models that take images (at most
    /// <see cref="PdfText.MaxPageImages"/>).</summary>
    public IReadOnlyList<PageImage> PageImages { get; init; } = [];
    /// <summary>Why the file couldn't be read, when it couldn't (damaged, password-protected, an unknown binary type).</summary>
    public string? Problem { get; init; }

    /// <summary>Whether there is any text to send.</summary>
    public bool Readable => Problem is null && TextLength > 0;

    /// <summary>"PDF · 42 pages", "Markdown · 1,204 lines", "Pasted text · 10,214 lines · 412 KB", "Image", "File · 2.1 MB".
    /// The chip shows it followed by how the file will be sent (<see cref="AttachmentDeliveries.Label"/>).</summary>
    public string Summary
    {
        get
        {
            var parts = new List<string> { Format.Label() };
            switch (Format)
            {
                case DigestFormat.Pasted:
                    parts.Add(AttachmentFormat.Count(Lines, "line", "lines"));
                    parts.Add(AttachmentFormat.Bytes(Bytes));
                    break;
                case DigestFormat.Image:
                    break;
                case DigestFormat.Binary:
                    parts.Add(AttachmentFormat.Bytes(Bytes));
                    break;
                default:
                    if (Pages is { } pages && Format.Unit() is { } unit) parts.Add(AttachmentFormat.Count(pages, unit.One, unit.Many));
                    else parts.Add(AttachmentFormat.Count(Lines, "line", "lines"));
                    break;
            }
            if (Scanned) parts.Add("scanned");
            return string.Join(" · ", parts);
        }
    }

    /// <summary>The digest with its text emptied (it now lives elsewhere); the counts stay.</summary>
    public AttachmentDigest WithoutText() => this with { Text = "", TextLength = TextLength };
}

/// <summary>One picture of a scanned page.</summary>
public sealed record PageImage(string Mime, byte[] Data);

// MARK: - Counting and formatting (pinned: Vision for macOS prints the same)

public static class AttachmentFormat
{
    /// <summary>Lines in a text: one per line break, plus one for a last line without a break. "" → 0, "a" → 1,
    /// "a\nb" → 2, "a\nb\n" → 2. (Text is normalised to "\n" line breaks before it is counted.)</summary>
    public static int LineCount(string text)
    {
        if (text.Length == 0) return 0;
        var breaks = text.AsSpan().Count('\n');
        return text[^1] == '\n' ? breaks : breaks + 1;
    }

    /// <summary>"\r\n" and a lone "\r" become "\n".</summary>
    public static string NormalizeNewlines(string text) =>
        text.Contains('\r') ? text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n') : text;

    /// <summary>10214 → "10,214" — always a comma every three digits, whatever the locale.</summary>
    public static string Grouped(long n) => n.ToString("#,0", CultureInfo.InvariantCulture);

    /// <summary>"1 line", "10,214 lines".</summary>
    public static string Count(long n, string one, string many) => $"{Grouped(n)} {(n == 1 ? one : many)}";

    /// <summary>"1 byte", "900 bytes", "412 KB" (bytes / 1024, rounded), "2.1 MB" (bytes / 1,048,576, one decimal).</summary>
    public static string Bytes(long n)
    {
        if (n < 1024) return n == 1 ? "1 byte" : $"{Grouped(n)} bytes";
        if (n < 1024 * 1024) return $"{Grouped((long)Math.Round(n / 1024.0, MidpointRounding.AwayFromZero))} KB";
        var mb = Math.Round(n / 1_048_576.0 * 10, MidpointRounding.AwayFromZero) / 10;
        return mb.ToString("0.0", CultureInfo.InvariantCulture) + " MB";
    }

    /// <summary>Tokens for a length in UTF-16 units, at four per token, rounded up.</summary>
    public static long Tokens(long length) => (length + AttachmentBudget.CharsPerToken - 1) / AttachmentBudget.CharsPerToken;
}

// MARK: - What a sent message says it carried

/// <summary>One attachment as the transcript shows it under the user's message, and as the chat's log keeps it — the
/// file's name and summary, not its content. <see cref="Stored"/> is the name its text is saved under with the chat
/// (<see cref="AttachmentStore"/>), so a reopened chat can tell the model how to read it again. JSON:
/// <c>{"name", "label", "image", "stored"}</c>, as Vision for macOS writes it.</summary>
public sealed record AttachmentBadge
{
    public string Name { get; init; } = "";
    public string Label { get; init; } = "";
    public bool Image { get; init; }
    public string? Stored { get; init; }

    public AttachmentBadge() { }

    public AttachmentBadge(string name, string label, bool image = false, string? stored = null)
    {
        Name = name;
        Label = label;
        Image = image;
        Stored = stored;
    }

    /// <summary>The badge for an attachment (prepared or not).</summary>
    public static AttachmentBadge Of(MessageAttachment attachment, string? stored = null) =>
        new(attachment.Name, attachment.Digest?.Summary ?? (attachment.Kind == AttachmentKind.Image ? "Image" : "File"),
            attachment.Kind == AttachmentKind.Image, stored);

    /// <summary>A large paste: its badge names it by its label alone ("Pasted text · 10,214 lines · 412 KB").</summary>
    [JsonIgnore] public bool IsPasted => Label.StartsWith(DigestFormat.Pasted.Label(), StringComparison.Ordinal);

    /// <summary>The badge's tooltip: where its text is saved, or its name.</summary>
    [JsonIgnore]
    public string Tooltip => Stored is { } stored ? $"Saved with this chat as {stored}; the model can read it again with {AttachmentPrep.ToolName}." : Name;

    /// <summary>What a rebuilt conversation (after the app restarts) tells the model about the attachment.</summary>
    [JsonIgnore]
    public string ReplayNote => Stored is { } stored
        ? $"[Attached: {Name} — {Label}. Its text is saved with this chat: read it with {AttachmentPrep.ToolName} {{\"name\": \"{stored}\"}} if you need it again.]"
        : $"[Attached: {Name} — {Label}. Its content was sent with this message and isn't kept.]";
}

internal static class TextScan
{
    /// <summary>Visible (non-white-space) characters in a text.</summary>
    public static int Visible(string text)
    {
        var count = 0;
        foreach (var c in text) if (!char.IsWhiteSpace(c)) count++;
        return count;
    }

    /// <summary>Lines joined, runs of blank lines collapsed to one, no blank lines at either end.</summary>
    public static string Tidy(IEnumerable<string> lines)
    {
        var output = new List<string>();
        foreach (var line in lines)
        {
            if (line.Length == 0 && (output.Count == 0 || output[^1].Length == 0)) continue;
            output.Add(line);
        }
        while (output.Count > 0 && output[^1].Length == 0) output.RemoveAt(output.Count - 1);
        return string.Join("\n", output);
    }

    /// <summary>A text with white space runs collapsed to single spaces.</summary>
    public static string Collapse(string text)
    {
        var builder = new StringBuilder(text.Length);
        var space = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                space = builder.Length > 0;
                continue;
            }
            if (space) builder.Append(' ');
            space = false;
            builder.Append(c);
        }
        return builder.ToString();
    }
}
