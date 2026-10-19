using System.IO.Compression;

namespace Vision.Core.Tests;

/// <summary>Chat files in each tool's real shape, with made-up content. The shapes were read off the files these tools wrote
/// on a working Mac (Claude Code 2.1, Codex Desktop 0.117, OpenClaw 2026.8 — keys, nesting, ordering and the odd entries
/// each writes: rewound branches, sidechains, compaction, task notices, synthetic errors, harness context); every value here
/// is invented, nothing is copied from a real conversation.</summary>
internal static class ChatFixtures
{
    // MARK: Claude Code

    public const string ClaudeSessionId = "5d1c0e2a-7b3f-4c1d-9e8a-0f1b2c3d4e5f";

    /// <summary>One session: a prompt (with a reminder appended), a reply split into thinking/text/tool blocks, tool results
    /// out of order, an abandoned (rewound) answer, a sidechain, a slash command and its output, a meta caveat, a background
    /// task's notice, a compaction, an image, a synthetic API error, an interruption, titles, and a line that isn't JSON.</summary>
    public static readonly string ClaudeSession = string.Join("\n",
        """{"type":"queue-operation","operation":"enqueue","timestamp":"2026-10-01T09:00:00.000Z","sessionId":"S","content":"Fix the login bug"}""",
        """{"parentUuid":null,"isSidechain":false,"userType":"external","cwd":"C:\\code\\shop","sessionId":"S","version":"2.1.0","gitBranch":"main","type":"user","message":{"role":"user","content":"Fix the login bug in auth.ts\n<system-reminder>Today is a Tuesday.</system-reminder>"},"uuid":"u1","timestamp":"2026-10-01T09:00:01.000Z","origin":{"kind":"human"},"promptSource":"sdk"}""",
        """{"parentUuid":"u1","isSidechain":false,"type":"attachment","attachment":{"type":"hook_additional_context","content":["ctx"]},"uuid":"a1","timestamp":"2026-10-01T09:00:01.500Z","cwd":"C:\\code\\shop"}""",
        """{"parentUuid":"a1","isSidechain":false,"type":"assistant","message":{"id":"msg_1","model":"claude-opus-4-5","role":"assistant","content":[{"type":"thinking","thinking":"","signature":"sig"}]},"uuid":"u2","timestamp":"2026-10-01T09:00:03.000Z","cwd":"C:\\code\\shop"}""",
        """{"parentUuid":"u2","isSidechain":false,"type":"assistant","message":{"id":"msg_1","model":"claude-opus-4-5","role":"assistant","content":[{"type":"text","text":"Let me look at the code."}]},"uuid":"u3","timestamp":"2026-10-01T09:00:04.000Z"}""",
        """{"parentUuid":"u3","isSidechain":false,"type":"assistant","message":{"id":"msg_1","model":"claude-opus-4-5","role":"assistant","content":[{"type":"tool_use","id":"toolu_1","name":"Bash","input":{"command":"grep -n login src/auth.ts","description":"Find login"}}]},"uuid":"u4","timestamp":"2026-10-01T09:00:05.000Z"}""",
        """{"parentUuid":"u4","isSidechain":false,"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_1","type":"tool_result","content":"12: export function login() {\n<system-reminder>noise</system-reminder>","is_error":false}]},"uuid":"u5","timestamp":"2026-10-01T09:00:06.000Z","toolUseResult":{"stdout":"12: export function login() {"}}""",
        """{"parentUuid":"u5","isSidechain":false,"type":"assistant","message":{"id":"msg_2","model":"claude-opus-4-5","role":"assistant","content":[{"type":"tool_use","id":"toolu_2","name":"Read","input":{"file_path":"C:\\code\\shop\\src\\auth.ts"}}]},"uuid":"u6","timestamp":"2026-10-01T09:00:07.000Z"}""",
        """{"parentUuid":"u6","isSidechain":false,"type":"assistant","message":{"id":"msg_2","model":"claude-opus-4-5","role":"assistant","content":[{"type":"tool_use","id":"toolu_3","name":"Grep","input":{"pattern":"session","path":"src"}}]},"uuid":"u7","timestamp":"2026-10-01T09:00:07.500Z"}""",
        """{"parentUuid":"u7","isSidechain":false,"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_3","type":"tool_result","content":"No matches found","is_error":true}]},"uuid":"u8","timestamp":"2026-10-01T09:00:08.000Z"}""",
        """{"parentUuid":"u8","isSidechain":false,"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_2","type":"tool_result","content":[{"type":"text","text":"     1\texport function login() {}"}]}]},"uuid":"u9","timestamp":"2026-10-01T09:00:09.000Z"}""",
        """{"parentUuid":"u9","isSidechain":false,"type":"assistant","message":{"id":"msg_x","model":"claude-opus-4-5","role":"assistant","content":[{"type":"text","text":"ABANDONED ANSWER"}]},"uuid":"x1","timestamp":"2026-10-01T09:00:10.000Z"}""",
        """{"parentUuid":"u9","isSidechain":true,"type":"user","message":{"role":"user","content":"SIDECHAIN PROMPT"},"uuid":"sc1","timestamp":"2026-10-01T09:00:10.500Z"}""",
        """{"parentUuid":"u9","isSidechain":false,"type":"assistant","message":{"id":"msg_3","model":"claude-opus-4-5","role":"assistant","content":[{"type":"text","text":"Fixed: login now checks the session."}]},"uuid":"u10","timestamp":"2026-10-01T09:00:11.000Z"}""",
        """{"type":"custom-title","customTitle":"Login bug fix","sessionId":"S"}""",
        """{"parentUuid":"u10","isSidechain":false,"type":"user","message":{"role":"user","content":"<command-name>/model</command-name>\n            <command-message>model</command-message>\n            <command-args>opus</command-args>"},"uuid":"u11","timestamp":"2026-10-01T09:05:00.000Z"}""",
        """{"parentUuid":"u11","isSidechain":false,"type":"user","message":{"role":"user","content":"<local-command-stdout>Set model to opus</local-command-stdout>"},"uuid":"u12","timestamp":"2026-10-01T09:05:01.000Z"}""",
        """{"parentUuid":"u12","isSidechain":false,"type":"user","isMeta":true,"message":{"role":"user","content":"Caveat: The messages below were generated by the user while running local commands."},"uuid":"u13","timestamp":"2026-10-01T09:05:02.000Z"}""",
        """{"parentUuid":"u13","isSidechain":false,"type":"user","message":{"role":"user","content":"<task-notification>\n<task-id>b1</task-id>\n<status>completed</status>\n<summary>Tests passed</summary>\n</task-notification>"},"uuid":"u14","timestamp":"2026-10-01T09:06:00.000Z","origin":{"kind":"task-notification"},"promptSource":"system"}""",
        """{"parentUuid":null,"logicalParentUuid":"u14","isSidechain":false,"type":"system","subtype":"compact_boundary","content":"Conversation compacted","isMeta":false,"uuid":"cb1","timestamp":"2026-10-01T09:10:00.000Z","level":"info","compactMetadata":{"trigger":"auto","preTokens":180000}}""",
        """{"parentUuid":"cb1","isSidechain":false,"type":"user","message":{"role":"user","content":"This session is being continued from a previous conversation. Summary: fixed login."},"isCompactSummary":true,"isVisibleInTranscriptOnly":true,"uuid":"u15","timestamp":"2026-10-01T09:10:01.000Z"}""",
        """{"parentUuid":"u15","isSidechain":false,"type":"user","message":{"role":"user","content":[{"type":"text","text":"What is wrong in this screenshot?"},{"type":"image","source":{"type":"base64","media_type":"image/png","data":"iVBORw0KGgo="}}]},"uuid":"u16","timestamp":"2026-10-01T09:11:00.000Z"}""",
        """{"parentUuid":"u16","isSidechain":false,"type":"assistant","message":{"id":"msg_4","model":"<synthetic>","role":"assistant","content":[{"type":"text","text":"API Error: 529 Overloaded"}]},"uuid":"u17","timestamp":"2026-10-01T09:11:05.000Z"}""",
        """{"parentUuid":"u17","isSidechain":false,"type":"user","message":{"role":"user","content":[{"type":"text","text":"[Request interrupted by user]"}]},"uuid":"u18","timestamp":"2026-10-01T09:11:06.000Z"}""",
        """{"parentUuid":"u18","isSidechain":false,"type":"user","message":{"role":"user","content":"try again"},"uuid":"u19","timestamp":"2026-10-01T09:12:00.000Z"}""",
        """{"parentUuid":"u19","isSidechain":false,"type":"assistant","message":{"id":"msg_5","model":"claude-opus-4-5","role":"assistant","content":[{"type":"text","text":"The button is hidden behind the banner."}]},"uuid":"u20","timestamp":"2026-10-01T09:12:30.000Z"}""",
        """{"type":"ai-title","aiTitle":"Auto title","sessionId":"S"}""",
        """{"type":"last-prompt","lastPrompt":"try again","leafUuid":"u20","sessionId":"S"}""",
        "not json at all");

