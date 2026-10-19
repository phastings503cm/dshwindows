using System.IO.Compression;
using System.Text;
using System.Xml;

namespace Vision.Core;

// MARK: - Text out of Office Open XML files (.docx, .pptx, .xlsx) and OpenDocument text (.odt)
//
// Each is a zip of XML parts; the text is in a handful of them. Word: word/document.xml (paragraphs, headings as "#",
// list items as "- ", table rows as "a | b | c"). PowerPoint: one "--- Slide N of M ---" section per slide in
// presentation order. Excel: one "--- Sheet N of M: Name ---" section per sheet, each row's cells separated by tabs.
// The same shapes as Vision for macOS (OfficeText.swift).

public static class OfficeText
{
    public enum Kind { Word, PowerPoint, Excel, OpenDocument }

    public sealed record Result(string Text, int? Pages);

    /// <summary>A zip archive's signature ("PK\x03\x04").</summary>
    public static bool LooksLikeZip(byte[] data) => data.Length >= 4 && data[0] == 0x50 && data[1] == 0x4B && data[2] == 0x03 && data[3] == 0x04;

    /// <summary>Parts larger than this are not read (a zip bomb, or a workbook no model should be sent whole).</summary>
    internal const long MaxPartBytes = 64L * 1024 * 1024;

    public static Result Extract(byte[] data, Kind kind)
    {
        using var zip = new ZipArchive(new MemoryStream(data, writable: false), ZipArchiveMode.Read);
        return kind switch
        {
            Kind.Word => new Result(Word(zip), null),
            Kind.PowerPoint => PowerPoint(zip),
            Kind.Excel => Excel(zip),
            _ => new Result(OpenDocument(zip), null),
        };
    }

    // MARK: Word

    private static string Word(ZipArchive zip)
    {
        var xml = Read(zip, "word/document.xml") ?? throw new InvalidDataException("no word/document.xml");
        var lines = new List<string>();
        var paragraph = new StringBuilder();
        bool inText = false, inRun = false, listItem = false;
        var heading = 0;
        var tableDepth = 0;
        var row = new List<string>();
        var cell = new StringBuilder();

        void FinishParagraph()
        {
            var line = paragraph.ToString().Trim(' ');
            paragraph.Clear();
            var wasHeading = heading;
            var wasList = listItem;
            heading = 0;
            listItem = false;
            if (tableDepth > 0)
            {
                if (line.Length > 0) cell.Append(cell.Length == 0 ? line : " " + line);
                return;
            }
            if (line.Length > 0)
            {
                if (wasHeading > 0) line = new string('#', wasHeading) + " " + line;
                else if (wasList) line = "- " + line;
            }
            lines.Add(line);
        }

        XmlWalker.Walk(xml, start: (name, attributes) =>
        {
            switch (name)
            {
                case "r": inRun = true; break;
                case "t": inText = inRun; break;
                // Tab stops (w:tabs/w:tab in the paragraph's properties) are not text; a tab in a run is.
                case "tab": if (inRun) paragraph.Append('\t'); break;
                case "br" or "cr": if (inRun) paragraph.Append(tableDepth > 0 ? " " : "\n"); break;
                case "pStyle":
                    var style = (attributes.GetValueOrDefault("val") ?? "").ToLowerInvariant();
                    if (style == "title") heading = 1;
                    else if (style.StartsWith("heading", StringComparison.Ordinal) && int.TryParse(style["heading".Length..], out var n) && n is >= 1 and <= 6) heading = n;
                    break;
                case "numPr": listItem = true; break;
                case "tbl": tableDepth++; break;
                case "tr": if (tableDepth == 1) row = []; break;
                case "tc": if (tableDepth == 1) cell.Clear(); break;
            }
        }, end: name =>
        {
            switch (name)
            {
                case "r": inRun = false; break;
                case "t": inText = false; break;
                case "p": FinishParagraph(); break;
                case "tc": if (tableDepth == 1) row.Add(cell.ToString().Trim(' ')); break;
                case "tr": if (tableDepth == 1 && row.Any(c => c.Length > 0)) lines.Add(string.Join(" | ", row)); break;
                case "tbl": tableDepth = Math.Max(0, tableDepth - 1); break;
            }
        }, text: s =>
        {
            if (inText) paragraph.Append(s);
        });
        return TextScan.Tidy(lines);
    }

