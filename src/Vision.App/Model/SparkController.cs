using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Vision.Core;

namespace Vision.App.Model;

/// <summary>One Spark Swapper box (a DGX Spark or a Strix Halo): what it is serving, switching it, and installing models
/// on it. The connection lives in settings; its token or password in Windows Credential Manager.</summary>
public sealed partial class SwapperBox : ObservableObject, IDisposable
{
    private readonly AppConfig _config;
    private SparkSwapperClient? _client;
    private string? _clientKey;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsSwitching), nameof(ProgressLine), nameof(Online))]
    private SwapperStatus? _status;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Online))] private string? _lastError;
    /// <summary>A certificate the user hasn't approved (a login connection whose box was reinstalled).</summary>
    [ObservableProperty] private string? _untrustedFingerprint;
    [ObservableProperty] private bool _refreshing;
    /// <summary>The model an install started from Vision is for, and how it is going.</summary>
    [ObservableProperty] private string? _installingKey;
    [ObservableProperty] private SwapperJob? _installJob;
    [ObservableProperty] private string? _installError;
    /// <summary>A model that finished installing just now, to offer switching to.</summary>
    [ObservableProperty] private string? _justInstalled;

    internal string? WatchedJob { get; set; }
    /// <summary>The self-test's made-up box: shown, never contacted.</summary>
    internal bool Demo { get; init; }

    public SwapperBox(AppConfig config, SwapperConnection connection)
    {
        _config = config;
        Connection = connection;
    }

    public SwapperConnection Connection { get; private set; }
    public string Id => Connection.Id;
    public string Name => Connection.Name.Length > 0 ? Connection.Name : Connection.Url;
    /// <summary>"DGX Spark", "Strix Halo" — what the box said it is (or null).</summary>
    public string? PlatformLabel => SwapperPlatforms.Label(Status?.Platform ?? Connection.Platform);
    public bool IsSwitching => Status?.IsSwitching ?? false;
    public bool Online => Status is not null && LastError is null;
    public string? ActiveTitle => Status?.ActiveModel?.Title;

    /// <summary>A newer copy of the connection (renamed, or a fresh secret) — the client is remade when it matters.</summary>
    internal void Update(SwapperConnection connection)
    {
        Connection = connection;
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(PlatformLabel));
    }

    /// <summary>The client for this box: its app token, or its login.</summary>
    public SparkSwapperClient Client()
    {
        var secret = _config.SwapperSecret(Connection);
        var key = $"{Connection.Url}|{Connection.Auth}|{Connection.Username}|{Connection.PinnedFingerprint}|{secret.GetHashCode()}";
        if (_client is not null && _clientKey == key) return _client;
        _client?.Dispose();
        if (Connection.Address is not { } url || secret.Length == 0) throw SwapperException.NotConfigured();
        _client = Connection.Auth == SwapperAuth.Token
            ? SparkSwapperClient.WithToken(url, secret, Connection.PinnedFingerprint)
            : new SparkSwapperClient(url, Connection.Username ?? "", secret, Connection.PinnedFingerprint);
        _clientKey = key;
        return _client;
    }

    public void Dispose()
    {
        _client?.Dispose();
        _client = null;
    }

    /// <summary>"Switching DGX Spark (spark-3f2a) to Qwen3.8 27B · Loading the model · 2m 05s".</summary>
    public string? ProgressLine
    {
        get
        {
            if (Status?.Job is not { IsRunning: true } job) return null;
            var title = Status.AllModels.GetValueOrDefault(job.Target)?.Title ?? job.Target;
            var step = job.CurrentStep?.Label ?? "Working";
            var elapsed = Math.Max(0, (int)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0 - job.Started));
            return $"Switching {Name} to {title} · {step} · {elapsed / 60}m {elapsed % 60:00}s";
        }
    }

    internal void Tick() => OnPropertyChanged(nameof(ProgressLine));

    /// <summary>"Installing Qwen3.8 27B on DGX Spark · Downloading · 3m 10s" — or null.</summary>
    public string? InstallLine
    {
        get
        {
            if (InstallJob is not { IsRunning: true } job) return null;
            var key = InstallingKey ?? SwapperJob.ModelOf(job.Target);
            var title = key is null ? "a model" : Status?.AllModels.GetValueOrDefault(key)?.Title ?? key;
            var elapsed = Math.Max(0, (int)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0 - job.Started));
            return $"Installing {title} on {Name} · {job.CurrentStep?.Label ?? "Working"} · {elapsed / 60}m {elapsed % 60:00}s";
        }
    }

    partial void OnInstallJobChanged(SwapperJob? value) => OnPropertyChanged(nameof(InstallLine));
}

