using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Dsh.Core;

namespace Dsh.App.Model;

/// <summary>Drives the Spark Swapper from the app: which model the Spark is serving, switching it,
/// and following the switch in every chat. Address, username and the pinned certificate fingerprint
/// live in settings; the password lives in Windows Credential Manager.</summary>
public sealed partial class SparkController : ObservableObject, IDisposable
{
    private readonly AppConfig _config;
    private readonly AgentHost _host;
    private SparkSwapperClient? _client;
    private CancellationTokenSource? _poll;
    private string? _watchedJob;
    private string? _passwordCache;
    private readonly DispatcherTimer _tick;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsSwitching), nameof(ProgressLine), nameof(ActiveTitle))]
    private SwapperStatus? _status;
    [ObservableProperty] private string? _lastError;
    /// <summary>A certificate the user hasn't approved yet (shown in Settings › Spark).</summary>
    [ObservableProperty] private string? _untrustedFingerprint;
    [ObservableProperty] private bool _refreshing;
    /// <summary>The model the user picked and is being asked to confirm.</summary>
    [ObservableProperty] private SwapperModel? _confirmTarget;

    public SparkController(AppConfig config, AgentHost host)
    {
        _config = config;
        _host = host;
        if (string.IsNullOrWhiteSpace(_config.SparkUrl)
            && _config.ActiveProvider is { IsSelfHosted: true } provider
            && SparkSwapperClient.DefaultUrl(provider.BaseUrl) is { } guess)
        {
            _config.SparkUrl = guess;
        }
        // Keep the elapsed time in the banner moving while a swap runs.
        _tick = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background,
            (_, _) => { if (IsSwitching) OnPropertyChanged(nameof(ProgressLine)); }, Dispatcher.CurrentDispatcher);
    }

    // MARK: - Configuration

    public string Url
    {
        get => _config.SparkUrl;
        set { if (_config.SparkUrl != value) { _config.SparkUrl = value; ResetClient(); OnPropertyChanged(); OnPropertyChanged(nameof(IsConfigured)); } }
    }

    public string Username
    {
        get => _config.SparkUser;
        set { if (_config.SparkUser != value) { _config.SparkUser = value; ResetClient(); OnPropertyChanged(); OnPropertyChanged(nameof(IsConfigured)); } }
    }

    public string? PinnedFingerprint
    {
        get => _config.SparkPin;
        set { if (_config.SparkPin != value) { _config.SparkPin = value; ResetClient(); OnPropertyChanged(); } }
    }

    public string Password
    {
        get
        {
            if (_passwordCache is not null) return _passwordCache;
            try { _passwordCache = SecretStore.Read(SecretStore.SparkTarget) ?? ""; }
            catch (Exception) { _passwordCache = ""; }
            return _passwordCache;
        }
        set
        {
            _passwordCache = value;
            try
            {
                if (value.Length == 0) SecretStore.Delete(SecretStore.SparkTarget);
                else SecretStore.Write(SecretStore.SparkTarget, value, Username);
            }
            catch (Exception)
            {
                // Kept in memory for this session.
            }
            ResetClient();
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsConfigured));
        }
    }

    public bool IsConfigured => Url.Trim().Length > 0 && Username.Trim().Length > 0 && Password.Length > 0;
    public bool IsSwitching => Status?.IsSwitching ?? false;
    public string? ActiveTitle => Status?.ActiveModel?.Title;

    private void ResetClient()
    {
        _client?.Dispose();
        _client = null;
    }

    private SparkSwapperClient MakeClient()
    {
        if (_client is not null) return _client;
        if (!IsConfigured || !Uri.TryCreate(Url.Trim(), UriKind.Absolute, out var uri)) throw SwapperException.NotConfigured();
        _client = new SparkSwapperClient(uri, Username.Trim(), Password, PinnedFingerprint);
        return _client;
    }

    /// <summary>Approve the certificate the server presented.</summary>
    public void TrustPresentedCertificate()
    {
        if (UntrustedFingerprint is not { } fingerprint) return;
        PinnedFingerprint = fingerprint;
        UntrustedFingerprint = null;
        LastError = null;
        _ = RefreshAsync();
    }

    // MARK: - Status

    public async Task RefreshAsync()
    {
        if (!IsConfigured)
        {
            Status = null;
            return;
        }
        Refreshing = true;
        try
        {
            var client = MakeClient();
            var status = await client.StatusAsync();
            var wasRunning = Status?.Job?.IsRunning ?? false;
            Status = status;
            LastError = null;
            UntrustedFingerprint = null;
            if (status.Job is { IsRunning: false } job && (wasRunning || _watchedJob == job.Id)) Finished(job, status);
        }
        catch (SwapperException error)
        {
            if (error.Kind == SwapperErrorKind.UntrustedCertificate) UntrustedFingerprint = error.Fingerprint;
            LastError = error.Message;
        }
        catch (Exception error)
        {
            LastError = error.Message;
        }
        finally
        {
            Refreshing = false;
        }
    }

    /// <summary>Poll quickly while a swap runs, slowly otherwise.</summary>
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
                try { await Task.Delay(TimeSpan.FromSeconds(IsSwitching ? 2 : 30), token); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    public void Dispose()
    {
        _poll?.Cancel();
        _tick.Stop();
        ResetClient();
    }

    // MARK: - Switching

    /// <summary>Ask to switch (the UI confirms via <see cref="ConfirmTarget"/>).</summary>
    public void Request(SwapperModel model)
    {
        if (model.Key == Status?.Active) return;
        ConfirmTarget = model;
    }

    /// <summary>Switch now. Returns an error message, or null when the swap started.</summary>
    public async Task<string?> SwapAsync(string key)
    {
        try
        {
            await MakeClient().SwapAsync(key);
            await RefreshAsync();
            _watchedJob = Status?.Job?.Id;
            StartMonitoring();
            var title = Status?.AllModels.GetValueOrDefault(key)?.Title ?? key;
            _host.Broadcast($"Switching the Spark to **{title}**… chats will pick it up automatically when it is ready.");
            return null;
        }
        catch (Exception error)
        {
            LastError = error.Message;
            return error.Message;
        }
    }

    private void Finished(SwapperJob job, SwapperStatus status)
    {
        _watchedJob = null;
        _host.ResetRouteCache();
        var target = status.AllModels.GetValueOrDefault(job.Target);
        var title = target?.Title ?? job.Target;
        if (job.State == "done" && target is not null)
        {
            var context = (target.ServedContext ?? target.Context).ToString("N0");
            _host.Broadcast($"✅ The Spark is now serving **{title}** (`{target.ServedId}`, {context}-token context).");
        }
        else if (job.State == "failed")
        {
            _host.Broadcast($"⚠️ Switching the Spark to {title} failed: {job.Error ?? "unknown error"}. {job.Note ?? ""}".TrimEnd(), error: true);
        }
    }

    /// <summary><c>/swap [model]</c>: list, or switch.</summary>
    public async Task HandleCommandAsync(string? arg, SessionVM vm)
    {
        if (!IsConfigured)
        {
            vm.Note(SwapperException.NotConfigured().Message, MessageRole.Error);
            return;
        }
        await RefreshAsync();
        if (Status is not { } status)
        {
            vm.Note(LastError ?? "Couldn't read the Spark's status.", MessageRole.Error);
            return;
        }
        if (string.IsNullOrWhiteSpace(arg))
        {
            var lines = status.Ordered.Select(m =>
            {
                var mark = m.Key == status.Active ? "● serving" : (m.Running ? "◌ loading" : "○");
                return $"{mark}  **{m.Title}** — `{m.Key}` · {m.Context:N0} ctx · {m.Engine ?? ""}";
            });
            vm.Note("Spark models:\n" + string.Join("\n", lines) + "\nSwitch with `/swap <name>` or the model menu under the composer.");
            return;
        }
        if (status.Resolve(arg) is not { } key || !status.AllModels.TryGetValue(key, out var target))
        {
            vm.Note($"No Spark model matches “{arg}”. Options: {string.Join(", ", status.Ordered.Select(m => m.Key))}.", MessageRole.Error);
            return;
        }
        if (key == status.Active)
        {
            vm.Note($"The Spark is already serving {target.Title}.");
            return;
        }
        if (status.IsSwitching)
        {
            vm.Note("The Spark is already switching models; wait for it to finish.", MessageRole.Error);
            return;
        }
        if (await SwapAsync(key) is { } error) vm.Note(error, MessageRole.Error);
    }

    /// <summary>Human-readable progress line for the banner.</summary>
    public string? ProgressLine
    {
        get
        {
            if (Status?.Job is not { IsRunning: true } job) return null;
            var title = Status.AllModels.GetValueOrDefault(job.Target)?.Title ?? job.Target;
            var step = job.CurrentStep?.Label ?? "Working";
            var elapsed = Math.Max(0, (int)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0 - job.Started));
            return $"Switching the Spark to {title} · {step} · {elapsed / 60}m {elapsed % 60:00}s";
        }
    }
}
