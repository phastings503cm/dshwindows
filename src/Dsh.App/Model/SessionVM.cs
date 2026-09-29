using System.Collections.ObjectModel;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Dsh.App.Infrastructure;
using Dsh.Core;

namespace Dsh.App.Model;

public enum MessageRole { User, Assistant, Notice, Error }

/// <summary>One row of the transcript. Messages, tool calls, todo snapshots, and compaction markers
/// share one ordered list so a tool call renders exactly where it happened — between the assistant
/// text that preceded it and the text that followed.</summary>
public abstract partial class ChatEntryVM : ObservableObject
{
    public string Id { get; }
    public DateTimeOffset At { get; }

    protected ChatEntryVM(string? id, DateTimeOffset? at)
    {
        Id = id ?? Guid.NewGuid().ToString("N");
        At = at ?? DateTimeOffset.Now;
    }
}

/// <summary>A chat message in the transcript.</summary>
public sealed partial class MessageEntryVM(MessageRole role, string text, string? id = null, DateTimeOffset? at = null)
    : ChatEntryVM(id, at)
{
    public MessageRole Role { get; } = role;
    [ObservableProperty] private string _text = text;

    public bool IsUser => Role == MessageRole.User;
    public bool IsAssistant => Role == MessageRole.Assistant;
    public bool IsNotice => Role == MessageRole.Notice;
    public bool IsError => Role == MessageRole.Error;
}

/// <summary>One tool call, from "started" through to its result.</summary>
public sealed partial class ToolEntryVM(string name, string preview, string? id = null, DateTimeOffset? at = null)
    : ChatEntryVM(id, at)
{
    public string Name { get; } = name;
    /// <summary>The interesting argument — a path, a command, a pattern.</summary>
    public string Preview { get; } = preview;
    /// <summary>One-line headline of the result.</summary>
    [ObservableProperty] private string? _summary;
    /// <summary>The full result, revealed on demand.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasOutput))] private string? _output;
    /// <summary>null while the call is still running.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsFinished), nameof(IsRunning), nameof(Failed))] private bool? _isOk;
    [ObservableProperty] private bool _isExpanded;

    /// <summary>What the tool captured (screenshots), as thumbnails — kept for the live session only.</summary>
    public ObservableCollection<BitmapSource> Images { get; } = [];

    public bool IsFinished => IsOk is not null;
    public bool IsRunning => IsOk is null;
    public bool Failed => IsOk == false;
    public bool HasOutput => !string.IsNullOrEmpty(Output);
    public string Label => Icons.ToolLabel(Name);
    public string Glyph => Icons.ForTool(Name);
}

/// <summary>The agent's task list.</summary>
public sealed class TodosEntryVM(IReadOnlyList<TodoItem> items) : ChatEntryVM(null, null)
{
    public IReadOnlyList<TodoItem> Items { get; } = items;
    public string Progress => $"{Items.Count(i => i.Status == TodoStatus.Completed)}/{Items.Count}";
}

/// <summary>A record that the older part of a conversation was summarized to fit the context
/// window. Rendered as a divider; expands to show the summary the model now carries.</summary>
public sealed partial class CompactionEntryVM(int removed, string summary, DateTimeOffset? at = null) : ChatEntryVM(null, at)
{
    public int Removed { get; } = removed;
    public string Summary { get; } = summary;
    [ObservableProperty] private bool _isExpanded;
}

/// <summary>A pending permission question surfaced by the engine.</summary>
public sealed record GateVM(string Id, string Name, string Detail)
{
    public string ToolLabel => Icons.ToolLabel(Name);
}

public sealed record GoalState(string Text, int Round, DateTimeOffset Started);

/// <summary>The model didn't answer; the engine is waiting to try again.</summary>
public sealed record RetryState(int Attempt, string Reason, DateTimeOffset NextAttempt);

