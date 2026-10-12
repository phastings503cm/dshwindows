using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Dsh.App.Infrastructure;
using Dsh.App.Model;
using Dsh.Core;
using static Dsh.App.Views.Guide.GuideVisuals;

namespace Dsh.App.Views.Guide;

/// <summary>The pages of "Connect my Spark Swapper".</summary>
public enum SwapperPage { Paste, Check, Model, Role, Done }

/// <summary>How a new model route takes part: answering you, taking side tasks, standing by.</summary>
public enum RouteUse { Primary, Subagent, Backup, JustAdd }

/// <summary>"Connect my Spark Swapper": paste the connection code from the Swapper's Connect tab (or sign in with an address
/// and login, for a Swapper older than 2.0), check the box answers with the certificate the code names, pick the model,
/// say how DSH should use it, and save — the model route with the certificate pinned and the key in Credential Manager,
/// plus the box itself for switching models — then say hello through it.</summary>
public sealed class SwapperConnectGuide : IWizardGuide
{
    private readonly AppModel _model;
    private readonly IGuideHost _host;
    private readonly Dispatcher _dispatcher;
    private bool _disposed;
    private bool _demo;

    public SwapperConnectGuide(AppModel model, IGuideHost host, bool login = false)
    {
        _model = model;
        _host = host;
        _dispatcher = host.Window.Dispatcher;
        _loginMode = login;
        _use = DefaultUse();
    }

    public SwapperPage Page { get; private set; } = SwapperPage.Paste;
    object IWizardGuide.PageKey => Page;

    public void Activate() => OnShown();

    // MARK: - Header

    private static readonly string[] SectionTitles = ["Paste the code", "Check it", "Pick the model", "How DSH uses it", "Say hello"];

    public int SectionCount => SectionTitles.Length;
    public int SectionIndex => (int)Page;
    public string Title => $"Connect my Spark Swapper · {SectionTitles[SectionIndex]}";
    public string StepText => $"Step {SectionIndex + 1} of {SectionCount}";

    public string NextLabel => Page switch
    {
        SwapperPage.Role => "Save and say hello",
        SwapperPage.Done => "Start chatting",
        _ => "Next",
    };

    public bool CanAdvance => Page switch
    {
        SwapperPage.Paste => _loginMode ? _login is not null : ParsedCode is not null,
        SwapperPage.Check => _status is not null && !_checking,
        SwapperPage.Model => !_switching && !_installing,
        SwapperPage.Role => true,
        _ => _saved && !_saving,
    };

    public string? SecondaryLabel => null;
    public void Secondary() { }

    public void Next()
    {
        if (!CanAdvance) return;
        try
        {
            if (Page == SwapperPage.Done) _host.FinishGuide();
            else Go(Page + 1);
        }
        catch (Exception error)
        {
            App.WriteCrashLog(error, "swapper guide");
        }
    }

    public void Back()
    {
        if (Page == SwapperPage.Paste)
        {
            _host.LeaveGuide();
            return;
        }
        Go(Page - 1);
    }

    private void Go(SwapperPage page)
    {
        Page = page;
        OnShown();
        _host.Render();
    }

    private void OnShown()
    {
        if (_demo) return;
        switch (Page)
        {
            case SwapperPage.Check:
                if (_status is null && !_checking) _ = CheckAsync();
                break;
            case SwapperPage.Role:
                if (!_useChosen) _use = DefaultUse();
                break;
            case SwapperPage.Done:
                if (!_saved && !_saving) _ = SaveAsync();
                break;
        }
    }

    private void RenderIf(SwapperPage page)
    {
        if (_disposed) return;
        if (Page == page) _host.Render();
        else _host.RefreshChrome();
    }

    public UIElement Render() => Page switch
    {
        SwapperPage.Paste => _loginMode ? LoginPage() : PastePage(),
        SwapperPage.Check => CheckPage(),
        SwapperPage.Model => ModelPage(),
        SwapperPage.Role => RolePage(),
        _ => DonePage(),
    };

    public void Dispose()
    {
        _disposed = true;
        _client?.Dispose();
    }

    // MARK: - What the box is

