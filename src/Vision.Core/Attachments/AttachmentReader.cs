using System.Text;

namespace Vision.Core;

// MARK: - Reading an attachment

/// <summary>Reads the text in a picture. The app supplies one where the machine has OCR (Windows.Media.Ocr); without
/// one, pictures and scanned pages carry no text.</summary>
public interface IOcrEngine
{
    /// <summary>The text in an encoded picture (PNG, JPEG, BMP …), top to bottom, one line per recognised line; "" when
    /// there is none or the picture can't be read. Called off the UI thread.</summary>
    string Read(byte[] image);
}

/// <summary>Works out what an attached file is and reads its text: done once, off the UI thread, when the file is
/// attached (and again when a message is sent with an attachment nobody read yet).</summary>
public static class AttachmentReader
{
    /// <summary>The machine's OCR, when it has one (set by the app at start-up).</summary>
    public static IOcrEngine? Ocr { get; set; }

    /// <summary>Extensions read as text, by the format they are labelled with (the same list as Vision for macOS).</summary>
    internal static readonly IReadOnlyDictionary<string, DigestFormat> TextExtensions = BuildTextExtensions();

    private static Dictionary<string, DigestFormat> BuildTextExtensions()
    {
        var map = new Dictionary<string, DigestFormat>(StringComparer.OrdinalIgnoreCase);
        void Add(DigestFormat format, params string[] extensions)
        {
            foreach (var ext in extensions) map[ext] = format;
        }
        Add(DigestFormat.Markdown, "md", "markdown", "mdx", "mdc");
        Add(DigestFormat.Csv, "csv");
        Add(DigestFormat.Tsv, "tsv", "tab");
        Add(DigestFormat.Json, "json", "jsonl", "ndjson", "geojson", "ipynb", "jsonc");
        Add(DigestFormat.Yaml, "yaml", "yml");
        Add(DigestFormat.Xml, "xml", "plist", "xsd", "xsl", "xslt", "svg", "rss", "atom", "csproj", "props", "targets", "resx", "xaml");
        Add(DigestFormat.Html, "html", "htm", "xhtml");
        Add(DigestFormat.Log, "log", "out", "err");
        Add(DigestFormat.Text, "txt", "text", "rst", "adoc", "asciidoc", "org", "tex", "ini", "cfg", "conf", "env", "properties",
            "toml", "srt", "vtt", "license", "readme", "gitignore", "gitattributes", "editorconfig");
        Add(DigestFormat.Code, "swift", "py", "pyi", "js", "mjs", "cjs", "ts", "tsx", "jsx", "java", "kt", "kts", "c", "h", "cc",
            "cpp", "cxx", "hpp", "hh", "hxx", "m", "mm", "cs", "fs", "fsx", "vb", "go", "rs", "rb", "php", "pl",
            "pm", "lua", "r", "scala", "sh", "bash", "zsh", "fish", "ps1", "psm1", "psd1", "bat", "cmd", "sql",
            "graphql", "gql", "proto", "css", "scss", "sass", "less", "vue", "svelte", "dart", "ex", "exs",
            "erl", "hrl", "hs", "clj", "cljs", "groovy", "gradle", "tf", "hcl", "nix", "zig", "nim", "jl",
            "cmake", "mk", "make", "dockerfile", "diff", "patch", "asm", "s", "v", "sv", "vhd", "sol", "razor",
            "cshtml", "astro", "elm", "ml", "mli", "pas", "f90", "cob", "ada", "d", "cr", "rkt", "lisp", "el",
            "scm", "vim", "awk", "sed", "bicep", "ino");
        return map;
    }

