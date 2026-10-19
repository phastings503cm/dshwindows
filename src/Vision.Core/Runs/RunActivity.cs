namespace Vision.Core;

// MARK: - Chat tasks: the rules
//
// A chat's task list is what it is doing now — the plan of the running turn or /goal, always shown as the first task — and
// what is queued after it. A prompt added with "Add to tasks" runs in its chat as that chat's next prompt, exactly as if the
// person had sent it then (a /goal, a skill or a plain prompt), once the chat is free; the list keeps taking new tasks while
// it moves. These are the decisions, as pure functions both harnesses share (the same words as Vision for macOS).

public static class ChatTaskRules
{
    /// <summary>What a prompt task becomes once its turn is over.</summary>
    public static QueueTaskStatus Status(PromptOutcome outcome) => outcome switch
    {
        PromptOutcome.Completed or PromptOutcome.Issues or PromptOutcome.Unknown => QueueTaskStatus.Complete,
        PromptOutcome.NeedsYou => QueueTaskStatus.Blocked,
        PromptOutcome.Failed => QueueTaskStatus.Failed,
        PromptOutcome.Stopped => QueueTaskStatus.Skipped,
        PromptOutcome.Running => QueueTaskStatus.Running,
        _ => QueueTaskStatus.Queued,
    };

    /// <summary>Whether the next waiting task may start by itself after the current work ended this way. A Stop, a failure
    /// or a goal that needs the person pauses the list: the next prompt may depend on what didn't happen.</summary>
    public static bool ContinuesAfter(PromptOutcome outcome) =>
        outcome is PromptOutcome.Completed or PromptOutcome.Issues or PromptOutcome.Unknown;

    /// <summary>The paused list's banner.</summary>
    public static string PauseReason(PromptOutcome outcome) => outcome switch
    {
        PromptOutcome.Stopped => "Paused — you stopped the current work.",
        PromptOutcome.Failed => "Paused — the last task failed.",
        PromptOutcome.NeedsYou => "Paused — the agent needs you.",
        _ => "Paused.",
    };

    /// <summary>The banner after a relaunch: tasks added with "Add to tasks" never start on their own when Vision opens.</summary>
    public const string PausedAtLaunch = "Paused — Vision was restarted. Resume to carry on.";

    /// <summary>What the chat says when a prompt goes on its list.</summary>
    public static string AddedNotice(int position, bool startsNow) => startsNow
        ? $"📋 Added to this chat's tasks (#{position}) — it starts now."
        : $"📋 Added to this chat's tasks (#{position}) — it starts when the current work finishes.";

    /// <summary>A task's title from its prompt: the first line, at most 80 characters.</summary>
    public static string TitleFor(string prompt)
    {
        var text = prompt.Trim();
        var first = text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? text;
        if (first.Length > 80) first = first[..79] + "…";
        return first.Length == 0 ? "Untitled task" : first;
    }

    /// <summary>A task's title from its prompt, or — for one queued with only attachments (a large paste, a PDF) — from
    /// what it carries: "📎 Pasted text, report.pdf".</summary>
    public static string TitleFor(string prompt, IReadOnlyList<string> attachmentNames)
    {
        if (prompt.Trim().Length > 0 || attachmentNames.Count == 0) return TitleFor(prompt);
        return TitleFor("📎 " + string.Join(", ", attachmentNames));
    }

    /// <summary>What the chat says when a prompt with attachments goes on its list (after <see cref="AddedNotice"/>).</summary>
    public static string CarriesNotice(int count) =>
        count == 1 ? "Its attachment goes with it." : $"Its {count} attachments go with it.";
}

// MARK: - Which plan step a subagent works

/// <summary>A plan's step for a subagent: so the plan can show who is working each step (name and model), and the background
/// activity menu what each subagent was assigned.</summary>
public static class PlanAssignment
{
    /// <summary>The step a subagent named <paramref name="description"/> (asked to do <paramref name="prompt"/>) is working:
    /// the open step its words match best; with no match, the step in progress when it started (when exactly one is); else
    /// none.</summary>
    public static string? Step(string description, string prompt, IReadOnlyList<TodoItem> todos)
    {
        var open = todos.Where(t => t.Status != TodoStatus.Completed).ToList();
        if (open.Count == 0) return null;
        var mine = SignificantWords(description + " " + (prompt.Length > 400 ? prompt[..400] : prompt));
        TodoItem? best = null;
        var bestScore = 0;
        foreach (var step in open)
        {
            var score = SignificantWords(step.Content).Intersect(mine).Count();
            if (score > 0 && score > bestScore)
            {
                best = step;
                bestScore = score;
            }
        }
        if (best is not null) return best.Content;
        var inProgress = open.Where(t => t.Status == TodoStatus.InProgress).ToList();
        return inProgress.Count == 1 ? inProgress[0].Content : null;
    }