    /// <summary>What the code (or the login) says about the box: how to reach it, its model API, its certificate.</summary>
    private sealed record Target(string Name, string? Platform, string? Hostname, Uri SwapperUrl, string ApiBase, string? ApiKey, string Fingerprint,
                                 SwapperAuth Auth, string Secret, string? User, string? Model);

    private Target? Box =>
        _login is { } login && login.Connection.Address is { } url
            // (An older Swapper may not say where its model API is: the setup guide puts it on the same host, port 11443.)
            ? new Target(login.Connection.Name, login.Connection.Platform, login.Connection.Hostname, url,
                login.Connection.ApiBase ?? SparkSetup.ModelBaseUrl(url.Host),
                login.Connect?.ApiKey ?? login.Credentials?.ApiKey, login.Connection.PinnedFingerprint ?? "", login.Connection.Auth, login.Secret,
                login.Connection.Username, login.Connect?.Model ?? login.Credentials?.ApiModel)
            : ParsedCode is { } code && Uri.TryCreate(code.SwapperUrl, UriKind.Absolute, out var swapper)
                ? new Target(code.Name, code.Platform, code.Hostname, swapper, code.ApiBase, code.ApiKey, code.Fingerprint, SwapperAuth.Token,
                    code.Token, code.User, code.Model)
                : null;

    private string BoxName => Box?.Name ?? "your box";
    private string KindLabel => SwapperPlatforms.Label(_status?.Platform ?? Box?.Platform) ?? "Spark Swapper";

    // MARK: - Paste the code

    private readonly TextBox _codeBox = new()
    {
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        Height = 110,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        VerticalContentAlignment = VerticalAlignment.Top,
        Padding = new Thickness(8, 6, 8, 6),
    };
    private bool _codeHooked;
    private string? _codeText;

    private SwapperConnectionCode? ParsedCode =>
        _codeText is { } text && SwapperConnectionCode.TryParse(text, out var code) ? code : null;

    private UIElement PastePage()
    {
        if (!_codeHooked)
        {
            _codeHooked = true;
            _codeBox.SetResourceReference(Control.FontFamilyProperty, "MonoFont");
            Placeholder.SetText(_codeBox, "SSC1:…");
            _codeBox.TextChanged += (_, _) =>
            {
                _codeText = _codeBox.Text;
                // A new code means a new check.
                _status = null;
                _checkError = null;
                _host.RefreshChrome();
                UpdateCodeNote();
            };
        }
        var panel = Ui.Stack(
            Heading("Connect your Spark Swapper"),
            Lead("Paste the connection code from the Swapper's Connect tab. It sets up the model, switching, and the security certificate in one go."),
            Field("Connection code", Detach(_codeBox)),
            Paragraph($"Where do I find it? Open your Swapper's web page (https://<box>:{SparkSetup.SwapperPort}), choose **Connect**, then **Connect DSH**, and copy the code.", 12.5, "TextFillColorSecondaryBrush"));
        panel.Children.Add(_codeNote);
        UpdateCodeNote();
        var login = Link("Use an address and login instead", () =>
        {
            _loginMode = true;
            _host.Render();
        });
        login.HorizontalAlignment = HorizontalAlignment.Left;
        panel.Children.Add(login);
        panel.Children.Add(Aside("The code holds the Swapper's address, the address of its model API and that API's key, the fingerprint of the box's security certificate, and an app token: a key for DSH alone, which the Swapper's Connect tab lists and can revoke. DSH keeps the token and the key in Windows Credential Manager, never in its settings."));
        panel.Children.Add(Stuck("No Connect tab? Your Swapper is older than 2.0: update it (on the box: git -C ~/spark-swapper pull && sudo bash ~/spark-swapper/deploy/install.sh), or use an address and login instead (the link above)."));
        return panel;
    }

    private readonly ContentControl _codeNote = new() { Focusable = false };

    private void UpdateCodeNote()
    {
        var text = _codeText?.Trim() ?? "";
        _codeNote.Content = text.Length == 0 ? null
            : ParsedCode is { } code ? Note(Tone.Success, $"{code.Name} at {code.SwapperUrl}.", "That's a connection code")
            : Note(Tone.Error, SwapperConnectionCode.Invalid);
    }