    /// <summary>A subagent's transcript beside the session (left out, counted).</summary>
    public const string ClaudeSubagent =
        """{"parentUuid":null,"isSidechain":true,"type":"user","message":{"role":"user","content":"Search the repo"},"uuid":"s1","timestamp":"2026-10-01T09:00:06.000Z","agentId":"a1"}""";

    /// <summary>A Claude Code profile with the session, its subagent, a second project's empty session and a title-only file.</summary>
    public static string WriteClaudeProfile(TempDirectory temp)
    {
        var project = "projects/C--code-shop/";
        temp.Write(project + ClaudeSessionId + ".jsonl", ClaudeSession);
        temp.Write(project + ClaudeSessionId + "/subagents/agent-a1.jsonl", ClaudeSubagent);
        temp.Write("projects/C--code-other/0b0b0b0b-0000-4000-8000-000000000000.jsonl",
            """{"type":"custom-title","customTitle":"Never used","sessionId":"0b0b"}""");
        return temp.Path;
    }

    // MARK: Codex

    public const string CodexId = "019aaaaa-bbbb-7ccc-8ddd-eeeeeeeeeeee";

    public static readonly string CodexRollout = string.Join("\n",
        """{"timestamp":"2026-09-02T10:00:00.000Z","type":"session_meta","payload":{"id":"019aaaaa-bbbb-7ccc-8ddd-eeeeeeeeeeee","timestamp":"2026-09-02T10:00:00.000Z","cwd":"C:\\code\\shop","originator":"codex_cli_rs","cli_version":"0.40.0","model_provider":"openai","base_instructions":{"text":"You are Codex."}}}""",
        """{"timestamp":"2026-09-02T10:00:00.100Z","type":"response_item","payload":{"type":"message","role":"developer","content":[{"type":"input_text","text":"<permissions instructions>sandboxed</permissions instructions>"}]}}""",
        """{"timestamp":"2026-09-02T10:00:00.200Z","type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"<environment_context>\n  <cwd>C:\\code\\shop</cwd>\n</environment_context>"}]}}""",
        """{"timestamp":"2026-09-02T10:00:01.000Z","type":"turn_context","payload":{"turn_id":"t1","cwd":"C:\\code\\shop","model":"gpt-5.5","effort":"medium"}}""",
        """{"timestamp":"2026-09-02T10:00:01.500Z","type":"event_msg","payload":{"type":"task_started","turn_id":"t1"}}""",
        """{"timestamp":"2026-09-02T10:00:02.000Z","type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"List the files"}]}}""",
        """{"timestamp":"2026-09-02T10:00:02.000Z","type":"event_msg","payload":{"type":"user_message","message":"List the files","images":[],"local_images":[],"text_elements":[]}}""",
        """{"timestamp":"2026-09-02T10:00:03.000Z","type":"response_item","payload":{"type":"reasoning","summary":[],"content":null,"encrypted_content":"gAAAA"}}""",
        """{"timestamp":"2026-09-02T10:00:04.000Z","type":"response_item","payload":{"type":"message","role":"assistant","content":[{"type":"output_text","text":"I'll list them."}],"phase":"commentary"}}""",
        """{"timestamp":"2026-09-02T10:00:04.000Z","type":"event_msg","payload":{"type":"agent_message","message":"I'll list them.","phase":"commentary"}}""",
        """{"timestamp":"2026-09-02T10:00:05.000Z","type":"response_item","payload":{"type":"function_call","name":"exec_command","arguments":"{\"cmd\":\"ls\",\"workdir\":\"C:\\\\code\\\\shop\"}","call_id":"call_1"}}""",
        """{"timestamp":"2026-09-02T10:00:06.000Z","type":"response_item","payload":{"type":"function_call_output","call_id":"call_1","output":"Command: ls\nChunk ID: ab12\nWall time: 0.01 seconds\nProcess exited with code 0\nOriginal token count: 4\nOutput:\nREADME.md\nsrc\n"}}""",
        """{"timestamp":"2026-09-02T10:00:07.000Z","type":"response_item","payload":{"type":"function_call","name":"exec_command","arguments":"{\"cmd\":\"cat missing.txt\"}","call_id":"call_2"}}""",
        """{"timestamp":"2026-09-02T10:00:08.000Z","type":"response_item","payload":{"type":"function_call_output","call_id":"call_2","output":"Command: cat missing.txt\nChunk ID: cd34\nWall time: 0.01 seconds\nProcess exited with code 1\nOriginal token count: 9\nOutput:\ncat: missing.txt: No such file or directory\n"}}""",
        """{"timestamp":"2026-09-02T10:00:09.000Z","type":"response_item","payload":{"type":"custom_tool_call","status":"completed","call_id":"call_3","name":"apply_patch","input":"*** Begin Patch\n*** Add File: notes.md\n+hi\n*** End Patch"}}""",
        """{"timestamp":"2026-09-02T10:00:10.000Z","type":"response_item","payload":{"type":"custom_tool_call_output","call_id":"call_3","output":"{\"output\":\"Success. Updated the following files:\\nA notes.md\\n\",\"metadata\":{\"exit_code\":0}}"}}""",
        """{"timestamp":"2026-09-02T10:00:11.000Z","type":"response_item","payload":{"type":"message","role":"assistant","content":[{"type":"output_text","text":"There are two entries: README.md and src."}],"phase":"final_answer"}}""",
        """{"timestamp":"2026-09-02T10:00:11.000Z","type":"event_msg","payload":{"type":"token_count","info":null}}""",
        """{"timestamp":"2026-09-02T10:00:12.000Z","type":"event_msg","payload":{"type":"task_complete","turn_id":"t1","last_agent_message":"There are two entries"}}""",
        """{"timestamp":"2026-09-02T10:30:00.000Z","type":"compacted","payload":{"message":"Summary: listed the files.","replacement_history":[]}}""",
        """{"timestamp":"2026-09-02T10:31:00.000Z","type":"event_msg","payload":{"type":"user_message","message":"Now delete notes.md","images":["data:image/png;base64,AAA"],"local_images":[]}}""",
        """{"timestamp":"2026-09-02T10:32:00.000Z","type":"event_msg","payload":{"type":"turn_aborted","reason":"interrupted"}}""");

