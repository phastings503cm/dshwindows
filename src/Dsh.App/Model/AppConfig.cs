using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Dsh.Core;

namespace Dsh.App.Model;

/// <summary>What the user chose for one chat: skills selected by hand, and whether the model may
/// also pick skills itself.</summary>
public sealed record SessionSkillSelection(IReadOnlyList<string> Pinned, bool Auto = true)
{
    public static SessionSkillSelection Default { get; } = new([], true);
}

/// <summary>What code mode reopens with for one project.</summary>
public sealed record CodeState(IReadOnlyList<string> Files, string? Active, bool Terminal, bool Tree, bool Chat = true);

/// <summary>Persisted app configuration: providers, active model, permission preset, recent
/// projects, editor/terminal preferences, skills choices, window placement. Stored as JSON in
/// %APPDATA%\DSH\settings.json. API keys are kept out of it — they live in Windows Credential
/// Manager, keyed by the provider's stable identity.</summary>
public sealed partial class AppConfig : ObservableObject
{
    private readonly string _path;
    private bool _loading;
    private bool _saveScheduled;
    /// <summary>API keys read from Credential Manager. Guarded by its own lock: a subagent resolving its server's key runs
    /// on a pool thread while the UI thread reads the active route's.</summary>
    private readonly Dictionary<string, string> _keyCache = new(StringComparer.Ordinal);
    private readonly object _keyLock = new();

    public ObservableCollection<ProviderProfile> Providers { get; } = [];
    public ObservableCollection<string> RecentProjects { get; } = [];

    /// <summary>Identity of the active route: "&lt;provider name&gt;|&lt;model&gt;".</summary>
    [ObservableProperty] private string? _activeRoute;
    /// <summary>A PermissionPreset raw value for new chats.</summary>
    [ObservableProperty] private string _preset = PermissionPreset.WorkspaceWrite.RawValue();
    [ObservableProperty] private bool _wizardCompleted;

    [ObservableProperty] private double _editorFontSize = 13;
    [ObservableProperty] private bool _editorWraps;
    [ObservableProperty] private bool _editorLineNumbers = true;
    [ObservableProperty] private double _terminalFontSize = 13;
    /// <summary>The terminal panel's shell: "" (PowerShell), a keyword, or a full command line.</summary>
    [ObservableProperty] private string _terminalShell = "";
    /// <summary>The agent's shell: auto | powershell | windowspowershell | cmd | bash | a path.</summary>
    [ObservableProperty] private string _agentShell = Dsh.Core.AgentShell.Auto;
    /// <summary>"chat" or "code" — the workspace reopens where you left it.</summary>
    [ObservableProperty] private string _lastMode = "chat";
    /// <summary>"system", "light" or "dark".</summary>
    [ObservableProperty] private string _theme = "system";
    [ObservableProperty] private bool _checkForUpdates = true;
    [ObservableProperty] private bool _computerToolsEnabled = true;
    [ObservableProperty] private int _skillSourcesRaw = (int)SkillSources.All;
    /// <summary>The one-time notice about bringing in Claude Code and Cursor has been shown or used, so it
    /// doesn't come back.</summary>
    [ObservableProperty] private bool _externalImportOffered;

    [ObservableProperty] private string _sparkUrl = "";
    [ObservableProperty] private string _sparkUser = "";
    [ObservableProperty] private string? _sparkPin;

    [ObservableProperty] private double _sidebarWidth = 260;
    public WindowPlacement? Window { get; set; }

    /// <summary>Chats whose task list was running when the app quit (or crashed): they pick it back up
    /// on launch. A list the user stopped, or that ran dry, isn't here.</summary>
    public HashSet<string> QueueResumeChats { get; } = new(StringComparer.Ordinal);
    /// <summary>The task list panel is showing.</summary>
    [ObservableProperty] private bool _queuePanelOpen;
    [ObservableProperty] private double _queuePanelWidth = 360;

    /// <summary>The Plan panel (what the agent is working through, top right) is showing.</summary>
    [ObservableProperty] private bool _planPanelOpen;
    [ObservableProperty] private double _planPanelWidth = 340;
    /// <summary>Open the Plan panel by itself when the agent makes a plan.</summary>
    [ObservableProperty] private bool _planPanelAutoOpen = true;
    /// <summary>Remember things between chats: notes ride along with messages when relevant, and the agent
    /// can save and look them up. Off switches all of it off.</summary>
    [ObservableProperty] private bool _memoryEnabled = true;
    /// <summary>When every subagent server (worker) is busy, let subagents also run on the main model server.</summary>
    [ObservableProperty] private bool _subagentsUsePrimary = true;

