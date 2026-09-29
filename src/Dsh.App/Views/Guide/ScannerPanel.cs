using System.Net;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Dsh.App.Infrastructure;
using Dsh.Core;

namespace Dsh.App.Views.Guide;

/// <summary>"Find servers on my network": runs <see cref="NetworkScanner"/> and shows what it finds
/// as friendly cards ("DGX Spark at 192.168.1.42 — spark-3f2a"), updating live, with a box to check
/// an address by hand. In <see cref="Purpose.Spark"/> mode a card is picked as "my Spark"; in
/// <see cref="Purpose.ModelServer"/> mode each model API on a card can be used for DSH.</summary>
public sealed class ScannerPanel : UserControl
{
    public enum Purpose { Spark, ModelServer }

    private readonly Purpose _purpose;
    private readonly Dictionary<IPAddress, FoundHost> _hosts = [];
    private readonly StackPanel _list = new();
    private readonly TextBlock _status = Ui.Text("", 12.5, brushKey: "TextFillColorSecondaryBrush");
    private readonly ProgressBar _progress = new() { Height = 4, Minimum = 0, Maximum = 1, Margin = new Thickness(0, 6, 0, 10) };
    private readonly Button _scan;
    private readonly TextBox _manual;
    private readonly Button _check;
    private readonly StackPanel _manualResult = new();
    private CancellationTokenSource? _cts;
    private bool _checking;

    /// <summary>The user picked a host ("This is my Spark").</summary>
    public event Action<FoundHost>? HostChosen;
    /// <summary>The user picked one model API on a host.</summary>
    public event Action<FoundHost, FoundService>? ServiceChosen;
    /// <summary>A scan finished (or was stopped).</summary>
    public event Action? ScanFinished;

    public IReadOnlyCollection<FoundHost> Hosts => _hosts.Values;
    public FoundHost? Selected { get; private set; }
    public bool IsScanning => _cts is not null;
    public bool HasRun { get; private set; }

    public ScannerPanel(Purpose purpose)
    {
        _purpose = purpose;
        _scan = Ui.Button("Scan again", Start);
        _scan.Margin = new Thickness(12, 0, 0, 0);
        _manual = Ui.Field("", "192.168.1.42 or spark-3f2a.local");
        AutomationProperties.SetName(_manual, "Address to check");
        _check = Ui.Button("Check", () => _ = CheckAsync());
        _check.Margin = new Thickness(8, 0, 0, 0);
        _manual.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            _ = CheckAsync();
        };

        var header = new DockPanel();
        DockPanel.SetDock(_scan, Dock.Right);
        header.Children.Add(_scan);
        _status.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        header.Children.Add(_status);

        var manualRow = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
        DockPanel.SetDock(_check, Dock.Right);
        manualRow.Children.Add(_check);
        manualRow.Children.Add(_manual);
        var manualLabel = Ui.Text(purpose == Purpose.Spark ? "Know its address? Type it here" : "Or check an address yourself", 12.5, FontWeights.SemiBold);
        manualLabel.Margin = new Thickness(0, 14, 0, 2);

