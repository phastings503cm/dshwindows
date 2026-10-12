using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Dsh.App.Infrastructure;
using Dsh.App.Model;
using Dsh.App.Views.Dialogs;
using Dsh.Core;

namespace Dsh.App.Views.Settings;

/// <summary>Settings › Model servers: the Spark Swapper boxes DSH knows — a DGX Spark, a Strix Halo — what each is
/// serving, switching it, installing models on it, and adding, renaming or removing a box.</summary>
public sealed class ModelServersPage : UserControl
{
    private readonly AppModel _model;
    private readonly StackPanel _boxes = new();
    private bool _refreshQueued;

    public ModelServersPage(AppModel model)
    {
        _model = model;
        var spark = model.Spark;

        var page = new StackPanel { MaxWidth = 760 };
        page.Children.Add(Ui.Title("Model servers"));
        var intro = Ui.Secondary("Your Spark Swapper boxes — a DGX Spark or a Strix Halo running Spark Swapper. DSH switches what each one serves " +
                                 "(from the model menu under the composer, or with /swap), installs models on it, and every chat follows a switch.");
        intro.Margin = new Thickness(0, 6, 0, 10);
        page.Children.Add(intro);
        page.Children.Add(Ui.Buttons(
            Ui.Button("Add with a connection code…", () => model.ShowSwapperGuide(), accent: true,
                tooltip: "On the Swapper's page choose Connect, then Connect DSH, and copy the code"),
            Ui.Button("Refresh", () => _ = spark.RefreshAsync())));
        var login = Ui.LinkButton("Connect with an address and login instead", () => model.ShowSwapperGuide(login: true));
        login.HorizontalAlignment = HorizontalAlignment.Left;
        login.Margin = new Thickness(0, 6, 0, 0);
        login.ToolTip = "For a Spark Swapper older than 2.0, which has no connection codes";
        page.Children.Add(login);
        page.Children.Add(_boxes);
        Content = Ui.Scroll(page);

        PropertyChangedEventHandler handler = (_, _) => QueueRefresh();
        spark.PropertyChanged += handler;
        Unloaded += (_, _) => spark.PropertyChanged -= handler;
        Refresh();
        if (spark.Boxes.Any(b => b.Status is null)) _ = spark.RefreshAsync();
    }

