using System.Text.Json;
using System.Text.Json.Nodes;

namespace Vision.Core;

// MARK: - One turn's command line and what goes down stdin

/// <summary>Everything one `claude -p` turn is started with.</summary>
public sealed record ClaudeCodeTurnOptions
{
    /// <summary>The CLI's model alias (opus, sonnet, haiku) or a full model id.</summary>
    public required string Model { get; init; }
    public PermissionPreset Preset { get; init; } = PermissionPreset.WorkspaceWrite;
    /// <summary>The chat's Claude Code session, carried on (--resume).</summary>
    public string? ResumeSession { get; init; }
    /// <summary>A new session under this id (--session-id) — the chat's first turn, or one after its session was lost.</summary>
    public string? NewSession { get; init; }
    public ThinkingLevel? Thinking { get; init; }
    /// <summary>Added to Claude Code's own system prompt (Vision's notes: the /goal end markers, the attachments folder).</summary>
    public string? AppendSystemPrompt { get; init; }
    /// <summary>More folders Claude Code may read (--add-dir): where a long attachment's full text is saved.</summary>
    public IReadOnlyList<string> AddDirs { get; init; } = [];
    /// <summary>Stream text and thinking as they are written (--include-partial-messages).</summary>
    public bool PartialMessages { get; init; } = true;
    /// <summary>Optional flags this CLI said it doesn't know ("--effort"): left out.</summary>
    public IReadOnlySet<string> Unsupported { get; init; } = new HashSet<string>();
    /// <summary>A one-off side task (a summary, a skill draft): Claude Code's system prompt replaced by this, no tools, no
    /// MCP servers, only the person's own settings, no session saved — and no permission or session flags.</summary>
    public string? OneShotSystemPrompt { get; init; }
    /// <summary>The one-off's system prompt saved in this file instead (--system-prompt-file): a long one would pass Windows'
    /// 32,767-character command line (8,191 through cmd.exe).</summary>
    public string? OneShotSystemPromptFile { get; init; }
    /// <summary>The folder's own Claude Code settings load too — the person trusted it in Claude Code (<see cref="ClaudeCodeTrust"/>);
    /// false = only the person's own (--setting-sources user), so a cloned repository's hooks and MCP servers don't run.</summary>
    public bool ProjectSettings { get; init; }
    /// <summary>At most this many agentic turns (--max-turns): a subagent job's step budget. Null = Claude Code's own limit.</summary>
    public int? MaxTurns { get; init; }
    /// <summary>Keep no session file (--no-session-persistence): a subagent job is one-off, and its session must not appear among
    /// the person's Claude Code sessions (which the chat import reads).</summary>
    public bool NoSessionPersistence { get; init; }
}

public static class ClaudeCodeCommand
{
    /// <summary>The flags Vision can drop when an older CLI says it doesn't know them (the turn still works without).</summary>
    public static readonly IReadOnlySet<string> OptionalFlags = new HashSet<string>(StringComparer.Ordinal)
    {
        "--effort", "--include-partial-messages", "--append-system-prompt", "--add-dir", "--max-turns", "--no-session-persistence",
    };

    /// <summary>What a one-off can also do without: it runs with no tools in the temp folder, so neither the folder's settings
    /// nor MCP servers could do much there.</summary>
    public static readonly IReadOnlySet<string> OneShotOptionalFlags = new HashSet<string>(StringComparer.Ordinal)
    {
        "--effort", "--include-partial-messages", "--setting-sources", "--strict-mcp-config",
    };

    /// <summary>A one-off's system prompt longer than this goes in a file (<see cref="ClaudeCodeTurnOptions.OneShotSystemPromptFile"/>).</summary>
    public const int SystemPromptFileThreshold = 8_000;