    /// <summary>Skill ids (file paths) switched off everywhere.</summary>
    public HashSet<string> DisabledSkills { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Per-chat skill choices.</summary>
    public Dictionary<string, SessionSkillSelection> SessionSkills { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, CodeState> CodeStates { get; } = new(StringComparer.OrdinalIgnoreCase);

    public sealed record WindowPlacement(double Left, double Top, double Width, double Height, bool Maximized);

    public AppConfig(string? path = null)
    {
        _path = path ?? AppPaths.Settings;
        Load();
        Providers.CollectionChanged += (_, _) => Persist();
        RecentProjects.CollectionChanged += (_, _) => Persist();
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        Persist();
    }

    public SkillSources SkillSources
    {
        get => (SkillSources)SkillSourcesRaw;
        set => SkillSourcesRaw = (int)value;
    }

    public PermissionPreset AsPreset => PermissionPresets.FromRaw(Preset) ?? PermissionPreset.WorkspaceWrite;

    public Dsh.Core.AgentShell ResolvedAgentShell => Dsh.Core.AgentShell.Resolve(AgentShell);

    // MARK: - Active route

    /// <summary>The provider/model pair new turns use, with its API key filled in.</summary>
    public ProviderProfile? ActiveProvider
    {
        get
        {
            if (ActiveRoute is null) return null;
            var stored = Providers.FirstOrDefault(p => p.RouteId == ActiveRoute);
            if (stored is null) return null;
            var resolved = stored.DeepCopy();
            var key = ApiKey(stored);
            resolved.ApiKey = key.Length == 0 ? null : key;
            return resolved;
        }
    }

    public bool IsConfigured => ActiveProvider is not null;

    /// <summary>Make this provider the active route (adding or replacing it).</summary>
    public void Activate(ProviderProfile provider)
    {
        var copy = provider.DeepCopy();
        copy.ApiKey = null;
        var index = Providers.ToList().FindIndex(p => p.RouteId == copy.RouteId);
        if (index >= 0) Providers[index] = copy; else Providers.Add(copy);
        ActiveRoute = copy.RouteId;
        OnPropertyChanged(nameof(ActiveProvider));
    }

    /// <summary>Add or replace a route without changing which one is active (editing a spare or a subagent server must not make it the main model).</summary>
    public void Store(ProviderProfile provider)
    {
        var copy = provider.DeepCopy();
        copy.ApiKey = null;
        var index = Providers.ToList().FindIndex(p => p.RouteId == copy.RouteId);
        if (index >= 0) Providers[index] = copy; else Providers.Add(copy);
        OnPropertyChanged(nameof(ActiveProvider));
    }

    public void RemoveProvider(ProviderProfile provider)
    {
        var existing = Providers.FirstOrDefault(p => p.RouteId == provider.RouteId);
        if (existing is not null) Providers.Remove(existing);
        if (ActiveRoute == provider.RouteId) ActiveRoute = Providers.FirstOrDefault()?.RouteId;
        OnPropertyChanged(nameof(ActiveProvider));
    }

    partial void OnActiveRouteChanged(string? value) => OnPropertyChanged(nameof(ActiveProvider));

    // MARK: - Secrets (Credential Manager)

    public void SetApiKey(string key, ProviderProfile provider)
    {
        lock (_keyLock) _keyCache[provider.RouteId] = key;
        try
        {
            SecretStore.Write(SecretStore.ProviderTarget(provider.RouteId), key);
        }
        catch (Exception)
        {
            // Keep it for this session at least.
        }
        OnPropertyChanged(nameof(ActiveProvider));
    }

    public string ApiKey(ProviderProfile provider)
    {
        lock (_keyLock)
        {
            if (_keyCache.TryGetValue(provider.RouteId, out var cached)) return cached;
        }
        string key;
        try
        {
            key = SecretStore.Read(SecretStore.ProviderTarget(provider.RouteId)) ?? "";
        }
        catch (Exception)
        {
            key = "";
        }
        lock (_keyLock) return _keyCache.TryAdd(provider.RouteId, key) ? key : _keyCache[provider.RouteId];
    }

    // MARK: - Projects

    /// <summary>Record a folder as recently opened (newest first, capped).</summary>
    public void TouchProject(string path)
    {
        var existing = RecentProjects.FirstOrDefault(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) RecentProjects.Remove(existing);
        RecentProjects.Insert(0, path);
        while (RecentProjects.Count > 12) RecentProjects.RemoveAt(RecentProjects.Count - 1);
    }

    public void ForgetProject(string path)
    {
        var existing = RecentProjects.FirstOrDefault(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) RecentProjects.Remove(existing);
    }

    /// <summary>Recent projects that still exist on disk.</summary>
    public IReadOnlyList<string> LiveRecentProjects => RecentProjects.Where(Directory.Exists).ToList();

    // MARK: - Skills

    public void SetSkillEnabled(string id, bool enabled)
    {
        if (enabled) DisabledSkills.Remove(id); else DisabledSkills.Add(id);
        OnPropertyChanged(nameof(DisabledSkills));
    }

    /// <summary>Remember (or forget) that <paramref name="chatId"/>'s task list should resume on launch.</summary>
    public void SetQueueResume(string chatId, bool resume)
    {
        if (resume ? QueueResumeChats.Add(chatId) : QueueResumeChats.Remove(chatId)) OnPropertyChanged(nameof(QueueResumeChats));
    }

    public SessionSkillSelection SkillsFor(string sessionId) =>
        SessionSkills.GetValueOrDefault(sessionId) ?? SessionSkillSelection.Default;

    public void SetSkillsFor(string sessionId, SessionSkillSelection? selection)
    {
        if (selection is null) SessionSkills.Remove(sessionId); else SessionSkills[sessionId] = selection;
        OnPropertyChanged(nameof(SessionSkills));
    }

    public void SetCodeState(string root, CodeState state)
    {
        CodeStates[root] = state;
        Persist();
    }

    public void SaveWindow(WindowPlacement placement)
    {
        Window = placement;
        Persist();
    }

    // MARK: - Persistence

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed class Stored
    {
        public List<ProviderProfile>? Providers { get; set; }
        public string? ActiveRoute { get; set; }
        public string? Preset { get; set; }
        public List<string>? RecentProjects { get; set; }
        public bool WizardCompleted { get; set; }
        public double? EditorFontSize { get; set; }
        public bool? EditorWraps { get; set; }
        public bool? EditorLineNumbers { get; set; }
        public double? TerminalFontSize { get; set; }
        public string? TerminalShell { get; set; }
        public string? AgentShell { get; set; }
        public string? LastMode { get; set; }
        public string? Theme { get; set; }
        public bool? CheckForUpdates { get; set; }
        public bool? ComputerToolsEnabled { get; set; }
        public int? SkillSources { get; set; }
        public bool? ExternalImportOffered { get; set; }
        public List<string>? DisabledSkills { get; set; }
        public Dictionary<string, SessionSkillSelection>? SessionSkills { get; set; }
        public Dictionary<string, CodeState>? CodeStates { get; set; }
        public string? SparkUrl { get; set; }
        public string? SparkUser { get; set; }
        public string? SparkPin { get; set; }
        public double? SidebarWidth { get; set; }
        public WindowPlacement? Window { get; set; }
        public List<string>? QueueResumeChats { get; set; }
        public bool? QueuePanelOpen { get; set; }
        public double? QueuePanelWidth { get; set; }
        public bool? PlanPanelOpen { get; set; }
        public double? PlanPanelWidth { get; set; }
        public bool? PlanPanelAutoOpen { get; set; }
        public bool? MemoryEnabled { get; set; }
        public bool? SubagentsUsePrimary { get; set; }
    }

    private void Load()
    {
        _loading = true;
        try
        {
            Stored? stored = null;
            try
            {
                if (File.Exists(_path)) stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(_path), Json);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                // A damaged settings file: keep a copy for the curious and start fresh.
                try { File.Copy(_path, _path + ".damaged", overwrite: true); } catch (Exception) { }
            }
            foreach (var p in stored?.Providers is { Count: > 0 } list ? list : ProviderProfile.Presets.ToList()) Providers.Add(p);
            ActiveRoute = stored?.ActiveRoute;
            Preset = stored?.Preset ?? PermissionPreset.WorkspaceWrite.RawValue();
            foreach (var p in stored?.RecentProjects ?? []) RecentProjects.Add(p);
            WizardCompleted = stored?.WizardCompleted ?? false;
            EditorFontSize = stored?.EditorFontSize ?? 13;
            EditorWraps = stored?.EditorWraps ?? false;
            EditorLineNumbers = stored?.EditorLineNumbers ?? true;
            TerminalFontSize = stored?.TerminalFontSize ?? 13;
            TerminalShell = stored?.TerminalShell ?? "";
            AgentShell = stored?.AgentShell ?? Dsh.Core.AgentShell.Auto;
            LastMode = stored?.LastMode ?? "chat";
            Theme = stored?.Theme ?? "system";
            CheckForUpdates = stored?.CheckForUpdates ?? true;
            ComputerToolsEnabled = stored?.ComputerToolsEnabled ?? true;
            SkillSourcesRaw = stored?.SkillSources ?? (int)SkillSources.All;
            ExternalImportOffered = stored?.ExternalImportOffered ?? false;
            foreach (var id in stored?.DisabledSkills ?? []) DisabledSkills.Add(id);
            foreach (var (k, v) in stored?.SessionSkills ?? []) SessionSkills[k] = v;
            foreach (var (k, v) in stored?.CodeStates ?? []) CodeStates[k] = v;
            SparkUrl = stored?.SparkUrl ?? "";
            SparkUser = stored?.SparkUser ?? "";
            SparkPin = stored?.SparkPin;
            SidebarWidth = stored?.SidebarWidth ?? 260;
            Window = stored?.Window;
            foreach (var id in stored?.QueueResumeChats ?? []) QueueResumeChats.Add(id);
            QueuePanelOpen = stored?.QueuePanelOpen ?? false;
            QueuePanelWidth = stored?.QueuePanelWidth ?? 360;
            PlanPanelOpen = stored?.PlanPanelOpen ?? false;
            PlanPanelWidth = stored?.PlanPanelWidth ?? 340;
            PlanPanelAutoOpen = stored?.PlanPanelAutoOpen ?? true;
            MemoryEnabled = stored?.MemoryEnabled ?? true;
            SubagentsUsePrimary = stored?.SubagentsUsePrimary ?? true;
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>Save soon: several changes in one UI tick are written once.</summary>
    public void Persist()
    {
        if (_loading) return;
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || !dispatcher.CheckAccess())
        {
            SaveNow();
            return;
        }
        if (_saveScheduled) return;
        _saveScheduled = true;
        dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _saveScheduled = false;
            SaveNow();
        });
    }

