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

    /// <summary>The model in use, and one click to change it: the Spark's models (served by the
    /// Spark Swapper) and the provider routes configured in Settings.</summary>
    public static ContextMenu Model(AppModel model)
    {
        var menu = new ContextMenu();
        var spark = model.Spark;
        if (spark.IsConfigured)
        {
            menu.Items.Add(Header("DGX Spark — what it serves"));
            if (spark.Status is { } status)
            {
                foreach (var m in status.Ordered)
                {
                    var suffix = m.Key == status.Active ? "  (serving)" : m.Running ? "  (loading)" : "";
                    menu.Items.Add(Item($"{m.Title} — {ShortTokens(m.ServedContext ?? m.Context)}{suffix}", () => spark.Request(m),
                        isChecked: m.Key == status.Active, enabled: m.Key != status.Active && !status.IsSwitching));
                }
            }
            else
            {
                menu.Items.Add(new MenuItem { Header = spark.LastError ?? "Connecting to the Spark…", IsEnabled = false });
            }
            menu.Items.Add(Item("Refresh", () => _ = spark.RefreshAsync(), Icons.Refresh));
            menu.Items.Add(new Separator());
        }
        menu.Items.Add(Header("Providers"));
        foreach (var provider in model.Config.Providers)
        {
            var route = provider.RouteId;
            menu.Items.Add(Item(provider.DisplayName, () =>
            {
                model.Config.ActiveRoute = route;
                model.Host.ResetRouteCache();
            }, isChecked: route == model.Config.ActiveRoute));
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Configure Models…", () => model.ShowSettings(SettingsTab.Models), Icons.Settings));
        if (!spark.IsConfigured) menu.Items.Add(Item("Set Up Spark Model Switching…", () => model.ShowSettings(SettingsTab.Spark)));
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