        Content = Ui.Stack(header, _progress, _list, manualLabel, manualRow, _manualResult);
        RenderList();
    }

    // MARK: - Scanning

    /// <summary>Scan (again). Earlier results stay until the new scan replaces them.</summary>
    public void Start()
    {
        Cancel();
        _cts = new CancellationTokenSource();
        _ = RunAsync(_cts);
    }

    public void Cancel()
    {
        _cts?.Cancel();
        _cts = null;
    }

    private async Task RunAsync(CancellationTokenSource cts)
    {
        HasRun = true;
        _scan.IsEnabled = false;
        _progress.Value = 0;
        _progress.Visibility = Visibility.Visible;
        var networks = await Task.Run(NetworkScanner.LocalNetworks);
        var where = networks.Count == 0
            ? "this PC"
            : string.Join(", ", networks.Select(n => $"{Prefix(n.Address)}x").Distinct().Take(3));
        _status.Text = $"Looking around your network ({where})…";
        var progress = new Progress<ScanProgress>(p =>
        {
            if (cts.IsCancellationRequested) return;
            _progress.Value = p.Fraction;
            _status.Text = $"Looking around your network ({where})… {p.Fraction:P0}";
        });
        var seen = new HashSet<IPAddress>();
        try
        {
            var options = new ScanOptions { IncludeLoopback = _purpose == Purpose.ModelServer };
            await foreach (var host in NetworkScanner.ScanAsync(options, progress, cts.Token))
            {
                seen.Add(host.Address);
                Upsert(host);
            }
            // Hosts from an earlier scan that didn't answer this time are gone.
            foreach (var stale in _hosts.Keys.Where(a => !seen.Contains(a)).ToList())
                if (!Equals(Selected?.Address, stale)) _hosts.Remove(stale);
            RenderList();
            var count = Relevant().Count;
            _status.Text = count switch
            {
                0 => _purpose == Purpose.Spark ? "No Spark found yet." : "No model servers found.",
                1 => "Found 1 device.",
                _ => $"Found {count} devices.",
            };
        }
        catch (OperationCanceledException)
        {
            if (!cts.IsCancellationRequested) _status.Text = "Stopped.";
        }
        catch (Exception error)
        {
            _status.Text = $"The scan failed: {error.Message}";
        }
        finally
        {
            if (ReferenceEquals(_cts, cts)) _cts = null;
            _progress.Visibility = Visibility.Collapsed;
            _scan.IsEnabled = true;
            ScanFinished?.Invoke();
        }
    }

    private static string Prefix(IPAddress address)
    {
        var parts = address.ToString().Split('.');
        return parts.Length == 4 ? $"{parts[0]}.{parts[1]}.{parts[2]}." : address + " ";
    }

    private void Upsert(FoundHost host)
    {
        if (host.Identified && host.Services.Count == 0) _hosts.Remove(host.Address);
        else _hosts[host.Address] = host;
        if (Selected is { } selected && selected.Address.Equals(host.Address) && host.Services.Count > 0) Selected = host;
        RenderList();
    }

    /// <summary>Show these results without scanning (the self-test's screenshots).</summary>
    public void ShowDemo(IEnumerable<FoundHost> hosts, FoundHost? selected = null, string status = "Found 2 devices.")
    {
        Cancel();
        HasRun = true;
        foreach (var host in hosts) _hosts[host.Address] = host;
        Selected = selected;
        _status.Text = status;
        _progress.Visibility = Visibility.Collapsed;
        RenderList();
    }

    /// <summary>Remember a host found elsewhere (a resumed guide) and mark it chosen.</summary>
    public void Choose(FoundHost host)
    {
        _hosts[host.Address] = host;
        Selected = host;
        RenderList();
    }

    private async Task CheckAsync()
    {
        var text = _manual.Text.Trim();
        if (text.Length == 0 || _checking) return;
        _checking = true;
        _check.IsEnabled = false;
        _manualResult.Children.Clear();
        _manualResult.Children.Add(GuideVisuals.Busy($"Checking {text}…"));
        try
        {
            var host = await NetworkScanner.ProbeHostAsync(text, new ScanOptions { IncludeLoopback = true });
            _manualResult.Children.Clear();
            if (host is null || host.Services.Count == 0)
            {
                _manualResult.Children.Add(GuideVisuals.Note(GuideVisuals.Tone.Warning,
                    $"Nothing answered at {text}. Check the address, and that the Spark is on and on the same network as this PC."));
                return;
            }
            _hosts[host.Address] = host;
            if (_purpose == Purpose.Spark)
            {
                Selected = host;
                HostChosen?.Invoke(host);
            }
            RenderList();
        }
        catch (Exception error)
        {
            _manualResult.Children.Clear();
            _manualResult.Children.Add(GuideVisuals.Note(GuideVisuals.Tone.Error, error.Message));
        }
        finally
        {
            _checking = false;
            _check.IsEnabled = true;
        }
    }

    // MARK: - Cards

    private List<FoundHost> Relevant() => _purpose switch
    {
        Purpose.Spark => _hosts.Values.Where(h => !h.IsLoopback && (h.LooksLikeSpark || h.Ssh is not null)).ToList(),
        _ => _hosts.Values.Where(h => h.ModelApis.Count > 0 || h.Swapper is not null || !h.Identified).ToList(),
    };

    private void RenderList()
    {
        _list.Children.Clear();
        var hosts = Relevant()
            .OrderByDescending(h => h.LooksLikeSpark)
            .ThenByDescending(h => h.IsLoopback)
            .ThenBy(h => h.Address.GetAddressBytes(), Comparer<byte[]>.Create((a, b) =>
            {
                for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
                    if (a[i] != b[i]) return a[i].CompareTo(b[i]);
                return a.Length.CompareTo(b.Length);
            }))
            .ToList();
        if (_purpose == Purpose.Spark)
        {
            var sparks = hosts.Where(h => h.LooksLikeSpark || Equals(h.Address, Selected?.Address)).ToList();
            var others = hosts.Except(sparks).ToList();
            foreach (var host in sparks) _list.Children.Add(Card(host));
            if (others.Count > 0)
            {
                var more = new StackPanel();
                foreach (var host in others) more.Children.Add(Card(host));
                _list.Children.Add(new Expander
                {
                    Header = Ui.Text($"Other computers with remote login ({others.Count}) — your Spark could be one of these if it has a different name",
                        12.5, brushKey: "TextFillColorSecondaryBrush"),
                    Content = more,
                    IsExpanded = sparks.Count == 0 && others.Count <= 3,
                    Margin = new Thickness(0, 4, 0, 0),
                });
            }
        }
        else
        {
            foreach (var host in hosts) _list.Children.Add(Card(host));
        }
        if (_list.Children.Count == 0 && HasRun && !IsScanning)
        {
            _list.Children.Add(Ui.Card(Ui.Secondary(_purpose == Purpose.Spark
                ? "Nothing that looks like a Spark answered. If it restarted a moment ago, give it a minute and press Scan again."
                : "No model servers answered on the usual ports. Start your server (or check it listens on 0.0.0.0, not only 127.0.0.1) and scan again.")));
        }
    }

    private UIElement Card(FoundHost host)
    {
        var selected = Selected is { } s && s.Address.Equals(host.Address);
        FrameworkElement icon;
        if (host.LooksLikeSpark && GuideVisuals.Bitmap("icon-spark", ThemeService.Instance.IsDark) is { } bitmap)
        {
            icon = new Image { Source = bitmap, Width = 44, Height = 28, Stretch = Stretch.Uniform };
        }
        else
        {
            icon = Ui.Glyph(host.IsLoopback ? Icons.Laptop : Icons.Connect, 18, "TextFillColorSecondaryBrush");
            icon.Width = 44;
            ((TextBlock)icon).TextAlignment = TextAlignment.Center;
        }
        icon.Margin = new Thickness(0, 2, 12, 0);
        icon.VerticalAlignment = VerticalAlignment.Top;

        var text = new StackPanel();
        text.Children.Add(Ui.Text(host.Headline, 13.5, FontWeights.SemiBold));
        var chips = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        foreach (var service in host.Services) chips.Children.Add(Chip(service));
        if (!host.Identified) chips.Children.Add(GuideVisuals.Busy("Checking…"));
        text.Children.Add(chips);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        if (_purpose == Purpose.Spark)
        {
            var use = Ui.Button(selected ? "Chosen" : "This is my Spark", () =>
            {
                Selected = host;
                RenderList();
                HostChosen?.Invoke(host);
            }, accent: !selected);
            use.IsEnabled = !selected;
            actions.Children.Add(use);
        }
        else
        {
            foreach (var api in host.ModelApis)
            {
                var label = api.Models.Count > 0 ? $"Use {api.Title} · {TextUtil.Prefix(api.Models[0], 28)}" : $"Use {api.Title} (port {api.Port})";
                var use = Ui.Button(label, () =>
                {
                    Selected = host;
                    ServiceChosen?.Invoke(host, api);
                    RenderList();
                });
                use.Margin = new Thickness(0, 0, 8, 0);
                actions.Children.Add(use);
            }
        }
        if (actions.Children.Count > 0) text.Children.Add(actions);

        var row = new DockPanel();
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);
        row.Children.Add(text);
        var card = Ui.Card(row, new Thickness(14, 12, 14, 12));
        card.Margin = new Thickness(0, 0, 0, 8);
        if (selected)
        {
            card.BorderThickness = new Thickness(2);
            card.SetResourceReference(Border.BorderBrushProperty, "AccentFillColorDefaultBrush");
        }
        AutomationProperties.SetName(card, host.Headline);
        return card;
    }

    private static Border Chip(FoundService service)
    {
        var (glyph, text) = service.Kind switch
        {
            ServiceKind.Ssh => (Icons.Terminal, "Remote login"),
            ServiceKind.SparkSwapper => (Icons.Bolt, service.SetupNeeded == true ? "Spark Swapper — no admin yet" : "Spark Swapper"),
            ServiceKind.SparkModelFront => (Icons.Lock, service.NeedsKey ? "Model API (HTTPS, key)" : service.Detail?.Contains("no model") == true ? "Model API — no model loaded" : "Model API (HTTPS)"),
            ServiceKind.OpenClawGateway => (Icons.Robot, "OpenClaw"),
            // A vLLM/SGLang detail already names its engine ("vLLM · qwen3-coder"); Ollama's is just the model.
            _ => (Icons.Robot, service.Detail is { } d
                ? TextUtil.Prefix(d.Contains(" · ", StringComparison.Ordinal) ? d : $"{service.Title} · {d}", 44)
                : $"{service.Title} (port {service.Port})"),
        };
        var icon = Ui.Glyph(glyph, 11, "AccentTextFillColorPrimaryBrush");
        icon.Margin = new Thickness(0, 0, 5, 0);
        var chip = new Border { Child = Ui.Stack(Orientation.Horizontal, icon, Ui.Text(text, 11.5, wrap: false)), Margin = new Thickness(0, 0, 6, 4) };
        chip.SetResourceReference(StyleProperty, "Chip");
        chip.ToolTip = service.Detail;
        return chip;
    }
}
