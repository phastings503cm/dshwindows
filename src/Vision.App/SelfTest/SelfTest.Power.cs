using System.IO;
using System.Windows;
using Vision.App.Model;
using Vision.App.Views;
using Vision.App.Views.Settings;
using Vision.Core;
using MessageRole = Vision.App.Model.MessageRole;

namespace Vision.App;

public sealed partial class SelfTest
{
    /// <summary>MCP servers for Settings › MCP Servers' picture (taken with the other settings tabs): one connected (an MCP server
    /// inside Vision, speaking the real protocol), one that failed with a reason, one switched off. Nothing is started or
    /// contacted.</summary>
    private async Task PrepareMcp(AppModel model)
    {
        try
        {
            McpManager.TransportOverride = config => config.Id switch
            {
                "github" => new LoopbackMcpTransport(GitHubDemo()),
                "linear" => new LoopbackMcpTransport(new McpLoopbackServer
                {
                    FailToStart = "mcp.linear.app refused the request (HTTP 401). It may need a sign-in header (Authorization) in Settings › MCP Servers.",
                }),
                _ => new LoopbackMcpTransport(new McpLoopbackServer()),
            };
            var store = model.Host.McpServers;
            store.Save(new McpServerConfig
            {
                Id = "github", Name = "github", Command = "npx", Args = ["-y", "@modelcontextprotocol/server-github"],
                // Its name only: nothing is written to this PC's Credential Manager.
                EnvNames = ["GITHUB_PERSONAL_ACCESS_TOKEN"], Trust = McpTrust.ReadOnly,
                Source = McpImport.ClaudeCode, SourceFile = Path.Combine(Home, ".claude.json"),
            });
            store.Save(new McpServerConfig
            {
                Id = "linear", Name = "linear", Transport = McpTransportKind.Http, Url = "https://mcp.linear.app/mcp", Source = McpImport.Cursor,
            });
            store.Save(new McpServerConfig
            {
                Id = "playwright", Name = "playwright", Command = "npx", Args = ["@playwright/mcp@latest"], Enabled = false, Project = Project,
                Source = "Claude Code (this project)",
            });
            await model.Host.Mcp.SyncAsync();
            // Saving also started a sync of its own: wait for every connection to settle.
            for (var i = 0; i < 100 && model.Host.Mcp.Statuses.Any(s => s.State == McpState.Connecting); i++) await Task.Delay(100);
            var statuses = model.Host.Mcp.Statuses;
            Note("mcp: " + string.Join("; ", statuses.Select(s => $"{s.Config.Id} {s.Headline}")));
            if (statuses.First(s => s.Config.Id == "github").State != McpState.Connected) throw new InvalidOperationException("the demo MCP server didn't connect");
        }
        catch (Exception error)
        {
            Fail("mcp servers", error);
        }
    }

    private static McpLoopbackServer GitHubDemo()
    {
        var server = new McpLoopbackServer { Name = "github-mcp-server" };
        server.Tools.Clear();
        foreach (var (name, description, readOnly) in new[]
                 {
                     ("search_issues", "Search issues and pull requests across GitHub repositories.", true),
                     ("get_file_contents", "Get the contents of a file or directory from a GitHub repository.", true),
                     ("create_issue", "Create a new issue in a GitHub repository.", false),
                     ("create_pull_request", "Create a new pull request in a GitHub repository.", false),
                 })
            server.Add(new McpToolInfo(name, description, """{"type":"object","properties":{}}""", readOnly), _ => McpLoopbackServer.Result("ok"));
        return server;
    }