/// <summary>Drives the Spark Swapper boxes from the app: what each serves, switching them, installing models on them,
/// and following a switch in every chat (every route at that box's address follows the new model).</summary>
public sealed partial class SparkController : ObservableObject, IDisposable
{
    private readonly AppConfig _config;
    private readonly AgentHost _host;
    private CancellationTokenSource? _poll;
    private readonly DispatcherTimer _tick;
    /// <summary>The UI thread: boxes are bound to views, so they change only there.</summary>
    private readonly Dispatcher _dispatcher;

    /// <summary>Every box, in the order they were added.</summary>
    public ObservableCollection<SwapperBox> Boxes { get; } = [];

    /// <summary>Bumped whenever anything about a box changes (status, error, an install) — views redraw on it.</summary>
    [ObservableProperty] private int _revision;
    /// <summary>The model the user picked and is being asked to confirm, and on which box.</summary>
    [ObservableProperty] private SwapperModel? _confirmTarget;
    public SwapperBox? ConfirmBox { get; private set; }

    public SparkController(AppConfig config, AgentHost host)
    {
        _config = config;
        _host = host;
        _dispatcher = Dispatcher.CurrentDispatcher;
        foreach (var connection in config.SwapperConnections) Attach(new SwapperBox(config, connection));
        config.SwapperConnections.CollectionChanged += (_, _) => Sync();
        // Keep the elapsed time in the banners moving while a swap or an install runs.
        _tick = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) =>
        {
            foreach (var box in Boxes.Where(b => b.IsSwitching || b.InstallJob is { IsRunning: true })) box.Tick();
            if (IsSwitching) OnPropertyChanged(nameof(ProgressLine));
        }, _dispatcher);
    }

    public bool IsConfigured => Boxes.Count > 0;
    public bool IsSwitching => Boxes.Any(b => b.IsSwitching);
    /// <summary>The first switching box's progress, for the banner over the composer.</summary>
    public string? ProgressLine => Boxes.Select(b => b.ProgressLine).FirstOrDefault(l => l is not null);

    private void Attach(SwapperBox box)
    {
        box.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SwapperBox.IsSwitching) or nameof(SwapperBox.ProgressLine))
            {
                OnPropertyChanged(nameof(IsSwitching));
                OnPropertyChanged(nameof(ProgressLine));
            }
            Revision++;
        };
        Boxes.Add(box);
    }

    /// <summary>Follow the settings' list: new boxes appear, removed ones go, edited ones are updated.</summary>
    private void Sync()
    {
        foreach (var gone in Boxes.Where(b => _config.SwapperConnections.All(c => c.Id != b.Id)).ToList())
        {
            gone.Dispose();
            Boxes.Remove(gone);
        }
        foreach (var connection in _config.SwapperConnections)
        {
            if (Boxes.FirstOrDefault(b => b.Id == connection.Id) is { } known) known.Update(connection);
            else
            {
                var box = new SwapperBox(_config, connection);
                Attach(box);
                _ = RefreshAsync(box);
            }
        }
        OnPropertyChanged(nameof(IsConfigured));
        OnPropertyChanged(nameof(IsSwitching));
        Revision++;
    }

    // MARK: - Which box

    public SwapperBox? Find(string id) => Boxes.FirstOrDefault(b => b.Id == id);

    /// <summary>The box behind a route: linked to it, or serving its address.</summary>
    public SwapperBox? BoxFor(ProviderProfile? route) =>
        route is null ? null : Boxes.FirstOrDefault(b => b.Connection.Serves(route));

    /// <summary>The box behind the primary route, if any.</summary>
    public SwapperBox? PrimaryBox => BoxFor(_config.Providers.FirstOrDefault(p => p.RouteId == _config.ActiveRoute));

    /// <summary>The primary model's box is switching models (chats wait for it).</summary>
    public bool IsSwitchingFor(ProviderProfile? route) => BoxFor(route)?.IsSwitching ?? false;

    // MARK: - Status

    public async Task RefreshAsync()
    {
        foreach (var box in Boxes.ToList()) await RefreshAsync(box);
    }

    public async Task RefreshAsync(SwapperBox box)
    {
        if (box.Demo) return;
        box.Refreshing = true;
        try
        {
            var status = await box.Client().StatusAsync();
            var wasRunning = box.Status?.Job?.IsRunning ?? false;
            box.Status = status;
            box.LastError = null;
            box.UntrustedFingerprint = null;
            if (status.Job is { IsRunning: false } job && (wasRunning || box.WatchedJob == job.Id)) Finished(box, job, status);
            // An install started elsewhere (the Swapper's page) shows here too.
            if (status.InstallJob is { } install && (box.InstallJob is null || box.InstallJob.Id == install.Id || install.IsRunning))
            {
                if (box.InstallJob is { IsRunning: true } && !install.IsRunning && install.IsDone) box.JustInstalled = SwapperJob.ModelOf(install.Target);
                box.InstallJob = install;
            }
            // What the box calls itself may have changed (a Swapper update, a new host name).
            if (status.Platform is { } platform && box.Connection.Platform != platform)
                _config.SaveSwapper(box.Connection with { Platform = platform, Hostname = status.Hostname ?? box.Connection.Hostname }, null);
        }
        catch (SwapperException error)
        {
            if (error.Kind == SwapperErrorKind.UntrustedCertificate) box.UntrustedFingerprint = error.Fingerprint;
            box.LastError = error.Message;
        }
        catch (Exception error)
        {
            box.LastError = error.Message;
        }
        finally
        {
            box.Refreshing = false;
        }
    }

    /// <summary>Poll quickly while a swap or an install runs, slowly otherwise.</summary>
    public void StartMonitoring()
    {
        if (_poll is not null) return;
        _poll = new CancellationTokenSource();
        var token = _poll.Token;
        _tick.Start();
        _ = Loop();

        async Task Loop()
        {
            while (!token.IsCancellationRequested)
            {
                if (IsConfigured) await RefreshAsync();
                var busy = IsSwitching || Boxes.Any(b => b.InstallJob is { IsRunning: true });
                try { await Task.Delay(TimeSpan.FromSeconds(busy ? 2 : 30), token); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    public void Dispose()
    {
        _poll?.Cancel();
        _tick.Stop();
        foreach (var box in Boxes) box.Dispose();
    }

    /// <summary>Approve the certificate a login connection's box presented (after a reinstall).</summary>
    public void TrustPresentedCertificate(SwapperBox box)
    {
        if (box.UntrustedFingerprint is not { } fingerprint) return;
        _config.SaveSwapper(box.Connection with { PinnedFingerprint = fingerprint }, null);
        box.UntrustedFingerprint = null;
        box.LastError = null;
        _ = RefreshAsync(box);
    }

    // MARK: - Adding, renaming, removing

    /// <summary>Save a box (from a connection code or a login) with its token or password, and start watching it.</summary>
    public SwapperBox Save(SwapperConnection connection, string? secret)
    {
        _config.SaveSwapper(connection, secret);
        var box = Find(connection.Id)!;
        box.Dispose(); // a fresh client with the new secret
        _ = RefreshAsync(box);
        StartMonitoring();
        return box;
    }

    public void Rename(SwapperBox box, string name)
    {
        if (name.Trim().Length == 0) return;
        _config.SaveSwapper(box.Connection with { Name = name.Trim() }, null);
    }

    /// <summary>Forget a box (its routes stay; they are ordinary routes now).</summary>
    public void Remove(SwapperBox box) => _config.RemoveSwapper(box.Id);

    /// <summary>Link a route to a box, so its model menu section and /swap know where it is.</summary>
    public void Link(SwapperBox box, ProviderProfile route)
    {
        var ids = box.Connection.RouteIds.Contains(route.RouteId, StringComparer.Ordinal) ? box.Connection.RouteIds : [.. box.Connection.RouteIds, route.RouteId];
        _config.SaveSwapper(box.Connection with { RouteIds = ids, ApiBase = box.Connection.ApiBase ?? route.BaseUrl }, null);
    }

    // MARK: - Switching

    /// <summary>Ask to switch (the UI confirms via <see cref="ConfirmTarget"/>).</summary>
    public void Request(SwapperBox box, SwapperModel model)
    {
        if (model.Key == box.Status?.Active || !model.IsInstalled) return;
        ConfirmBox = box;
        ConfirmTarget = model;
    }

    /// <summary>Switch now. Returns an error message, or null when the swap started.</summary>
    public async Task<string?> SwapAsync(SwapperBox box, string key)
    {
        try
        {
            await box.Client().SwapAsync(key);
            await RefreshAsync(box);
            box.WatchedJob = box.Status?.Job?.Id;
            StartMonitoring();
            var title = box.Status?.AllModels.GetValueOrDefault(key)?.Title ?? key;
            _host.Broadcast($"Switching {box.Name} to **{title}**… chats will pick it up automatically when it is ready.");
            return null;
        }
        catch (SwapperException error) when (error.Kind == SwapperErrorKind.NotInstalled)
        {
            return NotInstalledText(box);
        }
        catch (Exception error)
        {
            box.LastError = error.Message;
            return error.Message;
        }
    }

    public static string NotInstalledText(SwapperBox box) =>
        $"That model isn't installed on {box.Name} yet — install it from Settings › Model servers.";

    private void Finished(SwapperBox box, SwapperJob job, SwapperStatus status)
    {
        box.WatchedJob = null;
        // Every route at this box's address follows what it serves now (each route re-reads it).
        _host.ResetRouteCache();
        var target = status.AllModels.GetValueOrDefault(job.Target);
        var title = target?.Title ?? job.Target;
        if (job.State == "done" && target is not null)
        {
            var context = target.EffectiveContext.ToString("N0");
            _host.Broadcast($"✅ {box.Name} is now serving **{title}** (`{target.ServedId}`, {context}-token context).");
        }
        else if (job.State == "failed")
        {
            _host.Broadcast($"⚠️ Switching {box.Name} to {title} failed: {job.Error ?? "unknown error"}. {job.Note ?? ""}".TrimEnd(), error: true);
        }
    }

    // MARK: - Installing

    /// <summary>Install a catalog model on a box and follow it. Returns an error message, or null when it finished.</summary>
    public async Task<string?> InstallAsync(SwapperBox box, string key)
    {
        if (box.InstallJob is { IsRunning: true }) return "Another install is already running on this box. Wait for it to finish.";
        box.InstallingKey = key;
        box.InstallError = null;
        box.JustInstalled = null;
        StartMonitoring();
        try
        {
            // Progress arrives on a pool thread; the box changes on the UI thread (views follow it).
            var job = await SwapperSetupFlows.InstallModelAsync(box.Client(), key, j => _dispatcher.BeginInvoke(() => box.InstallJob = j));
            await RefreshAsync(box);
            if (job is { IsFailed: true } failed)
            {
                box.InstallError = failed.Error ?? "The install failed.";
                return box.InstallError;
            }
            box.JustInstalled = key;
            var title = box.Status?.AllModels.GetValueOrDefault(key)?.Title ?? key;
            _host.Broadcast($"✅ {title} is installed on {box.Name}. Switch to it from the model menu, Settings › Model servers, or `/swap`.");
            return null;
        }
        catch (SwapperException error) when (error.StatusCode == 409)
        {
            box.InstallError = "Another install is already running on this box. Wait for it to finish, then try again.";
            return box.InstallError;
        }
        catch (Exception error)
        {
            box.InstallError = error.Message;
            return error.Message;
        }
        finally
        {
            box.InstallingKey = null;
        }
    }

    // MARK: - /swap

    /// <summary><c>/swap</c>: every box's models; <c>/swap &lt;model&gt;</c>: switch the box behind the primary route (else
    /// the only box, else ask which); <c>/swap &lt;box&gt; &lt;model&gt;</c>: a box named by the start of its name.</summary>
    public async Task HandleCommandAsync(string? arg, SessionVM vm)
    {
        if (!IsConfigured)
        {
            vm.Note(SwapperException.NotConfigured().Message, MessageRole.Error);
            return;
        }
        await RefreshAsync();
        if (string.IsNullOrWhiteSpace(arg))
        {
            var sections = Boxes.Select(box =>
            {
                if (box.Status is not { } status) return $"**{box.Name}** — {box.LastError ?? "not reachable"}";
                var lines = status.Ordered.Select(m =>
                {
                    var mark = m.Key == status.Active ? "● serving" : !m.IsInstalled ? "○ not installed" : m.Running ? "◌ loading" : "○";
                    var speed = m.Speed(includeEstimate: false) is { } s ? $" · {s}" : "";
                    return $"{mark}  **{m.Title}** — `{m.Key}` · {m.EffectiveContext:N0} ctx{speed}";
                });
                return $"**{box.Name}**{(box.PlatformLabel is { } label && !box.Name.Contains(label) ? $" ({label})" : "")}:\n" + string.Join("\n", lines);
            });
            var how = Boxes.Count > 1 ? "Switch with `/swap <model>` (the primary model's box) or `/swap <box> <model>`." : "Switch with `/swap <name>` or the model menu under the composer.";
            vm.Note(string.Join("\n\n", sections) + "\n" + how);
            return;
        }
        var current = PrimaryBox is { } primary ? Boxes.IndexOf(primary) : (int?)null;
        var target = SwapTargets.Resolve(arg, Boxes.Select(b => (b.Name, b.Connection.Hostname)).ToList(), current);
        if (target.Box is not { } index)
        {
            vm.Note($"Which box? Use `/swap <box> <model>` — {string.Join(", ", Boxes.Select(b => b.Name))}.", MessageRole.Error);
            return;
        }
        var box = Boxes[index];
        if (box.Status is not { } boxStatus)
        {
            vm.Note(box.LastError ?? $"Couldn't read {box.Name}'s status.", MessageRole.Error);
            return;
        }
        if (target.Model.Length == 0 || boxStatus.Resolve(target.Model) is not { } key || !boxStatus.AllModels.TryGetValue(key, out var model))
        {
            vm.Note($"No model on {box.Name} matches “{target.Model}”. Options: {string.Join(", ", boxStatus.Ordered.Select(m => m.Key))}.", MessageRole.Error);
            return;
        }
        if (key == boxStatus.Active)
        {
            vm.Note($"{box.Name} is already serving {model.Title}.");
            return;
        }
        if (!model.IsInstalled)
        {
            vm.Note(NotInstalledText(box), MessageRole.Error);
            return;
        }
        if (boxStatus.IsSwitching)
        {
            vm.Note($"{box.Name} is already switching models; wait for it to finish.", MessageRole.Error);
            return;
        }
        if (await SwapAsync(box, key) is { } error) vm.Note(error, MessageRole.Error);
    }

    // MARK: - Self-test

    /// <summary>Self-test: show a box with made-up state. Nothing is saved, and the box is never contacted.</summary>
    internal SwapperBox ShowDemoBox(SwapperConnection connection, SwapperStatus? status, string? error = null, SwapperJob? install = null)
    {
        var box = new SwapperBox(_config, connection)
        {
            Demo = true,
            Status = status,
            LastError = error,
            InstallJob = install,
            InstallingKey = install is null ? null : SwapperJob.ModelOf(install.Target),
        };
        Attach(box);
        OnPropertyChanged(nameof(IsConfigured));
        return box;
    }

    /// <summary>Self-test: back to just the boxes in settings.</summary>
    internal void HideDemoBoxes() => Sync();
}