/// <summary>Observable state for one agent session, driven by <see cref="AgentHost"/>. UI-thread only.</summary>
public sealed partial class SessionVM : ObservableObject
{
    public string Id { get; }
    [ObservableProperty] private string _title;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ProjectName), nameof(WorkspacePath))] private string? _cwd;
    [ObservableProperty] private DateTimeOffset _updatedAt = DateTimeOffset.Now;
    /// <summary>Pinned when the session is created, like the preset a chat runs under.</summary>
    [ObservableProperty] private PermissionPreset _preset;

    public ObservableCollection<ChatEntryVM> Entries { get; } = [];
    public ObservableCollection<GateVM> PendingGates { get; } = [];
    /// <summary>Files this session's tools touched, newest first — shown as chips.</summary>
    public ObservableCollection<FileChange> ChangedFiles { get; } = [];

    [ObservableProperty] private IReadOnlyList<TodoItem> _todos = [];
    [ObservableProperty] private LlmUsage? _lastUsage;
    /// <summary>Best-known size of the current context, in tokens.</summary>
    [ObservableProperty] private int? _contextUsed;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsIdle))] private bool _running;
    [ObservableProperty] private bool _stopping;
    /// <summary>This chat's thinking level; null = the provider's default.</summary>
    [ObservableProperty] private ThinkingLevel? _thinking;
    /// <summary>The model's live reasoning for the current step (not persisted).</summary>
    [ObservableProperty] private string _reasoning = "";
    [ObservableProperty] private DateTimeOffset? _reasoningStarted;
    /// <summary>A transient status line ("Compacting conversation…") shown while busy.</summary>
    [ObservableProperty] private string? _activity;
    /// <summary>The active /goal, while its loop runs.</summary>
    [ObservableProperty] private GoalState? _goal;
    /// <summary>Tool call in flight — the composer shows what is happening.</summary>
    [ObservableProperty] private ToolEntryVM? _runningTool;
    /// <summary>Set while a failed model call waits to be retried.</summary>
    [ObservableProperty] private RetryState? _retry;
    /// <summary>The last /goal of this chat, so a bare /goal picks it back up.</summary>
    [ObservableProperty] private string? _lastGoal;
    /// <summary>Background subagents this chat launched (the bar above the composer).</summary>
    public ObservableCollection<BackgroundAgentJob> BackgroundJobs { get; } = [];
    public IReadOnlyList<BackgroundAgentJob> RunningBackgroundJobs =>
        BackgroundJobs.Where(j => j.Status == BackgroundAgentStatus.Running).ToList();

    /// <summary>Id of the assistant entry currently receiving streamed deltas.</summary>
    public string? StreamingId { get; private set; }

    /// <summary>Raised whenever the transcript grows or the streaming message changes, so the view
    /// can keep the bottom in sight.</summary>
    public event Action? ContentChanged;

    public SessionVM(string id, string title, string? cwd, PermissionPreset preset = PermissionPreset.WorkspaceWrite)
    {
        Id = id;
        _title = title;
        _cwd = cwd;
        _preset = preset;
    }

    public bool IsIdle => !Running;
    public bool IsEmpty => Entries.Count == 0;
    public string? WorkspacePath => string.IsNullOrEmpty(Cwd) ? null : Cwd;
    public string? ProjectName => WorkspacePath is { } p ? System.IO.Path.GetFileName(p.TrimEnd('\\', '/')) : null;
    public string UpdatedLabel => Formatting.Relative(UpdatedAt);

    partial void OnUpdatedAtChanged(DateTimeOffset value) => OnPropertyChanged(nameof(UpdatedLabel));

    /// <summary>Refresh the "5m ago" label.</summary>
    public void RefreshRelativeTime() => OnPropertyChanged(nameof(UpdatedLabel));

    // MARK: - Reasoning

    public void AppendReasoning(string chunk)
    {
        if (Reasoning.Length == 0) ReasoningStarted = DateTimeOffset.Now;
        var next = Reasoning + chunk;
        if (next.Length > 6_000) next = TextUtil.Suffix(next, 4_000);
        Reasoning = next;
    }

    public void ClearReasoning()
    {
        if (Reasoning.Length > 0) Reasoning = "";
        ReasoningStarted = null;
    }

    // MARK: - Mutation

    public string AppendMessage(MessageRole role, string text, DateTimeOffset? at = null)
    {
        var entry = new MessageEntryVM(role, text, at: at);
        Entries.Add(entry);
        UpdatedAt = DateTimeOffset.Now;
        ContentChanged?.Invoke();
        return entry.Id;
    }

    public void Note(string text, MessageRole role = MessageRole.Notice) => AppendMessage(role, text);

    /// <summary>Append streamed assistant text, opening a new bubble if none is live.</summary>
    public void AppendDelta(string chunk)
    {
        if (StreamingId is not null && Entries.LastOrDefault(e => e.Id == StreamingId) is MessageEntryVM { IsAssistant: true } live)
        {
            live.Text += chunk;
            ContentChanged?.Invoke();
        }
        else
        {
            StreamingId = AppendMessage(MessageRole.Assistant, chunk);
        }
        UpdatedAt = DateTimeOffset.Now;
    }

    /// <summary>Drop the live bubble: a failed attempt's partial reply is void, the retry streams the
    /// whole reply again.</summary>
    public void DropStreaming()
    {
        if (StreamingId is not null && Entries.LastOrDefault(e => e.Id == StreamingId) is MessageEntryVM message)
            Entries.Remove(message);
        StreamingId = null;
        ContentChanged?.Invoke();
    }

    /// <summary>Add or refresh a background agent's row.</summary>
    public void UpsertBackgroundJob(BackgroundAgentJob job)
    {
        var index = -1;
        for (var i = 0; i < BackgroundJobs.Count; i++)
        {
            if (BackgroundJobs[i].Id == job.Id)
            {
                index = i;
                break;
            }
        }
        if (index >= 0) BackgroundJobs[index] = job;
        else BackgroundJobs.Add(job);
        OnPropertyChanged(nameof(RunningBackgroundJobs));
    }

    /// <summary>Close the streaming bubble so the next text starts a fresh one (a tool call in
    /// between is what makes this matter). An empty bubble left by a tool-only turn is dropped.</summary>
    public void EndStreaming()
    {
        if (StreamingId is not null
            && Entries.LastOrDefault(e => e.Id == StreamingId) is MessageEntryVM message
            && message.Text.Trim().Length == 0)
        {
            Entries.Remove(message);
        }
        StreamingId = null;
    }

    public void StartTool(string id, string name, string preview, DateTimeOffset? at = null)
    {
        EndStreaming();
        var entry = new ToolEntryVM(name, preview, id, at);
        Entries.Add(entry);
        RunningTool = entry;
        UpdatedAt = DateTimeOffset.Now;
        ContentChanged?.Invoke();
    }

    public void FinishTool(string id, bool ok, string summary, string? output)
    {
        if (Entries.LastOrDefault(e => e.Id == id) is not ToolEntryVM tool) return;
        tool.Summary = summary;
        tool.Output = output;
        tool.IsOk = ok;
        if (ReferenceEquals(RunningTool, tool)) RunningTool = null;
        UpdatedAt = DateTimeOffset.Now;
        ContentChanged?.Invoke();
    }

    /// <summary>Show what a tool captured on its card. Only the newest cards keep their pictures.</summary>
    public void AttachImages(string id, IEnumerable<BitmapSource> images)
    {
        if (Entries.LastOrDefault(e => e.Id == id) is not ToolEntryVM tool) return;
        tool.Images.Clear();
        foreach (var image in images) tool.Images.Add(image);
        var kept = 0;
        for (var i = Entries.Count - 1; i >= 0; i--)
        {
            if (Entries[i] is not ToolEntryVM { Images.Count: > 0 } card) continue;
            if (++kept > 30) card.Images.Clear();
        }
        ContentChanged?.Invoke();
    }

    /// <summary>Record a completed tool in one step (replaying a stored log).</summary>
    public void AddFinishedTool(string id, string name, string preview, string? summary, string? output, bool ok, DateTimeOffset at)
    {
        Entries.Add(new ToolEntryVM(name, preview, id, at) { Summary = summary, Output = output, IsOk = ok });
    }

    public void SetTodos(IReadOnlyList<TodoItem> items)
    {
        Todos = items;
        // Keep one todo card, at the point the list last changed.
        foreach (var old in Entries.OfType<TodosEntryVM>().ToList()) Entries.Remove(old);
        if (items.Count == 0) return;
        EndStreaming();
        Entries.Add(new TodosEntryVM(items));
        UpdatedAt = DateTimeOffset.Now;
        ContentChanged?.Invoke();
    }

    public void RecordFileChanges(IEnumerable<FileChange> changes)
    {
        foreach (var change in changes)
        {
            foreach (var existing in ChangedFiles.Where(c => string.Equals(c.Path, change.Path, StringComparison.OrdinalIgnoreCase)).ToList())
                ChangedFiles.Remove(existing);
            ChangedFiles.Insert(0, change);
        }
        while (ChangedFiles.Count > 40) ChangedFiles.RemoveAt(ChangedFiles.Count - 1);
        ContentChanged?.Invoke();
    }

    public void NotifyContentChanged() => ContentChanged?.Invoke();
}
