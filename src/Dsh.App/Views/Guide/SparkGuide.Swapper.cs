using System.IO;
using System.Windows;
using System.Windows.Controls;
using Dsh.App.Infrastructure;
using Dsh.App.Model;
using Dsh.Core;
using static Dsh.App.Views.Guide.GuideVisuals;

namespace Dsh.App.Views.Guide;

// MARK: - The padlock, the Swapper's login, a model, and DSH's connection

public sealed partial class SparkGuide
{
    private Uri SwapperUrl => SparkSetup.SwapperUrl(SparkHost);

    // MARK: - Certificate

    private CertificateDetails? _cert;
    private string? _certError;
    private bool _probingCert;
    private bool? _certMatchesSpark;
    private bool _verifyingCert;
    private bool? _windowsTrusted;
    private bool _trustingWindows;

    private bool CertificatePinned => _cert is not null
                                      && string.Equals(_state.SwapperFingerprint, _cert.Fingerprint, StringComparison.OrdinalIgnoreCase)
                                      && string.Equals(_model.Spark.PinnedFingerprint, _cert.Fingerprint, StringComparison.OrdinalIgnoreCase);

    private async Task ProbeCertificateAsync()
    {
        _probingCert = true;
        _certError = null;
        RenderIf(GuidePage.Certificate);
        try
        {
            _cert = await CertificateProbe.ProbeAsync(SparkHost, SparkSetup.SwapperPort);
            if (OperatingSystem.IsWindows())
            {
                try { _windowsTrusted = CertificateProbe.IsTrustedByWindows(_cert.Fingerprint); } catch (Exception) { _windowsTrusted = null; }
            }
        }
        catch (Exception error)
        {
            _certError = $"{error.Message} Is Spark Swapper installed and running? (Previous step.)";
        }
        finally
        {
            _probingCert = false;
            RenderIf(GuidePage.Certificate);
        }
    }

    /// <summary>Pin the Swapper's certificate for DSH: its swapper client and, later, the model route.</summary>
    private void TrustInDsh()
    {
        if (_cert is not { } cert) return;
        _model.Spark.Url = SwapperUrl.ToString().TrimEnd('/');
        _model.Spark.PinnedFingerprint = cert.Fingerprint;
        _state.SwapperFingerprint = cert.Fingerprint;
        _state.Save();
    }