    /// <summary>A rollout from before the payload wrapper (2025): a session line, state records, bare items.</summary>
    public static readonly string CodexOldRollout = string.Join("\n",
        """{"id":"0a1b2c3d-1111-2222-3333-444455556666","timestamp":"2025-05-01T08:00:00.000Z","instructions":null}""",
        """{"record_type":"state"}""",
        """{"type":"message","role":"user","content":[{"type":"input_text","text":"<user_instructions>be brief</user_instructions>"}]}""",
        """{"type":"message","role":"user","content":[{"type":"input_text","text":"hello"}]}""",
        """{"type":"message","role":"assistant","content":[{"type":"output_text","text":"hi"}]}""",
        """{"type":"function_call","name":"shell","arguments":"{\"command\":[\"bash\",\"-lc\",\"ls\"]}","call_id":"c1"}""",
        """{"type":"function_call_output","call_id":"c1","output":"{\"output\":\"a.txt\\n\",\"metadata\":{\"exit_code\":0,\"duration_seconds\":0.1}}"}""");

    public static string WriteCodexHome(TempDirectory temp)
    {
        temp.Write($".codex/sessions/2026/09/02/rollout-2026-09-02T10-00-00-{CodexId}.jsonl", CodexRollout);
        temp.Write(".codex/archived_sessions/rollout-2025-05-01T08-00-00-0a1b2c3d-1111-2222-3333-444455556666.jsonl", CodexOldRollout);
        temp.Write(".codex/session_index.jsonl", string.Join("\n",
            $$"""{"id":"{{CodexId}}","thread_name":"First name","updated_at":"2026-09-02T10:00:01Z"}""",
            $$"""{"id":"{{CodexId}}","thread_name":"Shop file listing","updated_at":"2026-09-02T10:00:12Z"}"""));
        return temp[".codex"];
    }

