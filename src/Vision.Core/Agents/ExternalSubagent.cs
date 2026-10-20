namespace Vision.Core;

// MARK: - A subagent server with an engine of its own
//
// Most subagent servers are models Vision's own engine talks to. Some run their own agent instead — Claude Code works a
// task as one `claude -p` job in the chat's folder. Such a server's FleetTarget carries an IExternalSubagent, and the agent
// tool hands it the task instead of starting an engine; the rest of a subagent's life (its lease, the roster, failover,
// cancellation, its report) stays the same. Vision for macOS has the same shape (ExternalSubagent.swift).

/// <summary>An engine that works one subagent task by itself.</summary>
public interface IExternalSubagent
{
    /// <summary>Work the task. Throws <see cref="OperationCanceledException"/> when stopped, a server fault (see
    /// <see cref="AgentFleet.IsServerFault"/>) when it can't take the task, and anything else when the task itself failed.</summary>
    Task<ExternalSubagentResult> RunAsync(ExternalSubagentJob job, CancellationToken cancellationToken);
}

/// <summary>One task for an external engine.</summary>
public sealed record ExternalSubagentJob
{
    /// <summary>The task, as the main agent wrote it.</summary>
    public required string Prompt { get; init; }
    /// <summary>Added to the engine's own instructions: what it is (a subagent) and the agent type's prompt.</summary>
    public required string SystemNote { get; init; }
    /// <summary>The chat's folder (its scratch folder when it has no project).</summary>
    public required string Workspace { get; init; }
    /// <summary>The permissions it works under: the chat's, or Plan only for a read-only agent type.</summary>
    public PermissionPreset Preset { get; init; } = PermissionPreset.WorkspaceWrite;
    public bool ReadOnly { get; init; }
    /// <summary>Its step budget.</summary>
    public int MaxSteps { get; init; } = 30;
    /// <summary>What it is doing now; true counts it as a step (a tool call).</summary>
    public Action<string, bool> Activity { get; init; } = static (_, _) => { };
    /// <summary>It started something that changes the workspace (a retry elsewhere must look before it repeats a step).</summary>
    public Action NoteMutation { get; init; } = static () => { };
    /// <summary>Ask the person (the chat's own permission card, which says no by itself for unattended work).</summary>
    public PermissionGate RequestPermission { get; init; } = static (_, _, _) => Task.FromResult(false);
}

/// <summary>How an external engine's task ended.</summary>
public sealed record ExternalSubagentResult(bool Ok, string Report)
{
    /// <summary>It changed the workspace.</summary>
    public bool Mutated { get; init; }
    public long PromptTokens { get; init; }
    public long CompletionTokens { get; init; }
    /// <summary>What it would have cost at API prices, when the engine says.</summary>
    public double? CostUsd { get; init; }
}
