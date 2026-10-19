namespace Vision.Core.Tests;

/// <summary>The chat-task rules, which plan step a subagent works, and the background-activity list.</summary>
public sealed class RunActivityTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 18, 9, 0, 0, TimeSpan.Zero);

    // MARK: Chat tasks

    [Theory]
    [InlineData(PromptOutcome.Completed, QueueTaskStatus.Complete, true)]
    [InlineData(PromptOutcome.Issues, QueueTaskStatus.Complete, true)]
    [InlineData(PromptOutcome.Unknown, QueueTaskStatus.Complete, true)]
    [InlineData(PromptOutcome.NeedsYou, QueueTaskStatus.Blocked, false)]
    [InlineData(PromptOutcome.Failed, QueueTaskStatus.Failed, false)]
    [InlineData(PromptOutcome.Stopped, QueueTaskStatus.Skipped, false)]
    public void APromptTasksEndingDecidesItsStatusAndWhetherTheListMovesOn(PromptOutcome outcome, QueueTaskStatus status, bool continues)
    {
        Assert.Equal(status, ChatTaskRules.Status(outcome));
        Assert.Equal(continues, ChatTaskRules.ContinuesAfter(outcome));
    }

    [Fact]
    public void ThePauseBannersAreTheMacs()
    {
        Assert.Equal("Paused — you stopped the current work.", ChatTaskRules.PauseReason(PromptOutcome.Stopped));
        Assert.Equal("Paused — the last task failed.", ChatTaskRules.PauseReason(PromptOutcome.Failed));
        Assert.Equal("Paused — the agent needs you.", ChatTaskRules.PauseReason(PromptOutcome.NeedsYou));
        Assert.Equal("Paused — Vision was restarted. Resume to carry on.", ChatTaskRules.PausedAtLaunch);
    }

    [Fact]
    public void ATasksTitleIsItsPromptsFirstLine()
    {
        Assert.Equal("Fix the parser", ChatTaskRules.TitleFor("\n  Fix the parser  \nand the tests"));
        Assert.Equal(80, ChatTaskRules.TitleFor(new string('x', 200)).Length);
        Assert.EndsWith("…", ChatTaskRules.TitleFor(new string('x', 200)));
        Assert.Equal("Untitled task", ChatTaskRules.TitleFor("   "));
        Assert.Equal("📋 Added to this chat's tasks (#2) — it starts when the current work finishes.", ChatTaskRules.AddedNotice(2, startsNow: false));
    }

    [Fact]
    public void APromptTaskIsKeptAsOneInTheQueueFile()
    {
        using var dir = new TempDirectory("vision-prompt-task");
        var path = dir["task-queue.json"];
        var queue = new TaskQueue(path);
        var prompt = queue.Add(ChatTaskRules.TitleFor("/goal Ship it\nsoon"), "/goal Ship it\nsoon", chatId: "c1", asPrompt: true);
        var plain = queue.Add("Write tests", "all of them", chatId: "c1");
        Assert.Equal("/goal Ship it\nsoon", prompt.PromptText);  // sent exactly as written
        Assert.Equal("Write tests\n\nall of them", plain.PromptText);
        Assert.Contains("\"asPrompt\": true", File.ReadAllText(path));
        var back = new TaskQueue(path);
        Assert.True(back.Find(prompt.Id)!.AsPrompt);
        Assert.False(back.Find(plain.Id)!.AsPrompt);
        Assert.Equal(prompt, back.Find(prompt.Id));
    }

    // MARK: Plan assignment

    private static readonly TodoItem[] Plan =
    [
        new("1", "Read how stock levels are stored", TodoStatus.Completed),
        new("2", "Write parser tests for nested quotes", TodoStatus.InProgress),
        new("3", "Update the README build section"),
    ];

    [Fact]
    public void ASubagentWorksTheOpenStepItsWordsMatchBest()
    {
        Assert.Equal("Write parser tests for nested quotes", PlanAssignment.Step("Test the parser", "Write tests covering nested quotes", Plan));
        Assert.Equal("Update the README build section", PlanAssignment.Step("Docs", "Rewrite the README's build instructions", Plan));
        // A finished step is never assigned.
        Assert.NotEqual("Read how stock levels are stored", PlanAssignment.Step("Stock levels", "Read the stock levels", Plan));
    }

    [Fact]
    public void WithNoMatchItWorksTheStepInProgress()
    {
        Assert.Equal("Write parser tests for nested quotes", PlanAssignment.Step("scan", "look around", Plan));
        Assert.Null(PlanAssignment.Step("scan", "look around", [new("1", "a", TodoStatus.InProgress), new("2", "b", TodoStatus.InProgress)]));
        Assert.Null(PlanAssignment.Step("scan", "look", []));
    }

    [Fact]
    public void ATieIsNeverGuessedAndTheStepArgumentNamesTheStep()
    {
        // The review's probe: both steps share two words with the subagent; the first used to win.
        TodoItem[] plan = [new("1", "Write the parser", TodoStatus.InProgress), new("2", "Add tests for the parser")];
        Assert.Null(PlanAssignment.Step("Write tests for parser", "Write unit tests covering the parser's edge cases", plan));
        // What only one step says still decides a tie.
        TodoItem[] distinct = [new("1", "Parse the config", TodoStatus.InProgress), new("2", "Parse the command line"), new("3", "Validate the config")];
        Assert.Equal("Parse the command line", PlanAssignment.Step("Parse", "the command config", distinct));
        // The agent tool's step: a number (1 = first), the step's text, or a part only that step has.
        Assert.Equal("Add tests for the parser", PlanAssignment.Step("2", "Write tests for parser", "…", plan));
        Assert.Equal("Add tests for the parser", PlanAssignment.Step("#2", "x", "y", plan));
        Assert.Equal("Add tests for the parser", PlanAssignment.Step("add tests for the parser", "x", "y", plan));
        Assert.Equal("Add tests for the parser", PlanAssignment.Step("tests for", "x", "y", plan));
        // Naming nothing (or two steps) falls back to the guess.
        Assert.Null(PlanAssignment.Named("parser", plan));
        Assert.Null(PlanAssignment.Named("9", plan));
        Assert.Equal("Write the parser", PlanAssignment.Step("9", "scan", "look around", plan)); // the one in progress
        Assert.Equal("Add tests for the parser", PlanAssignment.Step(null, "tests", "add some tests", plan));
    }

    [Fact]
    public void TheStepArgumentIsReadAsANumberOrText()
    {
        Assert.Equal("2", AgentTool.StepArgument(JsonArgs.Object("""{"step":2}""")));
        Assert.Equal("3", AgentTool.StepArgument(JsonArgs.Object("""{"step":3.0}""")));
        Assert.Equal("Add tests", AgentTool.StepArgument(JsonArgs.Object("""{"step":"  Add tests "}""")));
        Assert.Null(AgentTool.StepArgument(JsonArgs.Object("""{"step":""}""")));
        Assert.Null(AgentTool.StepArgument(JsonArgs.Object("""{"step":true}""")));
        Assert.Null(AgentTool.StepArgument(JsonArgs.Object("{}")));
        Assert.Contains("\"step\"", AgentTool.SpecFor(AgentCatalog.Default).Parameters);
        var roster = new AgentRoster();
        var id = roster.Begin("tests", "general", false, "add tests", step: "2");
        Assert.Equal("2", roster.Run(id)!.RequestedStep);
    }

    // MARK: The activity list

    [Fact]
    public void EverythingWorkingIsListedWithWhereItRunsAndWhatItWasGiven()
    {
        var phase = new TurnPhaseState();
        phase.BeginPlanning(TaskKind.CodeEdit, 1, T0);
        phase.BeginExecution("plan", 120, "b", T0.AddSeconds(5));
        var chat = new ChatActivity("c1", "Fix the bug")
        {
            Running = true, Phase = phase, GoalRound = 2, WorkKind = "Goal", WorkTitle = "Fix the negative stock bug", WorkStartedAt = T0,
            Model = "qwen3-coder-30b", Route = "DGX Spark", RunningTool = "edit src/Stock.cs", Tokens = 1234,
            Agents =
            [
                new AgentRunInfo("a1", "Find callers", "explore", false, T0) { Route = "Spark 2", Model = "qwen3-8b", Assignment = "Find callers of Remove", Steps = 3, PromptTokens = 10, CompletionTokens = 5 },
                new AgentRunInfo("a2", "Review", "review", true, T0) { Route = "Kimi Code", Model = "kimi-k2", Placement = "tried first: Spark 2: didn't answer" },
                new AgentRunInfo("a3", "Old", "explore", false, T0) { Status = AgentRunStatus.Done },
            ],
            RunningJobs = [new BackgroundAgentJob("bg-1", "Review", T0), new BackgroundAgentJob("bg-2", "Untracked job", T0)],
            ListRunning = true, QueuedTasks = 2,
            ListActiveTask = new QueueTask { Title = "Write parser tests", StartedAt = T0, Rounds = 2, PromptTokens = 100, CompletionTokens = 50 },
        };
        var idle = new ChatActivity("c2", "Docs") { QueuedTasks = 1, NextTaskTitle = "Update the README", PausedReason = ChatTaskRules.PausedAtLaunch };

        var items = RunActivity.Build([chat, idle]);
        Assert.Equal(
            [ActivityKind.MainAgent, ActivityKind.Subagent, ActivityKind.BackgroundAgent, ActivityKind.BackgroundAgent, ActivityKind.QueueRunner, ActivityKind.ChatTasks],
            items.Select(i => i.Kind));

        var main = items[0];
        Assert.Equal(("Main agent", "Goal: Fix the negative stock bug", "qwen3-coder-30b on DGX Spark", "Execution · round 2", "edit src/Stock.cs"),
            (main.Title, main.Assignment, main.Placement, main.Status, main.Activity));
        Assert.Equal(1234, main.Tokens);

        var sub = items[1];
        Assert.Equal(("Find callers (explore)", "Plan step: Find callers of Remove", "qwen3-8b on Spark 2", "Running", 15L, "a1"),
            (sub.Title, sub.Assignment, sub.Placement, sub.Status, sub.Tokens!.Value, sub.AgentRunId));
        Assert.Equal("Running · tried first: Spark 2: didn't answer", items[2].Status);
        Assert.Equal(("Untracked job", "bg-2"), (items[3].Title, items[3].BackgroundJobId));
        Assert.Equal(("Task list", "Working: Write parser tests", "2 waiting", 150L), (items[4].Title, items[4].Assignment, items[4].Status, items[4].Tokens!.Value));

        var waiting = items[5];
        Assert.Equal(("1 task up next", "Next: Update the README", ChatTaskRules.PausedAtLaunch, false), (waiting.Title, waiting.Assignment, waiting.Status, waiting.Running));
        Assert.False(waiting.CanStop);

        // The toolbar's count: what is working, not what waits.
        Assert.Equal(5, RunActivity.Count(items));
    }

    [Fact]
    public void ASubagentWithoutAServerYetSaysSo()
    {
        var chat = new ChatActivity("c", "t") { Agents = [new AgentRunInfo("a1", "x", "general", false, T0)] };
        Assert.Equal("waiting for a model server", Assert.Single(RunActivity.Build([chat])).Placement);
        Assert.Empty(RunActivity.Build([new ChatActivity("c", "idle")]));
        Assert.Equal("Stopping…", RunActivity.Build([new ChatActivity("c", "t") { Running = true, Stopping = true }])[0].Status);
    }
}
