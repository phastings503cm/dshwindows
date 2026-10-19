using System.IO;
using System.Windows;
using Vision.App.Model;
using Vision.App.Views.Import;
using Vision.Core;

namespace Vision.App;

public sealed partial class SelfTest
{
    /// <summary>"Import chats" against a made-up profile (a Claude Code session, a Codex rollout, OpenClaw chats and a
    /// scheduled run) plus Guardian chats as if the account had answered: the list, OpenClaw's with its scheduled runs, and
    /// what an import did; then "Search all chats" over what came in. Nothing real is read and nothing is contacted.</summary>
    private async Task CaptureChatImport(Window owner, AppModel model)
    {
        var (home, state) = MakeChatProfile();
        var import = new ChatImportWindow(model, new ChatImportLocations(home, OpenClawStateDirs: [state]), useGuardian: false)
        {
            Owner = owner,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        try
        {
            import.Show();
            await import.ScanAsync();
            import.SelfTestAddGuardian(DemoGuardianChats());
            await Settle(800);
            Capture(import, "import-chats");
            import.SelfTestShow(ChatSource.OpenClaw, automated: true);
            await Settle(500);
            Capture(import, "import-chats-openclaw");
            import.SelfTestShow(null, automated: false);
            await import.ImportAsync();
            await Settle(800);
            Capture(import, "import-chats-done");
            Note($"chat import: {model.Host.Log.List().Count(s => s.Imported is not null)} chats imported");
        }
        catch (Exception error)
        {
            Fail("import-chats", error);
        }
        finally
        {
            try { import.Close(); } catch (Exception) { }
        }

        var search = new ChatSearchWindow(model) { Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        try
        {
            search.Show();
            await Settle(400);
            await search.SelfTestSearchAsync("login");
            await Settle(600);
            Capture(search, "chat-search");
        }
        catch (Exception error)
        {
            Fail("chat-search", error);
        }
        finally
        {
            try { search.Close(); } catch (Exception) { }
        }
    }

    /// <summary>Two Guardian chats (one shared by someone else) whose transcripts are made up here.</summary>
    private static IEnumerable<ChatCandidate> DemoGuardianChats()
    {
        ImportedChat Chat(string id, string title, DateTimeOffset at, string prompt, string reply) => new()
        {
            Source = ChatSource.Guardian,
            SourceId = id,
            Title = title,
            StartedAt = at,
            UpdatedAt = at.AddMinutes(4),
            From = "https://guardian.503cm.com",
            Rows =
            [
                new LogItemRow("", 0, "user", prompt, null, null, null, false, at),
                new LogItemRow("", 1, "assistant", reply, null, null, null, false, at.AddMinutes(4)),
            ],
        };
        var monday = DateTimeOffset.Now.Date.AddDays(-2).AddHours(10);
        var mine = Chat("g-1", "Quarterly numbers for the board", monday, "Pull Q3 revenue by clinic.", "Q3 revenue was up 4% on Q2; the table is below.");
        var theirs = Chat("g-2", "Login page outage notes", monday.AddDays(-1), "What broke the login page?", "A certificate expired at 02:00.");
        yield return mine.ToCandidate("https://guardian.503cm.com");
        yield return theirs.ToCandidate("https://guardian.503cm.com") with { Shared = true, Note = "Shared by Riley" };
    }

    /// <summary>A profile with Claude Code, Codex and OpenClaw chats in their real shapes (the content is invented).</summary>
    private (string Home, string OpenClawState) MakeChatProfile()
    {
        var home = Path.Combine(Output, "chat-profile");
        if (Directory.Exists(home)) Directory.Delete(home, recursive: true);
        void Write(string relative, params string[] lines)
        {
            var path = Path.Combine(home, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, string.Join("\n", lines));
        }
        var day = DateTimeOffset.Now.Date.AddDays(-1).ToUniversalTime();
        string At(double minutes) => day.AddHours(9).AddMinutes(minutes).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
        var project = Project.Replace("\\", "\\\\");

        Write(".claude/projects/C--demo-project/5d1c0e2a-7b3f-4c1d-9e8a-0f1b2c3d4e5f.jsonl",
            $$$"""{"parentUuid":null,"isSidechain":false,"cwd":"{{{project}}}","type":"user","message":{"role":"user","content":"Fix the login bug in Stock.cs"},"uuid":"u1","timestamp":"{{{At(0)}}}"}""",
            $$$"""{"parentUuid":"u1","isSidechain":false,"type":"assistant","message":{"id":"m1","model":"claude-opus-4-5","role":"assistant","content":[{"type":"text","text":"Looking at the code first."}]},"uuid":"u2","timestamp":"{{{At(0.2)}}}"}""",
            $$$"""{"parentUuid":"u2","isSidechain":false,"type":"assistant","message":{"id":"m1","model":"claude-opus-4-5","role":"assistant","content":[{"type":"tool_use","id":"t1","name":"Bash","input":{"command":"dotnet test"}}]},"uuid":"u3","timestamp":"{{{At(0.3)}}}"}""",
            $$$"""{"parentUuid":"u3","isSidechain":false,"type":"user","message":{"role":"user","content":[{"tool_use_id":"t1","type":"tool_result","content":"Passed! 12 tests"}]},"uuid":"u4","timestamp":"{{{At(1)}}}"}""",
            $$$"""{"parentUuid":"u4","isSidechain":false,"type":"assistant","message":{"id":"m2","model":"claude-opus-4-5","role":"assistant","content":[{"type":"text","text":"Fixed: the login check now runs before the stock update."}]},"uuid":"u5","timestamp":"{{{At(2)}}}"}""",
            """{"type":"custom-title","customTitle":"Login bug in the inventory service","sessionId":"5d1c"}""");
        Write(".claude/projects/C--demo-project/7a7a7a7a-0000-4000-8000-000000000001.jsonl",
            $$$"""{"parentUuid":null,"isSidechain":false,"cwd":"C:\\code\\website","type":"user","message":{"role":"user","content":"Write release notes for 2.4"},"uuid":"a1","timestamp":"{{{At(-600)}}}"}""",
            $$$"""{"parentUuid":"a1","isSidechain":false,"type":"assistant","message":{"id":"m9","model":"claude-sonnet-4-5","role":"assistant","content":[{"type":"text","text":"Here are the notes for 2.4."}]},"uuid":"a2","timestamp":"{{{At(-598)}}}"}""");
        Write(".codex/sessions/2026/10/01/rollout-2026-10-01T09-00-00-019aaaaa-bbbb-7ccc-8ddd-eeeeeeeeeeee.jsonl",
            $$$"""{"timestamp":"{{{At(-1500)}}}","type":"session_meta","payload":{"id":"019aaaaa-bbbb-7ccc-8ddd-eeeeeeeeeeee","cwd":"C:\\code\\shop"}}""",
            $$$"""{"timestamp":"{{{At(-1499)}}}","type":"event_msg","payload":{"type":"user_message","message":"Why is the build slow?","images":[]}}""",
            $$$"""{"timestamp":"{{{At(-1498)}}}","type":"response_item","payload":{"type":"message","role":"assistant","content":[{"type":"output_text","text":"Restore runs twice; caching the packages fixes it."}]}}""");
        Write(".codex/session_index.jsonl", """{"id":"019aaaaa-bbbb-7ccc-8ddd-eeeeeeeeeeee","thread_name":"Slow build"}""");
        var state = Path.Combine(home, ".openclaw");
        Write(".openclaw/agents/main/sessions/oc-1.jsonl",
            $$$"""{"type":"session","version":3,"id":"oc-1","timestamp":"{{{At(-200)}}}","cwd":"C:\\Users\\pat\\.openclaw\\workspace"}""",
            $$$"""{"type":"message","id":"e1","parentId":null,"timestamp":"{{{At(-200)}}}","message":{"role":"user","content":"Check the disk on the build box"}}""",
            $$$"""{"type":"message","id":"e2","parentId":"e1","timestamp":"{{{At(-199)}}}","message":{"role":"assistant","content":[{"type":"text","text":"The disk is 80% full."}]}}""");
        Write(".openclaw/agents/main/sessions/oc-2.jsonl",
            $$$"""{"type":"session","version":3,"id":"oc-2","timestamp":"{{{At(-30)}}}","cwd":"C:\\Users\\pat\\.openclaw\\workspace"}""",
            $$$"""{"type":"message","id":"c1","parentId":null,"timestamp":"{{{At(-30)}}}","message":{"role":"user","content":"Sweep the helpdesk queue"}}""",
            $$$"""{"type":"message","id":"c2","parentId":"c1","timestamp":"{{{At(-29)}}}","message":{"role":"assistant","content":"Closed 3 tickets."}}""");
        Write(".openclaw/agents/main/sessions/sessions.json",
            """{"agent:main:dashboard:1":{"sessionId":"oc-1","label":"Build box disk"},"agent:main:cron:7":{"sessionId":"oc-2","label":"Cron: helpdesk sweep"}}""");
        return (home, state);
    }
}