    public void SaveNow()
    {
        var stored = new Stored
        {
            Providers = Providers.Select(p => p.DeepCopy() with { ApiKey = null }).ToList(),
            ActiveRoute = ActiveRoute,
            Preset = Preset,
            RecentProjects = RecentProjects.ToList(),
            WizardCompleted = WizardCompleted,
            EditorFontSize = EditorFontSize,
            EditorWraps = EditorWraps,
            EditorLineNumbers = EditorLineNumbers,
            TerminalFontSize = TerminalFontSize,
            TerminalShell = TerminalShell,
            AgentShell = AgentShell,
            LastMode = LastMode,
            Theme = Theme,
            CheckForUpdates = CheckForUpdates,
            ComputerToolsEnabled = ComputerToolsEnabled,
            SkillSources = SkillSourcesRaw,
            ExternalImportOffered = ExternalImportOffered,
            DisabledSkills = DisabledSkills.OrderBy(s => s, StringComparer.Ordinal).ToList(),
            SessionSkills = new Dictionary<string, SessionSkillSelection>(SessionSkills),
            CodeStates = new Dictionary<string, CodeState>(CodeStates),
            SparkUrl = SparkUrl,
            SparkUser = SparkUser,
            SparkPin = SparkPin,
            SidebarWidth = SidebarWidth,
            Window = Window,
            QueueResumeChats = QueueResumeChats.Count == 0 ? null : QueueResumeChats.OrderBy(s => s, StringComparer.Ordinal).ToList(),
            QueuePanelOpen = QueuePanelOpen,
            QueuePanelWidth = QueuePanelWidth,
            PlanPanelOpen = PlanPanelOpen,
            PlanPanelWidth = PlanPanelWidth,
            PlanPanelAutoOpen = PlanPanelAutoOpen,
            MemoryEnabled = MemoryEnabled,
            SubagentsUsePrimary = SubagentsUsePrimary,
        };
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(stored, Json), TextUtil.Utf8NoBom);
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Settings are best effort; the app keeps running with what it has in memory.
        }
    }
}
