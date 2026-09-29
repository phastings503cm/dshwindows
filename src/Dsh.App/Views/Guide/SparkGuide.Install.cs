using System.IO;
using System.Windows;
using System.Windows.Controls;
using Dsh.App.Infrastructure;
using Dsh.Core;
using static Dsh.App.Views.Guide.GuideVisuals;

namespace Dsh.App.Views.Guide;

// MARK: - Find the Spark, and install Spark Swapper on it

public sealed partial class SparkGuide
{
    private readonly ScannerPanel _scanner;
    /// <summary>The scan saw the Swapper (set up) and the model front on the chosen Spark.</summary>
    private bool _foundInstalled;

    private UIElement FindPage()
    {
        var panel = Ui.Stack(
            Picture("scanner", "DSH scanning the network and finding the DGX Spark.", 200),
            Heading("Find your Spark"),
            Lead("DSH is looking around your network for it. When it appears, press **This is my Spark**."));
        if (_state.Host is not null && _scanner.Selected is null)
        {
            panel.Children.Add(Note(Tone.Success, $"Last time you chose {SparkLabel}. Continue with it, or pick another below.", "Your Spark"));
        }
        panel.Children.Add(Detach(_scanner));
        panel.Children.Add(Aside("DSH knocks on a few doors (network ports) of every device in your network's address range — 22 for remote login, 8999 for Spark Swapper, 11443 for the Spark's secure model address, and the usual ports of model servers — then asks each answer what it is. It only looks: nothing is signed into or changed. Names like spark-3f2a come from the devices themselves (mDNS) or your router."));
        panel.Children.Add(Stuck("Not found? Check this PC is on your home network (not the Spark's own Wi-Fi, a guest network or a VPN), and give a Spark that just restarted a couple of minutes. You can also find its address in your router's list of connected devices and type it in above."));
        return panel;
    }

    // MARK: - Install

    private enum InstallPhase { Idle, Connecting, AskKey, Checking, Installing, Front, Done, Failed }

    private InstallPhase _installPhase;
    private bool _installOk;
    private string? _installError;
    private SshHostKeyException? _hostKeyQuestion;
    private SparkMachineState? _machine;
    private SshSparkConnection? _ssh;
    private CancellationTokenSource? _installCts;
    private readonly GuideLog _installLog = new(200);
    private TextBox? _sshUser;
    private PasswordBox? _sshPassword;
    /// <summary>The Spark login's password, kept in memory for this guide only (never saved or shown).</summary>
    private string _sparkPassword = "";
    private bool _reinstall;

    private bool InstallBusy => _installPhase is InstallPhase.Connecting or InstallPhase.Checking or InstallPhase.Installing or InstallPhase.Front;