    /// <summary>Compare with the certificate file on the Spark itself, over the SSH login the user
    /// already trusted — a real check, not a guess.</summary>
    private async Task VerifyOverSshAsync()
    {
        if (_ssh is null || _cert is null || _sparkPassword.Length == 0) return;
        _verifyingCert = true;
        RenderIf(GuidePage.Certificate);
        try
        {
            var installer = new SparkInstaller(_ssh, _sparkPassword);
            var (_, _, lines) = await installer.RunInShellAsync("sudo openssl x509 -in /etc/spark-swapper/tls.crt -noout -fingerprint -sha256", CancellationToken.None);
            var line = lines.LastOrDefault(l => l.Contains("Fingerprint=", StringComparison.OrdinalIgnoreCase));
            var onSpark = CertificateProbe.NormalizeFingerprint(line);
            _certMatchesSpark = onSpark is not null && string.Equals(onSpark, _cert.Fingerprint, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            _certMatchesSpark = null;
        }
        finally
        {
            _verifyingCert = false;
            RenderIf(GuidePage.Certificate);
        }
    }

    private async Task TrustInWindowsAsync()
    {
        if (_cert is not { } cert || !OperatingSystem.IsWindows()) return;
        _trustingWindows = true;
        RenderIf(GuidePage.Certificate);
        try
        {
            // Windows shows its own "Security Warning" and waits for the user; keep the window responsive meanwhile.
            _windowsTrusted = await Task.Run(() => CertificateProbe.TrustInWindows(cert.RawData));
        }
        catch (Exception)
        {
            _windowsTrusted = false;
        }
        finally
        {
            _trustingWindows = false;
            RenderIf(GuidePage.Certificate);
        }
    }

    private UIElement CertificatePage()
    {
        var panel = Ui.Stack(
            Picture("padlock", "Left: a bank's website, vouched for by a public authority. Right: your Spark, which signed its own certificate."),
            Heading("The padlock warning — and why it's OK"),
            Subheading("What is a self-signed certificate?"),
            Paragraph("A website proves who it is with a **certificate** — a digital ID card. Public websites get theirs signed by a company browsers already trust (a \"certificate authority\"), like a passport stamped by a government."),
            Paragraph("Your Spark made its own ID card and signed it itself: **self-signed**. The connection is encrypted exactly as well; the only difference is that no outside company vouches for it — and none could, because the Spark lives on your home network, not the internet."),
            Subheading("Why the browser says \"Your connection isn't private\""),
            Paragraph("Browsers can't tell your Spark's own ID card from a fake one, so they warn you — the right thing on the internet. On **your own device, on your own network**, it's expected. The way to be sure is the **fingerprint** below: a code that's different for every certificate."));

        if (_probingCert) panel.Children.Add(Busy($"Looking at the certificate of {SparkLabel}…"));
        else if (_certError is { } error)
        {
            panel.Children.Add(Note(Tone.Error, error, "Couldn't reach Spark Swapper", Ui.Buttons(Ui.Button("Try again", () => _ = ProbeCertificateAsync()))));
        }
        else if (_cert is { } cert)
        {
            var details = Ui.Stack(
                Ui.Text(cert.CommonName, 13.5, FontWeights.SemiBold),
                Ui.Secondary($"{(cert.IsSelfSigned ? "Made by the Spark itself (self-signed)" : $"Issued by {CertificateProbe.CommonName(cert.Issuer)}")} · valid until {cert.NotAfter.LocalDateTime:d MMMM yyyy}"),
                Copyable(FingerprintLines(cert.Fingerprint), "Certificate fingerprint (SHA-256)"));
            if (_verifyingCert) details.Children.Add(Busy("Comparing with the certificate file on the Spark…"));
            else if (_certMatchesSpark == true) details.Children.Add(Note(Tone.Success, "It matches the certificate file on the Spark (checked over your secure login). This is your Spark."));
            else if (_certMatchesSpark == false) details.Children.Add(Note(Tone.Error, "It does NOT match the certificate file on the Spark. Don't trust it — something between this PC and the Spark isn't right."));
            else if (_ssh is not null && _sparkPassword.Length > 0)
                details.Children.Add(Ui.Buttons(Ui.Button("Check it against the Spark", () => _ = VerifyOverSshAsync())));
            else
                details.Children.Add(Ui.Secondary("To check by hand, run this on the Spark and compare: sudo openssl x509 -in /etc/spark-swapper/tls.crt -noout -fingerprint -sha256"));
            var card = Ui.Card(details);
            card.Margin = new Thickness(0, 6, 0, 8);
            panel.Children.Add(Subheading("Your Spark's certificate"));
            panel.Children.Add(card);

            panel.Children.Add(CertificatePinned
                ? Note(Tone.Success, "DSH trusts this certificate — only this one — for your Spark.", "Trusted in DSH")
                : Note(Tone.Info, "Press \"Trust it and continue\" (below) and DSH will accept exactly this certificate from your Spark, and nothing else that claims to be it.", "Trust it in DSH"));

            if (OperatingSystem.IsWindows())
            {
                var windows = _windowsTrusted == true
                    ? Note(Tone.Success, "Windows trusts it too: Edge and Chrome on this PC won't warn about your Spark any more.")
                    : Note(Tone.Info,
                        "Optional. Adds it to Windows' trusted certificates for your account, so browsers on this PC stop warning. Windows then shows a \"Security Warning\" saying it can't confirm the certificate is from \"" + cert.CommonName + "\" — check the thumbprint it shows is your Spark's and choose Yes. You can remove it later under \"Manage user certificates\".",
                        "Also trust it in Windows",
                        Ui.Buttons(Busyable(Ui.Button("Also trust it in Windows", () => _ = TrustInWindowsAsync()), _trustingWindows)));
                panel.Children.Add(windows);
            }
        }

        panel.Children.Add(Subheading("Opening Spark Swapper in your browser"));
        panel.Children.Add(Picture("browser-warning", "A browser warning page: press Advanced, then Continue to the Spark's address.", 230));
        panel.Children.Add(Steps(
            $"Open https://{SparkSetup.UrlHost(SparkHost)}:{SparkSetup.SwapperPort} — the button below does it.",
            "On the warning, press **Advanced** (Edge and Chrome) or **Show details** (Safari).",
            $"Press **Continue to {SparkHost} (unsafe)** — or \"Proceed\", \"visit this website\". The browser remembers your choice."));
        panel.Children.Add(Ui.Buttons(Ui.Button("Open Spark Swapper in the browser", () => ShellIntegration.Open(SwapperUrl.ToString()))));
        panel.Children.Add(Aside("\"Unsafe\" is the browser being careful: it means \"nobody I know vouched for this ID\", not \"this is dangerous\". The traffic is still encrypted with TLS. What would be dangerous is trusting a certificate you didn't expect — which is why DSH shows the fingerprint and, when it can, checks it against the file on the Spark."));
        panel.Children.Add(Stuck("The warning came back after it worked before? If the Spark's address changed (your router gave it a new one), browsers warn again — the certificate names the old address. Continuing is still fine if the fingerprint matches."));
        return panel;
    }

    private static Button Busyable(Button button, bool busy)
    {
        button.IsEnabled = !busy;
        if (busy) button.Content = "Waiting for Windows…";
        return button;
    }

    // MARK: - Admin login

    private SwapperSession? _session;
    private string? _sessionError;
    private bool _adminBusy;
    private string? _adminError;
    private TextBox? _adminUser;
    private PasswordBox? _adminPassword;
    private PasswordBox? _adminPassword2;
    private Button? _adminGo;
    private SparkSwapperClient? _swapper;

    /// <summary>A Swapper client signed in with the saved login (after this page).</summary>
    private SparkSwapperClient Swapper()
    {
        if (_swapper is not null) return _swapper;
        var spark = _model.Spark;
        var url = Uri.TryCreate(spark.Url, UriKind.Absolute, out var saved) ? saved : SwapperUrl;
        _swapper = new SparkSwapperClient(url, spark.Username, spark.Password, spark.PinnedFingerprint ?? _state.SwapperFingerprint);
        return _swapper;
    }

    private async Task LoadSessionAsync()
    {
        _adminBusy = true;
        _sessionError = null;
        RenderIf(GuidePage.Admin);
        try
        {
            using var anonymous = new SparkSwapperClient(SwapperUrl, "", "", _state.SwapperFingerprint);
            _session = await anonymous.SessionAsync();
            if (!_session.SetupNeeded && _state.AdminReady && _model.Spark.IsConfigured)
            {
                // Resumed: check the saved login still works.
                _swapper?.Dispose();
                _swapper = null;
                try { await Swapper().StatusAsync(); } catch (SwapperException) { _state.AdminReady = false; }
            }
        }
        catch (SwapperException error)
        {
            _sessionError = error.Kind == SwapperErrorKind.UntrustedCertificate
                ? "The Spark presented a different certificate from the one you trusted. Go back one step and look at it again."
                : error.Message;
        }
        finally
        {
            _adminBusy = false;
            RenderIf(GuidePage.Admin);
        }
    }

    private UIElement AdminPage()
    {
        var panel = Ui.Stack(
            Picture("swapper-setup", "Spark Swapper's first page, asking to create the admin login.", 230),
            Heading("Create your Spark Swapper login"),
            Lead("Spark Swapper has its own login, separate from the Spark's. **The first person to open its page becomes its admin** — so let's make that you, right now, before anyone else on your network could."));
        _adminUser ??= Ui.Field(_state.SshUser ?? _model.Spark.Username, "for example: admin");
        _adminPassword ??= PasswordField();
        if (_adminPassword2 is null)
        {
            _adminPassword2 = PasswordField();
            foreach (var box in new[] { _adminPassword, _adminPassword2 })
            {
                box.PreviewKeyDown += (_, e) =>
                {
                    if (e.Key != System.Windows.Input.Key.Enter) return;
                    e.Handled = true;
                    if (_adminGo is { IsEnabled: true }) _ = CreateAdminAsync(setup: _session?.SetupNeeded == true);
                };
            }
        }
        foreach (var box in new Control[] { _adminUser, _adminPassword, _adminPassword2 }) box.IsEnabled = !_adminBusy;
        _adminUser.TextChanged -= OnAdminTyped;
        _adminUser.TextChanged += OnAdminTyped;
        _adminPassword.PasswordChanged -= OnAdminTyped;
        _adminPassword.PasswordChanged += OnAdminTyped;
        _adminPassword2.PasswordChanged -= OnAdminTyped;
        _adminPassword2.PasswordChanged += OnAdminTyped;

        if (_state.AdminReady)
        {
            panel.Children.Add(Note(Tone.Success,
                $"Signed in to Spark Swapper as {_model.Spark.Username}. DSH keeps this login in Windows Credential Manager, to switch models for you.",
                "Your Swapper login is ready"));
            panel.Children.Add(Paragraph($"Use the same username and password in the browser at https://{SparkSetup.UrlHost(SparkHost)}:{SparkSetup.SwapperPort}. As the admin you can add logins for other people from its Users tab."));
        }
        else if (_session is null)
        {
            if (_adminBusy) panel.Children.Add(Busy("Asking Spark Swapper…"));
            if (_sessionError is { } error)
                panel.Children.Add(Note(Tone.Error, error, "Couldn't reach Spark Swapper", Ui.Buttons(Ui.Button("Try again", () => _ = LoadSessionAsync()))));
        }
        else if (_session.SetupNeeded)
        {
            panel.Children.Add(Field("Username", Detach(_adminUser)));
            panel.Children.Add(Field("Password", Detach(_adminPassword), "At least 8 characters. It can be the same as the Spark's, or different."));
            panel.Children.Add(Field("Password again", Detach(_adminPassword2)));
            _adminGo = Ui.Button("Create the admin login", () => _ = CreateAdminAsync(setup: true), accent: true);
            panel.Children.Add(Ui.Buttons(_adminGo));
        }
        else
        {
            panel.Children.Add(Note(Tone.Info, "Spark Swapper already has an admin login — maybe you (or someone) opened its page before. Sign in with it:", "Already set up"));
            panel.Children.Add(Field("Username", Detach(_adminUser)));
            panel.Children.Add(Field("Password", Detach(_adminPassword)));
            _adminGo = Ui.Button("Sign in", () => _ = CreateAdminAsync(setup: false), accent: true);
            var reset = _ssh is not null && _sparkPassword.Length > 0
                ? Link("Forgot it? Reset the Swapper's logins", () => _ = ResetSwapperLoginAsync())
                : null;
            panel.Children.Add(Ui.Buttons(_adminGo, reset));
            if (reset is null)
                panel.Children.Add(Ui.Secondary("Forgot it? On the Spark, run: sudo spark-swapper-reset-login — then come back and create a new one."));
        }
        UpdateAdminButton();
        if (_adminBusy && _session is not null) panel.Children.Add(Busy("Talking to Spark Swapper…"));
        if (_adminError is { } problem) panel.Children.Add(Note(Tone.Error, problem));

        panel.Children.Add(Aside("DSH creates the login through Spark Swapper's own web API (the same thing its page does), over the certificate you just trusted. Passwords are stored on the Spark scrambled (hashed), and DSH keeps its copy in Windows Credential Manager, never in its settings file."));
        panel.Children.Add(Stuck("\"The admin login already exists\" but you never made one? Someone opened the page first. Reset it (link above, or sudo spark-swapper-reset-login on the Spark) and create yours straight away."));
        return panel;
    }

    private void OnAdminTyped(object sender, RoutedEventArgs e) => UpdateAdminButton();

    private void UpdateAdminButton()
    {
        if (_adminGo is null || _adminUser is null || _adminPassword is null || _adminPassword2 is null) return;
        var setup = _session?.SetupNeeded == true;
        _adminGo.IsEnabled = !_adminBusy && _adminUser.Text.Trim().Length > 0
                             && (setup ? _adminPassword.Password.Length >= 8 && _adminPassword.Password == _adminPassword2.Password : _adminPassword.Password.Length > 0);
    }

    private async Task CreateAdminAsync(bool setup)
    {
        if (_adminUser is null || _adminPassword is null) return;
        var user = _adminUser.Text.Trim();
        var password = _adminPassword.Password;
        _adminBusy = true;
        _adminError = null;
        _host.Render();
        var client = new SparkSwapperClient(SwapperUrl, user, password, _state.SwapperFingerprint);
        try
        {
            if (setup) await client.SetupAdminAsync();
            else await client.StatusAsync();
            var spark = _model.Spark;
            spark.Url = SwapperUrl.ToString().TrimEnd('/');
            spark.Username = user;
            spark.Password = password;
            spark.PinnedFingerprint = _state.SwapperFingerprint;
            _swapper?.Dispose();
            _swapper = client;
            client = null;
            _state.AdminReady = true;
            _state.Save();
            _ = spark.RefreshAsync();
        }
        catch (SwapperException error)
        {
            _adminError = error.StatusCode == 409
                ? "Someone created the admin login a moment ago. If it wasn't you, reset it and try again."
                : error.Detail ?? error.Message;
            if (error.StatusCode == 409) _session = null;
        }
        finally
        {
            client?.Dispose();
            _adminBusy = false;
            if (_session is null) _ = LoadSessionAsync();
            RenderIf(GuidePage.Admin);
        }
    }

    private async Task ResetSwapperLoginAsync()
    {
        if (_ssh is null) return;
        _adminBusy = true;
        _adminError = null;
        _host.Render();
        try
        {
            var installer = new SparkInstaller(_ssh, _sparkPassword);
            var (exit, problem, _) = await installer.RunInShellAsync("sudo spark-swapper-reset-login", CancellationToken.None);
            if (problem is { } p) _adminError = SparkInstaller.Describe(p);
            else if (exit != 0) _adminError = $"The reset didn't work (exit code {exit}).";
            // The service restarts; give it a moment before asking again.
            await Task.Delay(TimeSpan.FromSeconds(3));
        }
        catch (Exception error)
        {
            _adminError = error.Message;
        }
        finally
        {
            _adminBusy = false;
            _session = null;
            _ = LoadSessionAsync();
        }
    }

    // MARK: - A model

    private SwapperStatus? _status;
    private IReadOnlyList<SwapperProvisionItem>? _provision;
    private string? _modelError;
    private bool _loadingModels;
    private string? _chosenModel;
    private bool _starting;
    private SwapperJob? _job;
    private string? _jobTitle;
    private readonly GuideLog _modelLog = new(170);
    private readonly SwapperLogTail _tail = new();
    private CancellationTokenSource? _pollCts;

    private SwapperModel? RunningModel => _status?.ActiveModel;

    private async Task LoadModelsAsync()
    {
        if (_demo) return;
        _loadingModels = true;
        _modelError = null;
        RenderIf(GuidePage.Model);
        try
        {
            _status = await Swapper().StatusAsync();
            _provision = await Swapper().ProvisioningAsync();
            if (_status.Job is { IsRunning: true } running)
            {
                _job = running;
                _jobTitle = $"Starting {TitleOf(running.Target)}";
                StartPolling();
            }
        }
        catch (SwapperException error)
        {
            _modelError = error.Message;
        }
        finally
        {
            _loadingModels = false;
            RenderIf(GuidePage.Model);
        }
    }

    private string TitleOf(string key) => _status?.AllModels.GetValueOrDefault(key)?.Title ?? key;

    private UIElement ModelPage()
    {
        var panel = Ui.Stack(
            Picture("swapper-dashboard", "Spark Swapper's model cards: one serving, one ready to switch to.", 230),
            Heading("Get a model running"),
            Lead("Spark Swapper runs one AI model at a time on the Spark. Pick one to start — you can switch any time later."));

        if (_loadingModels && _status is null) panel.Children.Add(Busy("Asking Spark Swapper what it has…"));
        if (_modelError is { } error)
            panel.Children.Add(Note(Tone.Error, error, "Couldn't talk to Spark Swapper", Ui.Buttons(Ui.Button("Try again", () => _ = LoadModelsAsync()))));

        if (RunningModel is { } active)
        {
            panel.Children.Add(Note(Tone.Success, $"{active.Title} is running ({active.ServedId}, {(active.ServedContext ?? active.Context):N0}-token context). You're ready to connect DSH.", "A model is running"));
        }

        if (_status is { } status)
        {
            if (_provision is { } items)
            {
                var ready = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
                foreach (var item in items)
                {
                    var glyph = Ui.Glyph(item.Present ? Icons.Completed : Icons.CircleRing, 12, item.Present ? "SystemFillColorSuccessBrush" : "TextFillColorTertiaryBrush");
                    glyph.Margin = new Thickness(0, 0, 5, 0);
                    var chip = new Border { Child = Ui.Stack(Orientation.Horizontal, glyph, Ui.Text(item.Title, 11.5, wrap: false)), Margin = new Thickness(0, 0, 6, 6), ToolTip = item.Detail };
                    chip.SetResourceReference(FrameworkElement.StyleProperty, "Chip");
                    ready.Children.Add(chip);
                }
                panel.Children.Add(Subheading("What's on the Spark"));
                panel.Children.Add(ready);
            }
            panel.Children.Add(Subheading("Models"));
            var recommended = status.Ordered.FirstOrDefault(m => m.Key == "standard")?.Key ?? status.Ordered.FirstOrDefault()?.Key;
            _chosenModel ??= status.Active ?? recommended;
            foreach (var m in status.Ordered)
            {
                var detail = string.Join(" · ", new[] { m.Tagline, m.Engine, $"{m.Context:N0}-token context" }.Where(s => !string.IsNullOrWhiteSpace(s)));
                if (m.Key == status.Active) detail = "Serving now · " + detail;
                else if (m.Key == recommended) detail = "Recommended first · " + detail;
                var key = m.Key;
                panel.Children.Add(Choice(m.Key == status.Active ? Icons.Completed : Icons.Robot, m.Title, detail, _chosenModel == key, () =>
                {
                    _chosenModel = key;
                    _host.Render();
                }));
            }
            if (_chosenModel == "flash" && status.Active != "flash")
                panel.Children.Add(Note(Tone.Warning, "Flash Next needs its model files (about 99 GB) downloaded on the Spark first, which Spark Swapper doesn't do by itself. Start with Qwen3.8 27B unless you've done that."));
            var busy = _starting || (_job?.IsRunning ?? false) || status.IsSwitching;
            var start = Ui.Button(_chosenModel == status.Active ? "It's running" : $"Start {TitleOf(_chosenModel ?? "")}", () => _ = StartModelAsync(_chosenModel!), accent: true);
            start.IsEnabled = !busy && _chosenModel is not null && _chosenModel != status.Active;
            panel.Children.Add(Ui.Buttons(start, Ui.Button("Refresh", () => _ = LoadModelsAsync())));
        }

        if (_job is { } job)
        {
            var steps = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            steps.Children.Add(Ui.Text(_jobTitle ?? "Working", 13.5, FontWeights.SemiBold));
            foreach (var step in job.Steps ?? [])
            {
                var (glyph, brush) = step.State switch
                {
                    "done" => (Icons.Completed, "SystemFillColorSuccessBrush"),
                    "running" => (Icons.Sync, "AccentTextFillColorPrimaryBrush"),
                    "failed" => (Icons.Error, "SystemFillColorCriticalBrush"),
                    "skipped" => (Icons.ChevronRight, "TextFillColorTertiaryBrush"),
                    _ => (Icons.CircleRing, "TextFillColorTertiaryBrush"),
                };
                var icon = Ui.Glyph(glyph, 13, brush);
                icon.Margin = new Thickness(0, 0, 8, 0);
                var row = Ui.Stack(Orientation.Horizontal, icon, Ui.Text(step.Label, 12.5, step.State == "running" ? FontWeights.SemiBold : FontWeights.Normal));
                row.Margin = new Thickness(0, 4, 0, 0);
                steps.Children.Add(row);
            }
            var elapsed = TimeSpan.FromSeconds(Math.Max(0, (job.Finished ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0) - job.Started));
            steps.Children.Add(Ui.Secondary($"{(job.IsRunning ? "Running for" : "Took")} {(int)elapsed.TotalMinutes}m {elapsed.Seconds:00}s"));
            panel.Children.Add(Ui.Card(steps));
            if (job.IsFailed) panel.Children.Add(Note(Tone.Error, $"{job.Error}{(job.Note is { } note ? " " + note : "")}", "It didn't start"));
            panel.Children.Add(new Expander { Header = Ui.Text("Details (the Spark's log)", 12.5), Content = Detach(_modelLog), IsExpanded = job.IsRunning || job.IsFailed, Margin = new Thickness(0, 6, 0, 0) });
        }

        panel.Children.Add(Aside("Starting a model loads it into the Spark's memory with Docker. The very first start also downloads it — tens of gigabytes — so expect 20–40 minutes; after that a switch takes a few minutes. It keeps going on the Spark even if you close this window. Spark Swapper also installs what's missing (Docker with GPU support, the model recipes) from its Provisioning tab."));
        panel.Children.Add(Stuck("It failed? Read the last lines of the log: \"not enough memory\" means another program is using the GPU; \"checkpoint not found\" means that model's files aren't downloaded. You can skip this step and start a model later from Spark Swapper's page or with /swap in DSH."));
        return panel;
    }

    private async Task StartModelAsync(string key)
    {
        _starting = true;
        _modelError = null;
        _host.Render();
        try
        {
            var client = Swapper();
            _provision = await client.ProvisioningAsync();
            var docker = _provision.FirstOrDefault(i => i.Action == "docker");
            if (docker is { Present: false })
                await RunInstallJobAsync(client, "docker", "Installing Docker with GPU support");
            if (key == "flash" && _provision.FirstOrDefault(i => i.Action == "flash_recipe") is { Present: false })
                await RunInstallJobAsync(client, "flash_recipe", "Getting the Flash Next recipe");
            var swap = await client.StartSwapAsync(key);
            _job = swap;
            _jobTitle = $"Starting {TitleOf(key)}";
            _modelLog.Append($"── Starting {TitleOf(key)} ──");
            StartPolling();
        }
        catch (SwapperException error)
        {
            _modelError = error.Detail ?? error.Message;
        }
        finally
        {
            _starting = false;
            RenderIf(GuidePage.Model);
        }
    }

    /// <summary>Run one provisioning action and follow its log until it finishes.</summary>
    private async Task RunInstallJobAsync(SparkSwapperClient client, string action, string title)
    {
        _jobTitle = title;
        _modelLog.Append($"── {title} ──");
        _job = await client.InstallAsync(action);
        RenderIf(GuidePage.Model);
        var tail = new SwapperLogTail();
        while (_job is { IsRunning: true } && !_disposed)
        {
            await Task.Delay(TimeSpan.FromSeconds(2));
            _job = await client.ProvisioningJobAsync();
            foreach (var line in tail.Next(_job)) _modelLog.Append(line.M);
            RenderIf(GuidePage.Model);
        }
        if (_job is { IsFailed: true } failed) throw SwapperException.Server(500, failed.Error ?? $"{title} failed.");
        _provision = await client.ProvisioningAsync();
    }

    private void StartPolling()
    {
        _pollCts?.Cancel();
        var cts = _pollCts = new CancellationTokenSource();
        _ = PollAsync(cts.Token);
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                _status = await Swapper().StatusAsync(cancellationToken);
                if (_status.Job is { } job)
                {
                    _job = job;
                    foreach (var line in _tail.Next(job)) _modelLog.Append(line.M);
                    if (!job.IsRunning)
                    {
                        if (job.IsDone) _state.ModelKey = job.Target;
                        _state.Save();
                        _model.Host.ResetRouteCache();
                        RenderIf(GuidePage.Model);
                        return;
                    }
                }
                RenderIf(GuidePage.Model);
                await Task.Delay(TimeSpan.FromSeconds(2.5), cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (SwapperException error)
        {
            _modelError = error.Message;
            RenderIf(GuidePage.Model);
        }
    }

    // MARK: - Connect DSH

    private enum StepState { Waiting, Running, Done, Warning, Failed }

    private readonly (StepState State, string Detail)[] _connect =
        [(StepState.Waiting, ""), (StepState.Waiting, ""), (StepState.Waiting, ""), (StepState.Waiting, "")];
    private bool _connectBusy;
    private string? _hello;
    private CertificateDetails? _frontCert;
    private CancellationTokenSource? _connectCts;

    private static readonly string[] ConnectSteps =
    [
        "Getting the model's key from Spark Swapper",
        "Checking the Spark's secure model address",
        "Saving the connection in DSH",
        "Saying hello to the model",
    ];

    private async Task ConnectAsync()
    {
        _connectCts?.Cancel();
        var cts = _connectCts = new CancellationTokenSource();
        _connectBusy = true;
        _hello = null;
        for (var i = 0; i < _connect.Length; i++) _connect[i] = (StepState.Waiting, "");
        RenderIf(GuidePage.Connect);
        var step = 0;
        void Set(StepState state, string detail = "")
        {
            _connect[step] = (state, detail);
            RenderIf(GuidePage.Connect);
        }
        try
        {
            Set(StepState.Running);
            var credentials = await Swapper().CredentialsAsync(cts.Token);
            var key = credentials.ApiKey ?? "";
            Set(StepState.Done, key.Length > 0 ? "Got it." : "The model has no key; that's fine.");

            step = 1;
            Set(StepState.Running);
            _frontCert = await CertificateProbe.ProbeAsync(SparkHost, SparkSetup.ModelFrontPort, TimeSpan.FromSeconds(10), cts.Token);
            var pin = _frontCert.Fingerprint;
            if (string.Equals(pin, _state.SwapperFingerprint, StringComparison.OrdinalIgnoreCase))
                Set(StepState.Done, $"https://{SparkSetup.UrlHost(SparkHost)}:{SparkSetup.ModelFrontPort} answers, with the same certificate you trusted.");
            else if (string.Equals(pin, _state.FrontFingerprint, StringComparison.OrdinalIgnoreCase))
                Set(StepState.Done, "It answers, with the certificate you trusted for it.");
            else
            {
                Set(StepState.Warning, $"It answers with a different certificate from Spark Swapper's ({CertificateProbe.Shorten(pin)}). If you set up its HTTPS yourself, trust it below.");
                return;
            }

            step = 2;
            Set(StepState.Running);
            _status = await Swapper().StatusAsync(cts.Token);
            var model = _status.ActiveModel?.ServedId ?? credentials.ApiModel ?? _status.Ordered.FirstOrDefault()?.ServedId ?? "model";
            var profile = SparkSetup.Provider(SparkHost, model, key, pin);
            SaveProvider(profile);
            _state.Connected = true;
            _state.Save();
            Set(StepState.Done, $"DSH now talks to {profile.BaseUrl} · {model}");

            step = 3;
            if (_status.ActiveModel is null)
            {
                Set(StepState.Warning, "No model is running on the Spark yet, so there's nobody to say hello. Start one (previous page), then press Try again.");
                return;
            }
            Set(StepState.Running, "The first reply can take a little while…");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            timeout.CancelAfter(TimeSpan.FromMinutes(3));
            _hello = await SparkSetup.SayHelloAsync(new OpenAiClient(profile), model, timeout.Token);
            Set(StepState.Done, "It answered:");
        }
        catch (OperationCanceledException) when (!cts.IsCancellationRequested)
        {
            Set(StepState.Failed, "No answer within 3 minutes. The model may still be loading — try again in a minute.");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            Set(StepState.Failed, step == 1
                ? $"{error.Message} The secure model address is set up by the install step — go back to \"Install Spark Swapper\" and run it again."
                : AgentHost.Describe(error));
        }
        finally
        {
            _connectBusy = false;
            RenderIf(GuidePage.Connect);
        }
    }

    /// <summary>Make the Spark DSH's model route (replacing an older route to the same address), with
    /// its key in Credential Manager.</summary>
    private void SaveProvider(ProviderProfile profile)
    {
        var config = _model.Config;
        var key = profile.ApiKey;
        var stored = profile.DeepCopy();
        stored.ApiKey = null;
        foreach (var old in config.Providers.Where(p => p.Name == stored.Name && p.BaseUrl == stored.BaseUrl && p.RouteId != stored.RouteId).ToList())
            config.RemoveProvider(old);
        config.Activate(stored);
        if (!string.IsNullOrEmpty(key)) config.SetApiKey(key, stored);
        _model.Host.ResetRouteCache();
    }

    private UIElement ConnectPage()
    {
        var panel = Ui.Stack(
            Picture("dsh-connected", "DSH's chat window connected to the DGX Spark.", 240),
            Heading("Connect DSH to your Spark"),
            Lead("Last step: DSH picks up the model's key and address from Spark Swapper, saves the connection, and sends a first message."));
        if (_state.Connected && !_connectBusy && _connect.All(s => s.State == StepState.Waiting))
        {
            // Resumed: connected in an earlier session.
            panel.Children.Add(Note(Tone.Success, $"DSH is connected to your Spark ({SparkSetup.ModelBaseUrl(SparkHost)}).", "Connected",
                Ui.Buttons(Ui.Button("Check again", () => _ = ConnectAsync()))));
            return panel;
        }
        var list = new StackPanel();
        for (var i = 0; i < ConnectSteps.Length; i++)
        {
            var (state, detail) = _connect[i];
            FrameworkElement icon = state == StepState.Running
                ? new Spinner { Width = 16, Height = 16 }
                : Ui.Glyph(state switch
                {
                    StepState.Done => Icons.Completed,
                    StepState.Failed => Icons.Error,
                    StepState.Warning => Icons.Warning,
                    _ => Icons.CircleRing,
                }, 16, state switch
                {
                    StepState.Done => "SystemFillColorSuccessBrush",
                    StepState.Failed => "SystemFillColorCriticalBrush",
                    StepState.Warning => "SystemFillColorCautionBrush",
                    _ => "TextFillColorTertiaryBrush",
                });
            if (icon is Spinner spinner) spinner.SetResourceReference(Spinner.ForegroundProperty, "AccentTextFillColorPrimaryBrush");
            icon.Margin = new Thickness(0, 2, 12, 0);
            icon.VerticalAlignment = VerticalAlignment.Top;
            var text = Ui.Stack(Ui.Text(ConnectSteps[i], 13.5, state == StepState.Running ? FontWeights.SemiBold : FontWeights.Normal));
            if (detail.Length > 0) text.Children.Add(Ui.Secondary(detail, 12));
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
            DockPanel.SetDock(icon, Dock.Left);
            row.Children.Add(icon);
            row.Children.Add(text);
            list.Children.Add(row);
        }
        panel.Children.Add(Ui.Card(list, new Thickness(16, 14, 16, 4)));
        if (_hello is { } hello)
        {
            var bubble = new Border
            {
                Child = Paragraph(hello, 13.5),
                Padding = new Thickness(14, 10, 14, 0),
                CornerRadius = new CornerRadius(12),
                Margin = new Thickness(0, 8, 60, 8),
            };
            bubble.SetResourceReference(Border.BackgroundProperty, "SubtleFillColorSecondaryBrush");
            panel.Children.Add(bubble);
        }
        if (_connect[1].State == StepState.Warning && _frontCert is { } front)
        {
            panel.Children.Add(Note(Tone.Warning, "", "A different certificate on port 11443", Ui.Stack(
                Copyable(FingerprintLines(front.Fingerprint), "Model address certificate fingerprint"),
                Ui.Buttons(Ui.Button("Trust this one too", () =>
                {
                    _state.FrontFingerprint = front.Fingerprint;
                    _state.Save();
                    _ = ConnectAsync();
                })))));
        }
        var again = Ui.Button("Try again", () => _ = ConnectAsync());
        again.IsEnabled = !_connectBusy;
        if (!_connectBusy && _connect.Any(s => s.State is StepState.Failed or StepState.Warning)) panel.Children.Add(Ui.Buttons(again));

        panel.Children.Add(Aside($"DSH saved a model route named \"{SparkSetup.ProviderName}\" pointing at https://{SparkSetup.UrlHost(SparkHost)}:{SparkSetup.ModelFrontPort}/v1, with the Spark's certificate pinned (DSH accepts exactly that certificate there) and the model key in Windows Credential Manager. It also saved Spark Swapper's address and login, so /swap and Settings › DGX Spark can switch models. Everything is editable in Settings."));
        panel.Children.Add(Stuck("\"Couldn't reach the model server\": a model may still be loading — wait for it on the previous page. \"Replied 401\": the key changed on the Spark; press Try again to fetch it anew."));
        return panel;
    }

    // MARK: - Done

    private UIElement DonePage()
    {
        var swapper = $"https://{SparkSetup.UrlHost(SparkHost)}:{SparkSetup.SwapperPort}";
        return Ui.Stack(
            Picture("done", "A PC and a Spark connected, with a big green check."),
            Heading("You're all set"),
            Lead($"Your Spark{(SparkHost.Length > 0 ? $" at {SparkLabel}" : "")}, Spark Swapper and DSH are working together. Here's how to use them:"),
            Bullets(
                (Icons.Chat, "**Chat.** Start a new chat — it runs on your Spark, privately, on your own network."),
                (Icons.Sync, "**Switch models.** Type /swap in a chat (or /swap flash), or use the model menu under the message box. Every chat follows the switch."),
                (Icons.Globe, $"**Manage the Spark** in Spark Swapper: {swapper} — bookmark it. In DSH: Settings › DGX Spark."),
                (Icons.Up, "**Update Spark Swapper** later: run this guide again, choose \"It's already set up\", and on its install step press \"Reinstall or update Spark Swapper\" — or on the Spark: git -C ~/spark-swapper pull && sudo bash ~/spark-swapper/deploy/install.sh"),
                (Icons.Lock, "**Forgot the Swapper login?** On the Spark run sudo spark-swapper-reset-login, then open its page and create a new one."),
                (Icons.Document, $"**Logs.** Spark Swapper: journalctl -u spark-swapper -f on the Spark. DSH: {AppPaths.Logs}")),
            Ui.Buttons(
                Ui.Button("Open Spark Swapper", () => ShellIntegration.Open(swapper)),
                Ui.Button("Open DSH's logs folder", () => ShellIntegration.OpenFolder(AppPaths.Logs))),
            Aside("Spark Swapper is open source (github.com/gnubyte/DGX-Spark-Swapper) and runs on the Spark as a small service. DSH is the app on this PC: it runs the agent and its tools here, and asks the model on the Spark to think."),
            Stuck("Something stopped working later? Run the setup wizard again (Help › Run Setup Wizard) — it picks up what's already done and checks the rest."));
    }
}