    // MARK: OpenClaw

    public static readonly string OpenClawSession = string.Join("\n",
        """{"type":"session","version":3,"id":"oc-1111","timestamp":"2026-09-10T12:00:00.000Z","cwd":"C:\\Users\\pat\\.openclaw\\workspace"}""",
        """{"type":"model_change","id":"e1","parentId":null,"timestamp":"2026-09-10T12:00:00.100Z","provider":"amazon-bedrock","modelId":"claude-opus-4-5"}""",
        """{"type":"message","id":"e2","parentId":"e1","timestamp":"2026-09-10T12:00:01.000Z","message":{"role":"user","content":[{"type":"text","text":"Check the disk on the build box"}],"timestamp":1789041601000}}""",
        """{"type":"custom","customType":"model-snapshot","data":{"provider":"amazon-bedrock"},"id":"e3","parentId":"e2","timestamp":"2026-09-10T12:00:02.000Z"}""",
        """{"type":"message","id":"e4","parentId":"e3","timestamp":"2026-09-10T12:00:03.000Z","message":{"role":"assistant","content":[{"type":"thinking","thinking":"","thinkingSignature":"x"},{"type":"text","text":"Checking."},{"type":"toolCall","id":"tc1","name":"exec","arguments":{"command":"df -h"}}],"model":"claude-opus-4-5","stopReason":"toolUse"}}""",
        """{"type":"message","id":"e5","parentId":"e4","timestamp":"2026-09-10T12:00:04.000Z","message":{"role":"toolResult","toolCallId":"tc1","toolName":"exec","content":[{"type":"text","text":"Filesystem Size Used\n/dev/disk1 500G 400G"}],"isError":false}}""",
        """{"type":"message","id":"e6","parentId":"e5","timestamp":"2026-09-10T12:00:05.000Z","message":{"role":"assistant","content":[{"type":"text","text":"ABANDONED"}]}}""",
        """{"type":"message","id":"e7","parentId":"e5","timestamp":"2026-09-10T12:00:06.000Z","message":{"role":"assistant","content":[{"type":"text","text":"The disk is 80% full."}]}}""",
        """{"type":"custom_message","customType":"openclaw.sessions_yield","content":"hidden","display":false,"id":"e8","parentId":"e7","timestamp":"2026-09-10T12:00:07.000Z"}""",
        """{"type":"compaction","id":"e9","parentId":"e8","timestamp":"2026-09-10T12:00:08.000Z","summary":"Checked the disk: 80% full.","firstKeptEntryId":"e7","tokensBefore":1000}""",
        """{"type":"custom_message","customType":"note","content":"Heads up: the box reboots at 2 am.","display":true,"id":"e10","parentId":"e9","timestamp":"2026-09-10T12:00:09.000Z"}""",
        """{"type":"message","id":"e11","parentId":"e10","timestamp":"2026-09-10T12:00:10.000Z","message":{"role":"assistant","content":[],"stopReason":"error","errorMessage":"Bedrock throttled the request"}}""",
        """{"type":"leaf","id":"e12","parentId":"e11","timestamp":"2026-09-10T12:00:11.000Z","targetId":"e11"}""");