    // MARK: - Or an address and a login

    private readonly TextBox _address = Ui.Field(placeholder: $"https://<box>:{SparkSetup.SwapperPort} — or just its name or IP");
    private readonly TextBox _user = Ui.Field(placeholder: "the Swapper login's username");
    private readonly PasswordBox _password = PasswordField();
    private bool _loginBusy;
    private string? _loginError;
    /// <summary>The certificate the box presented, waiting for the user to trust it.</summary>
    private string? _seenFingerprint;
    private SwapperLoginResult? _login;

    private UIElement LoginPage()
    {
        var panel = Ui.Stack(
            Heading("Sign in to your Spark Swapper"),
            Lead("For a Swapper older than 2.0, or if you'd rather sign in. DSH asks a Swapper 2.0 for an app token, so your password isn't kept; an older Swapper keeps working with the password, stored in Windows Credential Manager."),
            Field("Address", Detach(_address)),
            Field("Username", Detach(_user)),
            Field("Password", Detach(_password)));
        foreach (var box in new Control[] { _address, _user, _password }) box.IsEnabled = !_loginBusy;
        if (_login is { } done)
        {
            panel.Children.Add(Note(Tone.Success,
                done.UsesToken ? "Signed in. The Swapper gave DSH an app token, so your password isn't kept." : "Signed in. This Swapper has no app tokens, so DSH keeps the password in Windows Credential Manager.",
                $"Connected to {done.Connection.Name}"));
        }
        else if (_seenFingerprint is { } fingerprint)
        {
            panel.Children.Add(Note(Tone.Info, "", "The Swapper uses its own security certificate", Ui.Stack(
                Paragraph("Check this fingerprint is your Swapper's (on the box: sudo openssl x509 -in /etc/spark-swapper/tls.crt -noout -fingerprint -sha256), then trust it:", 12.5),
                Copyable(FingerprintLines(fingerprint), "Certificate fingerprint (SHA-256)"),
                Ui.Buttons(Ui.Button("Trust it and sign in", () => _ = SignInAsync(fingerprint), accent: true)))));
        }
        else
        {
            var go = Ui.Button(_loginBusy ? "Signing in…" : "Sign in", () => _ = SignInAsync(null), accent: true);
            go.IsEnabled = !_loginBusy;
            panel.Children.Add(Ui.Buttons(go));
        }
        if (_loginBusy) panel.Children.Add(Busy("Talking to the Swapper…"));
        if (_loginError is { } error) panel.Children.Add(Note(Tone.Error, error));
        var code = Link("Paste a connection code instead", () =>
        {
            _loginMode = false;
            _host.Render();
        });
        code.HorizontalAlignment = HorizontalAlignment.Left;
        panel.Children.Add(code);
        return panel;
    }

    private bool _loginMode;

    private async Task SignInAsync(string? trusted)
    {
        if (SwapperAddress.Normalize(_address.Text) is not { } address)
        {
            _loginError = "Type the Swapper's address — its name or IP is enough.";
            _host.Render();
            return;
        }
        if (_user.Text.Trim().Length == 0 || _password.Password.Length == 0)
        {
            _loginError = "Type the Swapper login's username and password.";
            _host.Render();
            return;
        }
        _loginBusy = true;
        _loginError = null;
        _host.Render();
        var url = new Uri(address);
        try
        {
            var pin = trusted;
            if (pin is null)
            {
                // What certificate does it present? (A self-signed one is shown to the user before anything is sent.)
                var seen = await SparkSwapperClient.ProbeFingerprintAsync(url);
                if (seen is not null)
                {
                    _seenFingerprint = seen;
                    return;
                }
            }
            _login = await SwapperSetupFlows.SignInAsync(url, _user.Text.Trim(), _password.Password, pin, SwapperAddress.TokenLabel());
            _seenFingerprint = null;
            _status = null;
            Go(SwapperPage.Check);
        }
        catch (SwapperException error)
        {
            _loginError = error.Kind switch
            {
                SwapperErrorKind.BadLogin => error.Detail ?? error.Message,
                SwapperErrorKind.Transport => $"Couldn't reach the Swapper at {address}. Is the box on, and on the same network as this PC? ({error.Message})",
                _ => error.Message,
            };
            _seenFingerprint = null;
        }
        catch (Exception error)
        {
            _loginError = error.Message;
        }
        finally
        {
            _loginBusy = false;
            RenderIf(SwapperPage.Paste);
        }
    }

