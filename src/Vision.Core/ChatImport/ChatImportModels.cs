using System.Globalization;
using System.Text.Json.Serialization;

namespace Vision.Core;

// MARK: - Chats from the other AIs on this PC, as Vision chats
//
// Patrick's "mixing bowl": the conversations people had in Claude Code, Codex, OpenClaw, Claude.ai, ChatGPT and Guardian
// come into Vision as REAL chats — the same stored rows a chat typed here has (user, assistant, tool, notice, compaction),
// with their own timestamps — so any model Vision talks to can carry them on. Tool calls become tool rows (name, a one-line
// summary of the arguments, the output), which the replay folds into readable text for whichever engine continues.
//
//   Claude Code   ~\.claude\projects\<project>\<session>.jsonl          (subagent transcripts are left out)
//   Codex CLI     ~\.codex\sessions\YYYY\MM\DD\rollout-*.jsonl          (titles from session_index.jsonl)
//   OpenClaw      ~\.openclaw\agents\<agent>\agent\openclaw-agent.sqlite (2026.8+) and agents\<agent>\sessions\*.jsonl
//   Claude.ai     the data export's .zip (conversations.json)
//   ChatGPT       the data export's .zip (conversations.json: a mapping tree per conversation)
//   Guardian      GET /api/chats + the paged GET /api/chats/:id (chats in Guardian), GET /api/harness/chats (Vision chats
//                 saved in Guardian)
//
// Nothing is changed where it is read from, nothing is run, and nothing leaves the PC except the Guardian calls.

/// <summary>Where an imported chat came from. The raw values are what <see cref="ChatOrigin.Source"/> stores, the same on
/// Vision for macOS.</summary>
public enum ChatSource { Vision, ClaudeCode, Codex, OpenClaw, ClaudeAi, ChatGpt, Guardian }

public static class ChatSources
{
    public static IReadOnlyList<ChatSource> All { get; } =
        [ChatSource.ClaudeCode, ChatSource.Codex, ChatSource.OpenClaw, ChatSource.ClaudeAi, ChatSource.ChatGpt, ChatSource.Guardian, ChatSource.Vision];

    public static string RawValue(this ChatSource source) => source switch
    {
        ChatSource.ClaudeCode => "claude-code",
        ChatSource.Codex => "codex",
        ChatSource.OpenClaw => "openclaw",
        ChatSource.ClaudeAi => "claude-ai",
        ChatSource.ChatGpt => "chatgpt",
        ChatSource.Guardian => "guardian",
        _ => "vision",
    };

    public static ChatSource? FromRaw(string? raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "claude-code" => ChatSource.ClaudeCode,
        "codex" => ChatSource.Codex,
        "openclaw" => ChatSource.OpenClaw,
        "claude-ai" => ChatSource.ClaudeAi,
        "chatgpt" => ChatSource.ChatGpt,
        "guardian" => ChatSource.Guardian,
        "vision" => ChatSource.Vision,
        _ => null,
    };

    public static string Label(this ChatSource source) => source switch
    {
        ChatSource.ClaudeCode => "Claude Code",
        ChatSource.Codex => "Codex",
        ChatSource.OpenClaw => "OpenClaw",
        ChatSource.ClaudeAi => "Claude.ai",
        ChatSource.ChatGpt => "ChatGPT",
        ChatSource.Guardian => "Guardian",
        _ => "Vision",
    };

    /// <summary>The source of a chat in the log: where it was imported from, or Vision for one typed here.</summary>
    public static ChatSource Of(LogSession session) => FromRaw(session.Imported?.Source) ?? ChatSource.Vision;
}

