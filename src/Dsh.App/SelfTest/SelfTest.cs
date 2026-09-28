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
            providers = new[] { new { kind = "openAICompat", name = "DGX Spark / vLLM", baseUrl = "http://127.0.0.1:9/v1", model = "qwen3-coder-30b-a3b", contextWindow = 262144 } },
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

            var memory = new MemoryWindow(model) { Owner = window };
            await ShowAndCapture(memory, "memory");
        }
        catch (Exception error)
        {
            Fail("run", error);
        }
        Finish();
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
        chat.Running = true;
        chat.PendingGates.Add(new GateVM("g1", "run_shell_command", "dotnet add tests package FluentAssertions --version 6.12.0"));
        return chat;
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
