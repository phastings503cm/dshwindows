namespace Dsh.Core;

// MARK: - Tool context / results

/// <summary>Asks the user whether a flagged tool call may proceed: (id, tool name, detail) → approved.</summary>
public delegate Task<bool> PermissionGate(string id, string name, string detail);

/// <summary>What a tool may touch while executing. Tools report side effects through
/// <see cref="ToolResult"/> rather than callbacks.</summary>
public sealed record ToolContext
{
    /// <summary>The project folder (absolute path).</summary>
    public required string Workspace { get; init; }
    public required PermissionPolicy Policy { get; init; }
    /// <summary>The live model client, so the agent tool can spawn a subagent.</summary>
    public required ILlmClient Client { get; init; }
    /// <summary>The registry, so a subagent gets the same capabilities.</summary>
    public required ToolRegistry Registry { get; init; }
    /// <summary>0 = top-level session. Bounded to keep recursion sane.</summary>
    public int Depth { get; init; }
    /// <summary>Model id subagents should use (same as the parent session).</summary>
    public string Model { get; init; } = "";
    /// <summary>The parent's resolved context window, so a subagent budgets auto-compaction against
    /// the real window instead of running uncompacted until it hits a hard wall.</summary>
    public int? ContextWindow { get; init; }
    /// <summary>The parent's thinking level, so subagents think as hard as it does.</summary>
    public ThinkingLevel? Thinking { get; init; }
    /// <summary>The shell run_shell_command (and plugin tools) execute commands with.</summary>
    public AgentShell Shell { get; init; } = AgentShell.Default;
    /// <summary>Asks the user for permission; subagents inherit the parent's hook.</summary>
    public PermissionGate RequestPermission { get; init; } = static (_, _, _) => Task.FromResult(true);
    /// <summary>Background subagents this chat has launched (agent with run_in_background, agent_status,
    /// agent_stop). Null for subagents.</summary>
    public BackgroundAgents? BackgroundAgents { get; init; }
}

public enum FileChangeKind { Created, Modified, Deleted }

/// <summary>A side effect the UI can observe (editor auto-reload, "files changed" chips).</summary>
public sealed record FileChange(string Path, FileChangeKind Kind);

public enum TodoStatus { Pending, InProgress, Completed }

/// <summary>One structured todo, as the todo_write tool manages them.</summary>
public sealed record TodoItem(string Id, string Content, TodoStatus Status = TodoStatus.Pending);

/// <summary>The outcome of executing one tool call.</summary>
public sealed record ToolResult(string Output)
{
    public IReadOnlyList<FileChange> Files { get; init; } = [];
    /// <summary>null = this call did not manage todos.</summary>
    public IReadOnlyList<TodoItem>? Todos { get; init; }
    /// <summary>Images the tool produced. Chat-completion tool messages are text-only, so the engine
    /// hands these to the model on a follow-up user message.</summary>
    public IReadOnlyList<MessageAttachment> Images { get; init; } = [];

    public static implicit operator ToolResult(string output) => new(output);
}

// MARK: - Executor contract

/// <summary>A concrete tool implementation. Names match the Qwen Code tool names.</summary>
public interface IToolExecutor
{
    string Name { get; }
    /// <summary>The OpenAI-shape tool spec sent to the model.</summary>
    ToolSpec Spec { get; }
    /// <summary>Run the call. <paramref name="arguments"/> is the model's raw JSON. Tools report
    /// failures as output starting with "Error:" rather than throwing.</summary>
    Task<ToolResult> ExecuteAsync(string arguments, ToolContext context, CancellationToken cancellationToken);
}

// MARK: - Registry

public sealed class ToolRegistry
{
    private readonly IReadOnlyList<IToolExecutor> _tools;

    public ToolRegistry(IEnumerable<IToolExecutor> tools) => _tools = tools.ToList();

    public IReadOnlyList<IToolExecutor> Tools => _tools;
    public IReadOnlyList<ToolSpec> Specs => _tools.Select(t => t.Spec).ToList();
    public IReadOnlyList<string> Names => _tools.Select(t => t.Name).ToList();

    public IToolExecutor? Tool(string name) => _tools.FirstOrDefault(t => t.Name == name);

    /// <summary>A copy of this registry with extra tools appended (plugin contributions).</summary>
    public ToolRegistry Adding(IEnumerable<IToolExecutor> extra) => new(_tools.Concat(extra));

    /// <summary>A copy without the named tools (subagents lose agent).</summary>
    public ToolRegistry Removing(params string[] names) => new(_tools.Where(t => !names.Contains(t.Name)));

    /// <summary>Tools for background subagents (the agent tool starts them with run_in_background).
    /// Added by the app next to a <see cref="Core.BackgroundAgents"/> pool.</summary>
    public static IReadOnlyList<IToolExecutor> BackgroundAgentTools() => [new AgentStatusTool(), new AgentStopTool()];

    /// <summary>The built-in tool set. The agent tool is only offered at the top level: subagents
    /// don't spawn subagents (keeps the permission surface and cost predictable).</summary>
    public static ToolRegistry Standard(int depth = 0, AgentShell? shell = null)
    {
        var tools = new List<IToolExecutor>
        {
            new ReadFileTool(), new WriteFileTool(), new EditTool(), new ListDirectoryTool(),
            new ReadManyFilesTool(), new GlobTool(), new GrepTool(),
            new RunShellCommandTool(shell), new WebFetchTool(), new TodoWriteTool(), new ExitPlanModeTool(),
        };
        if (depth == 0) tools.Add(new AgentTool());
        return new ToolRegistry(tools);
    }
}
