namespace Dsh.Core.Tests;

/// <summary>Runs a tool directly (no engine) against a scratch workspace.</summary>
public sealed class ToolTestContext : IDisposable
{
    public TempDirectory Root { get; } = new("dsh-tools");

    public ToolContext Context { get; }

    public ToolTestContext()
    {
        Context = new ToolContext
        {
            Workspace = Root.Path,
            Policy = new PermissionPolicy(PermissionPreset.WorkspaceWrite, Root.Path),
            Client = new ScriptedClient(),
            Registry = new ToolRegistry([]),
        };
    }

    public async Task<ToolResult> Run(IToolExecutor tool, object arguments) =>
        await tool.ExecuteAsync(arguments as string ?? Args.Json(arguments), Context, CancellationToken.None);

    public async Task<string> Output(IToolExecutor tool, object arguments) => (await Run(tool, arguments)).Output;

    public void Dispose() => Root.Dispose();
}