    /// <summary>A state folder in the pre-2026.8 layout: sessions.json, the session, a subagent's, and a reset copy.</summary>
    public static string WriteOpenClawJsonl(TempDirectory temp)
    {
        var sessions = ".openclaw/agents/main/sessions/";
        temp.Write(sessions + "oc-1111.jsonl", OpenClawSession);
        temp.Write(sessions + "oc-sub.jsonl", OpenClawSession.Replace("oc-1111", "oc-sub"));
        temp.Write(sessions + "oc-old.jsonl.reset.2026-09-01T00-00-00.000Z", OpenClawSession.Replace("oc-1111", "oc-old"));
        temp.Write(sessions + "sessions.json", """
            {"agent:main:dashboard:abc":{"sessionId":"oc-1111","label":"Build box disk","updatedAt":1789041610000},
             "agent:main:subagent:def":{"sessionId":"oc-sub","label":"helper","updatedAt":1789041610000}}
            """);
        return temp[".openclaw"];
    }

    /// <summary>The 2026.8+ layout: an agent's SQLite store with a chat, a scheduled run and a subagent's session.</summary>
    public static string WriteOpenClawSqlite(TempDirectory temp)
    {
        var path = temp[".openclaw/agents/main/agent/openclaw-agent.sqlite"];
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var db = SqliteReader.OpenForWriting(path);
        db.Execute("CREATE TABLE session_windows (session_id TEXT PRIMARY KEY, session_key TEXT NOT NULL, display_name TEXT, created_at INTEGER NOT NULL, updated_at INTEGER NOT NULL, model TEXT)");
        db.Execute("CREATE TABLE transcript_events (session_id TEXT NOT NULL, seq INTEGER NOT NULL, event_json TEXT NOT NULL, created_at INTEGER NOT NULL, PRIMARY KEY (session_id, seq))");
        void Session(string id, string key, string? name, long updated, params string[] events)
        {
            db.Execute("INSERT INTO session_windows VALUES (?, ?, ?, ?, ?, ?)", id, key, name, "1789040000000", updated.ToString(), "claude-opus-4-5");
            for (var i = 0; i < events.Length; i++) db.Execute("INSERT INTO transcript_events VALUES (?, ?, ?, ?)", id, i.ToString(), events[i], "1789040000000");
        }
        Session("s-dash", "agent:main:dashboard:1", "Count the tickets", 1789041700000,
            """{"type":"session","version":3,"id":"s-dash","timestamp":"2026-09-10T12:01:00.000Z","cwd":"C:\\code\\desk"}""",
            """{"type":"message","id":"d1","parentId":null,"timestamp":"2026-09-10T12:01:01.000Z","message":{"role":"user","content":"How many tickets are open?"}}""",
            """{"type":"message","id":"d2","parentId":"d1","timestamp":"2026-09-10T12:01:05.000Z","message":{"role":"assistant","content":[{"type":"text","text":"Twelve are open."}]}}""");
        Session("s-cron", "agent:main:cron:9:run:s-cron", "Cron: ticket sweep", 1789041800000,
            """{"type":"session","version":3,"id":"s-cron","timestamp":"2026-09-10T12:30:00.000Z","cwd":"C:\\Users\\pat\\.openclaw\\workspace"}""",
            """{"type":"message","id":"c1","parentId":null,"timestamp":"2026-09-10T12:30:01.000Z","message":{"role":"user","content":"Sweep the queue"}}""",
            """{"type":"message","id":"c2","parentId":"c1","timestamp":"2026-09-10T12:30:09.000Z","message":{"role":"assistant","content":[{"type":"text","text":"Closed 3."}]}}""");
        Session("s-sub", "agent:main:subagent:3", "helper", 1789041900000,
            """{"type":"message","id":"h1","parentId":null,"timestamp":"2026-09-10T12:40:01.000Z","message":{"role":"user","content":"sub task"}}""",
            """{"type":"message","id":"h2","parentId":"h1","timestamp":"2026-09-10T12:40:02.000Z","message":{"role":"assistant","content":"done"}}""");
        return temp[".openclaw"];
    }

