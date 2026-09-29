using System.Windows;
using System.Windows.Threading;
using Dsh.App.Model;
using Dsh.Core;

namespace Dsh.App.Views.Guide;

/// <summary>What the guide needs from the window it is shown in (the setup wizard).</summary>
public interface IGuideHost
{
    Window Window { get; }
    /// <summary>Rebuild the current page (its structure changed).</summary>
    void Render();
    /// <summary>Update only the header and the Back/Next buttons (a field was typed in, a status
    /// changed) — rebuilding the page would steal the keyboard focus.</summary>
    void RefreshChrome();
    /// <summary>Back to the wizard's first page.</summary>
    void LeaveGuide();
    /// <summary>Everything is set up: close the wizard.</summary>
    void FinishGuide();
}

/// <summary>The DGX Spark guide: a step-by-step, picture-per-page walk from a Spark in its box (or
/// one to reinstall) to DSH chatting with a model on it, for someone who has never used a terminal.
/// It finds the Spark on the network, installs Spark Swapper over SSH, explains and pins the
/// self-signed certificate, creates the Swapper's admin login, starts a model, and saves DSH's
/// connection. Progress is saved after every step, so it can be left and resumed.
///
/// Pages are rebuilt from state whenever something changes (like the rest of the wizard); live
/// controls (logs, the scanner) are kept in fields and moved into each rebuilt page.</summary>
public sealed partial class SparkGuide : IDisposable
{
    private readonly AppModel _model;
    private readonly IGuideHost _host;
    private readonly GuideState _state;
    private readonly KnownHosts _knownHosts = new();
    private readonly Dispatcher _dispatcher;
    private bool _disposed;
    /// <summary>Self-test screenshots: show sample state, touch no network.</summary>
    private bool _demo;

    public SparkGuide(AppModel model, IGuideHost host, GuideState? resume = null)
    {
        _model = model;
        _host = host;
        _dispatcher = host.Window.Dispatcher;
        _state = resume ?? new GuideState();
        _scanner = new ScannerPanel(ScannerPanel.Purpose.Spark);
        _scanner.HostChosen += ChooseHost;
        _scanner.ScanFinished += () => { if (Page == GuidePage.Find) _host.RefreshChrome(); };
        if (_state.SwapperInstalled)
        {
            // Resumed after the install: no need to sign in again unless asked to reinstall.
            _installOk = true;
            _installPhase = InstallPhase.Done;
        }
    }

    /// <summary>Start the current page's own work (a scan, a probe) — once the window shows the guide.</summary>
    public void Activate() => OnShown();

    public GuidePage Page => _state.Page;
    public GuideState State => _state;

    // MARK: - Sections (the progress bar)

    private static readonly string[] SectionTitles =
    [
        "What you need", "Get the Spark ready", "Find your Spark", "Install Spark Swapper", "The padlock warning",
        "Your Swapper login", "Get a model running", "Connect DSH", "Done",
    ];

    public int SectionCount => SectionTitles.Length;

    public int SectionIndex => Page switch
    {
        GuidePage.Welcome => 0,
        GuidePage.Find => 2,
        GuidePage.Install => 3,
        GuidePage.Certificate => 4,
        GuidePage.Admin => 5,
        GuidePage.Model => 6,
        GuidePage.Connect => 7,
        GuidePage.Done => 8,
        _ => 1,
    };

    public string Title => $"DGX Spark guide · {SectionTitles[SectionIndex]}";

    public string StepText
    {
        get
        {
            var part = PartOfSection();
            return part is { } p ? $"Step {SectionIndex + 1} of {SectionCount} · part {p.Index} of {p.Count}" : $"Step {SectionIndex + 1} of {SectionCount}";
        }
    }