    /// <summary>Checkpoints, the Rewind window, the MCP import, and a Sensitive chat's header: a turn's "Files changed (3)" row
    /// opened, the window with that turn's diff, the servers found in a made-up Claude Code / Cursor profile, and the header
    /// chip of a chat marked Sensitive.</summary>
    private async Task CapturePower(MainWindow window, AppModel model)
    {
        var host = model.Host;
        try
        {
            var chat = host.NewSession(Project);
            host.RenameSession(chat.Id, "Fix the low-stock report");
            var stock = Path.Combine(Project, "src", "Stock.cs");
            var report = Path.Combine(Project, "src", "StockReport.cs");
            var tests = Path.Combine(Project, "tests", "StockTests.cs");
            var original = File.ReadAllText(stock);
            var originalTests = File.ReadAllText(tests);
            var recorder = new TurnRecorder(host.Checkpoints, chat.Id, Guid.NewGuid().ToString(), Project,
                TurnCheckpoint.TitleOf("Fix the low-stock report: it should list every SKU under 5"));
            recorder.BeforeChange(stock, EditTool.ToolName);
            File.WriteAllText(stock, original.Replace("    // Returns the new level; never below zero.\n",
                "    /// <summary>The SKUs whose level is below <paramref name=\"threshold\"/>.</summary>\n" +
                "    public IReadOnlyList<string> Low(int threshold) => _levels.Where(l => l.Value < threshold).Select(l => l.Key).ToList();\n\n" +
                "    // Returns the new level; never below zero.\n"));
            recorder.BeforeChange(report, WriteFileTool.ToolName);
            File.WriteAllText(report, "namespace Inventory;\n\npublic static class StockReport\n{\n    public static string Low(Stock stock) => string.Join(\"\\n\", stock.Low(5));\n}\n");
            recorder.BeforeChange(tests, EditTool.ToolName);
            File.WriteAllText(tests, "public class StockTests\n{\n    [Fact] public void LowListsEverySkuUnderTheThreshold() { }\n}\n");
            var turn = recorder.Finish() ?? throw new InvalidOperationException("the demo turn recorded nothing");
            chat.AppendMessage(MessageRole.User, "Fix the low-stock report: it should list every SKU under 5");
            chat.AppendMessage(MessageRole.Assistant, "Added `Stock.Low(threshold)`, a `StockReport` that lists every SKU under 5, and a test for it.");
            chat.Entries.Add(new CheckpointEntryVM(turn.Id, turn.Heading) { Turn = turn, IsExpanded = true });
            model.Select(chat.Id);
            model.Mode = WorkspaceMode.Chat;
            await Settle(1000);
            Capture(window, "checkpoint-row");

            var rewind = new RewindWindow(model, chat, turn.Id, stock) { Owner = window };
            await ShowAndCapture(rewind, "rewind");

            // Rewind for real (the files go back), and the row says so.
            var plan = host.PlanRewind(chat, turn.Id) ?? throw new InvalidOperationException("no rewind plan");
            var result = host.Rewind(chat, plan, includeConflicts: false);
            Note($"rewind: {CheckpointRewind.Summary(result, plan.Turn.Title, Project)}");
            if (File.ReadAllText(stock) != original || File.Exists(report) || File.ReadAllText(tests) != originalTests)
                throw new InvalidOperationException("the rewind didn't put the files back");
            await Settle(600);
            Capture(window, "checkpoint-rewound");

            // A Sensitive chat: the header chip, and what the chat says.
            host.SetChatSensitive(chat, true);
            await Settle(600);
            Capture(window, "chat-sensitive");
            host.SetChatSensitive(chat, false);
        }
        catch (Exception error)
        {
            Fail("checkpoints", error);
        }

        try
        {
            // The import window over a made-up profile.
            var profile = Path.Combine(Output, "mcp-profile");
            Directory.CreateDirectory(Path.Combine(profile, ".cursor"));
            File.WriteAllText(Path.Combine(profile, ".claude.json"),
                """{"mcpServers":{"github":{"type":"stdio","command":"npx","args":["-y","@modelcontextprotocol/server-github"],"env":{"GITHUB_PERSONAL_ACCESS_TOKEN":"not-a-token"}},"sentry":{"type":"http","url":"https://mcp.sentry.dev/mcp","headers":{"Authorization":"Bearer not-a-token"}}}}""");
            File.WriteAllText(Path.Combine(profile, ".cursor", "mcp.json"),
                """{"mcpServers":{"figma":{"url":"http://127.0.0.1:3845/sse","type":"sse"}}}""");
            File.WriteAllText(Path.Combine(Project, ".mcp.json"), """{"mcpServers":{"playwright":{"command":"npx","args":["@playwright/mcp@latest"]}}}""");
            McpImportWindow.LocationsOverride = new ExternalLocations(profile);
            await ShowAndCapture(new McpImportWindow(model) { Owner = window }, "mcp-import");
        }
        catch (Exception error)
        {
            Fail("mcp import", error);
        }
        finally
        {
            McpImportWindow.LocationsOverride = null;
            try { File.Delete(Path.Combine(Project, ".mcp.json")); } catch (Exception) { }
        }
    }
}
