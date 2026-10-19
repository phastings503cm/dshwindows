using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Vision.App.Infrastructure;
using Vision.App.Model;
using Vision.App.Views.Guide;
using Vision.Core;

namespace Vision.App.Views;

/// <summary>Drop-down menus shared by the top bar and the composer.</summary>
public static class Menus
{
    public static void Open(ContextMenu menu, FrameworkElement target, PlacementMode placement = PlacementMode.Bottom)
    {
        menu.PlacementTarget = target;
        menu.Placement = placement;
        menu.IsOpen = true;
    }

    public static MenuItem Header(string text) =>
        new() { Header = text, IsEnabled = false, FontSize = 11.5, FontWeight = FontWeights.SemiBold };

    public static MenuItem Item(string header, Action action, string? glyph = null, bool isChecked = false, bool enabled = true)
    {
        var item = new MenuItem { Header = header, IsChecked = isChecked, IsEnabled = enabled };
        if (glyph is not null)
        {
            var icon = new TextBlock { Text = glyph, FontSize = 13 };
            icon.SetResourceReference(FrameworkElement.StyleProperty, "Glyph");
            item.Icon = icon;
        }
        item.Click += (_, _) => action();
        return item;
    }

    /// <summary>The model a chat uses, and one click to change it — for that chat only (its project's next chats start with
    /// it too): what each Spark Swapper box serves (picking a model asks that box to switch), then the configured routes by
    /// role — Primary (the default for new projects), Subagents, Backups, Other — each marked private (a lock) or masked (a
    /// shield), then — signed in to Guardian — the account's models under "Guardian", with nothing to add first. Switched-off
    /// routes aren't offered. Without a chat, picking a route makes it the primary.</summary>
    public static ContextMenu Model(AppModel model, SessionVM? session = null)
    {
        var menu = new ContextMenu();
        var spark = model.Spark;
        foreach (var box in spark.Boxes)
        {
            menu.Items.Add(Header($"{box.Name} — what it serves"));
            if (box.Status is { } status)
            {
                foreach (var m in status.Ordered)
                {
                    var facts = new List<string> { ShortTokens(m.EffectiveContext) };
                    if (m.Speed() is { } speed) facts.Add(speed);
                    var suffix = m.Key == status.Active ? "  (serving)"
                        : !m.IsInstalled ? "  · Not installed"
                        : m.Running ? "  (loading)" : "";
                    var item = Item($"{m.Title} — {string.Join(" · ", facts)}{suffix}", () => spark.Request(box, m),
                        isChecked: m.Key == status.Active, enabled: m.IsInstalled && m.Key != status.Active && !status.IsSwitching);
                    if (!m.IsInstalled)
                    {
                        item.ToolTip = $"Not installed — install it from Settings › Model servers{(m.InstallSummary is { } size ? $" ({size})" : "")}.";
                        ToolTipService.SetShowOnDisabled(item, true);
                    }
                    menu.Items.Add(item);
                }
            }
            else
            {
                menu.Items.Add(new MenuItem { Header = box.LastError ?? $"Connecting to {box.Name}…", IsEnabled = false });
            }
        }
        if (spark.IsConfigured)
        {
            menu.Items.Add(Item("Refresh", () => _ = spark.RefreshAsync(), Icons.Refresh));
            menu.Items.Add(new Separator());
        }
        var config = model.Config;
        var chosen = session is null ? config.ActiveRoute : model.Host.ChatRouteId(session);
        if (session is not null) menu.Items.Add(Header("This chat's model"));
        foreach (var (role, routes) in RouteRoster.Groups(config.Providers, config.ActiveRoute))
        {
            menu.Items.Add(Header(role == RouteRole.Primary && session is not null ? "Primary (new projects start with it)" : role.Title()));
            foreach (var provider in routes)
            {
                var route = provider.RouteId;
                var claudeCode = ClaudeCodeRoutes.IsClaudeCode(provider);
                // A Sensitive chat can't pick a model it may not use (Claude Code reads files itself; see SensitivityPolicy).
                var refusal = session is not null && route != chosen ? model.Host.SensitivityRefusal(session, provider) : null;
                var item = Item(claudeCode ? $"{provider.Name} · {ClaudeCodeRoutes.ModelTitle(provider.Model)}" : provider.DisplayName, () =>
                {
                    if (session is not null)
                    {
                        model.Host.SetChatRoute(session, route);
                        return;
                    }
                    if (config.ActiveRoute == route) return;
                    config.MakePrimary(route);
                    model.Host.ResetRouteCache();
                }, claudeCode ? Icons.Code : provider.IsPrivateRoute ? Icons.Lock : Icons.Shield, isChecked: route == chosen, enabled: refusal is null);
                item.ToolTip = refusal ?? (session is not null
                                   ? route == chosen ? "This chat's model. " : "Use this model in this chat (other chats keep theirs; new chats in this project start with it). "
                                   : role == RouteRole.Primary ? "The primary model: it answers you. " : "Make this the primary model. ") +
                               (claudeCode ? "Claude Code on this computer — your Claude plan, through the claude app (it reads your files itself, so nothing is masked)."
                                : provider.IsPrivateRoute ? "Private — it may see sensitive data." : "Not private — sensitive details are masked before anything is sent.");
                if (refusal is not null) ToolTipService.SetShowOnDisabled(item, true);
                if (route == chosen) item.FontWeight = FontWeights.SemiBold;
                menu.Items.Add(item);
            }
        }
        AddGuardianModels(menu, model, session, chosen);
        if (session is not null && chosen is not null && chosen != config.ActiveRoute
            && config.Providers.FirstOrDefault(p => p.RouteId == chosen) is { } chatRoute)
        {
            menu.Items.Add(new Separator());
            var primary = Item("Make this the primary (for new projects and the fleet)", () =>
            {
                config.MakePrimary(chatRoute.RouteId);
                model.Host.ResetRouteCache();
            }, Icons.Chat);
            primary.ToolTip = "The primary is what new projects start with, and the main server subagents fall back to. Other chats keep their own models.";
            menu.Items.Add(primary);
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Configure Models…", () => model.ShowSettings(SettingsTab.Models), Icons.Settings));
        if (!model.ClaudeCode.InMenu) menu.Items.Add(Item("Use Claude Code (your Claude plan)…", model.ShowClaudeCode, Icons.Code));
        if (!spark.IsConfigured) menu.Items.Add(Item("Connect a Spark Swapper…", () => model.ShowSwapperGuide(), Icons.Connect));
        menu.Items.Add(Item("Run Setup Wizard…", model.ShowWizard));
        return menu;
    }

    /// <summary>The "Guardian" section: the signed-in account's models (those not already saved as routes above), each a click
    /// away for this chat — or, without a chat, made the primary. Signed out, a chat on a Guardian model says so and offers the
    /// sign-in.</summary>
    private static void AddGuardianModels(ContextMenu menu, AppModel model, SessionVM? session, string? chosen)
    {
        var guardian = model.Guardian;
        var config = model.Config;
        if (session is not null && model.Host.GuardianSignInNeeded(session))
        {
            // Vision for macOS: one line naming the model and what to do; it opens Settings › Guardian.
            menu.Items.Add(Header("Guardian"));
            var id = GuardianRoutes.ModelOfRouteId(session.RouteId) ?? session.RouteId ?? "";
            var title = guardian.Models.FirstOrDefault(m => m.Id == id) is { } known ? GuardianRoutes.MenuTitle(known) : id;
            var waiting = Item($"{title} — {GuardianRoutes.SignInNeeded}", model.ShowGuardian, Icons.Shield, isChecked: true);
            waiting.ToolTip = "Opens Settings › Guardian. Or pick another model for this chat.";
            waiting.FontWeight = FontWeights.SemiBold;
            menu.Items.Add(waiting);
            return;
        }
        var models = guardian.MenuModels.Where(m => config.Providers.All(p => p.RouteId != GuardianRoutes.LiveRouteId(m.Id))).ToList();
        if (models.Count == 0) return;
        menu.Items.Add(Header("Guardian"));
        foreach (var guardianModel in models)
        {
            var routeId = GuardianRoutes.LiveRouteId(guardianModel.Id);
            // Guardian's label with the id when they differ, as on the Mac: "Claude Opus 5.5 (claude-opus-5-5)".
            var title = GuardianRoutes.MenuTitle(guardianModel);
            var picked = guardianModel;
            var item = Item(title, () =>
            {
                if (session is not null)
                {
                    model.Host.SetChatRoute(session, routeId);
                    return;
                }
                guardian.UseModel(picked, RouteUse.Primary);
            }, Icons.Shield, isChecked: routeId == chosen);
            item.ToolTip = $"{guardianModel.Id} through your Guardian account" +
                           (guardianModel.ContextWindow is > 0 and var window ? $" ({ShortTokens(window)} context{(guardianModel.InteractiveOnly == true ? ", interactive use only" : "")}). " : ". ") +
                           (session is not null
                               ? routeId == chosen ? "This chat's model. " : "Use this model in this chat (other chats keep theirs; new chats in this project start with it). "
                               : "Make this the primary model. ") +
                           "Not private — sensitive details are masked before anything is sent.";
            if (routeId == chosen) item.FontWeight = FontWeights.SemiBold;
            menu.Items.Add(item);
        }
        if (session is not null && models.FirstOrDefault(m => GuardianRoutes.LiveRouteId(m.Id) == chosen) is { } current)
        {
            var primary = Item("Make this the primary (for new projects and the fleet)", () => guardian.UseModel(current, RouteUse.Primary), Icons.Chat);
            primary.ToolTip = "Saves this Guardian model as a route in Settings › AI Models and makes it the primary. Other chats keep their own models.";
            menu.Items.Add(primary);
        }
    }

    /// <summary>Per-chat thinking level: only the levels the chat's model takes (GLM-5.3: Low, High, Max; Kimi K2.6: Off, On;
    /// Kimi K2.7 Code: none — it always thinks).</summary>
    public static ContextMenu Thinking(AppModel model, SessionVM session)
    {
        var menu = new ContextMenu();
        var fallback = model.Host.ChatRoute(session)?.Thinking;
        var rules = model.Host.RequestRules(session);
        var offered = rules?.ThinkingLevels ?? ThinkingLevels.All;
        string Name(ThinkingLevel level) => rules?.Label(level) ?? level.Label();
        var shownDefault = fallback is { } f && rules?.NearestLevel(f) is { } nearDefault ? Name(nearDefault) : fallback?.Label();
        menu.Items.Add(Item($"Provider default ({shownDefault ?? "server default"})", () => model.Host.SetChatThinking(session, null),
            isChecked: session.Thinking is null));
        menu.Items.Add(new Separator());
        if (offered.Count == 0)
        {
            menu.Items.Add(new MenuItem
            {
                Header = rules?.AlwaysThinks == true ? "This model always thinks and takes no thinking setting." : "This model has no thinking setting.",
                IsEnabled = false,
            });
            return menu;
        }
        // A level the model doesn't offer (chosen for another model) shows as the one it gets.
        var current = session.Thinking is { } own ? rules?.NearestLevel(own) ?? own : (ThinkingLevel?)null;
        foreach (var level in offered)
        {
            var blurb = rules?.Thinking.Kind == ThinkingControlKind.Toggle && level != ThinkingLevel.Off ? "Thinks before answering" : level.Blurb();
            menu.Items.Add(Item($"{Name(level)} — {blurb}", () => model.Host.SetChatThinking(session, level), isChecked: current == level));
        }
        if (rules?.AlwaysThinks == true)
            menu.Items.Add(new MenuItem { Header = "This model always thinks: Low asks for the least.", IsEnabled = false, FontSize = 11.5 });
        return menu;
    }

    /// <summary>"Thinking: High" for the composer: the level the chat's model really gets ("Always on" for one that takes no
    /// setting).</summary>
    public static string ThinkingLabel(AppModel model, SessionVM session)
    {
        var rules = model.Host.RequestRules(session);
        if (rules is { ThinkingLevels.Count: 0 }) return rules.AlwaysThinks ? "Always thinks" : "No thinking setting";
        if (model.Host.ThinkingFor(session) is not { } level) return "Thinking: Default";
        return $"Thinking: {(rules?.NearestLevel(level) is { } nearest ? rules.Label(nearest) : level.Label())}";
    }

    /// <summary>"128K", "1M" — context sizes for menus.</summary>
    public static string ShortTokens(int n) =>
        n >= 1_000_000
            ? (n / 1_000_000.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "M"
            : $"{n / 1024}K";
}