    /// <summary>Where a page sits within "Get the Spark ready", which has several pages.</summary>
    private (int Index, int Count)? PartOfSection()
    {
        GuidePage[] pages = _state.Start switch
        {
            GuideStart.Fresh => [GuidePage.Start, GuidePage.Download, GuidePage.WriteUsb, GuidePage.BootUsb, GuidePage.Sticker, GuidePage.Hotspot, GuidePage.SetupPage, GuidePage.Wait],
            GuideStart.New => [GuidePage.Start, GuidePage.Cables, GuidePage.Sticker, GuidePage.Hotspot, GuidePage.SetupPage, GuidePage.Wait],
            _ => [],
        };
        var index = Array.IndexOf(pages, Page);
        return index < 0 ? null : (index + 1, pages.Length);
    }

    // MARK: - Navigation

    public string NextLabel => Page switch
    {
        GuidePage.Welcome => "Let's start",
        GuidePage.Cables => "It's plugged in",
        GuidePage.Sticker => "I've found it",
        GuidePage.Hotspot => "I'm connected",
        GuidePage.SetupPage => "I've finished the setup page",
        GuidePage.Wait => "It's back on my network",
        GuidePage.BootUsb => "It's reinstalled",
        GuidePage.Certificate => CertificatePinned ? "Next" : "Trust it and continue",
        GuidePage.Done => "Start chatting",
        _ => "Next",
    };

    public bool CanAdvance => Page switch
    {
        GuidePage.Start => _state.Start != GuideStart.Unset,
        GuidePage.Download => _source is not null && (_sourceProblem is null) && (_source.HasEfi || _acceptNoEfi) && !_inspecting,
        GuidePage.WriteUsb => _usbDone && !_writing,
        GuidePage.Find => _scanner.Selected is not null || _state.Host is not null,
        GuidePage.Install => _installOk && !InstallBusy,
        GuidePage.Certificate => _cert is not null && !_probingCert,
        GuidePage.Admin => _state.AdminReady && !_adminBusy,
        GuidePage.Model => RunningModel is not null,
        GuidePage.Connect => _state.Connected && !_connectBusy,
        _ => true,
    };

    /// <summary>The footer's second button, when a page has one ("Skip for now").</summary>
    public string? SecondaryLabel => Page switch
    {
        GuidePage.WriteUsb when !_usbDone && !_writing => "I already have a stick",
        GuidePage.Model when RunningModel is null => "Skip for now",
        _ => null,
    };

    public void Secondary()
    {
        switch (Page)
        {
            case GuidePage.WriteUsb:
                Go(GuidePage.BootUsb);
                break;
            case GuidePage.Model:
                Go(GuidePage.Connect);
                break;
        }
    }

    public void Next()
    {
        if (!CanAdvance) return;
        try
        {
            switch (Page)
            {
                case GuidePage.Welcome:
                    Go(GuidePage.Start);
                    break;
                case GuidePage.Start:
                    Go(_state.Start switch
                    {
                        GuideStart.Fresh => GuidePage.Download,
                        GuideStart.Existing => GuidePage.Find,
                        _ => GuidePage.Cables,
                    });
                    break;
                case GuidePage.BootUsb:
                    Go(GuidePage.Sticker);
                    break;
                case GuidePage.Certificate:
                    if (!CertificatePinned) TrustInDsh();
                    Go(GuidePage.Admin);
                    break;
                case GuidePage.Done:
                    _state.Completed = true;
                    _state.Save();
                    _host.FinishGuide();
                    break;
                default:
                    Go(Page + 1);
                    break;
            }
        }
        catch (Exception error)
        {
            App.WriteCrashLog(error, "guide");
        }
    }

    public void Back()
    {
        if (Page == GuidePage.Welcome)
        {
            _host.LeaveGuide();
            return;
        }
        Go(Previous(Page));
    }

    private GuidePage Previous(GuidePage page) => page switch
    {
        GuidePage.Download or GuidePage.Cables => GuidePage.Start,
        GuidePage.Sticker => _state.Start == GuideStart.Fresh ? GuidePage.BootUsb : GuidePage.Cables,
        GuidePage.Find => _state.Start == GuideStart.Existing ? GuidePage.Start : GuidePage.Wait,
        _ => page - 1,
    };

    public void Go(GuidePage page)
    {
        OnHidden();
        _state.Page = page;
        _state.Save();
        OnShown();
        _host.Render();
    }