    // MARK: Claude.ai and ChatGPT exports

    public const string ClaudeAiConversations = """
        [
         {"uuid":"c-1","name":"Trip plan","summary":"","created_at":"2026-08-01T10:00:00.000000Z","updated_at":"2026-08-01T10:05:00.000000Z","account":{"uuid":"acct"},
          "chat_messages":[
           {"uuid":"m1","text":"Plan a trip","content":[{"start_timestamp":"2026-08-01T10:00:00Z","stop_timestamp":"2026-08-01T10:00:00Z","type":"text","text":"Plan a trip to Bend","citations":[]}],
            "sender":"human","created_at":"2026-08-01T10:00:00.000000Z","updated_at":"2026-08-01T10:00:00.000000Z",
            "attachments":[{"file_name":"notes.txt","file_size":10,"file_type":"txt","extracted_content":"Leave Friday"}],"files":[{"file_name":"map.png"}]},
           {"uuid":"m2","text":"","content":[{"type":"text","text":"Here is a plan."},
             {"type":"tool_use","name":"artifacts","input":{"id":"plan","type":"text/markdown","title":"Bend itinerary","command":"create","content":"# Day 1\nHike"}},
             {"type":"tool_result","name":"artifacts","content":[{"type":"text","text":"OK"}],"is_error":false},
             {"type":"text","text":"Enjoy!"}],
            "sender":"assistant","created_at":"2026-08-01T10:00:30.000000Z","updated_at":"2026-08-01T10:00:30.000000Z","attachments":[],"files":[]},
           {"uuid":"m3","text":"Thanks","content":[],"sender":"human","created_at":"2026-08-01T10:05:00.000000Z","updated_at":"2026-08-01T10:05:00.000000Z","attachments":[],"files":[]}
          ]},
         {"uuid":"c-2","name":"","created_at":"2026-08-02T10:00:00Z","updated_at":"2026-08-02T10:00:00Z","chat_messages":[]}
        ]
        """;