    /// <summary>File names without a useful extension that are text.</summary>
    internal static readonly HashSet<string> TextNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "makefile", "dockerfile", "gemfile", "rakefile", "procfile", "license", "readme", "changelog", "authors", "notice",
        "copying", "podfile", "brewfile", "vagrantfile", "jenkinsfile", "cmakelists.txt",
    };

    internal static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "png", "jpg", "jpeg", "gif", "webp", "heic", "heif", "tif", "tiff", "bmp", "ico", "jp2",
    };

    /// <summary>The attachment with its digest filled in. Never throws.</summary>
    public static MessageAttachment Digested(MessageAttachment attachment, bool ocr = true)
    {
        if (attachment.Digest is not null) return attachment;
        var digest = Read(attachment.Name, attachment.Data, attachment.Kind, ocr);
        var output = attachment with { Digest = digest };
        if (output.Kind == AttachmentKind.Image) output = output with { Text = ImageText(output.Name, digest.Text) };
        return output;
    }

    /// <summary>A pasted block of text as an attachment ("Pasted text · 10,214 lines · 412 KB").</summary>
    public static MessageAttachment Pasted(string text, string name = "pasted-text.txt")
    {
        var normalized = AttachmentFormat.NormalizeNewlines(text);
        var data = TextUtil.Utf8NoBom.GetBytes(normalized);
        return new MessageAttachment(AttachmentKind.File, name, data)
        {
            Digest = new AttachmentDigest
            {
                Format = DigestFormat.Pasted,
                Lines = AttachmentFormat.LineCount(normalized),
                Bytes = data.Length,
                Text = normalized,
            },
        };
    }

    /// <summary>Read a file's text. Never throws: what can't be read says why in <see cref="AttachmentDigest.Problem"/>.</summary>
    public static AttachmentDigest Read(string name, byte[] data, AttachmentKind kind, bool ocr = true)
    {
        try
        {
            return ReadUnguarded(name, data, kind, ocr);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new AttachmentDigest
            {
                Format = FormatGuess(name, kind),
                Bytes = data.Length,
                Problem = "Vision couldn't read this file (it may be damaged).",
            };
        }
    }

    private static AttachmentDigest ReadUnguarded(string name, byte[] data, AttachmentKind kind, bool ocr)
    {
        var bytes = data.LongLength;
        var ext = Ext(name);
        if (kind == AttachmentKind.Image || ImageExtensions.Contains(ext))
        {
            var text = ocr && Ocr is { } engine ? AttachmentFormat.NormalizeNewlines(SafeOcr(engine, data)) : "";
            return new AttachmentDigest
            {
                Format = DigestFormat.Image, Lines = AttachmentFormat.LineCount(text), Bytes = bytes, Text = text, Ocr = text.Length > 0,
            };
        }
        if (ext == "pdf" || data.AsSpan().StartsWith("%PDF"u8))
        {
            var pdf = PdfText.Extract(data, ocr);
            return new AttachmentDigest
            {
                Format = DigestFormat.Pdf, Pages = pdf.Pages, Lines = AttachmentFormat.LineCount(pdf.Text), Bytes = bytes,
                Text = pdf.Text, Ocr = pdf.Ocr, Scanned = pdf.Scanned, PageImages = pdf.PageImages, Problem = pdf.Problem,
            };
        }
        if (OfficeKind(ext) is { } office && OfficeText.LooksLikeZip(data))
        {
            var format = office switch
            {
                OfficeText.Kind.Word or OfficeText.Kind.OpenDocument => DigestFormat.Word,
                OfficeText.Kind.PowerPoint => DigestFormat.PowerPoint,
                _ => DigestFormat.Excel,
            };
            try
            {
                var result = OfficeText.Extract(data, office);
                var text = AttachmentFormat.NormalizeNewlines(result.Text);
                return new AttachmentDigest { Format = format, Pages = result.Pages, Lines = AttachmentFormat.LineCount(text), Bytes = bytes, Text = text };
            }
            catch (Exception ex) when (ex is InvalidDataException or System.Xml.XmlException or IOException or InvalidOperationException)
            {
                return new AttachmentDigest { Format = format, Bytes = bytes, Problem = $"Vision couldn't read this {format.Label()} file (it may be damaged)." };
            }
        }
        if (ext == "rtf" || data.AsSpan().StartsWith("{\\rtf"u8))
        {
            var text = RtfText.Extract(data);
            if (text is not null)
                return new AttachmentDigest { Format = DigestFormat.Rtf, Lines = AttachmentFormat.LineCount(text), Bytes = bytes, Text = text };
            return new AttachmentDigest { Format = DigestFormat.Rtf, Bytes = bytes, Problem = "Vision couldn't read this Rich text file." };
        }
        if (ext is "doc" or "dot" or "ppt" or "xls")
        {
            var format = ext switch { "ppt" => DigestFormat.PowerPoint, "xls" => DigestFormat.Excel, _ => DigestFormat.Word };
            return new AttachmentDigest
            {
                Format = format, Bytes = bytes,
                Problem = $"Vision can't read the old {format.Label()} format (.{ext}); save it as .{ext}x and attach that.",
            };
        }
        var baseName = Path.GetFileName(name);
        DigestFormat? known = TextExtensions.TryGetValue(ext, out var f) ? f : TextNames.Contains(baseName) ? DigestFormat.Text : null;
        if (DecodeText(data, lenient: known is not null) is { } decoded)
        {
            var text = AttachmentFormat.NormalizeNewlines(decoded);
            return new AttachmentDigest { Format = known ?? DigestFormat.Text, Lines = AttachmentFormat.LineCount(text), Bytes = bytes, Text = text };
        }
        return new AttachmentDigest
        {
            Format = DigestFormat.Binary, Bytes = bytes, Problem = "Vision can't read this kind of file, so only its name is sent.",
        };
    }

    /// <summary>What a model that can't take images is sent for one: the text OCR found in it.</summary>
    public static string ImageText(string name, string ocrText) => ocrText.Length == 0
        ? $"[Image: {name} — this model can't take images, and no text was found in it]"
        : $"[Image: {name} — this model can't take images; the text in it, read by OCR:]\n{ocrText}";

    internal static string SafeOcr(IOcrEngine engine, byte[] image)
    {
        try
        {
            return engine.Read(image).Trim();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return "";
        }
    }

    // MARK: Helpers

    internal static string Ext(string name) => Path.GetExtension(name).TrimStart('.').ToLowerInvariant();

    private static DigestFormat FormatGuess(string name, AttachmentKind kind)
    {
        var ext = Ext(name);
        if (kind == AttachmentKind.Image || ImageExtensions.Contains(ext)) return DigestFormat.Image;
        if (ext == "pdf") return DigestFormat.Pdf;
        if (OfficeKind(ext) is { } office)
            return office switch { OfficeText.Kind.PowerPoint => DigestFormat.PowerPoint, OfficeText.Kind.Excel => DigestFormat.Excel, _ => DigestFormat.Word };
        return TextExtensions.TryGetValue(ext, out var f) ? f : DigestFormat.Binary;
    }

    internal static OfficeText.Kind? OfficeKind(string ext) => ext switch
    {
        "docx" or "docm" or "dotx" or "dotm" => OfficeText.Kind.Word,
        "pptx" or "pptm" or "potx" or "ppsx" => OfficeText.Kind.PowerPoint,
        "xlsx" or "xlsm" or "xltx" or "xltm" => OfficeText.Kind.Excel,
        "odt" => OfficeText.Kind.OpenDocument,
        _ => null,
    };

    private static readonly UTF8Encoding StrictUtf8 = new(false, throwOnInvalidBytes: true);
    private static readonly Encoding StrictUtf16 = new UnicodeEncoding(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true);
    private static readonly Encoding StrictUtf16BigEndian = new UnicodeEncoding(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: true);

    /// <summary>Text in UTF-8 (with or without a byte-order mark) or UTF-16 with one. A file named as text whose bytes
    /// aren't valid UTF-8 but hold no NULs is read as Windows-1252 (old Windows text files); an unknown file is text only
    /// when it decodes cleanly. Null when it isn't text.</summary>
    public static string? DecodeText(byte[] data, bool lenient = true)
    {
        string? text;
        try
        {
            if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF) text = StrictUtf8.GetString(data, 3, data.Length - 3);
            else if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE) text = StrictUtf16.GetString(data, 2, data.Length - 2);
            else if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF) text = StrictUtf16BigEndian.GetString(data, 2, data.Length - 2);
            else
            {
                if (data.AsSpan().Contains((byte)0)) return null;
                try
                {
                    text = StrictUtf8.GetString(data);
                }
                catch (DecoderFallbackException) when (lenient)
                {
                    text = Windows1252.GetString(data);
                }
            }
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
        text = text.TrimStart('﻿');
        return text.Contains('\0') ? null : text;
    }

    /// <summary>Windows-1252 without the code-pages provider: Latin-1 with the 0x80–0x9F block mapped as Windows maps it.</summary>
    private static readonly Encoding Windows1252 = new Cp1252();

    private sealed class Cp1252 : Encoding
    {
        private static readonly char[] High =
        [
            '€', '\u0081', '‚', 'ƒ', '„', '…', '†', '‡', 'ˆ', '‰', 'Š', '‹', 'Œ', '\u008D', 'Ž', '\u008F',
            '\u0090', '‘', '’', '“', '”', '•', '–', '—', '˜', '™', 'š', '›', 'œ', '\u009D', 'ž', 'Ÿ',
        ];

        public override int GetByteCount(char[] chars, int index, int count) => count;
        public override int GetBytes(char[] chars, int charIndex, int charCount, byte[] bytes, int byteIndex) => throw new NotSupportedException();
        public override int GetCharCount(byte[] bytes, int index, int count) => count;

        public override int GetChars(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex)
        {
            for (var i = 0; i < byteCount; i++)
            {
                var b = bytes[byteIndex + i];
                chars[charIndex + i] = b is >= 0x80 and <= 0x9F ? High[b - 0x80] : (char)b;
            }
            return byteCount;
        }

        public override int GetMaxByteCount(int charCount) => charCount;
        public override int GetMaxCharCount(int byteCount) => byteCount;
    }
}