    // MARK: - Check it

    private bool _checking;
    private string? _checkError;
    private SwapperStatus? _status;
    private SparkSwapperClient? _client;

    /// <summary>A client for the box that accepts exactly the certificate the code (or the login) names.</summary>
    private SparkSwapperClient Client()
    {
        if (_client is not null) return _client;
        var box = Box ?? throw new InvalidOperationException("No box yet.");
        _client = box.Auth == SwapperAuth.Token
            ? SparkSwapperClient.WithToken(box.SwapperUrl, box.Secret, box.Fingerprint.Length > 0 ? box.Fingerprint : null, pinOnly: box.Fingerprint.Length > 0)
            : new SparkSwapperClient(box.SwapperUrl, box.User ?? "", box.Secret, box.Fingerprint.Length > 0 ? box.Fingerprint : null);
        return _client;
    }

    private async Task CheckAsync()
    {
        _checking = true;
        _checkError = null;
        _client?.Dispose();
        _client = null;
        RenderIf(SwapperPage.Check);
        var url = Box?.SwapperUrl.ToString().TrimEnd('/') ?? "";
        try
        {
            _status = await Client().StatusAsync();
            _chosen = _status.Active ?? _status.Recommended?.Key;
        }
        catch (SwapperException error)
        {
            _checkError = error.Kind switch
            {
                SwapperErrorKind.UntrustedCertificate =>
                    $"The Swapper at {url} showed a different security certificate than the code expects. If you reinstalled the Swapper, copy a fresh code. Otherwise don't continue — something may be intercepting the connection.",
                SwapperErrorKind.Transport =>
                    $"Couldn't reach the Swapper at {url}. Is the box switched on, and on the same network as this PC? ({error.Message})",
                _ => error.Message,
            };
        }
        catch (Exception error)
        {
            _checkError = error.Message;
        }
        finally
        {
            _checking = false;
            RenderIf(SwapperPage.Check);
        }
    }

    private UIElement CheckPage()
    {
        var panel = Ui.Stack(
            Heading($"Checking {BoxName}"),
            Lead("DSH connects with the security certificate the code names — and only that one — then asks the Swapper what it is serving."));
        if (_checking) panel.Children.Add(Busy($"Connecting to {Box?.SwapperUrl.Authority ?? "the Swapper"}…"));
        else if (_checkError is { } error)
            panel.Children.Add(Note(Tone.Error, error, "Couldn't connect", Ui.Buttons(Ui.Button("Try again", () => _ = CheckAsync(), accent: true))));
        else if (_status is { } status)
        {
            var serving = status.ActiveModel is { } active
                ? $"serving {active.Title} ({Fmt.Short(active.EffectiveContext)} context)"
                : "nothing is serving right now";
            panel.Children.Add(Note(Tone.Success, $"Connected to {BoxName} — {KindLabel} — {serving}.", "It answers"));
            if (status.Version is { } version) panel.Children.Add(Paragraph($"Spark Swapper {version}.", 12.5, "TextFillColorSecondaryBrush"));
        }
        panel.Children.Add(Aside("A self-signed certificate is how your box proves who it is on your own network. The code carries its fingerprint, so DSH can tell your box from anything pretending to be it — without the browser-style warning."));
        return panel;
    }

    // MARK: - Pick the model

    private string? _chosen;
    private bool _switching;
    private bool _installing;
    private string? _modelError;
    private SwapperJob? _job;
    private string? _installed;