    // MARK: PowerPoint

    private static Result PowerPoint(ZipArchive zip)
    {
        var slides = SlideOrder(zip);
        if (slides.Count == 0) throw new InvalidDataException("no slides");
        var sections = new List<string>();
        for (var index = 0; index < slides.Count; index++)
        {
            if (Read(zip, slides[index]) is not { } xml) continue;
            var lines = new List<string>();
            var paragraph = new StringBuilder();
            var inText = false;
            XmlWalker.Walk(xml, start: (name, _) =>
            {
                switch (name)
                {
                    case "t": inText = true; break;
                    case "br": paragraph.Append('\n'); break;
                    case "tab": paragraph.Append('\t'); break;
                }
            }, end: name =>
            {
                switch (name)
                {
                    case "t": inText = false; break;
                    case "p":
                        var line = paragraph.ToString().Trim(' ');
                        if (line.Length > 0) lines.Add(line);
                        paragraph.Clear();
                        break;
                }
            }, text: s =>
            {
                if (inText) paragraph.Append(s);
            });
            sections.Add($"--- Slide {index + 1} of {slides.Count} ---\n" + string.Join("\n", lines));
        }
        return new Result(string.Join("\n\n", sections), slides.Count);
    }

    /// <summary>Slide parts in presentation order (p:sldIdLst through the relationships), else by number.</summary>
    private static List<string> SlideOrder(ZipArchive zip)
    {
        var numbered = zip.Entries.Select(e => e.FullName)
            .Where(n => n.StartsWith("ppt/slides/slide", StringComparison.Ordinal) && n.EndsWith(".xml", StringComparison.Ordinal))
            .OrderBy(NumberIn).ToList();
        if (Read(zip, "ppt/presentation.xml") is not { } xml || Read(zip, "ppt/_rels/presentation.xml.rels") is not { } rels) return numbered;
        var targets = Relationships(rels, "ppt/");
        var ordered = new List<string>();
        XmlWalker.Walk(xml, start: (name, attributes) =>
        {
            if (name == "sldId" && (attributes.GetValueOrDefault("id:r") ?? attributes.GetValueOrDefault("r:id")) is { } id && targets.TryGetValue(id, out var target))
                ordered.Add(target);
        });
        var present = numbered.ToHashSet(StringComparer.Ordinal);
        var inOrder = ordered.Where(present.Contains).ToList();
        return inOrder.Count == 0 ? numbered : inOrder;
    }

    // MARK: Excel