    /// <summary>Claude Code's permission mode for a chat's preset. Plan only → plan (Claude Code itself refuses to change
    /// anything); Workspace write → default (every tool call that needs permission is asked of Vision, which decides with
    /// the chat's own policy — writes inside the project and read-only commands go ahead, the rest asks the person);
    /// Full access → acceptEdits (edits go ahead without a round trip; Vision still answers the rest, all yes).</summary>
    public static string PermissionMode(PermissionPreset preset) => preset switch
    {
        PermissionPreset.Plan => "plan",
        PermissionPreset.FullAccess => "acceptEdits",
        _ => "default",
    };

    /// <summary>Claude Code's effort for a thinking level (it has no "off": Off asks for the least).</summary>
    public static string Effort(ThinkingLevel level) => level switch
    {
        ThinkingLevel.Off or ThinkingLevel.Low => "low",
        ThinkingLevel.Medium => "medium",
        ThinkingLevel.High => "high",
        _ => "max",
    };

    /// <summary>The arguments after the program (and its prefix): headless, stream-json both ways, the chat's model,
    /// permission mode and session, with permission questions sent to Vision over the control protocol.</summary>
    public static IReadOnlyList<string> Args(ClaudeCodeTurnOptions o)
    {
        var args = new List<string>
        {
            "-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose",
        };
        void Optional(string flag, params string[] values)
        {
            if (o.Unsupported.Contains(flag)) return;
            args.Add(flag);
            args.AddRange(values);
        }
        if (o.PartialMessages) Optional("--include-partial-messages");
        args.Add("--model");
        args.Add(o.Model);
        if (o.Thinking is { } level) Optional("--effort", Effort(level));
        if (o.OneShotSystemPrompt is not null || o.OneShotSystemPromptFile is not null)
        {
            Optional("--setting-sources", "user");
            Optional("--strict-mcp-config");
            if (o.OneShotSystemPromptFile is { } file) args.AddRange(["--system-prompt-file", file]);
            else args.AddRange(["--system-prompt", o.OneShotSystemPrompt!]);
            args.AddRange(["--tools", "", "--no-session-persistence"]);
            return args;
        }
        args.Add("--permission-mode");
        args.Add(PermissionMode(o.Preset));
        args.Add("--permission-prompt-tool");
        args.Add("stdio");
        // Only the person's own settings unless they trusted the folder in Claude Code (headless shows no trust question).
        if (!o.ProjectSettings && !o.Unsupported.Contains("--setting-sources")) args.AddRange(["--setting-sources", "user"]);
        if (o.ResumeSession is { Length: > 0 } resume)
        {
            args.Add("--resume");
            args.Add(resume);
        }
        else if (o.NewSession is { Length: > 0 } fresh)
        {
            args.Add("--session-id");
            args.Add(fresh);
        }
        if (o.AppendSystemPrompt is { Length: > 0 } extra) Optional("--append-system-prompt", extra);
        foreach (var dir in o.AddDirs.Where(d => d.Length > 0)) Optional("--add-dir", dir);
        if (o.MaxTurns is > 0 and var turns) Optional("--max-turns", turns.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (o.NoSessionPersistence) Optional("--no-session-persistence");
        return args;
    }

    /// <summary>The flag in "error: unknown option '--effort'" (null when the error is about something else).</summary>
    public static string? UnknownOption(string stderr)
    {
        const string marker = "unknown option '";
        var at = stderr.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (at < 0) return null;
        var start = at + marker.Length;
        var end = stderr.IndexOf('\'', start);
        return end > start ? stderr[start..end] : null;
    }

    /// <summary>How Claude Code takes attachments, within what the Messages API accepts in one request (32 MB, base64
    /// included; a picture's 5 MB counted on its base64): up to 20 pictures (PNG, JPEG, GIF, WebP) of at most 3.75 MB and up
    /// to 5 PDFs of at most 20 MB, at most 20 MB of them per message (<see cref="AttachmentRoute.MaxRequestMediaBytes"/>), as
    /// themselves; past that — and everything else — as the text Vision read out of it. The same limits as Vision for macOS.</summary>
    public static readonly AttachmentRoute Attachments = new()
    {
        Vision = true,
        Documents = true,
        ImageTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "image/png", "image/jpeg", "image/gif", "image/webp" },
        MaxImages = 20,
        MaxImageBytes = 3_750_000,
        MaxDocuments = 5,
        MaxDocumentBytes = 20L * 1024 * 1024,
        MaxRequestMediaBytes = 20L * 1024 * 1024,
    };

