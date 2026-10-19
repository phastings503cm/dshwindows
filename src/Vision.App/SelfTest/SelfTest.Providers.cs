using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Vision.App.Model;
using Vision.App.Views;
using Vision.App.Views.Guide;
using Vision.App.Views.Settings;
using Vision.App.Views.Wizard;
using Vision.Core;
using MessageRole = Vision.App.Model.MessageRole;

namespace Vision.App;

// MARK: - Providers and each chat's own model
//
// Z.ai's endpoint picker and key check (a Coding Plan key on the General API), the route editor for a Z.ai route and for a
// model that always thinks, and two chats on two different models at once — with the model and thinking menus of the
// selected one. Nothing is contacted: the routes point nowhere and every result is sample state.

public sealed partial class SelfTest
{
    private async Task CaptureProviders(Window window, AppModel model)
    {
        var zaiGuide = new SetupWizardWindow(model) { Owner = window, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        try
        {
            zaiGuide.Show();
            foreach (var page in new[] { PlanPage.Choose, PlanPage.Check })
            {
                zaiGuide.ShowZaiDemo(page);
                await Settle(900);
                Capture(zaiGuide, $"subscription-zai-{page.ToString().ToLowerInvariant()}");
            }
            // The endpoint picker, under the plans.
            zaiGuide.ShowZaiDemo(PlanPage.Choose);
            await Settle(400);
            zaiGuide.ScrollGuide(1.0);
            await Settle(400);
            Capture(zaiGuide, "subscription-zai-choose-more");
        }
        catch (Exception error)
        {
            Fail("z.ai guide", error);
        }
        finally
        {
            try { zaiGuide.Close(); } catch (Exception) { }
        }

        try
        {
            var zai = new ProviderEditorWindow(model, new ProviderProfile(ProviderKind.OpenAICompat, ZaiEndpoint.General.RouteName(),
                ZaiEndpoint.General.BaseUrl(), "glm-5.3") { ContextWindow = 1_000_000, ReasoningEffort = "medium" }) { Owner = window, Height = 900 };
            zai.Loaded += (_, _) => zai.ShowZaiDemo();
            await ShowAndCapture(zai, "route-editor-zai");
            await ShowAndCapture(new ProviderEditorWindow(model, new ProviderProfile(ProviderKind.OpenAICompat, "Moonshot",
                "https://api.moonshot.ai/v1", "kimi-k2.7-code") { Temperature = 0.6 }) { Owner = window, Height = 1200 }, "route-editor-kimi");
        }
        catch (Exception error)
        {
            Fail("route editor", error);
        }

        // Two chats at once, each on its own model: the second one is on a Z.ai route while the first stays on the Spark.
        try
        {
            var zaiRoute = new ProviderProfile(ProviderKind.OpenAICompat, "GLM Coding Plan", ZaiEndpoint.Coding.BaseUrl(), "glm-5.3")
            {
                ContextWindow = 1_000_000,
            };
            model.Config.Store(zaiRoute);
            var spark = model.NewChat();
            spark.Title = "Spark chat";
            spark.AppendMessage(MessageRole.User, "Run the tests on the Spark.");
            var glm = model.NewChat();
            glm.Title = "GLM chat";
            model.Host.SetChatRoute(glm, zaiRoute.RouteId);
            model.Host.SetChatThinking(glm, ThinkingLevel.Medium);
            glm.AppendMessage(MessageRole.User, "Review the diff with GLM-5.3.");
            Note($"chat routes: spark={model.Host.ChatRouteId(spark)}, glm={model.Host.ChatRouteId(glm)}");
            if (model.Host.ChatRouteId(spark) == model.Host.ChatRouteId(glm)) Fail("chat routes", new InvalidOperationException("both chats on one route"));
            model.Select(glm.Id);
            await Settle(1000);
            Capture(window, "chat-own-model");
            await CaptureMenu(window, Menus.Model(model, glm), "menu-model");
            await CaptureMenu(window, Menus.Thinking(model, glm), "menu-thinking");
            model.Select(spark.Id);
            await Settle(600);
            Capture(window, "chat-own-model-other");

            // A setting the Z.ai route's server refused, as Settings › AI Models shows it (with Forget).
            RequestAdaptationStore.Shared.Learn(new RequestChange.Drop("temperature"), RequestAdaptationStore.Key(zaiRoute.BaseUrl, zaiRoute.Model));
            await ShowAndCapture(new SettingsWindow(model, SettingsTab.Models, null) { Owner = window, Width = 1000, Height = 1000 }, "settings-models-learned");
        }
        catch (Exception error)
        {
            Fail("chat routes", error);
        }
    }

    /// <summary>A drop-down menu, opened over the window and rendered on its own (a window capture leaves popups out).</summary>
    private async Task CaptureMenu(Window window, ContextMenu menu, string name)
    {
        try
        {
            menu.PlacementTarget = window;
            menu.Placement = PlacementMode.Center;
            menu.IsOpen = true;
            await Settle(500);
            menu.UpdateLayout();
            var dpi = VisualTreeHelper.GetDpi(menu);
            var width = (int)Math.Ceiling(menu.ActualWidth * dpi.DpiScaleX);
            var height = (int)Math.Ceiling(menu.ActualHeight * dpi.DpiScaleY);
            if (width <= 0 || height <= 0) throw new InvalidOperationException("The menu has no size.");
            var bitmap = new RenderTargetBitmap(width, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            bitmap.Render(menu);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            var path = System.IO.Path.Combine(Output, $"{name}-{Theme ?? "light"}.png");
            using (var stream = System.IO.File.Create(path)) encoder.Save(stream);
            Note($"captured {System.IO.Path.GetFileName(path)} ({width}x{height})");
        }
        catch (Exception error)
        {
            Fail(name, error);
        }
        finally
        {
            menu.IsOpen = false;
        }
    }
}
