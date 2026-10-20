using System.Windows;
using Vision.App.Model;
using Vision.App.Views.Settings;
using Vision.Core;

namespace Vision.App;

// MARK: - The primary-led subagent pool
//
// Settings › AI Models with routes of every kind ticked "Subagent" — Amazon Bedrock, Claude Code, and one of Guardian's models
// through the Guardian map (no saved route) — and the pool card under them; then the catalogue a chat on the Spark gets in its
// agent tool, written to the report. Sample state only: nothing is contacted, and the Guardian sign-in is the demo one. The
// flags are put back afterwards.

public sealed partial class SelfTest
{
    private async Task CapturePool(Window window, AppModel model)
    {
        var config = model.Config;
        var guardian = model.Guardian;
        var claudeCode = ClaudeCodeRoutes.Route("sonnet");
        claudeCode.SubagentWorker = true;
        var bedrock = config.Providers.FirstOrDefault(p => p.Kind == ProviderKind.Bedrock);
        const string guardianModel = "global.anthropic.claude-sonnet-5-5";
        try
        {
            guardian.ShowDemo(signedIn: true);
            if (bedrock is not null)
            {
                bedrock.SubagentWorker = true;
                bedrock.SubagentParallel = 4;
            }
            config.Store(claudeCode);
            config.SetGuardianSubagent(guardianModel, true);
            model.Host.RefreshFleet();

            // What a chat on the primary (the Spark) is told: the catalogue in its agent tool's description.
            var chat = model.NewChat();
            chat.Title = "Pool chat";
            if (config.ActiveRoute is { } primary) model.Host.SetChatRoute(chat, primary);
            var pool = model.Host.SubagentPoolFor(chat);
            Note("pool catalogue:\n" + (SubagentPool.Describe(pool) ?? "(none)"));
            if (pool?.Dropped is { Count: > 0 } dropped) Note("pool dropped: " + string.Join(", ", dropped.Select(d => $"{d.Id} ({d.Reason})")));
            if (pool is not { HasWorkers: true }) Fail("subagent pool", new InvalidOperationException("the chat's pool has no subagent servers"));

            var settings = new SettingsWindow(model, SettingsTab.Models, null) { Owner = window, Width = 1000, Height = 1000 };
            try
            {
                settings.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                settings.Show();
                await Settle(800);
                Capture(settings, "settings-models-pool");
                // Further down: the Guardian group and the pool card.
                settings.SelfTestScroll(0.55);
                await Settle(500);
                Capture(settings, "settings-models-pool-card");
            }
            finally
            {
                try { settings.Close(); } catch (Exception) { }
            }
        }
        catch (Exception error)
        {
            Fail("subagent pool", error);
        }
        finally
        {
            if (bedrock is not null)
            {
                bedrock.SubagentWorker = false;
                bedrock.SubagentParallel = null;
            }
            config.RemoveProvider(claudeCode);
            config.SetGuardianSubagent(guardianModel, false);
            guardian.ShowDemo(signedIn: false);
            guardian.Status = null;
            model.Host.RefreshFleet();
            model.Host.NotifyRoutesChanged();
        }
    }
}