    private UIElement ModelPage()
    {
        var panel = Ui.Stack(
            Heading("Pick the model"),
            Lead($"What {BoxName} can serve. The one serving now is chosen; pick another and DSH can switch the box to it."));
        if (_status is not { } status) return panel;
        foreach (var m in status.Ordered)
        {
            var facts = new List<string> { $"{Fmt.Short(m.EffectiveContext)} context" };
            if (m.Speed() is { } speed) facts.Add(speed);
            if (m.Key == status.Active) facts.Insert(0, "Serving now");
            else if (m.IsRecommended) facts.Insert(0, "Recommended");
            if (!m.IsInstalled) facts.Add("Not installed" + (m.InstallSummary is { } size ? $" — {size}" : ""));
            var key = m.Key;
            if (m.IsInstalled)
            {
                panel.Children.Add(Choice(key == status.Active ? Icons.Completed : Icons.Robot, m.Title, string.Join(" · ", facts), _chosen == key, () =>
                {
                    _chosen = key;
                    _host.Render();
                }));
            }
            else
            {
                var installing = _installing && (_job is null || SwapperJob.ModelOf(_job.Target) == key);
                var install = Ui.Button(installing ? "Installing…" : $"Install on {BoxName}", () => _ = InstallAsync(key));
                install.IsEnabled = !_installing && !_switching;
                var card = Ui.Row(m.Title, string.Join(" · ", facts), install, Icons.Download);
                card.Opacity = installing ? 1 : 0.8;
                panel.Children.Add(card);
            }
        }
        if (_chosen is { } chosen && chosen != status.Active && status.AllModels.GetValueOrDefault(chosen) is { } pick)
        {
            var current = status.ActiveModel?.Title;
            var buttons = Ui.Buttons(
                Ui.Button($"Switch {BoxName} to {pick.Title} now (takes {SwapperSetupFlows.SwitchTime(pick)})", () => _ = SwitchAsync(pick), accent: true),
                current is null ? null : Ui.Button($"Keep {current}", () =>
                {
                    _chosen = status.Active;
                    _host.Render();
                }));
            buttons.IsEnabled = !_switching && !_installing;
            panel.Children.Add(Note(Tone.Info, current is null
                ? $"Nothing is serving yet. Start {pick.Title} now, or carry on and start it later."
                : $"{BoxName} is serving {current}. Switching stops it and loads {pick.Title} — for everyone using the box.", "Switch now?", buttons));
        }
        if (_installed is { } done && status.AllModels.GetValueOrDefault(done) is { IsInstalled: true } ready && ready.Key != status.Active)
            panel.Children.Add(Note(Tone.Success, $"{ready.Title} is installed. Pick it above and start it, whenever you like."));
        if (_job is { } job)
        {
            var steps = new StackPanel();
            foreach (var step in job.Steps ?? [])
            {
                var icon = Ui.Glyph(step.State switch { "done" => Icons.Completed, "running" => Icons.Sync, "failed" => Icons.Error, _ => Icons.CircleRing }, 13,
                    step.State switch { "done" => "SystemFillColorSuccessBrush", "failed" => "SystemFillColorCriticalBrush", "running" => "AccentTextFillColorPrimaryBrush", _ => "TextFillColorTertiaryBrush" });
                icon.Margin = new Thickness(0, 0, 8, 0);
                steps.Children.Add(Ui.Stack(Orientation.Horizontal, icon, Ui.Text(step.Label, 12.5)));
            }
            if (job.Log is { Count: > 0 } log) steps.Children.Add(Ui.Secondary(log[^1].M, 11.5));
            panel.Children.Add(Ui.Card(steps));
            if (job.IsFailed) panel.Children.Add(Note(Tone.Error, job.Error ?? "It didn't finish.", "It didn't work"));
        }
        if (_switching || _installing) panel.Children.Add(Busy(_switching ? "Switching models — this keeps going on the box even if you close this window…" : "Installing — this keeps going on the box even if you close this window…"));
        if (_modelError is { } error) panel.Children.Add(Note(Tone.Error, error));
        panel.Children.Add(Aside("A box serves one model at a time. Switching takes from a few seconds to a few minutes; installing downloads the model first (tens of gigabytes, sometimes more). Both carry on on the box if you leave."));
        return panel;
    }

