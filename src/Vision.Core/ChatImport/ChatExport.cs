using System.Globalization;
using System.Text.Json.Nodes;

namespace Vision.Core;

/// <summary>A chat written out — in the same shapes as Vision for macOS: Markdown to read or share, or JSON
/// (<c>"format":"vision-chat"</c>, version 1) that either harness imports again.</summary>
public static class ChatExport
{
    /// <summary>The format's name in the JSON (<c>"format"</c>), and its version.</summary>
    public const string Format = "vision-chat";
    public const int FormatVersion = 1;

    /// <summary>The chat as JSON, keys sorted: <c>{cwd, createdAt, exportedAt, format, harness, id, rows:[{argSummary, at, isError,
    /// kind, output, text, toolName}], source, sourceId, title, updatedAt, version}</c> — times as ISO-8601 with milliseconds,
    /// absent fields left out, <c>source</c>/<c>sourceId</c> only for an imported chat.</summary>
    public static string Json(LogSession session, IEnumerable<LogItemRow> rows, DateTimeOffset? exportedAt = null)
    {
        var list = rows.Where(r => Kinds.Contains(r.Kind)).ToList();
        var items = new JsonArray();
        foreach (var row in list)
        {
            var item = new JsonObject();
            if (row.ArgSummary is { } args) item["argSummary"] = args;
            item["at"] = GuardianClient.Iso(row.At);
            item["isError"] = row.IsError;
            item["kind"] = row.Kind;
            if (row.Output is { } output) item["output"] = output;
            if (row.Text is { } text) item["text"] = text;
            if (row.ToolName is { } tool) item["toolName"] = tool;
            items.Add(item);
        }
        var chat = new JsonObject();
        if (session.Cwd is { Length: > 0 } cwd) chat["cwd"] = cwd;
        if (list.Count > 0) chat["createdAt"] = GuardianClient.Iso(list[0].At);
        chat["exportedAt"] = GuardianClient.Iso(exportedAt ?? DateTimeOffset.Now);
        chat["format"] = Format;
        chat["harness"] = GuardianClient.Source;
        chat["id"] = session.Id;
        chat["rows"] = items;
        if (session.Imported is { Source.Length: > 0, Id.Length: > 0 } origin)
        {
            chat["source"] = origin.Source;
            chat["sourceId"] = origin.Id;
        }
        chat["title"] = session.Title;
        chat["updatedAt"] = GuardianClient.Iso(session.UpdatedAt);
        chat["version"] = FormatVersion;
        return chat.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    /// <summary>The kinds a chat file holds (a plan row carries no text in the log, so it is left out).</summary>
    private static readonly HashSet<string> Kinds = new(StringComparer.Ordinal) { "user", "assistant", "tool", "notice", "error", "compaction" };

    /// <summary>The chat as Markdown: a title; when it was exported, where it came from, its folder and its span; then each row
    /// — "**You** · time", "**Assistant** · time", "**Tool: name** `args` → result", its output folded in a details block,
    /// notices quoted, errors and summaries headed.</summary>
    public static string Markdown(LogSession session, IEnumerable<LogItemRow> rows, DateTimeOffset? exportedAt = null)
    {
        var list = rows.Where(r => Kinds.Contains(r.Kind)).ToList();
        var lines = new List<string> { "# " + session.Title, "", "- Exported from Vision on " + Stamp(exportedAt ?? DateTimeOffset.Now) };
        if (session.Imported is { Source.Length: > 0 } origin)
            lines.Add($"- Imported from {(ChatSources.FromRaw(origin.Source) ?? ChatSource.Vision).Label()}" + (origin.Id.Length > 0 ? $" ({origin.Id})" : ""));
        if (session.Cwd is { Length: > 0 } cwd) lines.Add("- Folder: " + cwd);
        var messages = list.Count(r => r.Kind is "user" or "assistant");
        if (list.Count > 0) lines.Add($"- {messages} message{(messages == 1 ? "" : "s")}, {Stamp(list[0].At)} – {Stamp(list[^1].At)}");
        lines.AddRange(["", "---", ""]);
        foreach (var row in list)
        {
            switch (row.Kind)
            {
                case "user":
                    lines.AddRange([$"**You** · {Stamp(row.At)}", "", row.Text ?? "", ""]);
                    break;
                case "assistant":
                    lines.AddRange([$"**Assistant** · {Stamp(row.At)}", "", row.Text ?? "", ""]);
                    break;
                case "tool":
                    var args = string.IsNullOrEmpty(row.ArgSummary) ? "" : $" `{row.ArgSummary.Replace('`', '\'')}`";
                    var result = row.Text is null ? "" : $" → {row.Text}";
                    lines.AddRange([$"**Tool: {row.ToolName ?? "tool"}**{args}{result}{(row.IsError ? " (failed)" : "")}", ""]);
                    if (row.Output is { Length: > 0 } output)
                    {
                        var clipped = ChatRowBuilder.Cap(output, ChatRowBuilder.MaxToolOutput);
                        var fence = Fence(clipped);
                        lines.AddRange(["<details><summary>Output</summary>", "", fence + "text", clipped, fence, "", "</details>", ""]);
                    }
                    break;
                case "notice":
                    lines.AddRange((row.Text ?? "").Split('\n').Select(l => "> " + l));
                    lines.Add("");
                    break;
                case "error":
                    lines.AddRange([$"**Error** · {Stamp(row.At)}", "", row.Text ?? "", ""]);
                    break;
                case "compaction":
                    lines.AddRange([$"**Earlier conversation, summarized** · {Stamp(row.At)}", "", row.Text ?? "", ""]);
                    break;
            }
        }
        return string.Join("\n", lines);
    }

    /// <summary>"2026-10-19 14:02", local time.</summary>
    private static string Stamp(DateTimeOffset at) => at.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    /// <summary>A code fence no run of backticks in <paramref name="text"/> closes early.</summary>
    internal static string Fence(string text)
    {
        int longest = 0, run = 0;
        foreach (var c in text)
        {
            run = c == '`' ? run + 1 : 0;
            longest = Math.Max(longest, run);
        }
        return new string('`', Math.Max(3, longest + 1));
    }

    /// <summary>A file name for the chat: its title without path separators or characters Windows and Finder refuse, at most 80
    /// long ("Chat" when nothing is left).</summary>
    public static string FileName(LogSession session, string extension)
    {
        var banned = "/\\:*?\"<>|\n\r\t";
        var name = new string(session.Title.Select(c => banned.Contains(c) ? ' ' : c).ToArray()).Trim(' ', '.');
        if (name.Length > 80) name = TextUtil.Prefix(name, 80).Trim();
        if (name.Length == 0) name = "Chat";
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