    private UIElement InstallPage()
    {
        _sshUser ??= Ui.Field(_state.SshUser ?? "", "the username you created on the Spark");
        if (_sshPassword is null)
        {
            _sshPassword = PasswordField();
            // Enter in the password box signs in (rather than pressing the footer's Next).
            _sshPassword.PreviewKeyDown += (_, e) =>
            {
                if (e.Key != System.Windows.Input.Key.Enter) return;
                e.Handled = true;
                if (_installGo is { IsEnabled: true }) _ = InstallAsync(null);
            };
        }
        _sshUser.IsEnabled = _sshPassword.IsEnabled = !InstallBusy;
        _sshUser.TextChanged -= OnLoginTyped;
        _sshUser.TextChanged += OnLoginTyped;
        _sshPassword.PasswordChanged -= OnLoginTyped;
        _sshPassword.PasswordChanged += OnLoginTyped;

        var panel = Ui.Stack(
            Picture("terminal", "DSH's install log, and the Spark's remembered ID card.", 230),
            Heading("Install Spark Swapper"),
            Lead($"DSH signs in to your Spark at {SparkLabel} and installs **Spark Swapper**, the Spark's web control panel. Use the **username and password you created on the Spark's setup page**."));

        if (_installPhase == InstallPhase.AskKey && _hostKeyQuestion is { } question)
        {
            panel.Children.Add(HostKeyQuestion(question));
        }
        else if (!_installOk)
        {
            panel.Children.Add(Field("Spark username", Detach(_sshUser)));
            panel.Children.Add(Field("Spark password", Detach(_sshPassword), "Only used for this install — DSH doesn't save it."));
            _installGo = Ui.Button(_foundInstalled ? "Sign in and check" : "Sign in and install", () => _ = InstallAsync(null), accent: true);
            UpdateInstallButton();
            panel.Children.Add(Ui.Buttons(_installGo));
        }

        switch (_installPhase)
        {
            case InstallPhase.Connecting:
                panel.Children.Add(Busy($"Signing in to {SparkLabel}…"));
                break;
            case InstallPhase.Checking:
                panel.Children.Add(Busy("Looking around the Spark…"));
                break;
            case InstallPhase.Installing:
                panel.Children.Add(Busy("Installing Spark Swapper — this takes a minute or two…"));
                break;
            case InstallPhase.Front:
                panel.Children.Add(Busy("Setting up the secure model address…"));
                break;
            case InstallPhase.Failed when _installError is { } error:
                panel.Children.Add(Note(Tone.Error, error, "That didn't work"));
                break;
        }
        if (_machine is { } machine && _installPhase is not InstallPhase.Connecting)
        {
            var facts = new List<string>();
            if (machine.OsName is { } os) facts.Add(os);
            if (machine.Gpu is { } gpu) facts.Add(gpu.Split(',')[0]);
            if (machine.DockerVersion is { } docker) facts.Add(docker.Replace("Docker version", "Docker").Split(',')[0]);
            if (facts.Count > 0) panel.Children.Add(Ui.Secondary("On the Spark: " + string.Join(" · ", facts)));
        }
        if (_installOk)
        {
            var again = Link("Reinstall or update Spark Swapper", () =>
            {
                _reinstall = true;
                _installOk = false;
                _installPhase = InstallPhase.Idle;
                _host.Render();
            });
            panel.Children.Add(Note(Tone.Success, $"Spark Swapper is running at https://{SparkSetup.UrlHost(SparkHost)}:{SparkSetup.SwapperPort}, and the model API has its secure address on port {SparkSetup.ModelFrontPort}.",
                "Installed", again));
        }
        if (!_installLog.IsEmpty || InstallBusy) panel.Children.Add(Detach(_installLog));

        panel.Children.Add(Aside("DSH signs in the way a terminal's \"ssh\" command would, then runs Spark Swapper's official one-line installer:\n" +
                                 SparkInstaller.SwapperInstallCommand + "\n" +
                                 "When the installer asks for your password to install software (sudo), DSH types it for you — it's never shown or saved. " +
                                 "It also puts a secure (HTTPS) address in front of the model on port 11443, which is how DSH talks to your model. Running it again is safe: it updates Spark Swapper and keeps its settings."));
        panel.Children.Add(Stuck("\"Didn't accept that username and password\": use the account from the Spark's setup page (not your Wi-Fi password). \"Couldn't reach … port 22\": the Spark may still be restarting — wait a minute. \"Isn't allowed to install software\": sign in with the first account created on the Spark."));
        return panel;
    }

    private Button? _installGo;

    /// <summary>Typing only enables the button: rebuilding the page would take the focus away.</summary>
    private void OnLoginTyped(object sender, RoutedEventArgs e) => UpdateInstallButton();

    private void UpdateInstallButton()
    {
        if (_installGo is null || _sshUser is null || _sshPassword is null) return;
        _installGo.IsEnabled = !InstallBusy && _sshUser.Text.Trim().Length > 0 && _sshPassword.Password.Length > 0;
    }

    private UIElement HostKeyQuestion(SshHostKeyException question)
    {
        var key = question.Presented;
        var lines = Ui.Stack(
            Copyable(key.Fingerprint, "SSH fingerprint"),
            Ui.Secondary($"Key type: {key.Algorithm}"));
        if (question.IsChanged)
        {
            var trust = Ui.Button("I reinstalled it — trust the new ID", () =>
            {
                _knownHosts.Forget(key.Host, key.Port);
                _ = InstallAsync(key.Fingerprint);
            });
            var cancel = Ui.Button("Stop", () =>
            {
                _installPhase = InstallPhase.Idle;
                _hostKeyQuestion = null;
                _host.Render();
            }, accent: true);
            lines.Children.Add(Ui.Buttons(cancel, trust));
            ((FrameworkElement)lines.Children[^1]).Margin = new Thickness(0, 10, 0, 0);
            return Note(Tone.Error,
                "Your PC remembers a different ID for this Spark. If you just reinstalled it (starting fresh gives it a new ID), that's expected. If you didn't, stop: another device on your network may be pretending to be your Spark.",
                "The Spark's ID changed", lines);
        }
        var yes = Ui.Button("Yes, it's my Spark — continue", () => _ = InstallAsync(key.Fingerprint), accent: true);
        var no = Ui.Button("Cancel", () =>
        {
            _installPhase = InstallPhase.Idle;
            _hostKeyQuestion = null;
            _host.Render();
        });
        lines.Children.Add(Ui.Buttons(yes, no));
        ((FrameworkElement)lines.Children[^1]).Margin = new Thickness(0, 10, 0, 0);
        lines.Children.Insert(0, Paragraph(
            "Every Spark has its own secret ID. The first time you connect, your PC is shown a short code made from it — its **fingerprint** — and asked whether to trust it, like saving a new contact. " +
            "If you set this Spark up yourself, on your own network, it's safe to say yes. DSH remembers it and will warn you if a different device ever claims to be your Spark.", 12.5, "TextFillColorSecondaryBrush"));
        lines.Children.Add(Ui.Secondary("Want to double-check? On the Spark's own screen, run: ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub — the SHA256 code should match."));
        return Note(Tone.Info, "", "Your PC hasn't met this Spark before", lines);
    }

