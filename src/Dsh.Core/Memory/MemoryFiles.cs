using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Dsh.Core;

/// <summary>Cuts markdown notes (a MEMORY.md, a folder of daily logs — DSH's, or another agent's) into
/// note-sized pieces for the store: a heading and what follows it, or a run of bullets, each a few hundred
/// characters, so a search returns the one paragraph that matters instead of a whole file.</summary>
public static partial class MemoryFiles
{
    /// <summary>Recall shows the first ~420 characters of a note, so a piece is cut to fit: what matched must be in view.</summary>
    private const int TargetChars = 380;
    private const int MinChars = 6;
    private const int IntroChars = 160;

    [GeneratedRegex(@"^(\d{4})-(\d{2})-(\d{2})(?:[-_ ].*)?$", RegexOptions.CultureInvariant)]
    private static partial Regex DayName();

    /// <summary>A date in a file name like <c>2026-03-14.md</c> (a daily log), else null.</summary>
    public static DateTimeOffset? DayOf(string fileName)
    {
        var match = DayName().Match(Path.GetFileNameWithoutExtension(fileName));
        if (!match.Success) return null;
        return DateTimeOffset.TryParse($"{match.Groups[1].Value}-{match.Groups[2].Value}-{match.Groups[3].Value}",
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var day) ? day : null;
    }

    /// <summary>Notes for one file. <paramref name="source"/> labels where they came from
    /// (e.g. "import:openclaw/MEMORY.md") and makes a re-import replace rather than duplicate.</summary>
    public static IReadOnlyList<MemoryDraft> Chunk(string markdown, string source, string fileName, string? project = null,
                                                   string kind = MemoryKinds.Note, IEnumerable<string>? tags = null)
    {
        var text = StripFrontmatter(markdown.Replace("\r\n", "\n"));
        var day = DayOf(fileName);
        var baseTags = (tags ?? []).ToList();
        if (day is not null) baseTags.Add("daily");
        var fileTitle = Path.GetFileNameWithoutExtension(fileName);

        var sections = new List<(string Heading, string Body)>();
        var heading = "";
        var headingLevel = 0;
        var seenHeading = false;
        var inFence = false;
        var body = new StringBuilder();
        void Flush()
        {
            var content = body.ToString().Trim();
            // A short blurb under the file's own "# Title" ("Durable facts about this project…") describes
            // the file, it isn't a fact worth remembering.
            var intro = !seenHeading && headingLevel == 1 && content.Length < IntroChars;
            if (content.Length >= MinChars && !intro) sections.Add((heading, content));
            if (headingLevel > 0) seenHeading = true;
            body.Clear();
        }
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd();
            // A "# comment" inside a code block is code, not a heading.
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal) || line.TrimStart().StartsWith("~~~", StringComparison.Ordinal))
                inFence = !inFence;
            if (!inFence && line.StartsWith('#'))
            {
                Flush();
                headingLevel = line.TakeWhile(c => c == '#').Count();
                heading = line.TrimStart('#').Trim();
                continue;
            }
            body.Append(line).Append('\n');
        }
        Flush();
        if (sections.Count == 0 && text.Trim().Length >= MinChars) sections.Add(("", text.Trim()));

        var drafts = new List<MemoryDraft>();
        // A re-import maps notes one to one only if titles are unique within the file.
        var usedTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (sectionHeading, sectionBody) in sections)
        {
            var pieces = Split(sectionBody);
            for (var i = 0; i < pieces.Count; i++)
            {
                var title = sectionHeading.Length > 0 ? sectionHeading : fileTitle;
                if (pieces.Count > 1) title += $" ({i + 1})";
                var unique = title;
                for (var n = 2; !usedTitles.Add(unique); n++) unique = $"{title} #{n}";
                drafts.Add(new MemoryDraft
                {
                    Title = unique,
                    Body = pieces[i],
                    Tags = baseTags,
                    Kind = kind,
                    Project = project,
                    Source = source,
                    CreatedAt = day,
                });
            }
        }
        return drafts;
    }

    /// <summary>Split a section at paragraph or bullet boundaries into pieces of about <see cref="TargetChars"/>.</summary>
    private static List<string> Split(string section)
    {
        var pieces = new List<string>();
        var current = new StringBuilder();
        foreach (var whole in Blocks(section))
        {
            foreach (var block in Fit(whole))
            {
                if (current.Length > 0 && current.Length + block.Length > TargetChars)
                {
                    pieces.Add(current.ToString().Trim());
                    current.Clear();
                }
                current.Append(block).Append('\n');
            }
        }
        if (current.ToString().Trim().Length >= MinChars) pieces.Add(current.ToString().Trim());
        return pieces;
    }

    /// <summary>A block longer than a piece is cut at sentence ends (or lines, or words), so no piece outgrows what recall shows.</summary>
    private static IEnumerable<string> Fit(string block)
    {
        var rest = block;
        while (rest.Length > TargetChars)
        {
            var window = rest[..TargetChars];
            var at = window.LastIndexOfAny(['.', '!', '?', '\n', ';']);
            if (at < TargetChars / 3) at = window.LastIndexOf(' ');
            if (at < TargetChars / 3) at = TargetChars - 1;
            if (char.IsHighSurrogate(rest[at])) at--; // never split a surrogate pair
            yield return rest[..(at + 1)].Trim();
            rest = rest[(at + 1)..].TrimStart();
        }
        if (rest.Length > 0) yield return rest;
    }

    private static IEnumerable<string> Blocks(string section)
    {
        var block = new StringBuilder();
        foreach (var line in section.Split('\n'))
        {
            var startsBullet = line.TrimStart().StartsWith("- ", StringComparison.Ordinal) || line.TrimStart().StartsWith("* ", StringComparison.Ordinal);
            var indented = line.StartsWith(' ') || line.StartsWith('\t');
            if (line.Trim().Length == 0 || (startsBullet && !indented))
            {
                if (block.Length > 0)
                {
                    yield return block.ToString().TrimEnd();
                    block.Clear();
                }
                if (line.Trim().Length == 0) continue;
            }
            block.Append(line).Append('\n');
        }
        if (block.Length > 0) yield return block.ToString().TrimEnd();
    }

    private static string StripFrontmatter(string text)
    {
        if (!text.StartsWith("---\n", StringComparison.Ordinal)) return text;
        var end = text.IndexOf("\n---", 4, StringComparison.Ordinal);
        if (end < 0) return text;
        var after = text.IndexOf('\n', end + 4);
        return after < 0 ? "" : text[(after + 1)..];
    }
}
