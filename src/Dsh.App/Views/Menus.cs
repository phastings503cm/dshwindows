using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Dsh.App.Infrastructure;
using Dsh.App.Model;
using Dsh.Core;

namespace Dsh.App.Views;

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

    /// <summary>The model in use, and one click to change it: what each Spark Swapper box serves (picking a model asks that
    /// box to switch), then the configured routes by role — Primary, Subagents, Backups, Other — each marked private (a
    /// lock) or masked (a shield). Switched-off routes aren't offered.</summary>
    public static ContextMenu Model(AppModel model)
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
        foreach (var (role, routes) in RouteRoster.Groups(config.Providers, config.ActiveRoute))
        {
            menu.Items.Add(Header(role.Title()));
            foreach (var provider in routes)
            {
                var route = provider.RouteId;
                var item = Item(provider.DisplayName, () =>
                {
                    if (config.ActiveRoute == route) return;
                    config.MakePrimary(route);
                    model.Host.ResetRouteCache();
                }, provider.IsPrivateRoute ? Icons.Lock : Icons.Shield);
                item.ToolTip = (role == RouteRole.Primary ? "The primary model: it answers you. " : "Make this the primary model. ") +
                               (provider.IsPrivateRoute ? "Private — it may see sensitive data." : "Not private — sensitive details are masked before anything is sent.");
                if (role == RouteRole.Primary) item.FontWeight = FontWeights.SemiBold;
                menu.Items.Add(item);
            }
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Configure Models…", () => model.ShowSettings(SettingsTab.Models), Icons.Settings));
        if (!spark.IsConfigured) menu.Items.Add(Item("Connect a Spark Swapper…", () => model.ShowSwapperGuide(), Icons.Connect));
        menu.Items.Add(Item("Run Setup Wizard…", model.ShowWizard));
        return menu;
    }

    /// <summary>Per-chat thinking level.</summary>
    public static ContextMenu Thinking(AppModel model, SessionVM session)
    {
        var menu = new ContextMenu();
        var fallback = model.Config.ActiveProvider?.Thinking;
        menu.Items.Add(Item($"Provider default ({fallback?.Label() ?? "server default"})", () => session.Thinking = null,
            isChecked: session.Thinking is null));
        menu.Items.Add(new Separator());
        foreach (var level in ThinkingLevels.All)
        {
            menu.Items.Add(Item($"{level.Label()} — {level.Blurb()}", () => session.Thinking = level, isChecked: session.Thinking == level));
        }
        return menu;
    }

    /// <summary>"128K", "1M" — context sizes for menus.</summary>
    public static string ShortTokens(int n) =>
        n >= 1_000_000
            ? (n / 1_000_000.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "M"
            : $"{n / 1024}K";
}