    public const string ChatGptConversations = """
        [{"title":"Python help","create_time":1756720000.5,"update_time":1756720100.0,"conversation_id":"g-1","id":"g-1","default_model_slug":"gpt-4o","current_node":"n6",
          "mapping":{
           "root":{"id":"root","message":null,"parent":null,"children":["n0"]},
           "n0":{"id":"n0","message":{"id":"n0","author":{"role":"system"},"create_time":null,"content":{"content_type":"text","parts":[""]},"metadata":{"is_visually_hidden_from_conversation":true},"recipient":"all"},"parent":"root","children":["n1"]},
           "n1":{"id":"n1","message":{"id":"n1","author":{"role":"user"},"create_time":1756720001.0,"content":{"content_type":"multimodal_text","parts":[{"content_type":"image_asset_pointer","asset_pointer":"file-service://x"},"Sum 2 and 3 in Python"]},"metadata":{},"recipient":"all"},"parent":"n0","children":["n2","n2b"]},
           "n2b":{"id":"n2b","message":{"id":"n2b","author":{"role":"assistant"},"create_time":1756720002.0,"content":{"content_type":"text","parts":["REGENERATED AWAY"]},"metadata":{},"recipient":"all"},"parent":"n1","children":[]},
           "n2":{"id":"n2","message":{"id":"n2","author":{"role":"assistant"},"create_time":1756720003.0,"content":{"content_type":"code","language":"python","text":"print(2+3)"},"metadata":{"model_slug":"gpt-4o"},"recipient":"python"},"parent":"n1","children":["n3"]},
           "n3":{"id":"n3","message":{"id":"n3","author":{"role":"tool","name":"python"},"create_time":1756720004.0,"content":{"content_type":"execution_output","text":"5"},"metadata":{},"recipient":"all"},"parent":"n2","children":["n4"]},
           "n4":{"id":"n4","message":{"id":"n4","author":{"role":"assistant"},"create_time":1756720005.0,"content":{"content_type":"thoughts","thoughts":[]},"metadata":{},"recipient":"all"},"parent":"n3","children":["n5"]},
           "n5":{"id":"n5","message":{"id":"n5","author":{"role":"assistant"},"create_time":1756720006.0,"content":{"content_type":"text","parts":["The sum is 5."]},"metadata":{"model_slug":"gpt-4o"},"recipient":"all"},"parent":"n4","children":["n6"]},
           "n6":{"id":"n6","message":{"id":"n6","author":{"role":"user"},"create_time":1756720100.0,"content":{"content_type":"text","parts":["thanks"]},"metadata":{},"recipient":"all"},"parent":"n5","children":[]}
          }}]
        """;

    /// <summary>A data export .zip with conversations.json (and the other files exports carry).</summary>
    public static string WriteExportZip(TempDirectory temp, string name, string conversations)
    {
        var path = temp[name];
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        void Add(string entryName, string text)
        {
            using var writer = new StreamWriter(zip.CreateEntry(entryName).Open());
            writer.Write(text);
        }
        Add("conversations.json", conversations);
        Add("users.json", """[{"uuid":"acct","full_name":"Pat"}]""");
        return path;
    }
}
