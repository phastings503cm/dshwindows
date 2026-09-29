using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Dsh.App.Model.Code;
using Dsh.Core;
using Microsoft.Win32;

namespace Dsh.App.Model;

/// <summary>Which half of the window is showing.</summary>
public enum WorkspaceMode { Chat, Code }

public enum SettingsTab { General, Models, Spark, Skills, Plugins, Editor }

public enum SkillsAction { Generate, Import, NewManual }

/// <summary>Root-level state: the config, the host that drives sessions, the current project
/// folder, and the code-mode workspace.</summary>
public sealed partial class AppModel : ObservableObject
{
    public AppConfig Config { get; }
    public AgentHost Host { get; }
    /// <summary>The DGX Spark's model switcher.</summary>
    public SparkController Spark { get; }
    /// <summary>File tree, open editors, and terminals for the current project.</summary>
    public CodeWorkspace Code { get; }

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsChatMode), nameof(IsCodeMode))] private WorkspaceMode _mode;
    /// <summary>The folder both modes operate in.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ProjectName))] private string? _project;
    /// <summary>A newer release is available (shown as a banner until dismissed).</summary>
    [ObservableProperty] private UpdateInfo? _update;

    public sealed record UpdateInfo(string Version, string Url);

    /// <summary>Views ask the shell to open windows through these.</summary>
    public event Action<SettingsTab, SkillsAction?>? SettingsRequested;
    public event Action? WizardRequested;
    public event Action? MemoryRequested;
    public event Action<string>? ImageRequested;

    public AppModel(AppConfig config, ConversationLog log, Dispatcher dispatcher)
    {
        Config = config;
        Host = new AgentHost(config, log, dispatcher);
        Spark = new SparkController(config, Host);
        Code = new CodeWorkspace(config, dispatcher);
        _mode = config.LastMode == "code" ? WorkspaceMode.Code : WorkspaceMode.Chat;

        Host.IsServerSwitching = () => Spark.IsSwitching;
        Host.OnSwapCommand = (arg, vm) => _ = Spark.HandleCommandAsync(arg, vm);
        // The agent's file writes drive the editor's live reload.
        Host.OnFilesChanged = changes => Code.ApplyExternalChanges(changes);
        Code.ImageRequested += path => ImageRequested?.Invoke(path);

        // Reopen the most recent project so the app comes back where it left off.
        if (config.LiveRecentProjects.FirstOrDefault() is { } recent) OpenProject(recent, activateSession: false);
    }

    public bool IsChatMode => Mode == WorkspaceMode.Chat;
    public bool IsCodeMode => Mode == WorkspaceMode.Code;
    public string? ProjectName => Project is null ? null : Path.GetFileName(Project.TrimEnd('\\', '/'));
    public SessionVM? SelectedSession => Host.Selected;
    public bool NeedsSetup => !Config.WizardCompleted || !Config.IsConfigured;

    partial void OnModeChanged(WorkspaceMode value) => Config.LastMode = value == WorkspaceMode.Code ? "code" : "chat";

    public void Start()
    {
        Spark.StartMonitoring();
        Host.EnsureContextProbe();
        // An interrupted task queue (crash, restart, quit) resumes by itself; one the user stopped
        // on purpose does not.
        Host.ResumeQueueIfNeeded();
    }

    // MARK: - Projects

    /// <summary>Ask for a folder and adopt it.</summary>
    public void ChooseProject()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose the folder the agent should work in",
            Multiselect = false,
        };
        if (Project is not null && Directory.Exists(Project)) dialog.InitialDirectory = Project;
        var owner = Application.Current?.MainWindow;
        if ((owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner)) == true) OpenProject(dialog.FolderName);
    }

    /// <summary>Adopt a folder as the project: it becomes the agent's working directory, the
    /// permission boundary, and the root of the file tree.</summary>
    public void OpenProject(string path, bool activateSession = true)
    {
        path = Path.GetFullPath(path);
        if (!Directory.Exists(path))
        {
            Config.ForgetProject(path);
            Host.Banner = $"{path} no longer exists.";
            return;
        }
        Project = path;
        Config.TouchProject(path);
        Host.AdoptProject(path);
        Code.Open(path);

        if (!activateSession) return;
        // Return to this project's most recent chat rather than piling up empty ones.
        if (Host.Sessions.FirstOrDefault(s => SamePath(s.Cwd, path)) is { } existing) Select(existing.Id);
        else NewChat();
    }

    public void CloseProject()
    {
        if (!Code.ConfirmCloseAll()) return;
        Project = null;
        Host.AdoptProject(null);
        Code.Close();
    }

    public void ForgetProject(string path)
    {
        Config.ForgetProject(path);
        if (SamePath(Project, path)) CloseProject();
    }

    private static bool SamePath(string? a, string? b) =>
        a is not null && b is not null &&
        string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    // MARK: - Sessions

    public void Select(string? id)
    {
        Host.SelectedId = id;
        if (Host.Selected is not { } session) return;
        Host.Hydrate(session);
        // Make sure the context gauge has a number before the user asks.
        Host.EnsureContextProbe();
        // Following a chat into its project keeps both modes in step.
        if (session.Cwd is { } cwd && !SamePath(cwd, Project) && Directory.Exists(cwd)) OpenProject(cwd, activateSession: false);
    }

    public SessionVM NewChat()
    {
        var session = Host.NewSession(Project);
        Host.EnsureContextProbe();
        return session;
    }

    public void Send(string text, SessionVM session, IReadOnlyList<MessageAttachment>? attachments = null) =>
        Host.Send(text, session.Id, attachments);

    // MARK: - Stopping

    public void StopSelected()
    {
        if (SelectedSession is { Running: true } session) Host.StopSession(session.Id);
    }

    public void StopAll() => Host.StopAll();

    /// <summary>App exit: cancel everything without marking the queue as stopped by the user.</summary>
    public void Shutdown() => Host.Shutdown();

    /// <summary>Anything working — a turn, a queue task, or a background agent.</summary>
    public bool AnythingRunning => Host.AnythingRunning;

    /// <summary>Jump to a chat by id (the queue's "Open chat").</summary>
    public void OpenSession(string id)
    {
        Mode = WorkspaceMode.Chat;
        Select(id);
    }

    // MARK: - Windows

    public void ShowSettings(SettingsTab tab = SettingsTab.General, SkillsAction? action = null) => SettingsRequested?.Invoke(tab, action);
    public void ShowWizard() => WizardRequested?.Invoke();
    public void ShowMemory() => MemoryRequested?.Invoke();
}