    private static readonly HashSet<string> Stop = new(StringComparer.Ordinal)
    {
        "the", "and", "for", "with", "that", "this", "from", "into", "each", "every", "then",
        "file", "files", "code", "make", "sure", "about", "their", "there", "where", "which",
    };

    public static HashSet<string> SignificantWords(string text)
    {
        var words = new List<string>();
        var current = new System.Text.StringBuilder();
        foreach (var c in text.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c) || c is '_' or '-') current.Append(c);
            else Flush();
        }
        Flush();
        return words.Where(w => w.Length >= 4 && !Stop.Contains(w)).Select(StemOf).ToHashSet(StringComparer.Ordinal);

        void Flush()
        {
            if (current.Length > 0) words.Add(current.ToString());
            current.Clear();
        }
    }

    /// <summary>A crude stem, so "tests"/"test" and "parsing"/"parser"/"parse" meet.</summary>
    private static string StemOf(string word)
    {
        foreach (var suffix in new[] { "ing", "ers", "er", "es", "s", "ed", "e" })
            if (word.Length > suffix.Length + 3 && word.EndsWith(suffix, StringComparison.Ordinal)) return word[..^suffix.Length];
        return word;
    }
}

// MARK: - Background activity

/// <summary>What one line of the background-activity menu is.</summary>
public enum ActivityKind
{
    /// <summary>A chat's main agent, working a turn, a /goal or a task.</summary>
    MainAgent,
    /// <summary>A subagent working inside the main agent's tool call.</summary>
    Subagent,
    /// <summary>A subagent working in the background while the chat carries on.</summary>
    BackgroundAgent,
    /// <summary>A chat's task list, being worked.</summary>
    QueueRunner,
    /// <summary>Tasks waiting in a chat that isn't working its list.</summary>
    ChatTasks,
}