    private static Result Excel(ZipArchive zip)
    {
        var shared = SharedStrings(zip);
        var sheets = new List<(string Name, string Path)>();
        if (Read(zip, "xl/workbook.xml") is { } workbook)
        {
            var targets = Read(zip, "xl/_rels/workbook.xml.rels") is { } rels ? Relationships(rels, "xl/") : new Dictionary<string, string>();
            XmlWalker.Walk(workbook, start: (name, attributes) =>
            {
                if (name != "sheet") return;
                var rid = attributes.GetValueOrDefault("id:r") ?? attributes.GetValueOrDefault("r:id") ?? "";
                if (targets.TryGetValue(rid, out var path)) sheets.Add((attributes.GetValueOrDefault("name") ?? $"Sheet{sheets.Count + 1}", path));
            });
        }
        if (sheets.Count == 0)
        {
            sheets = zip.Entries.Select(e => e.FullName)
                .Where(n => n.StartsWith("xl/worksheets/sheet", StringComparison.Ordinal) && n.EndsWith(".xml", StringComparison.Ordinal))
                .OrderBy(NumberIn)
                .Select((path, i) => ($"Sheet{i + 1}", path)).ToList();
        }
        if (sheets.Count == 0) throw new InvalidDataException("no sheets");
        var sections = new List<string>();
        for (var index = 0; index < sheets.Count; index++)
        {
            if (Read(zip, sheets[index].Path) is not { } xml) continue;
            var rows = new List<string>();
            var cells = new Dictionary<int, string>();
            var column = 0;
            var type = "";
            var value = new StringBuilder();
            bool inValue = false, inInline = false;
            XmlWalker.Walk(xml, start: (name, attributes) =>
            {
                switch (name)
                {
                    case "row": cells = []; column = 0; break;
                    case "c":
                        type = attributes.GetValueOrDefault("t") ?? "";
                        value.Clear();
                        if (attributes.GetValueOrDefault("r") is { } reference && ColumnIndex(reference) is { } col) column = col;
                        else column++;
                        break;
                    case "v": inValue = true; break;
                    case "is": inInline = true; break;
                    case "t": if (inInline) inValue = true; break;
                }
            }, end: name =>
            {
                switch (name)
                {
                    case "v": inValue = false; break;
                    case "t": if (inInline) inValue = false; break;
                    case "is": inInline = false; break;
                    case "c":
                        var shown = value.ToString();
                        switch (type)
                        {
                            case "s":
                                shown = int.TryParse(shown.Trim(), out var at) && at >= 0 && at < shared.Count ? shared[at] : "";
                                break;
                            case "b":
                                shown = shown == "1" ? "TRUE" : shown == "0" ? "FALSE" : shown;
                                break;
                        }
                        shown = shown.Replace('\t', ' ').Replace('\n', ' ');
                        if (shown.Length > 0) cells[column] = shown;
                        break;
                    case "row":
                        if (cells.Count == 0) return;
                        var last = cells.Keys.Max();
                        rows.Add(string.Join("\t", Enumerable.Range(1, Math.Max(1, last)).Select(c => cells.GetValueOrDefault(c, ""))));
                        break;
                }
            }, text: s =>
            {
                if (inValue) value.Append(s);
            });
            sections.Add($"--- Sheet {index + 1} of {sheets.Count}: {sheets[index].Name} ---\n" + string.Join("\n", rows));
        }
        return new Result(string.Join("\n\n", sections), sheets.Count);
    }

    private static List<string> SharedStrings(ZipArchive zip)
    {
        var strings = new List<string>();
        if (Read(zip, "xl/sharedStrings.xml") is not { } xml) return strings;
        var current = new StringBuilder();
        bool inText = false, phonetic = false;
        XmlWalker.Walk(xml, start: (name, _) =>
        {
            switch (name)
            {
                case "si": current.Clear(); break;
                case "rPh": phonetic = true; break;
                case "t": inText = !phonetic; break;
            }
        }, end: name =>
        {
            switch (name)
            {
                case "si": strings.Add(current.ToString()); break;
                case "rPh": phonetic = false; break;
                case "t": inText = false; break;
            }
        }, text: s =>
        {
            if (inText) current.Append(s);
        });
        return strings;
    }

    /// <summary>"C12" → 3 (A = 1).</summary>
    internal static int? ColumnIndex(string reference)
    {
        var index = 0;
        var any = false;
        foreach (var c in reference)
        {
            if (c is not (>= 'A' and <= 'Z' or >= 'a' and <= 'z')) break;
            index = index * 26 + (char.ToUpperInvariant(c) - 'A' + 1);
            any = true;
        }
        return any ? index : null;
    }

    // MARK: OpenDocument text

