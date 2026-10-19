using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Vision.App.Infrastructure;
using Vision.App.Model;
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
    /// shield). Switched-off routes aren't offered. Without a chat, picking a route makes it the primary.</summary>
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
                var item = Item(provider.DisplayName, () =>
                {
                    if (session is not null)
                    {
                        model.Host.SetChatRoute(session, route);
                        return;
                    }
                    if (config.ActiveRoute == route) return;
                    config.MakePrimary(route);
                    model.Host.ResetRouteCache();
                }, provider.IsPrivateRoute ? Icons.Lock : Icons.Shield, isChecked: route == chosen);
                // A Sensitive chat can't pick a route its policy refuses (Claude Code; online ones while the shield is off).
                if (session is not null && model.Host.AdmissionFor(session, provider) is { IsAllowed: false, Refusal: { } refusal })
                {
                    item.IsEnabled = route == chosen;
                    item.ToolTip = refusal;
                    ToolTipService.SetShowOnDisabled(item, true);
                    menu.Items.Add(item);
                    continue;
                }
                item.ToolTip = (session is not null
                                   ? route == chosen ? "This chat's model. " : "Use this model in this chat (other chats keep theirs; new chats in this project start with it). "
                                   : role == RouteRole.Primary ? "The primary model: it answers you. " : "Make this the primary model. ") +
                               (provider.IsPrivateRoute ? "Private — it may see sensitive data." : "Not private — sensitive details are masked before anything is sent.");
                if (route == chosen) item.FontWeight = FontWeights.SemiBold;
                menu.Items.Add(item);
            }
        }
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
        if (!spark.IsConfigured) menu.Items.Add(Item("Connect a Spark Swapper…", () => model.ShowSwapperGuide(), Icons.Connect));
        menu.Items.Add(Item("Run Setup Wizard…", model.ShowWizard));
        return menu;
    }

    /// <summary>The header's Sensitive menu (Vision for macOS's composer menu): this chat, and what new chats in its project start
    /// with, and what the policy is.</summary>
    public static ContextMenu Sensitivity(AppModel model, SessionVM? session)
    {
        var menu = new ContextMenu();
        var host = model.Host;
        var project = session is not null ? session.WorkspacePath : model.Project;
        if (session is not null)
            menu.Items.Add(Item("Sensitive Chat", () => host.SetChatSensitive(session, !session.Sensitive), Icons.Lock, isChecked: session.Sensitive));
        var name = project is { } p ? System.IO.Path.GetFileName(p.TrimEnd('\\', '/')) : null;
        var starts = host.ProjectStartsSensitive(project);
        menu.Items.Add(Item(name is null ? "New Chats Without a Project Start Sensitive" : $"New Chats in {name} Start Sensitive",
            () => host.SetProjectStartsSensitive(project, !starts), isChecked: starts));
        menu.Items.Add(new Separator());
        var explanation = new MenuItem
        {
            Header = new TextBlock { Text = SensitivityPolicy.Explanation, TextWrapping = TextWrapping.Wrap, MaxWidth = 380 },
            IsEnabled = false,
        };
        menu.Items.Add(explanation);
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Sensitive-Data Shield Settings…", () => model.ShowSettings(SettingsTab.Models), Icons.Settings));
        return menu;
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