    private async Task SwitchAsync(SwapperModel pick)
    {
        _switching = true;
        _modelError = null;
        _host.Render();
        try
        {
            var client = Client();
            _job = await client.StartSwapAsync(pick.Key);
            RenderIf(SwapperPage.Model);
            while (!_disposed)
            {
                await Task.Delay(TimeSpan.FromSeconds(2.5));
                _status = await client.StatusAsync();
                _job = _status.Job ?? _job;
                RenderIf(SwapperPage.Model);
                if (_status.Job is not { IsRunning: true }) break;
            }
            if (_status?.Active == pick.Key) _chosen = pick.Key;
        }
        catch (SwapperException error) when (error.Kind == SwapperErrorKind.NotInstalled)
        {
            _modelError = $"That model isn't installed on {BoxName} yet — install it first.";
        }
        catch (Exception error)
        {
            _modelError = error.Message;
        }
        finally
        {
            _switching = false;
            RenderIf(SwapperPage.Model);
        }
    }

    private async Task InstallAsync(string key)
    {
        _installing = true;
        _modelError = null;
        _installed = null;
        _host.Render();
        try
        {
            var job = await SwapperSetupFlows.InstallModelAsync(Client(), key, j =>
            {
                _job = j;
                _dispatcher.BeginInvoke(() => RenderIf(SwapperPage.Model));
            });
            _status = await Client().StatusAsync();
            if (job is { IsDone: true }) _installed = key;
        }
        catch (SwapperException error) when (error.StatusCode == 409)
        {
            _modelError = "Another install is already running on this box. Wait for it to finish, then try again.";
        }
        catch (Exception error)
        {
            _modelError = error.Message;
        }
        finally
        {
            _installing = false;
            RenderIf(SwapperPage.Model);
        }
    }

    // MARK: - How DSH uses it

    private RouteUse _use;
    /// <summary>The user picked a role (the default no longer applies).</summary>
    private bool _useChosen;
    private bool _private = true;

    /// <summary>The route this box already has (pasted again, or set up by the DGX Spark guide), if any.</summary>
    private ProviderProfile? ExistingRoute =>
        Box is { } box ? _model.Config.Providers.FirstOrDefault(p => SwapperConnection.SameBase(p.BaseUrl, box.ApiBase)) : null;

    /// <summary>A box pasted again keeps its role (the primary stays the primary); a new box answers you when nothing else
    /// does yet, and otherwise helps out with side tasks.</summary>
    private RouteUse DefaultUse()
    {
        var config = _model.Config;
        if (ExistingRoute is { } existing)
        {
            if (existing.RouteId == config.ActiveRoute) return RouteUse.Primary;
            if (existing.SubagentWorker) return RouteUse.Subagent;
            if (existing.IsBackup) return RouteUse.Backup;
        }
        return config.ActiveProvider is null ? RouteUse.Primary : RouteUse.Subagent;
    }

    private UIElement RolePage()
    {
        var panel = Ui.Stack(
            Heading("How should DSH use it?"),
            Lead("You can change this any time in Settings › AI Models."));
        void Use(RouteUse use, string glyph, string title, string detail) =>
            panel.Children.Add(Choice(glyph, title, detail, _use == use, () =>
            {
                _use = use;
                _useChosen = true;
                _host.Render();
            }));
        Use(RouteUse.Primary, Icons.Chat, "Primary", "It answers you, and runs the main agent.");
        Use(RouteUse.Subagent, Icons.People, "Subagent", "It takes side tasks the primary hands out, in parallel.");
        Use(RouteUse.Backup, Icons.Sync, "Backup", "It takes over when the primary stops answering.");
        if (_use != RouteUse.Primary && ExistingRoute is { } existing && existing.RouteId == _model.Config.ActiveRoute)
            panel.Children.Add(Note(Tone.Info, "It's your primary model now, and stays so: to hand that over, make another model primary in Settings › AI Models."));
        var isPrivate = Ui.Check("Private — your own hardware", _private, on => _private = on);
        isPrivate.Margin = new Thickness(0, 10, 0, 0);
        isPrivate.FontWeight = FontWeights.SemiBold;
        panel.Children.Add(isPrivate);
        panel.Children.Add(Paragraph("It runs on your own box, so DSH sends it everything as it is — nothing is masked.", 12.5, "TextFillColorSecondaryBrush"));
        return panel;
    }

    // MARK: - Save and say hello

    private bool _saving;
    private bool _saved;
    private string? _saveError;
    private string? _hello;
    private ProviderProfile? _route;