    /// <summary>Several changes at once (a poll, an install tick) redraw once.</summary>
    private void QueueRefresh()
    {
        if (_refreshQueued) return;
        _refreshQueued = true;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
        {
            _refreshQueued = false;
            Refresh();
        });
    }

    private void Refresh()
    {
        _boxes.Children.Clear();
        var spark = _model.Spark;
        if (spark.Boxes.Count == 0)
        {
            var empty = Ui.Card(Ui.Stack(
                Ui.Text("No model servers yet", 14, FontWeights.SemiBold),
                Ui.Secondary("Add a box with its connection code: open the Swapper's page in a browser (https://<box>:8999), choose Connect, then Connect DSH, and copy the code.")));
            empty.Margin = new Thickness(0, 16, 0, 0);
            _boxes.Children.Add(empty);
            return;
        }
        foreach (var box in spark.Boxes) _boxes.Children.Add(BoxCard(box));
    }

    private UIElement BoxCard(SwapperBox box)
    {
        var spark = _model.Spark;
        var panel = new StackPanel();
        panel.Children.Add(Ui.Section(box.Name));

        // Name, kind, state, version — and what to do with the box.
        var dot = new System.Windows.Shapes.Ellipse { Width = 9, Height = 9, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty,
            box.Online ? "SystemFillColorSuccessBrush" : box.LastError is null ? "TextFillColorTertiaryBrush" : "SystemFillColorCriticalBrush");
        var facts = new List<string>();
        if (box.PlatformLabel is { } platform) facts.Add(platform);
        facts.Add(box.Online ? "online" : box.Refreshing ? "connecting…" : box.LastError is null ? "not checked yet" : "not answering");
        if (box.Status?.Version is { } version) facts.Add($"Spark Swapper {version}");
        if (box.Connection.Auth == SwapperAuth.Password) facts.Add("signed in with a login");
        var serving = box.Status is not { } status ? box.LastError ?? "Asking it what it serves…"
            : status.ActiveModel is { } active
                ? $"Serving {active.Title} · {active.EffectiveContext:N0}-token context" + (active.Speed() is { } speed ? $" · {speed}" : "")
                : "Nothing is serving right now.";
        var head = new DockPanel();
        var actions = Ui.Buttons(
            Ui.Button("Open its web page", () => ShellIntegration.Open(box.Connection.Url)),
            Ui.Button("Rename", () =>
            {
                if (Dialog.Prompt("Rename", "A name for this box:", box.Name, "Rename") is { } name) spark.Rename(box, name);
            }),
            IconButton(Icons.Delete, "Remove", () =>
            {
                if (!Dialog.Confirm($"Remove {box.Name}?",
                        "DSH forgets this box and its connection. Its models stay where they are, and its routes in AI Models stay too. " +
                        "The box still lists this app's connection: revoke it on the Swapper's Connect tab if you won't use it again.",
                        "Remove", destructive: true)) return;
                spark.Remove(box);
            }));
        DockPanel.SetDock(actions, Dock.Right);
        head.Children.Add(actions);
        DockPanel.SetDock(dot, Dock.Left);
        head.Children.Add(dot);
        var titles = Ui.Stack(Ui.Text(string.Join(" · ", facts), 12.5, brushKey: "TextFillColorSecondaryBrush"), Ui.Text(serving, 13.5));
        titles.VerticalAlignment = VerticalAlignment.Center;
        head.Children.Add(titles);
        var card = new StackPanel();
        card.Children.Add(head);

        // Not answering now: what it said last stays above, and why it isn't answering shows here.
        if (box.LastError is { } stale && box.Status is not null && box.UntrustedFingerprint is null)
            card.Children.Add(Spaced(Ui.Status(stale, "SystemFillColorCriticalBrush")));
        if (box.ProgressLine is { } line) card.Children.Add(Spaced(Ui.Status(line, "SystemFillColorCautionBrush")));
        if (box.InstallLine is { } installing) card.Children.Add(Spaced(Ui.Status(installing, "SystemFillColorCautionBrush")));
        if (box.InstallError is { } installError) card.Children.Add(Spaced(Ui.Status(installError, "SystemFillColorCriticalBrush")));
        if (box.UntrustedFingerprint is { } fingerprint)
        {
            var print = Ui.Field(fingerprint, mono: true);
            print.IsReadOnly = true;
            print.Margin = new Thickness(0, 6, 0, 6);
            card.Children.Add(Spaced(Ui.Stack(
                Ui.Secondary(box.Connection.Auth == SwapperAuth.Token
                    ? "This box showed a different security certificate than the one DSH trusts. If you reinstalled the Swapper, paste a fresh connection code. Otherwise don't continue — something may be intercepting the connection."
                    : "The box's self-signed certificate isn't the one DSH trusts. Trust it only if this fingerprint matches the Swapper's (sudo openssl x509 -in /etc/spark-swapper/tls.crt -noout -fingerprint -sha256):"),
                print,
                box.Connection.Auth == SwapperAuth.Token
                    ? Ui.Button("Paste a New Code…", () => _model.ShowSwapperGuide(), accent: true)
                    : Ui.Button("Trust This Certificate", () => spark.TrustPresentedCertificate(box), accent: true))));
        }
        else if (box.LastError is { } error && error.Contains("connection code", StringComparison.Ordinal))
        {
            card.Children.Add(Spaced(Ui.Buttons(Ui.Button("Paste a New Code…", () => _model.ShowSwapperGuide(), accent: true))));
        }
        panel.Children.Add(Ui.Card(card));

        // Its models: switch to one, or install one it doesn't have yet.
        if (box.Status is { } current)
        {
            foreach (var m in current.Ordered) panel.Children.Add(ModelRow(box, current, m));
        }
        return panel;
    }

    private UIElement ModelRow(SwapperBox box, SwapperStatus status, SwapperModel m)
    {
        var spark = _model.Spark;
        var serving = m.Key == status.Active;
        var installingThis = box.InstallJob is { IsRunning: true } job && (box.InstallingKey ?? SwapperJob.ModelOf(job.Target)) == m.Key;
        var details = new List<string> { m.ServedId, $"{m.EffectiveContext:N0} ctx" };
        if (m.Speed() is { } speed) details.Add(speed);
        if (!m.IsInstalled) details.Add("Not installed" + (m.InstallSummary is { } size ? $" ({size})" : ""));
        if (m.IsRecommended) details.Add("recommended");
        UIElement action;
        if (serving)
        {
            action = Ui.Text("Serving", 12, FontWeights.SemiBold, "SystemFillColorSuccessBrush", wrap: false);
        }
        else if (installingThis)
        {
            action = Ui.Text("Installing…", 12, FontWeights.SemiBold, "SystemFillColorCautionBrush", wrap: false);
        }
        else if (!m.IsInstalled)
        {
            var install = Ui.Button($"Install on {box.Name}", async () =>
            {
                if (!Dialog.Confirm($"Install {m.Title} on {box.Name}?",
                        (m.InstallSummary is { } summary ? $"It takes {summary}. " : "") +
                        "It downloads and prepares the model on the box; DSH follows it here, and you can switch to it when it's done.", "Install"))
                    return;
                if (await spark.InstallAsync(box, m.Key) is { } error) Dialog.Info($"Couldn't install {m.Title}", error);
            });
            install.IsEnabled = box.InstallJob is not { IsRunning: true };
            action = install;
        }
        else
        {
            var label = box.JustInstalled == m.Key ? "Start it" : "Switch";
            var button = Ui.Button(label, () => spark.Request(box, m));
            button.IsEnabled = !status.IsSwitching;
            button.ToolTip = $"Switching takes {SwapperSetupFlows.SwitchTime(m)}.";
            action = button;
        }
        var glyph = serving ? Icons.Completed : !m.IsInstalled ? Icons.Download : Icons.CircleRing;
        var row = Ui.Row(m.Title, string.Join(" · ", details), action, glyph);
        if (!m.IsInstalled && !installingThis) row.Opacity = 0.8;
        if (m.Tagline is { Length: > 0 } tagline) row.ToolTip = tagline;
        return row;
    }

    private static FrameworkElement Spaced(FrameworkElement element)
    {
        element.Margin = new Thickness(0, 8, 0, 0);
        return element;
    }

    private static Button IconButton(string glyph, string tooltip, Action action)
    {
        var button = new Button { Content = glyph, ToolTip = tooltip, Margin = new Thickness(6, 0, 0, 0) };
        button.SetResourceReference(StyleProperty, "IconButton");
        System.Windows.Automation.AutomationProperties.SetName(button, tooltip);
        button.Click += (_, _) => action();
        return button;
    }
}
