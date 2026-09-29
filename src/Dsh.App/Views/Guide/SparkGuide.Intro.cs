using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Dsh.App.Infrastructure;
using Dsh.Core;
using static Dsh.App.Views.Guide.GuideVisuals;

namespace Dsh.App.Views.Guide;

// MARK: - What you need, new or fresh, and the Spark's first-boot setup

public sealed partial class SparkGuide
{
    public const string FirstBootDocs = "https://docs.nvidia.com/dgx/dgx-spark/first-boot.html";

    private UIElement WelcomePage() => Ui.Stack(
        Picture("need", "A DGX Spark, a home router, a PC, and — only for reinstalling — a USB stick, keyboard and screen."),
        Heading("Let's set up your DGX Spark"),
        Lead("This guide takes you from a Spark in its box to chatting with it here in DSH. Every step has a picture, and you won't need to type anything into a terminal."),
        Subheading("What you need"),
        Bullets(
            (Icons.Bolt, "Your **DGX Spark** and its power adapter."),
            (Icons.Globe, "Your **home network** — Wi-Fi, or a network cable to your router."),
            (Icons.Laptop, "**This PC**, on that same network."),
            (Icons.Stopwatch, "About **20 minutes** — longer the first time a model downloads."),
            (Icons.Download, "Only if you want to erase and reinstall the Spark: a **USB stick of 16 GB or more**, and a keyboard and screen for the Spark.")),
        Aside("DSH will help you with the Spark's first-time setup, find it on your network, install Spark Swapper (a free, open-source control panel for the Spark) over a secure remote login, get a model running, and connect this app to it. Everything stays on your own network."),
        Stuck("You can close this window at any time — the guide remembers where you were. Open it again from Settings › General › Setup wizard, or Help › Run Setup Wizard."));

    private UIElement StartPage()
    {
        void Choose(GuideStart start)
        {
            _state.Start = start;
            _state.Save();
            _host.Render();
        }
        return Ui.Stack(
            Picture("choice", "Left: a brand-new Spark. Right: a Spark being reinstalled from a USB stick."),
            Heading("Is your Spark brand new, or starting fresh?"),
            Lead("A new Spark already has its operating system (DGX OS, a version of Ubuntu Linux) installed — there's nothing to reinstall."),
            Choice(Icons.Bolt, "Brand new", "Just out of the box, never set up. Most people pick this.",
                _state.Start == GuideStart.New, () => Choose(GuideStart.New)),
            Choice(Icons.Refresh, "Start fresh — erase and reinstall",
                "Reinstall DGX OS from a USB stick DSH makes for you. Everything on the Spark is erased.",
                _state.Start == GuideStart.Fresh, () => Choose(GuideStart.Fresh)),
            Choice(Icons.CheckMark, "It's already set up", "It's on my network and I know the username and password I created for it.",
                _state.Start == GuideStart.Existing, () => Choose(GuideStart.Existing)),
            Aside("Starting fresh uses NVIDIA's \"System Recovery\": a USB stick with a complete copy of DGX OS. The Spark starts from the stick and reinstalls itself, then it behaves like a brand-new one."),
            Stuck("Not sure? If you've never turned it on, choose Brand new. If someone else set it up and you don't know its login, choose Start fresh — but only if nothing on it needs keeping."));
    }

    private UIElement CablesPage() => Ui.Stack(
        Picture("cables", "A network cable from the router into the Spark, and the power adapter plugged in last."),
        Heading("Cables first, power last"),
        Steps(
            "If you'll use a **network cable**, plug it into the Spark and into your router now. (No cable? Wi-Fi works too.)",
            "Plug in the **power adapter last**. The Spark has no power button — it starts the moment it gets power, and a small light comes on."),
        Aside("The first time it starts, the Spark makes its own small Wi-Fi network (a \"hotspot\") so you can set it up from a laptop or phone, without a screen. That's the next step."),
        Stuck("Prefer a screen? Plug a screen, keyboard and mouse into the Spark before the power, and you'll see the same setup on its screen. Do it there, then skip ahead with Next until \"Let it finish\"."));

    private UIElement StickerPage() => Ui.Stack(
        Picture("quickstart-card", "The Quick Start card from the box, with a sticker listing the Wi-Fi name, Wi-Fi password and setup page."),
        Heading("Find the sticker"),
        Lead("In the box there's a Quick Start card with a sticker on it. Keep it next to you — it has three things you'll need:"),
        Bullets(
            (Icons.Globe, "The Spark's **Wi-Fi name** — something like spark-3f2a."),
            (Icons.Lock, "Its **Wi-Fi password**."),
            (Icons.Link, "The **setup page** address — something like spark-3f2a.local.")),
        _state.Start == GuideStart.Fresh
            ? Note(Tone.Info, "After a reinstall the Spark starts this same first-time setup again. If you still have a screen and keyboard plugged into it, the setup appears on that screen — you can do it there instead.")
            : null!,
        Aside("Every Spark has its own name and password, so the ones in these pictures won't work on yours."),
        Stuck("Lost the card? Plug a screen and keyboard into the Spark instead: the setup appears on its screen. NVIDIA's first-boot page explains both ways: " + FirstBootDocs));