/// <summary>One line of the background-activity menu: what is working, on which model and server, assigned to what, for how
/// long, and what it used.</summary>
public sealed record ActivityItem(string Id, ActivityKind Kind, string? ChatId, string ChatTitle, string Title)
{
    /// <summary>What it was given: the plan step, the prompt, the task.</summary>
    public string? Assignment { get; init; }
    /// <summary>"Qwen3-Coder-30B on DGX Spark".</summary>
    public string? Placement { get; init; }
    public string Status { get; init; } = "Running";
    /// <summary>What it is doing this moment ("grep: TODO").</summary>
    public string? Activity { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public bool Running { get; init; } = true;
    public long? Tokens { get; init; }
    public int? Steps { get; init; }
    public string? AgentRunId { get; init; }
    public string? BackgroundJobId { get; init; }

    public bool CanStop => Kind != ActivityKind.ChatTasks || Running;
}

/// <summary>A chat as the activity menu sees it (the host fills it in on the UI thread).</summary>
public sealed record ChatActivity(string ChatId, string Title)
{
    public bool Running { get; init; }
    public bool Stopping { get; init; }
    public TurnPhaseState? Phase { get; init; }
    public int? GoalRound { get; init; }
    /// <summary>"Goal", "Task", "Prompt", "Skill" and what it is.</summary>
    public string? WorkKind { get; init; }
    public string? WorkTitle { get; init; }
    public DateTimeOffset? WorkStartedAt { get; init; }
    /// <summary>The model and route the main agent's current turn runs on.</summary>
    public string? Model { get; init; }
    public string? Route { get; init; }
    public string? RunningTool { get; init; }
    public string? ActivityLine { get; init; }
    public long Tokens { get; init; }
    public IReadOnlyList<AgentRunInfo> Agents { get; init; } = [];
    public IReadOnlyList<BackgroundAgentJob> RunningJobs { get; init; } = [];
    /// <summary>The chat's task list is being worked (or winding down from a Stop).</summary>
    public bool ListRunning { get; init; }
    public bool ListStopping { get; init; }
    public QueueTask? ListActiveTask { get; init; }
    public int QueuedTasks { get; init; }
    public string? NextTaskTitle { get; init; }
    public string? PausedReason { get; init; }
}

public static class RunActivity
{
    /// <summary>Everything working right now, across chats: each chat's main agent, its subagents (foreground and
    /// background), its task list being worked, and the chats with tasks waiting.</summary>
    public static IReadOnlyList<ActivityItem> Build(IEnumerable<ChatActivity> chats)
    {
        var items = new List<ActivityItem>();
        foreach (var chat in chats)
        {
            if (chat.Running)
            {
                var status = chat.Stopping ? "Stopping…"
                    : (chat.Phase is { } phase ? phase.IsOver ? "Finishing" : phase.Phase.Label() : "Working")
                      + (chat.GoalRound is { } round ? $" · round {round}" : "");
                items.Add(new ActivityItem($"main-{chat.ChatId}", ActivityKind.MainAgent, chat.ChatId, chat.Title, "Main agent")
                {
                    Assignment = chat.WorkTitle is null ? null : $"{chat.WorkKind ?? "Prompt"}: {chat.WorkTitle}",
                    Placement = Placement(chat.Model, chat.Route),
                    Status = status,
                    Activity = chat.RunningTool ?? chat.ActivityLine,
                    StartedAt = chat.WorkStartedAt,
                    Tokens = chat.Tokens,
                    Steps = chat.Phase?.Steps.Count,
                });
            }
            foreach (var run in chat.Agents.Where(a => a.IsRunning))
            {
                items.Add(new ActivityItem($"agent-{chat.ChatId}-{run.Id}", run.Background ? ActivityKind.BackgroundAgent : ActivityKind.Subagent,
                    chat.ChatId, chat.Title, $"{run.Description} ({run.AgentType})")
                {
                    Assignment = run.Assignment is null ? null : "Plan step: " + run.Assignment,
                    Placement = run.PlacementLabel ?? "waiting for a model server",
                    Status = run.Placement is { } note ? "Running · " + note : "Running",
                    Activity = run.Activity,
                    StartedAt = run.StartedAt,
                    Tokens = run.TotalTokens,
                    Steps = run.Steps,
                    AgentRunId = run.Id,
                });
            }
            // A background job the roster doesn't know (shouldn't happen — but never hide work).
            foreach (var job in chat.RunningJobs.Where(j => !chat.Agents.Any(a => a.IsRunning && a.Background && a.Description == j.Description)))
            {
                items.Add(new ActivityItem($"job-{chat.ChatId}-{job.Id}", ActivityKind.BackgroundAgent, chat.ChatId, chat.Title, job.Description)
                {
                    StartedAt = job.StartedAt,
                    BackgroundJobId = job.Id,
                });
            }
            if (chat.ListRunning || chat.ListStopping)
            {
                var active = chat.ListActiveTask;
                items.Add(new ActivityItem($"list-{chat.ChatId}", ActivityKind.QueueRunner, chat.ChatId, chat.Title, "Task list")
                {
                    Assignment = active is null ? null : "Working: " + active.Title,
                    Placement = Placement(chat.Model, chat.Route),
                    Status = chat.ListStopping ? "Stopping…" : $"{chat.QueuedTasks} waiting",
                    StartedAt = active?.StartedAt,
                    Tokens = active?.TotalTokens,
                    Steps = active?.Rounds,
                });
            }
            else if (chat.QueuedTasks > 0)
            {
                items.Add(new ActivityItem($"tasks-{chat.ChatId}", ActivityKind.ChatTasks, chat.ChatId, chat.Title,
                    $"{chat.QueuedTasks} task{(chat.QueuedTasks == 1 ? "" : "s")} up next")
                {
                    Assignment = chat.NextTaskTitle is null ? null : "Next: " + chat.NextTaskTitle,
                    Status = chat.PausedReason ?? (chat.Running ? "Starts when the current work finishes" : "Waiting — press Start in the task list"),
                    Running = false,
                });
            }
        }
        return items;
    }

    /// <summary>How many things are working now (the toolbar's count): main agents, subagents and task lists being worked.</summary>
    public static int Count(IEnumerable<ActivityItem> items) =>
        items.Count(i => i.Running && i.Kind is not ActivityKind.ChatTasks);

    public static string? Placement(string? model, string? route) => (model, route) switch
    {
        ({ Length: > 0 } m, { Length: > 0 } r) => $"{m} on {r}",
        ({ Length: > 0 } m, _) => m,
        (_, { Length: > 0 } r) => r,
        _ => null,
    };

    /// <summary>The glyph name family for each kind (the views map it to their icon font).</summary>
    public static string KindLabel(ActivityKind kind) => kind switch
    {
        ActivityKind.MainAgent => "Main agent",
        ActivityKind.Subagent => "Subagent",
        ActivityKind.BackgroundAgent => "Background agent",
        ActivityKind.QueueRunner => "Task list",
        _ => "Waiting tasks",
    };
}