    private static string OpenDocument(ZipArchive zip)
    {
        var xml = Read(zip, "content.xml") ?? throw new InvalidDataException("no content.xml");
        var lines = new List<string>();
        var paragraph = new StringBuilder();
        var depth = 0;
        var heading = 0;
        XmlWalker.Walk(xml, start: (name, attributes) =>
        {
            switch (name)
            {
                case "p": depth++; break;
                case "h":
                    depth++;
                    heading = int.TryParse(attributes.GetValueOrDefault("outline-level"), out var level) ? Math.Clamp(level, 1, 6) : 1;
                    break;
                case "tab": if (depth > 0) paragraph.Append('\t'); break;
                case "line-break": if (depth > 0) paragraph.Append('\n'); break;
                case "s": if (depth > 0) paragraph.Append(' ', int.TryParse(attributes.GetValueOrDefault("c"), out var spaces) ? Math.Clamp(spaces, 1, 64) : 1); break;
            }
        }, end: name =>
        {
            if (name is not ("p" or "h")) return;
            depth = Math.Max(0, depth - 1);
            if (depth > 0) return;
            var line = paragraph.ToString().Trim(' ');
            paragraph.Clear();
            if (name == "h" && line.Length > 0) line = new string('#', heading) + " " + line;
            heading = 0;
            lines.Add(line);
        }, text: s =>
        {
            if (depth > 0) paragraph.Append(s);
        });
        return TextScan.Tidy(lines);
    }

    // MARK: Shared

    /// <summary>A part's bytes, or null when the archive doesn't have it (or it is implausibly large).</summary>
    private static byte[]? Read(ZipArchive zip, string path)
    {
        var entry = zip.GetEntry(path);
        if (entry is null || entry.Length > MaxPartBytes) return null;
        using var stream = entry.Open();
        using var buffer = new MemoryStream((int)Math.Min(entry.Length, int.MaxValue));
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>Relationship id → part path, resolved against <paramref name="baseFolder"/> ("ppt/", "xl/").</summary>
    private static Dictionary<string, string> Relationships(byte[] xml, string baseFolder)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        XmlWalker.Walk(xml, start: (name, attributes) =>
        {
            if (name != "Relationship" || attributes.GetValueOrDefault("Id") is not { } id || attributes.GetValueOrDefault("Target") is not { } target) return;
            if (target.StartsWith('/'))
            {
                map[id] = target[1..];
                return;
            }
            var parts = baseFolder.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
            foreach (var piece in target.Split('/'))
            {
                if (piece == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); }
                else if (piece is not ("." or "")) parts.Add(piece);
            }
            map[id] = string.Join("/", parts);
        });
        return map;
    }

    private static int NumberIn(string path)
    {
        var digits = new string(path.Reverse().SkipWhile(c => !char.IsAsciiDigit(c)).TakeWhile(char.IsAsciiDigit).Reverse().ToArray());
        return int.TryParse(digits, out var n) ? n : 0;
    }
}

// MARK: - A small streaming XML walker

/// <summary>Calls back for every element start (local name, attributes keyed by local name — a prefixed attribute such
/// as <c>r:id</c> also appears as "id:r", so it can't be confused with a plain <c>id</c>), element end and run of text.
/// No DTDs, no external entities.</summary>
internal static class XmlWalker
{
    private static readonly XmlReaderSettings Settings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        IgnoreWhitespace = false,
        CheckCharacters = false,
    };

    public static void Walk(byte[] xml, Action<string, Dictionary<string, string>>? start = null, Action<string>? end = null,
                            Action<string>? text = null)
    {
        using var reader = XmlReader.Create(new MemoryStream(xml, writable: false), Settings);
        while (reader.Read())
        {
            switch (reader.NodeType)
            {
                case XmlNodeType.Element:
                {
                    var name = reader.LocalName;
                    var empty = reader.IsEmptyElement;
                    var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
                    if (reader.HasAttributes)
                    {
                        while (reader.MoveToNextAttribute())
                        {
                            if (reader.Prefix.Length > 0)
                            {
                                attributes[$"{reader.LocalName}:{reader.Prefix}"] = reader.Value;
                                attributes.TryAdd(reader.LocalName, reader.Value);
                            }
                            else
                            {
                                attributes[reader.LocalName] = reader.Value;
                            }
                        }
                        reader.MoveToElement();
                    }
                    start?.Invoke(name, attributes);
                    if (empty) end?.Invoke(name);
                    break;
                }
                case XmlNodeType.EndElement:
                    end?.Invoke(reader.LocalName);
                    break;
                case XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.SignificantWhitespace or XmlNodeType.Whitespace:
                    text?.Invoke(reader.Value);
                    break;
            }
        }
    }
}