    /// <summary>Start what a page does by itself when it appears (a scan, a probe, polling).</summary>
    private void OnShown()
    {
        if (_demo) return;
        switch (Page)
        {
            case GuidePage.Wait:
                StartWatching();
                break;
            case GuidePage.Find:
                if (!_scanner.HasRun && !_scanner.IsScanning) _scanner.Start();
                break;
            case GuidePage.WriteUsb:
                if (_disks is null && !_listing) _ = ListDisksAsync();
                break;
            case GuidePage.Certificate:
                if (_cert is null && !_probingCert) _ = ProbeCertificateAsync();
                break;
            case GuidePage.Admin:
                if (_session is null && !_adminBusy) _ = LoadSessionAsync();
                break;
            case GuidePage.Model:
                _ = LoadModelsAsync();
                break;
            case GuidePage.Connect:
                if (!_state.Connected && !_connectBusy) _ = ConnectAsync();
                break;
        }
    }

    private void OnHidden()
    {
        _watchCts?.Cancel();
        _watchCts = null;
        _pollCts?.Cancel();
        _pollCts = null;
        _scanner.Cancel();
    }

    /// <summary>Run on the UI thread (installer output arrives on background threads).</summary>
    private void OnUi(Action action)
    {
        if (_disposed) return;
        if (_dispatcher.CheckAccess()) action();
        else _dispatcher.BeginInvoke(action);
    }

    /// <summary>Rebuild the page, but only if the user is still on <paramref name="page"/>.</summary>
    private void RenderIf(GuidePage page)
    {
        if (!_disposed && Page == page) _host.Render();
        else if (!_disposed) _host.RefreshChrome();
    }

    // MARK: - Pages

    public UIElement Render() => Page switch
    {
        GuidePage.Welcome => WelcomePage(),
        GuidePage.Start => StartPage(),
        GuidePage.Download => DownloadPage(),
        GuidePage.WriteUsb => WriteUsbPage(),
        GuidePage.BootUsb => BootUsbPage(),
        GuidePage.Cables => CablesPage(),
        GuidePage.Sticker => StickerPage(),
        GuidePage.Hotspot => HotspotPage(),
        GuidePage.SetupPage => SetupPagePage(),
        GuidePage.Wait => WaitPage(),
        GuidePage.Find => FindPage(),
        GuidePage.Install => InstallPage(),
        GuidePage.Certificate => CertificatePage(),
        GuidePage.Admin => AdminPage(),
        GuidePage.Model => ModelPage(),
        GuidePage.Connect => ConnectPage(),
        _ => DonePage(),
    };

    /// <summary>The Spark's address as the guide uses it (IP when known, else the name typed).</summary>
    private string SparkHost => _state.Host ?? "";

    private string SparkLabel => _state.HostName is { Length: > 0 } name && name != SparkHost ? $"{ShortName(name)} ({SparkHost})" : SparkHost;

    private static string ShortName(string name)
    {
        var dot = name.IndexOf('.');
        return dot > 0 ? name[..dot] : name;
    }

    private void ChooseHost(FoundHost host)
    {
        var address = host.Address.ToString();
        if (_state.Host != address)
        {
            // A different Spark: what was learned about the old one no longer applies.
            _state.SwapperInstalled = false;
            _state.SwapperFingerprint = null;
            _state.FrontFingerprint = null;
            _state.AdminReady = false;
            _state.Connected = false;
            _cert = null;
            _session = null;
            _installOk = false;
        }
        _state.Host = address;
        _state.HostName = host.HostName;
        if (host.Swapper is { SetupNeeded: false } && host.ModelFront is not null) _foundInstalled = true;
        _state.Save();
        RenderIf(GuidePage.Find);
    }

    public void Dispose()
    {
        if (_disposed) return;
        OnHidden();
        _installCts?.Cancel();
        _connectCts?.Cancel();
        // A finished guide already removed its progress file; don't write it back.
        if (!_state.Completed) _state.Save();
        _disposed = true;
        _ssh?.Dispose();
        _swapper?.Dispose();
    }
}
