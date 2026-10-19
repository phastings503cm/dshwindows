using System.IO;
using System.Windows;
using Vision.App.Model;
using Vision.App.Views;
using Vision.App.Views.Settings;
using Vision.Core;

namespace Vision.App;

public sealed partial class SelfTest
{
    /// <summary>Settings › Import &amp; Export (a found OpenClaw with its counts, and an export's result), and the OpenClaw
    /// import's choices with the clash pickers, read from a small install written for the test.</summary>
    private async Task CaptureImportExport(Window owner, AppModel model)
    {
        // Settings › Import & Export: a found OpenClaw with its counts, and an export's result.
        var exportWindow = new SettingsWindow(model, SettingsTab.ImportExport, null) { Owner = owner, Width = 1000, Height = 900, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        try
        {
            exportWindow.Show();
            await Settle(600);
            var page = (ImportExportPage)exportWindow.CurrentPage!;
            page.ShowDemo();
            await Settle(600);
            Capture(exportWindow, "import-export");
            page.ScrollTo(1.0);
            await Settle(400);
            Capture(exportWindow, "import-export-more");
        }
        catch (Exception error)
        {
            Fail("import-export", error);
        }
        finally
        {
            try { exportWindow.Close(); } catch (Exception) { }
        }

        // The OpenClaw import's choices, with the clash pickers (a small install written for the test, read from disk).
        var import = new OpenClawImportWindow(model) { Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner, Height = 820 };
        try
        {
            var (source, install) = MakeOpenClaw();
            var bundle = await OpenClawReader.ReadAsync(source, install);
            import.Show();
            import.ShowChooseDemo(bundle);
            await Settle(800);
            Capture(import, "import-openclaw-choose");
            import.ScrollChoose(0.55);
            await Settle(400);
            Capture(import, "import-openclaw-choose-more");
        }
        catch (Exception error)
        {
            Fail("import-openclaw-choose", error);
        }
        finally
        {
            try { import.Close(); } catch (Exception) { }
        }
    }

    /// <summary>A small OpenClaw install under the self-test's folder: a config, an .env, a workspace with memory, and two
    /// skills. The keys are made up.</summary>
    private (IFileSource Source, OpenClawInstall Install) MakeOpenClaw()
    {
        var home = Path.Combine(Output, "openclaw-home");
        var state = Path.Combine(home, ".openclaw");
        if (Directory.Exists(home)) Directory.Delete(home, recursive: true);
        void Write(string relative, string text)
        {
            var path = Path.Combine(state, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }
        Write("openclaw.json", """
            {
              "agents": { "defaults": { "workspace": "~/.openclaw/workspace", "model": { "primary": "spark/qwen3-coder" } } },
              "env": { "OPENROUTER_API_KEY": "sk-or-v1-a1b2c3d4e5f60718293a4b5c6d7e8f90" },
              "models": { "providers": { "spark": { "baseUrl": "http://192.168.1.40:8002/v1", "apiKey": "${SPARK_KEY}", "api": "openai-completions", "models": [ { "id": "qwen3-coder", "contextWindow": 131072 } ] } } }
            }
            """);
        Write(".env", "SPARK_KEY=spk-test-4f8a1c9d2e7b6a305d11\nGITHUB_TOKEN=ghp_testabcdefghijklmnopqrstuvwxyz0123\n");
        Write("workspace/MEMORY.md", "# Memory\n\n- The inventory service deploys with `dotnet publish -c Release`.\n- Pat prefers short answers.\n");
        Write("workspace/USER.md", "# About Pat\n\nRuns IT for 503CM.\n");
        Write("workspace/skills/release-notes/SKILL.md", "---\nname: release-notes\ndescription: Write release notes from the git log\n---\nRead the log and write notes.\n");
        Write("skills/freshdesk/SKILL.md", "---\nname: freshdesk\ndescription: Look up helpdesk tickets\n---\nUse the Freshdesk API.\n");
        var source = new LocalFileSource(home, new Dictionary<string, string>());
        return (source, new OpenClawInstall { StateDir = state, ConfigPath = Path.Combine(state, "openclaw.json"), FoundBy = "~/.openclaw" });
    }
}