    private async Task SaveAsync()
    {
        if (Box is not { } box || _status is not { } status) return;
        _saving = true;
        _saveError = null;
        _hello = null;
        RenderIf(SwapperPage.Done);
        try
        {
            var config = _model.Config;
            var model = status.ActiveModel?.ServedId ?? (_chosen is { } key ? status.AllModels.GetValueOrDefault(key)?.ServedId : null) ?? box.Model ?? "model";
            // A route at this box's model address already (the code pasted again, the DGX Spark guide's) is updated in place:
            // its address, key, certificate and model are the box's now, and everything else the user set on it stays — its
            // context, thinking, headers, how many subagents at a time, and its roles. The primary stays the primary.
            var existing = ExistingRoute;
            var route = existing?.DeepCopy() ?? new ProviderProfile(ProviderKind.OpenAICompat, box.Name, box.ApiBase, model);
            route.BaseUrl = box.ApiBase;
            route.Model = model;
            route.ApiKey = null;
            route.PinnedCertificate = box.Fingerprint.Length > 0 ? box.Fingerprint : route.PinnedCertificate;
            route.IsPrivate = _private;
            route.Enabled = null;
            if (_use == RouteUse.Subagent) route.SubagentWorker = true;
            if (_use == RouteUse.Backup) route.Backup = true;
            // (Another box that would make the same route id gets a name of its own rather than overwriting that route.)
            route = config.ReplaceProvider(existing?.RouteId, route, box.ApiKey ?? "");
            if (_use == RouteUse.Primary || config.ActiveRoute is null) config.MakePrimary(route.RouteId);
            _route = route;

            // The box itself, for switching models: the one already known at this address is updated.
            var known = config.SwapperConnections.FirstOrDefault(c => SwapperConnection.SameBase(c.Url, box.SwapperUrl.ToString())
                                                                       || c.Hostname is { Length: > 0 } host && host == box.Hostname);
            var connection = (known?.DeepCopy() ?? new SwapperConnection()) with
            {
                Name = known?.Name is { Length: > 0 } name && name != SwapperConnectionCode.DefaultName(known.Platform, known.Hostname, known.Url) ? name : box.Name,
                Url = box.SwapperUrl.ToString().TrimEnd('/'),
                Platform = status.Platform ?? box.Platform,
                Hostname = status.Hostname ?? box.Hostname,
                Auth = box.Auth,
                Username = box.User,
                PinnedFingerprint = box.Fingerprint.Length > 0 ? box.Fingerprint : null,
                ApiBase = box.ApiBase,
            };
            connection.RouteIds = connection.RouteIds.Where(id => config.Providers.Any(p => p.RouteId == id)).Append(route.RouteId).Distinct().ToList();
            _model.Spark.Save(connection, box.Secret);
            _model.Host.ResetRouteCache();
            _saved = true;
            RenderIf(SwapperPage.Done);

            var hello = route.DeepCopy();
            hello.ApiKey = box.ApiKey;
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            _hello = status.ActiveModel is null
                ? null
                : await SparkSetup.SayHelloAsync(ProviderClients.Create(hello), model, $"Say hello to someone who just connected DSH to their {KindLabel}, in one short, friendly sentence.", timeout.Token);
        }
        catch (OperationCanceledException)
        {
            _saveError = "No answer within 3 minutes. The model may still be loading — chats will reach it once it's ready.";
        }
        catch (Exception error)
        {
            _saveError = AgentHost.Describe(error);
        }
        finally
        {
            _saving = false;
            RenderIf(SwapperPage.Done);
        }
    }

