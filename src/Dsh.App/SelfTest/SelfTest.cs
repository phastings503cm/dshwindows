using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Dsh.App.Infrastructure;
using Dsh.App.Model;
using Dsh.App.Views;
using Dsh.App.Views.Guide;
using Dsh.App.Views.Import;
using Dsh.App.Views.Settings;
using Dsh.App.Views.Wizard;
using Dsh.Core;
using MessageRole = Dsh.App.Model.MessageRole;

namespace Dsh.App;

/// <summary><c>DSH.exe --self-test &lt;folder&gt; [--theme light|dark]</c>: start against a throwaway
/// data folder and a demo project, fill a chat with every kind of transcript row, and render the main
/// views and windows to PNGs. CI runs it on Windows as a smoke test of the whole UI (any exception
/// fails the run); it also produces the screenshots for reviewing a change.</summary>
public sealed class SelfTest
{
    public required string Output { get; init; }
    public string? Theme { get; init; }
    public string Home => Path.Combine(Output, "home");
    public string Project => Path.Combine(Output, "demo-project");

    private readonly List<string> _report = [];
    private int _failures;

    /// <summary>The run in progress, if any (the app reports unhandled errors to it instead of
    /// showing a dialog).</summary>
    public static SelfTest? Current { get; private set; }

    public static SelfTest? Parse(string[] args)
    {
        var index = Array.FindIndex(args, a => a.Equals("--self-test", StringComparison.OrdinalIgnoreCase));
        if (index < 0) return null;
        var output = index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal)
            ? args[index + 1]
            : Path.Combine(Path.GetTempPath(), "dsh-self-test");
        var themeIndex = Array.FindIndex(args, a => a.Equals("--theme", StringComparison.OrdinalIgnoreCase));
        var theme = themeIndex >= 0 && themeIndex + 1 < args.Length ? args[themeIndex + 1] : null;
        Current = new SelfTest { Output = Path.GetFullPath(output), Theme = theme };
        return Current;
    }

    /// <summary>A fresh data folder with a route that points nowhere (nothing is sent anywhere), and a
    /// small project to open.</summary>
    public void Prepare()
    {
        if (Directory.Exists(Home)) Directory.Delete(Home, recursive: true);
        Directory.CreateDirectory(Home);
        var settings = new
        {
            providers = new object[]
            {
                new { kind = "openAICompat", name = "DGX Spark / vLLM", baseUrl = "http://127.0.0.1:9/v1", model = "qwen3-coder-30b-a3b", contextWindow = 262144 },
                new { kind = "bedrock", name = "Amazon Bedrock", baseUrl = "https://bedrock-runtime.us-east-1.amazonaws.com", model = "us.anthropic.claude-sonnet-4-5-20250929-v1:0", awsProfile = "dsh-bedrock", awsRegion = "us-east-1" },
            },
            activeRoute = "DGX Spark / vLLM|qwen3-coder-30b-a3b",
            wizardCompleted = true,
            checkForUpdates = false,
            theme = Theme ?? "light",
            recentProjects = Array.Empty<string>(),
        };
        File.WriteAllText(Path.Combine(Home, "settings.json"), JsonSerializer.Serialize(settings));

        Directory.CreateDirectory(Path.Combine(Project, "src"));
        Directory.CreateDirectory(Path.Combine(Project, "tests"));
        File.WriteAllText(Path.Combine(Project, "README.md"), "# Inventory service\n\nA small API that tracks stock levels.\n\n## Build\n\n```powershell\ndotnet build\ndotnet test\n```\n");
        File.WriteAllText(Path.Combine(Project, "AGENTS.md"), "# Agent notes\n\n- Run `dotnet test` before saying a change works.\n- Keep controllers thin; logic lives in `src/Stock.cs`.\n");
        File.WriteAllText(Path.Combine(Project, ".gitignore"), "bin/\nobj/\n*.user\n");
        File.WriteAllText(Path.Combine(Project, "src", "Stock.cs"),
            "using System;\nusing System.Collections.Generic;\n\nnamespace Inventory;\n\n/// <summary>Stock levels per SKU.</summary>\npublic sealed class Stock\n{\n" +
            "    private readonly Dictionary<string, int> _levels = new();\n\n    // Returns the new level; never below zero.\n" +
            "    public int Remove(string sku, int count)\n    {\n        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));\n" +
            "        var level = _levels.GetValueOrDefault(sku) - count;\n        _levels[sku] = Math.Max(0, level);\n        return _levels[sku];\n    }\n}\n");
        File.WriteAllText(Path.Combine(Project, "src", "build.ps1"), "param([string]$Configuration = 'Release')\n\n# Build and test\ndotnet build -c $Configuration\nif ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }\ndotnet test --no-build -c $Configuration\n");
        File.WriteAllText(Path.Combine(Project, "tests", "StockTests.cs"), "public class StockTests { }\n");
    }

    public async void Run(MainWindow window, AppModel model)
    {
        try
        {
            Directory.CreateDirectory(Output);
            window.Width = 1440;
            window.Height = 900;
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            window.Show();
            model.OpenProject(Project, activateSession: false);
            await Settle();

            var chat = FillChat(model);
            model.Select(chat.Id);
            model.Mode = WorkspaceMode.Chat;
            await Settle(1500);
            Capture(window, "chat");

            chat.Running = false;
            chat.PendingGates.Clear();
            model.Mode = WorkspaceMode.Code;
            model.Code.OpenFile(Path.Combine(Project, "README.md"));
            model.Code.OpenFile(Path.Combine(Project, "src", "Stock.cs"));
            model.Code.NewTerminal();
            await Settle(4000);
            Capture(window, "code");
            Note($"terminal: {model.Code.ActiveTerminal?.Transcript.Split('\n').FirstOrDefault(l => l.Trim().Length > 0) ?? "(none)"}");

            model.Mode = WorkspaceMode.Chat;
            model.NewChat();
            await Settle();
            Capture(window, "new-chat");

            foreach (var tab in Enum.GetValues<SettingsTab>())
            {
                var settings = new SettingsWindow(model, tab, null) { Owner = window, Width = 1000, Height = 720 };
                await ShowAndCapture(settings, $"settings-{tab.ToString().ToLowerInvariant()}");
            }

            var wizard = new SetupWizardWindow(model) { Owner = window };
            await ShowAndCapture(wizard, "wizard");

            await CaptureGuide(window, model);

            var memory = new MemoryWindow(model) { Owner = window };
            await ShowAndCapture(memory, "memory");

            await CaptureImport(window, model);

            FillQueue(model, chat.Id);
            window.ShowQueuePanel(true);
            model.Select(chat.Id);
            await Settle(1000);
            Capture(window, "queue");
            await ShowAndCapture(new QueueLogWindow(model.Host, chat.Id) { Owner = window }, "queue-log");

            // The add card, opened again while open and used twice (it once threw "already the logical child of another element").
            window.SelfTestAddQueueTasks("Check the add card", "Check it once more");
            await Settle(500);
            Capture(window, "queue-added");
            window.ShowQueuePanel(false);

            // The plan panel beside a working chat: its steps, the goal, and subagents on two servers.
            FillAgents(chat);
            chat.Running = true;
            window.ShowPlanPanel(true);
            model.Select(chat.Id);
            await Settle(1000);
            Capture(window, "plan");
            window.ShowPlanPanel(false);
            chat.Running = false;

            // Remembered notes, and the OpenClaw import's first page (nothing is scanned until you press the button).
            FillMemories(model);
            await ShowAndCapture(new MemoriesWindow(model) { Owner = window }, "memories");
            await ShowAndCapture(new OpenClawImportWindow(model) { Owner = window }, "import-openclaw");

            var vault = FillVault(model);
            var vaultWindow = new VaultWindow(model.Host) { Owner = window };
            if (vault is not null) vaultWindow.SelectEntry(vault);
            await ShowAndCapture(vaultWindow, "vault");
        }
        catch (Exception error)
        {
            Fail("run", error);
        }
        Finish();
    }

    /// <summary>The DGX Spark guide's pages with sample state (no network is touched), and the
    /// normal wizard's connection step with the network scanner open.</summary>
    private async Task CaptureGuide(Window owner, AppModel model)
    {
        var wizard = new SetupWizardWindow(model) { Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        try
        {
            wizard.Show();
            GuidePage[] pages =
            [
                GuidePage.Welcome, GuidePage.Start, GuidePage.Sticker, GuidePage.Download, GuidePage.WriteUsb, GuidePage.Find,
                GuidePage.Install, GuidePage.Certificate, GuidePage.Admin, GuidePage.Model, GuidePage.Connect, GuidePage.Done,
            ];
            foreach (var page in pages)
            {
                wizard.ShowGuideDemo(page);
                await Settle(900);
                Capture(wizard, $"guide-{page.ToString().ToLowerInvariant()}");
                if (page is GuidePage.Install or GuidePage.Certificate or GuidePage.WriteUsb or GuidePage.Model)
                {
                    wizard.ScrollGuide(0.55);
                    await Settle(400);
                    Capture(wizard, $"guide-{page.ToString().ToLowerInvariant()}-more");
                }
            }
        }
        catch (Exception error)
        {
            Fail("guide", error);
        }
        finally
        {
            try { wizard.Close(); } catch (Exception) { }
        }

        // The Amazon Bedrock guide, with sample state (no AWS CLI or network).
        var bedrock = new SetupWizardWindow(model) { Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        try
        {
            bedrock.Show();
            foreach (var page in Enum.GetValues<BedrockPage>())
            {
                bedrock.ShowBedrockDemo(page);
                await Settle(900);
                Capture(bedrock, $"bedrock-{page.ToString().ToLowerInvariant()}");
                if (page is BedrockPage.Model or BedrockPage.Access or BedrockPage.Done)
                {
                    bedrock.ScrollGuide(0.6);
                    await Settle(400);
                    Capture(bedrock, $"bedrock-{page.ToString().ToLowerInvariant()}-more");
                }
            }
            bedrock.ShowBedrockDemo(BedrockPage.Access, terms: true);
            await Settle(900);
            Capture(bedrock, "bedrock-terms");
            bedrock.ScrollGuide(0.6);
            await Settle(400);
            Capture(bedrock, "bedrock-terms-more");
        }
        catch (Exception error)
        {
            Fail("bedrock guide", error);
        }
        finally
        {
            try { bedrock.Close(); } catch (Exception) { }
        }

        var scanning = new SetupWizardWindow(model) { Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner, Height = 760 };
        try
        {
            scanning.Show();
            scanning.ShowScannerDemo(
            [
                new FoundHost(System.Net.IPAddress.Parse("192.168.1.42"),
                [
                    new FoundService(22, ServiceKind.Ssh, "Remote login (SSH)") { Identified = true, Detail = "OpenSSH 9.6p1 · Ubuntu" },
                    new FoundService(8999, ServiceKind.SparkSwapper, "Spark Swapper") { Identified = true, Tls = true },
                    new FoundService(11443, ServiceKind.SparkModelFront, "Spark model API") { Identified = true, Tls = true, NeedsKey = true, BaseUrl = "https://192.168.1.42:11443/v1" },
                ]) { HostName = "spark-3f2a.local", Identified = true },
                new FoundHost(System.Net.IPAddress.Parse("192.168.1.9"),
                [
                    new FoundService(8000, ServiceKind.ModelServer, "vLLM server") { Identified = true, Engine = "vllm", Models = ["qwen3-coder-30b-a3b"], Detail = "vLLM · qwen3-coder-30b-a3b", BaseUrl = "http://192.168.1.9:8000/v1" },
                ]) { HostName = "workstation.lan", Identified = true },
                new FoundHost(System.Net.IPAddress.Loopback,
                [
                    new FoundService(11434, ServiceKind.Ollama, "Ollama") { Identified = true, Models = ["qwen3:8b"], Detail = "qwen3:8b", BaseUrl = "http://127.0.0.1:11434/v1" },
                ]) { Identified = true },
            ]);
            await Settle(900);
            Capture(scanning, "wizard-scan");
        }
        catch (Exception error)
        {
            Fail("wizard-scan", error);
        }
        finally
        {
            try { scanning.Close(); } catch (Exception) { }
        }
    }

    /// <summary>"Bring in Claude Code &amp; Cursor" against a made-up profile — what it offers, then what
    /// it did. The profile is scratch, so the run never reads the machine's real ~\.claude or ~\.cursor.</summary>
    private async Task CaptureImport(Window owner, AppModel model)
    {
        var import = new ExternalImportWindow(model, new ExternalLocations(MakeExternalProfile()))
        {
            Owner = owner,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Height = 780,
        };
        try
        {
            import.Show();
            await Settle(1500); // the scan runs off the UI thread
            Capture(import, "import-external");
            await import.ImportAsync();
            await Settle(600);
            Capture(import, "import-external-done");
        }
        catch (Exception error)
        {
            Fail("import-external", error);
        }
        finally
        {
            try { import.Close(); } catch (Exception) { }
        }
    }

    private string MakeExternalProfile()
    {
        var profile = Path.Combine(Path.GetTempPath(), "dsh-self-test-profile");
        if (Directory.Exists(profile)) Directory.Delete(profile, recursive: true);
        void Write(string relative, string text)
        {
            var path = Path.Combine(profile, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }
        Write(".claude/CLAUDE.md", "# About me\n\nI prefer small, reviewable changes and plain language.\nAlways run the tests before saying something works.\n");
        Write(".claude/skills/release-notes/SKILL.md", "---\nname: release-notes\ndescription: Use when writing release notes from a list of merged pull requests.\n---\n\nGroup by area, lead with what users notice.\n");
        Write(".claude/skills/db-migrations/SKILL.md", "---\nname: db-migrations\ndescription: Use when adding or changing a database migration.\n---\n\nNever edit a shipped migration.\n");
        Write(".claude/skills/db-migrations/scripts/check.ps1", "Write-Host 'checking'\n");
        Write(".claude/commands/review.md", "---\ndescription: Review the current branch\n---\nReview the diff against main.\n");
        Write(".claude/rules/style.md", "---\npaths:\n  - \"src/**\"\n---\nKeep controllers thin.\n");
        Write(".claude/agents/code-reviewer.md", "---\nname: code-reviewer\ndescription: Expert code reviewer. Use proactively after code changes.\ntools: Read, Grep, Glob\n---\nYou review code for bugs and unclear names. Be brief.\n");
        var notes = $".claude/projects/{ClaudeProjectNames.Encode(Project)}/memory";
        Write($"{notes}/MEMORY.md", "- [How we test](testing.md) — integration tests hit a real database\n");
        Write($"{notes}/testing.md", "Integration tests must hit a real database, not a mock.\n");
        Write(".cursor/skills/api-design/SKILL.md", "---\nname: api-design\ndescription: Use when designing or reviewing a REST API.\n---\n\nPrefer nouns; version in the path.\n");
        Write(".cursor/commands/ship.md", "Ship the current branch: tests, changelog, tag.\n");
        foreach (var builtIn in new[] { "create-rule", "create-skill", "statusline" })
            Write($".cursor/skills-cursor/{builtIn}/SKILL.md", $"---\nname: {builtIn}\ndescription: Use when working on Cursor itself ({builtIn}).\n---\n\nBuilt into Cursor.\n");
        Write(".cursor/mcp.json", "{\"mcpServers\":{\"figma\":{\"url\":\"https://example.invalid/mcp\"}}}");
        return profile;
    }

    /// <summary>A chat with one of everything the transcript can show.</summary>
    private static SessionVM FillChat(AppModel model)
    {
        var chat = model.NewChat();
        chat.Title = "Fix the negative stock bug";
        chat.AppendMessage(MessageRole.User, "Removing more items than we have makes the stock go negative in the report. Can you find and fix it, and add a test?");
        chat.AppendMessage(MessageRole.Assistant,
            "I'll look at how stock is removed, then add a regression test.\n\n" +
            "## What I found\n\n`Stock.Remove` clamps the **stored** level at zero, but the report reads the *unclamped* value:\n\n" +
            "```csharp\nvar level = _levels.GetValueOrDefault(sku) - count;\n_levels[sku] = Math.Max(0, level);\n```\n\n" +
            "| Case | Before | After |\n|---|---|---|\n| remove 3 of 2 | -1 | 0 |\n| remove 2 of 5 | 3 | 3 |\n\n" +
            "1. Return the clamped level\n2. Add `RemovingMoreThanInStockStopsAtZero`\n3. Run the tests");
        var at = DateTimeOffset.Now;
        chat.AddFinishedTool("t1", "read_file", "src/Stock.cs", "22 lines", "using System;\n...", true, at);
        chat.AddFinishedTool("t2", "grep", "Remove\\(", "3 matches in 2 files", "src/Stock.cs:13\ntests/StockTests.cs:8", true, at);
        chat.AddFinishedTool("t3", "run_shell_command", "dotnet test --no-build", "exit 1 — 1 failed",
            "Failed StockTests.RemovingMoreThanInStockStopsAtZero\n  Expected: 0\n  Actual:   -1", false, at);
        chat.SetTodos(
        [
            new TodoItem("1", "Find where the report reads stock levels", TodoStatus.Completed),
            new TodoItem("2", "Return the clamped level from Stock.Remove", TodoStatus.InProgress),
            new TodoItem("3", "Add a regression test and run the suite"),
        ]);
        chat.Entries.Add(new CompactionEntryVM(14, "The user asked to fix negative stock levels. The agent read src/Stock.cs and found ..."));
        chat.StartTool("t4", "edit", "src/Stock.cs");
        chat.Note("Switched to **qwen3-coder-30b-a3b** — 262,144-token window.");
        chat.Note("The model server closed the connection. Retrying in 2s…", MessageRole.Error);
        chat.RecordFileChanges([new FileChange(Path.Combine(model.Project!, "src", "Stock.cs"), FileChangeKind.Modified),
                                new FileChange(Path.Combine(model.Project!, "tests", "StockTests.cs"), FileChangeKind.Created)]);
        chat.LastUsage = new LlmUsage(18_452, 1_210);
        chat.Goal = new GoalState("Fix the negative stock bug and add a regression test", 3, DateTimeOffset.Now.AddMinutes(-4));
        chat.UpsertBackgroundJob(new BackgroundAgentJob("bg-1", "audit the other callers of Remove", DateTimeOffset.Now.AddSeconds(-42)));
        chat.Running = true;
        chat.PendingGates.Add(new GateVM("g1", "run_shell_command", "dotnet add tests package FluentAssertions --version 6.12.0"));
        return chat;
    }

    /// <summary>A chat's task list with one task in each state.</summary>
    private static void FillQueue(AppModel model, string chatId)
    {
        var queue = model.Host.Queue;
        var done = model.Host.QueueAdd(chatId, "Write parser tests", "Cover empty input, nested quotes and the 64 KB limit.");
        queue.Start(done.Id);
        queue.RecordRound(done.Id, 1, 18_000, 2_400);
        queue.RecordRound(done.Id, 2, 21_000, 1_900);
        queue.Finish(done.Id, QueueTaskStatus.Complete);
        var blocked = model.Host.QueueAdd(chatId, "Deploy the staging build", "Needs the deploy token from the vault.");
        queue.Start(blocked.Id);
        queue.RecordRound(blocked.Id, 1, 9_000, 800);
        queue.Finish(blocked.Id, QueueTaskStatus.Blocked, "The DEPLOY_TOKEN credential is set to Never.");
        model.Host.QueueAdd(chatId, "Refactor Stock into a repository", "Keep the public API; move persistence behind IStockStore.");
        model.Host.QueueAdd(chatId, "Update the README build section");
    }

    /// <summary>Subagents in the states the plan panel draws: working on two servers, and one that finished.</summary>
    private static void FillAgents(SessionVM chat)
    {
        var now = DateTimeOffset.Now;
        chat.UpsertAgent(new AgentRunInfo("a1", "Find every caller of Stock.Remove", "explore", false, now.AddSeconds(-38))
            { Server = "Spark 2", Activity = "grep: Remove\\(", Steps = 6 });
        chat.UpsertAgent(new AgentRunInfo("a2", "Review the clamping change", "review", true, now.AddSeconds(-12))
            { Server = "Spark 3", Activity = "read_file: src/Stock.cs", Steps = 2 });
        chat.UpsertAgent(new AgentRunInfo("a3", "List the report queries", "explore", false, now.AddMinutes(-2))
            {
                Server = "Spark 2", Status = AgentRunStatus.Done, FinishedAt = now.AddSeconds(-70), Steps = 9,
                Report = "Three queries read stock: ReportStock, LowStock and the nightly export.",
            });
    }

    /// <summary>A few remembered notes: one pinned, one for this project only.</summary>
    private static void FillMemories(AppModel model)
    {
        var memory = model.Host.Memory;
        memory.Save(new MemoryDraft { Title = "Test command", Body = "Run the unit tests with dotnet test --no-build; the integration tests need the dev database up.", Kind = MemoryKinds.Procedure, Source = "user" });
        memory.Save(new MemoryDraft { Title = "Style", Body = "The user prefers small, reviewable commits and plain language in summaries.", Kind = MemoryKinds.Preference, Source = "user", Pinned = true });
        memory.Save(new MemoryDraft { Title = "Staging server", Body = "The staging server is called orion; deploy it with scripts/ship.ps1.", Kind = MemoryKinds.Fact, Project = model.Project, Source = "agent" });
    }

    /// <summary>Two credentials; returns the id of the one to show.</summary>
    private string? FillVault(AppModel model)
    {
        try
        {
            var vault = model.Host.Vault;
            var token = vault.Add("GITHUB_TOKEN", "ghp_selftest0000000000000000000000000000", VaultKind.Token,
                "Push to the inventory repo", ["github", "ci"], access: VaultAccess.Allowed);
            vault.Add("DEPLOY_TOKEN", "deploy-selftest-1234", VaultKind.ApiKey, "Staging deploys", ["deploy"], access: VaultAccess.Ask);
            return token.Id;
        }
        catch (Exception error)
        {
            Fail("vault", error);
            return null;
        }
    }

    private async Task ShowAndCapture(Window window, string name)
    {
        try
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            window.Show();
            await Settle(800);
            Capture(window, name);
        }
        catch (Exception error)
        {
            Fail(name, error);
        }
        finally
        {
            try { window.Close(); } catch (Exception) { }
        }
    }

    private static async Task Settle(int milliseconds = 600)
    {
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        await Task.Delay(milliseconds);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }

    private void Capture(Window window, string name)
    {
        try
        {
            window.UpdateLayout();
            var dpi = VisualTreeHelper.GetDpi(window);
            var width = (int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX);
            var height = (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY);
            if (width <= 0 || height <= 0) throw new InvalidOperationException("The window has no size.");
            var bitmap = new RenderTargetBitmap(width, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            bitmap.Render(window);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            var path = Path.Combine(Output, $"{name}-{Theme ?? "light"}.png");
            using (var stream = File.Create(path)) encoder.Save(stream);
            Note($"captured {Path.GetFileName(path)} ({width}x{height})");
        }
        catch (Exception error)
        {
            Fail(name, error);
        }
    }

    private void Note(string line) => _report.Add(line);

    public void Fail(string step, Exception error)
    {
        _failures++;
        _report.Add($"FAILED {step}: {error}");
    }

    private void Finish()
    {
        var application = Application.Current;
        foreach (var session in App.Model.Host.Sessions) session.Running = false;
        foreach (var terminal in App.Model.Code.Terminals.ToList()) terminal.Dispose();
        _report.Insert(0, $"DSH {AppInfo.Version} self-test, theme {Theme ?? "light"}: {(_failures == 0 ? "OK" : $"{_failures} failure(s)")}");
        File.WriteAllText(Path.Combine(Output, $"report-{Theme ?? "light"}.txt"), string.Join(Environment.NewLine, _report), Encoding.UTF8);
        Console.WriteLine(string.Join(Environment.NewLine, _report));
        application.Shutdown(_failures == 0 ? 0 : 1);
    }
}
