using System.IO;
using System.Windows;
using Vision.App.Model;
using Vision.App.Views;
using Vision.App.Views.Dialogs;
using Vision.App.Views.Guide;
using Vision.App.Views.Settings;
using Vision.App.Views.Wizard;
using Vision.Core;

namespace Vision.App;

public sealed partial class SelfTest
{
    /// <summary>Guardian: Settings › Guardian waiting on a 2FA code and signed in (with its limits, models, notebook, projects
    /// and secrets), the refusal dialogs, and the wizard's Guardian guide. Sample state only: nothing is contacted, and nothing
    /// is saved.</summary>
    private async Task CaptureGuardian(Window owner, AppModel model)
    {
        var guardian = model.Guardian;
        try
        {
            guardian.ShowDemo(signedIn: false, twoFactor: true);
            await ShowAndCapture(new SettingsWindow(model, SettingsTab.Guardian, null) { Owner = owner, Width = 1000, Height = 760 }, "guardian-2fa");

            guardian.ShowDemo(signedIn: true);
            var settings = new SettingsWindow(model, SettingsTab.Guardian, null) { Owner = owner, Width = 1000, Height = 900, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            try
            {
                settings.Show();
                await Settle(600);
                var page = (GuardianPage)settings.CurrentPage!;
                page.ShowDemoLists();
                await Settle(600);
                Capture(settings, "guardian-signed-in");
                foreach (var (fraction, name) in new[] { (0.45, "guardian-signed-in-more"), (1.0, "guardian-signed-in-end") })
                {
                    page.ScrollTo(fraction);
                    await Settle(400);
                    Capture(settings, name);
                }
            }
            finally
            {
                try { settings.Close(); } catch (Exception) { }
            }
        }
        catch (Exception error)
        {
            Fail("guardian settings", error);
        }

        // Refusals, in Guardian's own words (the bodies harness-errors.ts sends).
        var refusals = new (string Name, int Status, string Body)[]
        {
            ("guardian-refusal-401", 401, """{"error":"unauthorized","status":401,"message":"Your Guardian sign-in has ended or was revoked. Sign in again from Vision."}"""),
            ("guardian-refusal-403", 403, """{"error":"forbidden","status":403,"reason":"module_disabled","needed":"files","message":"My Files is not turned on for you in this Guardian org. Ask Pat Hastings or Sam Lee (admins of 503CM) to turn it on for you."}"""),
            ("guardian-refusal-402", 402, $$"""{"error":"budget_exceeded","status":402,"limit":{"kind":"spend","window":"week","used":50.12,"max":50,"unit":"usd","resetsAt":"{{DateTimeOffset.UtcNow.AddDays(3).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")}}"},"message":"You've used your $50.00 weekly Vision budget in 503CM. It resets on Monday, or an admin can raise it."}"""),
            ("guardian-refusal-429", 429, """{"error":"rate_limited","status":429,"limit":{"kind":"requests","window":"run","used":6,"max":6,"unit":"streams","resetsAt":null},"message":"You already have 6 Vision requests running. Wait for one to finish."}"""),
        };
        foreach (var (name, status, body) in refusals)
            await ShowAndCapture(new GuardianRefusalWindow(GuardianError.FromResponse(status, body)) { Owner = owner }, name);

        // The setup wizard's Guardian guide.
        var wizard = new SetupWizardWindow(model) { Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        try
        {
            wizard.Show();
            foreach (var page in Enum.GetValues<GuardianGuidePage>())
            {
                wizard.ShowGuardianDemo(page);
                await Settle(900);
                Capture(wizard, $"guardian-guide-{page.ToString().ToLowerInvariant()}");
            }
        }
        catch (Exception error)
        {
            Fail("guardian guide", error);
        }
        finally
        {
            try { wizard.Close(); } catch (Exception) { }
            guardian.ShowDemo(signedIn: false);
            guardian.Status = null;
        }
    }
}