    private UIElement DonePage()
    {
        var panel = Ui.Stack(
            Picture("dsh-connected", "DSH's chat window connected to a model on your own box.", 220),
            Heading(_saved ? "You're connected" : "Saving the connection"));
        if (_saving && !_saved) panel.Children.Add(Busy("Saving…"));
        if (_saved)
        {
            var primary = _route is { } saved && saved.RouteId == _model.Config.ActiveRoute;
            var use = primary ? "It's your primary model now: new messages go to it." : _use switch
            {
                RouteUse.Primary => "It's your primary model now: new messages go to it.",
                RouteUse.Subagent => "It takes subagents' side tasks.",
                RouteUse.Backup => "It stands by as a backup for the primary.",
                _ => "It's ready in the model menu.",
            };
            panel.Children.Add(Note(Tone.Success, $"{use} Switch what {BoxName} serves from the model menu under the message box, in Settings › Model servers, or with /swap.",
                $"{BoxName} is set up"));
            if (_saving) panel.Children.Add(Busy("Saying hello — the first reply can take a little while…"));
            else if (_hello is { } hello) panel.Children.Add(Note(Tone.Info, hello, "It answered"));
            else if (_status?.ActiveModel is null) panel.Children.Add(Note(Tone.Info, "Nothing is serving yet, so there's nobody to say hello. Start a model from the model menu, or Settings › Model servers."));
        }
        if (_saveError is { } error)
            panel.Children.Add(Note(Tone.Warning, error, _saved ? "Saved, but no hello yet" : "It didn't save",
                Ui.Buttons(Ui.Button("Try again", () => _ = SaveAsync()))));
        panel.Children.Add(Aside($"DSH saved a model route named \"{_route?.Name ?? BoxName}\" at {Box?.ApiBase ?? "the box's model address"}, with the box's certificate pinned and the model's key in Windows Credential Manager — and the box itself, for switching models, with its app token in Credential Manager too. Everything is editable in Settings."));
        return panel;
    }

    // MARK: - Self-test

    internal void ShowDemo(SwapperPage page)
    {
        _demo = true;
        _codeText = SwapperConnectionCode.Encode(new System.Text.Json.Nodes.JsonObject
        {
            ["v"] = 1, ["name"] = "DGX Spark (spark-3f2a)", ["hostname"] = "spark-3f2a", ["platform"] = "nvidia-gb10", ["version"] = "2.0.0",
            ["swapper_url"] = "https://192.168.1.42:8999", ["api_base"] = "https://192.168.1.42:11443/v1", ["api_key"] = "vllm-local",
            ["model"] = "Qwen3.8-27B", ["context"] = 262144,
            ["fingerprint"] = "3F:A2:9C:41:7B:E0:55:12:C8:6D:0A:F3:94:2E:B7:61:D5:08:4A:9E:23:CB:70:1F:66:E2:3D:A8:5C:91:04:BE",
            ["token"] = "sst_1a2b3c4d_demoTokenDemoTokenDemoTokenDemoToken0", ["user"] = "alice",
        });
        _codeBox.Text = _codeText;
        _status = System.Text.Json.JsonSerializer.Deserialize<SwapperStatus>("""
            {"active": "tf27b", "busy": false, "version": "2.0.0", "platform": "nvidia-gb10", "hostname": "spark-3f2a",
             "models": {
               "tf27b": {"key": "tf27b", "title": "Qwen3.8 27B", "context": 262144, "served_id": "Qwen3.8-27B", "running": true, "healthy": true,
                         "order": 1, "installed": true, "recommended": true, "eta": "about a minute",
                         "perf": {"decode": 58.3, "estimate": false}, "measured": {"decode": 27.1, "at": 1791234567.0}},
               "tfflash": {"key": "tfflash", "title": "Qwen3.8 Flash Next", "context": 524288, "served_id": "Qwen3.8-Flash-Next", "running": false,
                           "healthy": false, "order": 2, "installed": false, "recommended": true, "eta": "a few minutes",
                           "perf": {"decode": 64.4, "estimate": false}, "install": {"size_gb": 165, "time": "1–2 hours"}},
               "nemotron": {"key": "nemotron", "title": "Nemotron 3.5 Lightning", "context": 262144, "served_id": "nemotron", "running": false,
                            "healthy": false, "order": 3, "installed": true, "perf": {"decode": 124.2, "estimate": true}}}}
            """, SwapperStatus.Json);
        _chosen = page == SwapperPage.Model ? "nemotron" : "tf27b";
        if (page == SwapperPage.Done)
        {
            _saved = true;
            _hello = "Hello! Your DGX Spark is all set — what shall we work on first?";
        }
        Page = page;
        _host.Render();
    }
}