/// <summary>Where an imported chat came from, kept on the chat (<see cref="LogSession.Imported"/>): the dedupe key
/// (<see cref="Source"/> + <see cref="Id"/>), what it was read from and what it held when it came in.</summary>
public sealed record ChatOrigin
{
    /// <summary>A <see cref="ChatSource"/> raw value: claude-code, codex, openclaw, claude-ai, chatgpt, guardian, vision.</summary>
    public string Source { get; init; } = "";
    /// <summary>The chat's id where it came from (a session id, a conversation uuid, a Guardian chat id).</summary>
    public string Id { get; init; } = "";
    /// <summary>What it was read from: a file, an export, or the Guardian address.</summary>
    public string? From { get; init; }
    /// <summary>The folder it ran in there (Claude Code's and Codex's cwd), whether or not it exists on this PC.</summary>
    public string? Project { get; init; }
    public string? Model { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
    /// <summary>Its user and assistant messages when it came in.</summary>
    public int Messages { get; init; }
    public DateTimeOffset ImportedAt { get; init; }

    /// <summary>"claude-code:5d1c…" — what a re-import compares.</summary>
    [JsonIgnore]
    public string Key => ChatCandidate.KeyOf(Source, Id);
}

public enum ChatImportStatus
{
    /// <summary>Not in Vision yet.</summary>
    New,
    /// <summary>Imported before (or, for a Vision chat saved in Guardian, already on this PC): a re-import leaves it.</summary>
    Imported,
}

/// <summary>One chat a scan found, for the preview: what it is and how big, and how to read it in full.</summary>
public sealed record ChatCandidate
{
    public required ChatSource Source { get; init; }
    public required string SourceId { get; init; }
    public required string Title { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    /// <summary>User and assistant messages; null while not known yet (Guardian counts arrive after the list).</summary>
    public int? Messages { get; init; }
    /// <summary>The folder it ran in there.</summary>
    public string? Project { get; init; }
    /// <summary>Where it was read from (a file, an export, Guardian's address).</summary>
    public string Location { get; init; } = "";
    /// <summary>A short remark for the preview: "Scheduled run", "Shared by Ada", "3 subagent transcripts left out".</summary>
    public string? Note { get; init; }
    /// <summary>A scheduled or automated run (OpenClaw cron): listed, but not ticked for you.</summary>
    public bool Automated { get; init; }
    /// <summary>Someone else's chat shared with the person (Guardian): listed, but not ticked for you.</summary>
    public bool Shared { get; init; }
    public ChatImportStatus Status { get; init; }
    /// <summary>Reads the whole chat (the preview only summarised it).</summary>
    public Func<CancellationToken, Task<ImportedChat?>>? Load { get; init; }

    public string Key => KeyOf(Source.RawValue(), SourceId);

    /// <summary>Ticked when the preview opens: new, and neither an automated run nor someone else's.</summary>
    public bool SelectedByDefault => Status == ChatImportStatus.New && !Automated && !Shared && Messages is not 0;

    public static string KeyOf(string source, string id) => source + ":" + id;
}

/// <summary>A chat read in full, converted to Vision's stored rows.</summary>
public sealed record ImportedChat
{
    public required ChatSource Source { get; init; }
    public required string SourceId { get; init; }
    public required string Title { get; init; }
    /// <summary>Rows with SessionId "" and Seq in order; the importer gives them the new chat's id.</summary>
    public required IReadOnlyList<LogItemRow> Rows { get; init; }
    public string? Project { get; init; }
    public string? Model { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public string? From { get; init; }
    /// <summary>A Vision chat (saved in Guardian, or exported as JSON) keeps its id, so the same chat is one chat on every PC.</summary>
    public string? KeepId { get; init; }
    /// <summary>What the conversion left out, said plainly (subagent transcripts, images).</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>User and assistant messages.</summary>
    public int Messages => Rows.Count(r => r.Kind is "user" or "assistant");

    /// <summary>The preview's line for it, from what was read.</summary>
    public ChatCandidate ToCandidate(string location, Func<CancellationToken, Task<ImportedChat?>>? load = null) => new()
    {
        Source = Source,
        SourceId = SourceId,
        Title = Title,
        StartedAt = StartedAt,
        UpdatedAt = UpdatedAt,
        Messages = Messages,
        Project = Project,
        Location = location,
        Note = Notes.Count > 0 ? Notes[0] : null,
        Load = load ?? (_ => Task.FromResult<ImportedChat?>(this)),
    };
}

/// <summary>What an import did.</summary>
public sealed record ChatImportResult
{
    /// <summary>The new chats' ids, in the order they were written.</summary>
    public IReadOnlyList<string> Imported { get; init; } = [];
    public int AlreadyThere { get; init; }
    public IReadOnlyList<(ChatCandidate Chat, string Reason)> Failed { get; init; } = [];
    public int Messages { get; init; }

    /// <summary>"Imported 12 chats (1,840 messages). 3 were already in Vision. 1 couldn't be read."</summary>
    public string Summary()
    {
        var parts = new List<string>
        {
            Imported.Count == 0 ? "Nothing new to import." : $"Imported {Words.Count(Imported.Count, "chat", "chats")} ({Words.Count(Messages, "message", "messages")}).",
        };
        if (AlreadyThere > 0) parts.Add($"{AlreadyThere} {(AlreadyThere == 1 ? "was" : "were")} already in Vision.");
        if (Failed.Count > 0) parts.Add($"{Failed.Count} couldn't be read.");
        return string.Join(" ", parts);
    }
}

internal static class Words
{
    public static string Count(int n, string one, string many) => n == 1 ? $"1 {one}" : $"{n.ToString("N0", CultureInfo.InvariantCulture)} {many}";
}
