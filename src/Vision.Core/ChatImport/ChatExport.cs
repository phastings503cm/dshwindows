using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Vision.Core;

/// <summary>A chat written out: Markdown to read or share, or JSON to bring into Vision again (on this PC, another one, or
/// the Mac — it is the transcript shape both harnesses keep in Guardian, with a few added fields the readers ignore).</summary>
public static class ChatExport
{
    /// <summary>The format's name in the JSON (<c>"format"</c>), and its version.</summary>
    public const string Format = "vision-chat";
    public const int FormatVersion = 1;

    /// <summary>Tool output in Markdown stops after this many characters.</summary>
    public const int MarkdownToolOutput = 4_000;

    /// <summary>The chat as JSON: <c>{format, version, id, title, cwd, rows:[{kind, text, toolName, argSummary, output, isError,
    /// at}], harness, exportedAt, imported?}</c> — <c>at</c> in Apple reference-date seconds, as Guardian keeps it.</summary>
    public static string Json(LogSession session, IEnumerable<LogItemRow> rows, DateTimeOffset? exportedAt = null)
    {
        var chat = JsonNode.Parse(GuardianChats.ToPayload(session.Id, session.Title, session.Cwd, rows))!.AsObject();
        var ordered = new JsonObject { ["format"] = Format, ["version"] = FormatVersion };
        foreach (var (key, value) in chat.ToList())
        {
            chat.Remove(key);
            ordered[key] = value;
        }
        ordered["exportedAt"] = GuardianClient.Iso(exportedAt ?? DateTimeOffset.Now);
        if (session.Imported is { } origin)
        {
            var from = new JsonObject { ["source"] = origin.Source, ["id"] = origin.Id };
            if (origin.Project is { } project) from["project"] = project;
            if (origin.Model is { } model) from["model"] = model;
            if (origin.StartedAt is { } started) from["startedAt"] = GuardianClient.Iso(started);
            from["importedAt"] = GuardianClient.Iso(origin.ImportedAt);
            ordered["imported"] = from;
        }
        return ordered.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>The chat as Markdown: a title, where it came from, then each message under "You" or "Assistant" with its time;
    /// tool calls as one line each with their output folded in a code block; notices as quotes.</summary>
    public static string Markdown(LogSession session, IEnumerable<LogItemRow> rows)
    {
        var text = new StringBuilder();
        text.Append("# ").Append(session.Title.Trim().Length == 0 ? "Chat" : session.Title.Trim()).Append("\n\n");
        var facts = new List<string>();
        if (session.Imported is { } origin)
            facts.Add($"Imported from {(ChatSources.FromRaw(origin.Source) ?? ChatSource.Vision).Label()}" + (origin.Project is { } p ? $" ({p})" : ""));
        if (session.Cwd is { Length: > 0 } cwd) facts.Add($"Project: {cwd}");
        facts.Add("Exported from Vision " + Stamp(DateTimeOffset.Now));
        text.Append('_').Append(string.Join(" · ", facts)).Append("_\n\n");
        foreach (var row in rows)
        {
            switch (row.Kind)
            {
                case "user":
                    text.Append("## You · ").Append(Stamp(row.At)).Append("\n\n").Append(row.Text?.TrimEnd()).Append("\n\n");
                    break;
                case "assistant":
                    text.Append("## Assistant · ").Append(Stamp(row.At)).Append("\n\n").Append(row.Text?.TrimEnd()).Append("\n\n");
                    break;
                case "tool":
                    text.Append("**Tool** `").Append(Code(row.ToolName ?? "tool")).Append('`');
                    if (row.ArgSummary is { Length: > 0 } args) text.Append(" `").Append(Code(args)).Append('`');
                    if (row.Text is { Length: > 0 } summary) text.Append(" → ").Append(summary);
                    else if (row.IsError) text.Append(" → failed");
                    text.Append("\n\n");
                    if (row.Output is { Length: > 0 } output)
                    {
                        var shown = output.Length > MarkdownToolOutput ? TextUtil.Prefix(output, MarkdownToolOutput) + $"\n… ({output.Length - MarkdownToolOutput:N0} more characters)" : output;
                        var fence = shown.Contains("```", StringComparison.Ordinal) ? "````" : "```";
                        text.Append(fence).Append('\n').Append(shown.TrimEnd()).Append('\n').Append(fence).Append("\n\n");
                    }
                    break;
                case "notice":
                case "error":
                    text.Append("> ").Append((row.Kind == "error" ? "Error: " : "") + (row.Text ?? "").Trim().Replace("\n", "\n> ")).Append("\n\n");
                    break;
                case "compaction":
                    text.Append("---\n\n*Earlier messages were compacted into this summary:*\n\n").Append(row.Text?.Trim()).Append("\n\n---\n\n");
                    break;
            }
        }
        return text.ToString().TrimEnd() + "\n";
    }

    private static string Stamp(DateTimeOffset at) => at.ToLocalTime().ToString("d MMM yyyy, HH:mm", CultureInfo.InvariantCulture);

    private static string Code(string text) => text.Replace('`', '\'').Replace('\n', ' ');

    /// <summary>A file name for the chat: its title with characters Windows refuses taken out.</summary>
    public static string FileName(LogSession session, string extension)
    {
        var name = new string(session.Title.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*' ? ' ' : c).ToArray());
        name = string.Join(" ", name.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim('.', ' ');
        if (name.Length == 0) name = "Chat";
        if (name.Length > 80) name = name[..80].TrimEnd();
        return name + extension;
    }
}

/// <summary>One search over every chat on this PC — titles and the text of their messages, tool calls and outputs — with a
/// source filter (only Claude Code chats, only Guardian's…). Every word must appear in the chat; title matches first, then
/// the most recent.</summary>
public static class ChatSearch
{
    public sealed record Match(int Seq, string Kind, string Snippet, DateTimeOffset At);

    public sealed record Result(LogSession Session, ChatSource Source, bool TitleMatch, IReadOnlyList<Match> Matches, int MatchCount);

    public const int MaxMatchesPerChat = 5;

    public static IReadOnlyList<Result> Run(ConversationLog log, string query, ChatSource? source = null, int maxChats = 200, CancellationToken cancellationToken = default) =>
        Run(Snapshot(log), query, source, maxChats, cancellationToken);

    /// <summary>Every chat and its rows as they are now — taken on the log's thread, so the search itself can run on another.</summary>
    public static IReadOnlyList<(LogSession Session, IReadOnlyList<LogItemRow> Rows)> Snapshot(ConversationLog log) =>
        log.List().Select(s => (s, (IReadOnlyList<LogItemRow>)log.LoadItems(s.Id).ToArray())).ToList();

    public static IReadOnlyList<Result> Run(IReadOnlyList<(LogSession Session, IReadOnlyList<LogItemRow> Rows)> chats, string query, ChatSource? source = null,
                                            int maxChats = 200, CancellationToken cancellationToken = default)
    {
        var terms = Terms(query);
        var results = new List<Result>();
        if (terms.Count == 0) return results;
        foreach (var (session, rows) in chats)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var from = ChatSources.Of(session);
            if (source is { } wanted && from != wanted) continue;
            var title = session.Title;
            // Every word somewhere in the chat: its title or any row.
            var found = new bool[terms.Count];
            for (var t = 0; t < terms.Count; t++) found[t] = title.Contains(terms[t], StringComparison.OrdinalIgnoreCase);
            var matches = new List<Match>();
            var count = 0;
            foreach (var row in rows)
            {
                var hit = false;
                for (var t = 0; t < terms.Count; t++)
                {
                    if (Contains(row, terms[t]))
                    {
                        found[t] = true;
                        hit = true;
                    }
                }
                if (!hit) continue;
                count++;
                if (matches.Count < MaxMatchesPerChat) matches.Add(new Match(row.Seq, row.Kind, Snippet(row, terms), row.At));
            }
            if (!found.All(f => f)) continue;
            var titleMatch = terms.All(t => title.Contains(t, StringComparison.OrdinalIgnoreCase));
            results.Add(new Result(session, from, titleMatch, matches, count));
        }
        return results.OrderByDescending(r => r.TitleMatch).ThenByDescending(r => r.Session.UpdatedAt).Take(maxChats).ToList();
    }

    /// <summary>The words of a query; a "quoted phrase" is one term.</summary>
    internal static IReadOnlyList<string> Terms(string query)
    {
        var terms = new List<string>();
        var text = query.Trim();
        var i = 0;
        while (i < text.Length)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                i++;
                continue;
            }
            if (text[i] == '"')
            {
                var close = text.IndexOf('"', i + 1);
                var phrase = close < 0 ? text[(i + 1)..] : text[(i + 1)..close];
                if (phrase.Trim().Length > 0) terms.Add(phrase.Trim());
                i = close < 0 ? text.Length : close + 1;
                continue;
            }
            var end = i;
            while (end < text.Length && !char.IsWhiteSpace(text[end])) end++;
            terms.Add(text[i..end]);
            i = end;
        }
        return terms.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool Contains(LogItemRow row, string term) =>
        row.Text?.Contains(term, StringComparison.OrdinalIgnoreCase) == true
        || row.Kind == "tool" && (row.ArgSummary?.Contains(term, StringComparison.OrdinalIgnoreCase) == true
                                  || row.ToolName?.Contains(term, StringComparison.OrdinalIgnoreCase) == true
                                  || row.Output?.Contains(term, StringComparison.OrdinalIgnoreCase) == true);

    /// <summary>About 160 characters around the first term found, on one line.</summary>
    internal static string Snippet(LogItemRow row, IReadOnlyList<string> terms)
    {
        var sources = row.Kind == "tool"
            ? new[] { $"{row.ToolName} {row.ArgSummary}".Trim(), row.Text ?? "", row.Output ?? "" }
            : new[] { row.Text ?? "" };
        foreach (var text in sources)
        {
            foreach (var term in terms)
            {
                var at = text.IndexOf(term, StringComparison.OrdinalIgnoreCase);
                if (at < 0) continue;
                var start = Math.Max(0, at - 60);
                var end = Math.Min(text.Length, at + term.Length + 100);
                var snippet = ChatRowBuilder.OneLine(text[start..end], 200);
                return (start > 0 ? "…" : "") + snippet + (end < text.Length ? "…" : "");
            }
        }
        return ChatRowBuilder.OneLine(sources[0], 160);
    }
}
