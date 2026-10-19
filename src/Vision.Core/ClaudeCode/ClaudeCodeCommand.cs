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
    /// session saved — and no permission or session flags.</summary>
    public string? OneShotSystemPrompt { get; init; }
}

public static class ClaudeCodeCommand
{
    /// <summary>The flags Vision can drop when an older CLI says it doesn't know them (the turn still works without).</summary>
    public static readonly IReadOnlySet<string> OptionalFlags = new HashSet<string>(StringComparer.Ordinal)
    {
        "--effort", "--include-partial-messages", "--append-system-prompt", "--add-dir",
    };

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
        if (o.OneShotSystemPrompt is { } system)
        {
            args.AddRange(["--system-prompt", system, "--tools", "", "--no-session-persistence"]);
            return args;
        }
        args.Add("--permission-mode");
        args.Add(PermissionMode(o.Preset));
        args.Add("--permission-prompt-tool");
        args.Add("stdio");
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
