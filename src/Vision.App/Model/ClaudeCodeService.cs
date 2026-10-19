using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Vision.App.Views.Guide;
using Vision.Core;

namespace Vision.App.Model;

/// <summary>Claude Code on this computer, for Settings › Claude Code and the wizard's guide: whether the claude CLI is
/// installed and signed in (checked with `claude --version` and `claude auth status`, which say whether — never with what
/// — it is signed in), where it is, whether it runs in WSL, and the model-menu routes (Claude Code · Opus, Sonnet, Haiku)
/// that put chats on it. Opt-in: nothing is added until the person turns it on.</summary>
public sealed partial class ClaudeCodeService : ObservableObject
{
    private readonly AppConfig _config;
    private readonly AgentHost _host;
    private readonly Dispatcher _dispatcher;
    private bool _demo;

    /// <summary>The last check (null: not checked yet).</summary>
    [ObservableProperty] private ClaudeCodeStatus? _status;
    [ObservableProperty] private bool _busy;
    /// <summary>WSL distributions found (empty off Windows or without WSL).</summary>
    [ObservableProperty] private IReadOnlyList<string> _wslDistros = [];
    [ObservableProperty] private DateTimeOffset? _checkedAt;

    public ClaudeCodeService(AppConfig config, AgentHost host, Dispatcher dispatcher)
    {
        _config = config;
        _host = host;
        _dispatcher = dispatcher;
        ClaudeCodeClient.ConfiguredPath = config.ClaudeCodePath;
        config.Providers.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(InMenu));
            OnPropertyChanged(nameof(UsesWsl));
        };
    }

    /// <summary>The Claude Code routes in the model menu.</summary>
    public IReadOnlyList<ProviderProfile> Routes => _config.Providers.Where(ClaudeCodeRoutes.IsClaudeCode).ToList();

    /// <summary>Claude Code is offered in chats' model menus.</summary>
    public bool InMenu => Routes.Count > 0;

    /// <summary>Its routes run the CLI inside WSL.</summary>
    public bool UsesWsl => Routes.FirstOrDefault() is { } route && ClaudeCodeRoutes.UsesWsl(route);

    public string? WslDistro => Routes.FirstOrDefault() is { } route ? ClaudeCodeRoutes.WslDistro(route) : null;

    /// <summary>Check the CLI (on this computer, or in WSL when its routes — or <paramref name="wsl"/> — say so).</summary>
    public async Task CheckAsync(bool? wsl = null, string? distro = null)
    {
        if (_demo || Busy) return;
        Busy = true;
        try
        {
            var inWsl = wsl ?? UsesWsl;
            var probe = ClaudeCodeRoutes.Route(ClaudeCodeRoutes.DefaultModel, inWsl, inWsl ? distro ?? WslDistro : null);
            var status = await Task.Run(() => ClaudeCodeClient.CheckAsync(probe));
            if (_demo) return; // the self-test's sample state took over meanwhile
            Status = status;
            if (OperatingSystem.IsWindows() && WslDistros.Count == 0) WslDistros = await ClaudeCodeCli.WslDistrosAsync();
            CheckedAt = DateTimeOffset.Now;
            _host.ResetClaudeCode();
        }
        catch (Exception error)
        {
            Status = new ClaudeCodeStatus.Broken(_config.ClaudeCodePath.Length > 0 ? _config.ClaudeCodePath : "claude", error.Message);
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>Put Claude Code in the model menu (one route per model) and, for <see cref="RouteUse.Primary"/>, make
    /// <paramref name="model"/> the primary. Returns the route for <paramref name="model"/>.</summary>
    public ProviderProfile Use(RouteUse use, string model = ClaudeCodeRoutes.DefaultModel, bool wsl = false, string? distro = null)
    {
        ProviderProfile? chosen = null;
        foreach (var m in ClaudeCodeRoutes.Models)
        {
            var route = ClaudeCodeRoutes.Route(m.Alias, wsl, distro);
            if (_config.Providers.FirstOrDefault(p => p.RouteId == route.RouteId) is { } existing)
            {
                route.Enabled = existing.Enabled;
                route.ReasoningEffort = existing.ReasoningEffort;
            }
            _config.Store(route);
            if (string.Equals(m.Alias, model, StringComparison.OrdinalIgnoreCase)) chosen = route;
        }
        chosen ??= ClaudeCodeRoutes.Route(model, wsl, distro);
        if (use == RouteUse.Primary || _config.ActiveRoute is null) _config.MakePrimary(chosen.RouteId);
        _host.ResetClaudeCode();
        _host.ResetRouteCache();
        OnPropertyChanged(nameof(InMenu));
        OnPropertyChanged(nameof(UsesWsl));
        return chosen;
    }

    /// <summary>Take Claude Code out of the model menu (chats on it move to the primary when they next send).</summary>
    public void Remove()
    {
        foreach (var route in Routes) _config.RemoveProvider(route);
        _host.ResetClaudeCode();
        _host.ResetRouteCache();
        OnPropertyChanged(nameof(InMenu));
    }

    /// <summary>Run the CLI inside WSL (or not), keeping the routes and the chats on them.</summary>
    public void SetWsl(bool on, string? distro)
    {
        if (InMenu)
        {
            foreach (var route in Routes)
            {
                var moved = route.DeepCopy();
                moved.BaseUrl = ClaudeCodeRoutes.Address(on, distro);
                _config.Store(moved);
            }
        }
        _host.ResetClaudeCode();
        OnPropertyChanged(nameof(UsesWsl));
        _ = CheckAsync(on, distro);
    }

    /// <summary>The claude program to run ("" = search for it).</summary>
    public void SetPath(string path)
    {
        _config.ClaudeCodePath = path.Trim().Trim('"');
        ClaudeCodeClient.ConfiguredPath = _config.ClaudeCodePath;
        _host.ResetClaudeCode();
        _ = CheckAsync();
    }

    /// <summary>Self-test: sample state (nothing is run, nothing is saved).</summary>
    internal void ShowDemo(ClaudeCodeStatus? status)
    {
        _demo = true;
        Status = status;
        CheckedAt = DateTimeOffset.Now;
        WslDistros = ["Ubuntu-24.04"];
    }

    internal void EndDemo()
    {
        _demo = false;
        Status = null;
    }

    /// <summary>A ready, signed-in CLI, for the self-test's pictures.</summary>
    internal static ClaudeCodeStatus DemoReady =>
        new ClaudeCodeStatus.Ready("2.1.299", ClaudeCodeLaunch.Direct(@"C:\Users\pat\.local\bin\claude.exe"),
            new ClaudeCodeAuth(true, "claude.ai", "firstParty", "pat@503cm.com", "max"));

    internal static ClaudeCodeStatus DemoSignedOut =>
        new ClaudeCodeStatus.SignedOut("2.1.299", ClaudeCodeLaunch.Direct(@"C:\Users\pat\.local\bin\claude.exe"));
}