    /// <summary>The attachments as the user message's content blocks (<see cref="Attachments"/>).</summary>
    public static IReadOnlyList<AttachmentWirePart> AttachmentParts(IReadOnlyList<MessageAttachment> attachments) =>
        AttachmentWire.Parts(attachments, Attachments);

    private static readonly JsonSerializerOptions Compact = new() { WriteIndented = false };

    /// <summary>The prompt, as the stream-json line that starts a turn: its text, then pictures and PDFs as content blocks.</summary>
    public static string UserMessage(string text, IReadOnlyList<AttachmentWirePart>? parts = null)
    {
        var content = new JsonArray();
        if (text.Length > 0) content.Add(new JsonObject { ["type"] = "text", ["text"] = text });
        foreach (var part in parts ?? [])
        {
            switch (part)
            {
                case AttachmentWirePart.Text t:
                    content.Add(new JsonObject { ["type"] = "text", ["text"] = t.Value });
                    break;
                case AttachmentWirePart.Image image:
                    content.Add(new JsonObject
                    {
                        ["type"] = "image",
                        ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = image.Mime, ["data"] = Convert.ToBase64String(image.Data) },
                    });
                    break;
                case AttachmentWirePart.Document doc:
                    content.Add(new JsonObject
                    {
                        ["type"] = "document",
                        ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = "application/pdf", ["data"] = Convert.ToBase64String(doc.Data) },
                        ["title"] = doc.Name,
                    });
                    break;
            }
        }
        if (content.Count == 0) content.Add(new JsonObject { ["type"] = "text", ["text"] = "(empty message)" });
        var message = new JsonObject
        {
            ["type"] = "user",
            ["message"] = new JsonObject { ["role"] = "user", ["content"] = content },
            ["parent_tool_use_id"] = null,
            ["session_id"] = "",
        };
        return message.ToJsonString(Compact);
    }

    /// <summary>Stop the turn (Claude Code ends it cleanly, keeping what was done, and says so in its result).</summary>
    public static string Interrupt(string requestId) =>
        new JsonObject
        {
            ["type"] = "control_request",
            ["request_id"] = requestId,
            ["request"] = new JsonObject { ["subtype"] = "interrupt" },
        }.ToJsonString(Compact);

    /// <summary>Yes to a permission question: the tool runs with its input as asked.</summary>
    public static string Allow(string requestId, JsonNode? input, string? toolUseId) =>
        Response(requestId, new JsonObject
        {
            ["behavior"] = "allow",
            ["updatedInput"] = input?.DeepClone() ?? new JsonObject(),
            ["toolUseID"] = toolUseId,
        });

    /// <summary>No to a permission question: Claude Code tells the model <paramref name="message"/> instead of running it.</summary>
    public static string Deny(string requestId, string message, string? toolUseId) =>
        Response(requestId, new JsonObject
        {
            ["behavior"] = "deny",
            ["message"] = message,
            ["toolUseID"] = toolUseId,
        });

    /// <summary>A control request Vision doesn't handle: an error answer, so the CLI never waits on it.</summary>
    public static string Unhandled(string requestId, string subtype) =>
        new JsonObject
        {
            ["type"] = "control_response",
            ["response"] = new JsonObject
            {
                ["subtype"] = "error",
                ["request_id"] = requestId,
                ["error"] = $"Vision doesn't handle {subtype} requests.",
            },
        }.ToJsonString(Compact);

    private static string Response(string requestId, JsonObject payload)
    {
        if (payload["toolUseID"] is null) payload.Remove("toolUseID");
        return new JsonObject
        {
            ["type"] = "control_response",
            ["response"] = new JsonObject { ["subtype"] = "success", ["request_id"] = requestId, ["response"] = payload },
        }.ToJsonString(Compact);
    }
}