    private UIElement HotspotPage() => Ui.Stack(
        Picture("hotspot", "A laptop's Wi-Fi list with the Spark's network selected."),
        Heading("Join the Spark's own Wi-Fi"),
        Steps(
            "On a laptop or phone, open the list of Wi-Fi networks.",
            "Choose the **network name from the sticker** and type the **Wi-Fi password** from the sticker.",
            "Wait until it says Connected. (It may say \"No internet\" — that's expected.)"),
        Note(Tone.Info, "If you use this PC to join the Spark's Wi-Fi, it leaves your home network for a few minutes. That's fine — you'll come back to it after the setup."),
        Aside("The Spark's hotspot only exists until its setup is done. It's a direct, private link between your device and the Spark, just for the setup page."),
        Stuck("Don't see it in the list? Give the Spark two minutes after plugging in the power, and move closer to it. If the list refreshes slowly, turn Wi-Fi off and on. If you already finished the setup once, the hotspot is gone — press Next."));

    private TextBox? _setupAddress;

    private UIElement SetupPagePage()
    {
        _setupAddress ??= Ui.Field("", "spark-3f2a.local  (from the sticker)", mono: true);
        var open = Ui.Button("Open the setup page", () =>
        {
            var address = _setupAddress.Text.Trim();
            if (address.Length == 0) return;
            if (!address.StartsWith("http", StringComparison.OrdinalIgnoreCase)) address = "http://" + address;
            ShellIntegration.Open(address);
        });
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(open, Dock.Right);
        open.Margin = new Thickness(8, 0, 0, 0);
        row.Children.Add(open);
        row.Children.Add(Detach(_setupAddress));
        return Ui.Stack(
            Picture("setup-page", "A browser showing the Spark's setup page, next to a note with the username and password written down."),
            Heading("Create your account on the Spark"),
            Steps(
                "On the device that joined the Spark's Wi-Fi, open a browser (Edge, Chrome, Safari…) and go to the **setup page from the sticker**, like http://spark-3f2a.local.",
                "Choose a **username** and a **password**. Write them down — DSH needs them in a few minutes.",
                "Pick your **home Wi-Fi** and type its password — or choose the wired network if you plugged in a cable.",
                "Accept the license and finish the setup."),
            Field("Setup page (optional — if this PC is on the Spark's Wi-Fi)", _setupAddress),
            row,
            Aside("This creates the Spark's first user account — its administrator. It's the same account you'd use to sign in on its screen. DSH will use it once, to install Spark Swapper, and doesn't save the password."),
            Stuck("Page won't open? Check the device is still connected to the Spark's Wi-Fi, and type the address exactly as on the sticker, starting with http://. Some phones switch back to mobile data when Wi-Fi has no internet: turn mobile data off for a moment."));
    }

    // MARK: - Waiting for the Spark to come back

    private CancellationTokenSource? _watchCts;
    private FoundHost? _watchFound;
    private DateTime _waitStarted = DateTime.Now;
    private readonly TextBlock _waitClock = Ui.Text("", 13, brushKey: "TextFillColorSecondaryBrush");
    private DispatcherTimer? _clockTimer;

    private void StartWatching()
    {
        _waitStarted = DateTime.Now;
        _watchCts?.Cancel();
        var cts = _watchCts = new CancellationTokenSource();
        _clockTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) =>
        {
            var elapsed = DateTime.Now - _waitStarted;
            _waitClock.Text = $"Waiting {elapsed.Minutes}:{elapsed.Seconds:00} — DSH is watching your network for the Spark.";
        }, _dispatcher);
        _clockTimer.Start();
        _ = WatchAsync(cts.Token);
    }

    /// <summary>Scan every 20 seconds until something that looks like a Spark shows up.</summary>
    private async Task WatchAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
            while (!cancellationToken.IsCancellationRequested && _watchFound is null)
            {
                await foreach (var host in NetworkScanner.ScanAsync(new ScanOptions { IncludeLoopback = false }, null, cancellationToken))
                {
                    if (host.Identified && host.LooksLikeSpark && host.Ssh is not null)
                    {
                        _watchFound = host;
                        break;
                    }
                }
                if (_watchFound is not null) break;
                await Task.Delay(TimeSpan.FromSeconds(20), cancellationToken);
            }
            if (_watchFound is { } found)
            {
                _scanner.Choose(found);
                ChooseHost(found);
                RenderIf(GuidePage.Wait);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _clockTimer?.Stop();
        }
    }

    private UIElement WaitPage()
    {
        var status = _watchFound is { } found
            ? Note(Tone.Success, $"It's back: {found.Headline}. Press the button below to continue.", "Found your Spark")
            : (UIElement)Ui.Stack(Busy("Watching your network for the Spark…"), Detach(_waitClock));
        return Ui.Stack(
            Picture("waiting", "The Spark updating, with a ten-minute timer."),
            Heading("Let it finish"),
            Steps(
                "After the setup page, the Spark switches off its own Wi-Fi, joins your home network, installs updates and restarts. **This takes about 10 minutes.** Leave it plugged in.",
                "Put this PC (and the laptop or phone you used) back on your **home Wi-Fi**.",
                "When DSH spots the Spark on your network it tells you here — or just continue once 10 minutes have passed."),
            status,
            Aside("The update downloads the latest DGX OS fixes and drivers. The light on the Spark may go off and on while it restarts; that's normal."),
            Stuck("More than 20 minutes and still nothing? Check the Spark's light is on and that your router shows a new device called spark-…. If you picked the wrong Wi-Fi or mistyped its password, the hotspot comes back — join it again and redo the setup page."));
    }
}
