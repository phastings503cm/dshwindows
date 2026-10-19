using System.Windows;
using Vision.App.Model;
using Vision.App.Views;
using Vision.App.Views.Guide;
using Vision.App.Views.Wizard;
using Vision.Core;
using MessageRole = Vision.App.Model.MessageRole;

namespace Vision.App;

// MARK: - The mixing bowl's models
//
// DeepSeek and Anthropic (API key) in the plan guide, the wizard's cards for them, and Guardian's models in a chat's model
// menu: signed in (a "Guardian" section, nothing to add first) and signed out (the chat on a Guardian model says "Sign in to
// Guardian to use this model"). Sample state only: nothing is contacted, and the Guardian sign-in is the demo one.

public sealed partial class SelfTest
{
    private async Task CaptureModels(Window window, AppModel model)
    {
        var wizard = new SetupWizardWindow(model) { Owner = window, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        try
        {
            wizard.Show();
            await Settle(600);
            // The welcome page's new cards, under the others.
            wizard.ScrollGuide(1.0);
            await Settle(400);
            Capture(wizard, "wizard-welcome-more");
            var shots = new (string Plan, PlanPage Page, string Key, IReadOnlyList<string>? Listed, string Name)[]
            {
                (SubscriptionPlans.DeepSeek, PlanPage.Key, "", null, "subscription-deepseek-key"),
                (SubscriptionPlans.DeepSeek, PlanPage.Model, "sk-0123456789abcdef0123456789abcdef", ["deepseek-flash", "deepseek-v4-pro"], "subscription-deepseek-model"),
                (SubscriptionPlans.Anthropic, PlanPage.Key, "", null, "subscription-anthropic-key"),
                (SubscriptionPlans.Anthropic, PlanPage.Paste, "sk-ant-oat01-0123456789abcdef", null, "subscription-anthropic-paste-subscription"),
                (SubscriptionPlans.Anthropic, PlanPage.Model, "sk-ant-api03-0123456789abcdef",
                    ["claude-fable-5-1", "claude-haiku-4-5", "claude-haiku-5-5", "claude-opus-4-8", "claude-opus-5-5", "claude-sonnet-5-5"], "subscription-anthropic-model"),
            };
            foreach (var (plan, page, key, listed, name) in shots)
            {
                wizard.ShowPlanDemo(plan, page, key, listed);
                await Settle(800);
                Capture(wizard, name);
            }
        }
        catch (Exception error)
        {
            Fail("deepseek/anthropic guide", error);
        }
        finally
        {
            try { wizard.Close(); } catch (Exception) { }
        }

        // Guardian's models in a chat's model menu, with no route saved for them.
        var guardian = model.Guardian;
        try
        {
            guardian.ShowDemo(signedIn: true);
            var chat = model.NewChat();
            chat.Title = "Guardian chat";
            model.Host.SetChatRoute(chat, GuardianRoutes.LiveRouteId("global.anthropic.claude-sonnet-5-5"));
            chat.AppendMessage(MessageRole.User, "Summarise yesterday's pipeline alerts.");
            Note($"guardian chat route: {model.Host.ChatRouteId(chat)}");
            if (model.Host.ChatRouteId(chat) != GuardianRoutes.LiveRouteId("global.anthropic.claude-sonnet-5-5"))
                Fail("guardian model route", new InvalidOperationException("the chat didn't take the Guardian model"));
            model.Select(chat.Id);
            await Settle(800);
            Capture(window, "chat-guardian-model");
            await CaptureMenu(window, Menus.Model(model, chat), "menu-model-guardian");

            // Signed out: the chat keeps its model and says how to get it back.
            guardian.ShowDemo(signedIn: false);
            model.Host.NotifyRoutesChanged();
            chat.Note(GuardianRoutes.SignInNeeded, MessageRole.Error);
            await Settle(800);
            Capture(window, "chat-guardian-signed-out");
            await CaptureMenu(window, Menus.Model(model, chat), "menu-model-guardian-signed-out");
        }
        catch (Exception error)
        {
            Fail("guardian models", error);
        }
        finally
        {
            guardian.ShowDemo(signedIn: false);
            guardian.Status = null;
            model.Host.NotifyRoutesChanged();
        }
    }
}