    private async Task InstallAsync(string? approvedFingerprint)
    {
        if (_sshUser is null || _sshPassword is null || SparkHost.Length == 0) return;
        var user = _sshUser.Text.Trim();
        var password = _sshPassword.Password.Length > 0 ? _sshPassword.Password : _sparkPassword;
        if (user.Length == 0 || password.Length == 0) return;
        _sparkPassword = password;
        _state.SshUser = user;
        _state.Save();
        _installCts?.Cancel();
        var cts = _installCts = new CancellationTokenSource();
        _hostKeyQuestion = null;
        _installError = null;
        _installPhase = InstallPhase.Connecting;
        _host.Render();
        try
        {
            _ssh?.Dispose();
            _ssh = null;
            try
            {
                _ssh = await SshSparkConnection.ConnectAsync(SparkHost, user, password, _knownHosts, approvedFingerprint, SparkSetup.SshPort, cts.Token);
            }
            catch (SshHostKeyException question)
            {
                _hostKeyQuestion = question;
                _installPhase = InstallPhase.AskKey;
                return;
            }
            _installLog.Append($"Signed in to {SparkLabel} as {user}.");
            _installPhase = InstallPhase.Checking;
            RenderIf(GuidePage.Install);

            var installer = new SparkInstaller(_ssh, password);
            installer.Output += line => OnUi(() => _installLog.Append(line));
            _machine = await installer.DetectAsync(cts.Token);
            if (!_machine.CanSudo)
                throw new SparkRemoteException(SparkInstaller.Describe(TranscriptProblem.NotAllowed));
            if (!_machine.HasCurl)
                throw new SparkRemoteException("The Spark is missing the \"curl\" download tool the installer needs. That's unusual for DGX OS — try reinstalling it, or ask for help with sudo apt install curl.");

            if (_machine.SwapperActive && !_reinstall)
            {
                _installLog.Append("Spark Swapper is already installed and running — nothing to install.");
            }
            else
            {
                _installPhase = InstallPhase.Installing;
                RenderIf(GuidePage.Install);
                var outcome = await installer.InstallSwapperAsync(cts.Token);
                if (!outcome.Success) throw new SparkRemoteException(outcome.Summary);
                _machine = await installer.DetectAsync(cts.Token);
            }
            _reinstall = false;

            if (!_machine.ModelFrontListening)
            {
                _installPhase = InstallPhase.Front;
                RenderIf(GuidePage.Install);
                var front = await installer.SetUpModelFrontAsync(cts.Token);
                if (!front.Success) throw new SparkRemoteException(front.Summary);
            }
            else
            {
                _installLog.Append($"The secure model address (port {SparkSetup.ModelFrontPort}) is already set up.");
            }

            // From this PC's side: is the page reachable through the network (and the firewall)?
            _installLog.Append($"Checking Spark Swapper answers at https://{SparkSetup.UrlHost(SparkHost)}:{SparkSetup.SwapperPort} …");
            var certificate = await CertificateProbe.ProbeAsync(SparkHost, SparkSetup.SwapperPort, TimeSpan.FromSeconds(15), cts.Token);
            _cert ??= certificate;
            _installLog.Append("It answers. Done.");
            _installOk = true;
            _installPhase = InstallPhase.Done;
            _state.SwapperInstalled = true;
            _state.Save();
        }
        catch (OperationCanceledException)
        {
            _installPhase = InstallPhase.Idle;
        }
        catch (Exception error) when (error is SparkRemoteException or IOException or System.Net.Sockets.SocketException
                                          or Renci.SshNet.Common.SshException or InvalidOperationException)
        {
            _installError = error.Message;
            _installPhase = InstallPhase.Failed;
            _installLog.Append("✖ " + error.Message);
        }
        catch (Exception error)
        {
            _installError = $"Something unexpected went wrong: {error.Message}";
            _installPhase = InstallPhase.Failed;
            App.WriteCrashLog(error, "guide-install");
        }
        finally
        {
            RenderIf(GuidePage.Install);
        }
    }
}
