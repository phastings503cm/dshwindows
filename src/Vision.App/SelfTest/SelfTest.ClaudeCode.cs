using System.IO;
using System.Windows;
using Vision.App.Model;
using Vision.App.Views;
using Vision.App.Views.Guide;
using Vision.App.Views.Settings;
using Vision.App.Views.Wizard;
using Vision.Core;
using MessageRole = Vision.App.Model.MessageRole;

namespace Vision.App;

public sealed partial class SelfTest
{
    /// <summary>Claude Code: Settings › Claude Code when it isn't installed, when it isn't signed in, and ready and in the
    /// model menu; the wizard's guide; a chat working on Claude Code (its rows, a permission card, the usage line); and a
    /// Sensitive chat refusing it. Sample state only: no claude is run, and only the self-test's own settings change.</summary>
    private async Task CaptureClaudeCode(MainWindow window, AppModel model)
    {
        var service = model.ClaudeCode;
        try
        {
            foreach (var (status, name) in new (ClaudeCodeStatus, string)[]
                     {
                         (new ClaudeCodeStatus.Missing(), "claudecode-missing"),
                         (ClaudeCodeService.DemoSignedOut, "claudecode-signed-out"),
                     })
            {
                service.ShowDemo(status);
                await ShowAndCapture(new SettingsWindow(model, SettingsTab.ClaudeCode, null) { Owner = window, Width = 1000, Height = 820 }, name);
            }

            service.ShowDemo(ClaudeCodeService.DemoReady);
            service.Use(RouteUse.JustAdd);
            var settings = new SettingsWindow(model, SettingsTab.ClaudeCode, null) { Owner = window, Width = 1000, Height = 820, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            try
            {
                settings.Show();
                await Settle(600);
                Capture(settings, "claudecode-ready");
                if (settings.CurrentPage is ClaudeCodePage page)
                {
                    page.ScrollTo(1);
                    await Settle(400);
                    Capture(settings, "claudecode-ready-end");
                }
            }
            finally
            {
                try { settings.Close(); } catch (Exception) { }
            }
        }
        catch (Exception error)
        {
            Fail("claude code settings", error);
        }

        // The wizard's guide.
        var wizard = new SetupWizardWindow(model) { Owner = window, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        try
        {
            wizard.Show();
            foreach (var (page, status, name) in new (ClaudeCodeGuidePage, ClaudeCodeStatus?, string)[]
                     {
                         (ClaudeCodeGuidePage.Check, new ClaudeCodeStatus.Missing(), "claudecode-guide-missing"),
                         (ClaudeCodeGuidePage.Check, ClaudeCodeService.DemoReady, "claudecode-guide-check"),
                         (ClaudeCodeGuidePage.Model, ClaudeCodeService.DemoReady, "claudecode-guide-model"),
                         (ClaudeCodeGuidePage.Done, ClaudeCodeService.DemoReady, "claudecode-guide-done"),
                     })
            {
                wizard.ShowClaudeCodeDemo(page, status);
                await Settle(800);
                Capture(wizard, name);
            }
        }
        catch (Exception error)
        {
            Fail("claude code guide", error);
        }
        finally
        {
            try { wizard.Close(); } catch (Exception) { }
        }

        // A chat on Claude Code, mid-turn: what it said and did, a permission card, and the line each turn ends with.
        try
        {
            var chat = model.NewChat();
            chat.Title = "Add a low-stock warning";
            model.Host.SetChatRoute(chat, ClaudeCodeRoutes.Route("sonnet").RouteId);
            var at = DateTimeOffset.Now.AddMinutes(-2);
            chat.AppendMessage(MessageRole.User, "Warn in the report when an item drops below its reorder level, and add a test.");
            chat.AppendMessage(MessageRole.Assistant, "I'll read how the report is built, add the warning, then run the tests.");
            chat.SetTodos(
            [
                new TodoItem("1", "Read Report.cs and Stock.cs", TodoStatus.Completed),
                new TodoItem("2", "Add the low-stock warning", TodoStatus.InProgress),
                new TodoItem("3", "Add a test and run dotnet test"),
            ]);
            chat.AddFinishedTool("c1", "read_file", "src/Report.cs", "Read 48 lines of Report.cs", "namespace Inventory;\n…", true, at);
            chat.AddFinishedTool("c2", "edit", "src/Report.cs", "Edited Report.cs (+9 −1)", "The file src/Report.cs has been updated successfully.", true, at.AddSeconds(20));
            chat.AddFinishedTool("c3", "run_shell_command", "dotnet test --no-build", "Passed!  - Failed: 0, Passed: 12", "Passed!  - Failed: 0, Passed: 12", true, at.AddSeconds(50));
            chat.AppendMessage(MessageRole.Assistant, "The report now flags items below their reorder level, and `LowStockIsFlagged` covers it. All 12 tests pass.");
            chat.Note(AgentHost.UsageLine(new ClaudeCodeResult("success", false, "") { NumTurns = 7, TotalCostUsd = 0.184 },
                new LlmUsage(48_210, 2_904, 41_000), null, "none")!);
            chat.AppendMessage(MessageRole.User, "Now remove the old CSV export.");
            chat.StartTool("c4", "run_shell_command", "git rm src/CsvExport.cs");
            chat.PendingGates.Add(new GateVM("g-cc", "run_shell_command", "Run command: git rm src/CsvExport.cs"));
            chat.Running = true;
            model.Select(chat.Id);
            await Settle(1200);
            Capture(window, "claudecode-chat");
            chat.Running = false;
            chat.PendingGates.Clear();

            // The same model in a Sensitive chat: refused, with the reason, and the message kept.
            var sensitive = model.NewChat();
            sensitive.Title = "Payroll export check";
            model.Host.SetChatRoute(sensitive, ClaudeCodeRoutes.Route("sonnet").RouteId);
            model.Host.SetSensitive(sensitive, true);
            sensitive.AppendMessage(MessageRole.Notice, AgentHost.UndeliveredPrefix + "Check last month's payroll export for duplicate rows.");
            sensitive.Note(SensitivityPolicy.RefusalMessage(ClaudeCodeRoutes.Route("sonnet"), model.Host.ShieldOn)!, MessageRole.Error);
            model.Select(sensitive.Id);
            await Settle(1000);
            Capture(window, "claudecode-sensitive");
        }
        catch (Exception error)
        {
            Fail("claude code chat", error);
        }
        finally
        {
            service.Remove();
            service.EndDemo();
        }
    }
}
