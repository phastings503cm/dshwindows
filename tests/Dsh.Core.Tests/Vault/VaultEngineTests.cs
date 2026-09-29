namespace Dsh.Core.Tests;

/// <summary>Records the raw arguments it ran with; answers "ran with: &lt;text&gt;".</summary>
public sealed class RecordingTool : IToolExecutor
{
    public const string ToolName = "record";
    private readonly List<string> _seen = [];
    public string Name => ToolName;
    public ToolSpec Spec { get; } = new(ToolName, "records", """{"type":"object","properties":{"text":{"type":"string"}}}""");

    public IReadOnlyList<string> Seen
    {
        get
        {
            lock (_seen) return [.. _seen];
        }
    }

    public Task<ToolResult> ExecuteAsync(string arguments, ToolContext context, CancellationToken cancellationToken)
    {
        lock (_seen) _seen.Add(arguments);
        return Task.FromResult<ToolResult>($"ran with: {JsonArgs.String(arguments, "text") ?? ""}");
    }
}

/// <summary>Ported from the engine-level cases of CredentialVaultTests.swift: the tool gets the value,
/// the model never sees it, and the access rules hold.</summary>
public sealed class VaultEngineTests : IDisposable
{
    private readonly TempDirectory _root = new("dsh-vault-engine");

    public void Dispose() => _root.Dispose();

    private CredentialVault Vault() => new(_root.Path, new MemoryBlobStore());

    private (Engine Engine, ScriptedClient Client) MakeEngine(CredentialVault vault, RecordingTool tool, IEnumerable<ToolCall> calls,
                                                              PermissionGate? gate = null, VaultGrants? grants = null)
    {
        var client = new ScriptedClient([.. calls.Select(c => Turn.Calling(c)), new Turn("done")]);
        var engine = new Engine(client, new ToolRegistry([tool]), "s", new EngineConfig("m") { MaxIterations = 8 },
            _root.Path, new PermissionPolicy(PermissionPreset.FullAccess, _root.Path), gate ?? Gates.Allow)
        {
            Vault = vault,
            VaultGrants = grants ?? new VaultGrants(),
        };
        return (engine, client);
    }

    [Fact]
    public async Task ToolGetsTheValueAndTheModelNeverSeesIt()
    {
        var vault = Vault();
        vault.Add("API_KEY", "sk-live-abcdef123");
        var tool = new RecordingTool();
        var (engine, client) = MakeEngine(vault, tool, [new ToolCall("1", "record", """{"text":"{{vault:API_KEY}}"}""")]);
        var outputs = new List<string>();
        var result = await engine.RunAsync([], "go", sink: e =>
        {
            if (e is EngineEvent.ToolFinished f) lock (outputs) outputs.Add(f.Output);
        });
        Assert.Single(tool.Seen);
        Assert.Contains("sk-live-abcdef123", tool.Seen[0]); // the tool got the real value
        Assert.Equal(["ran with: [vault:API_KEY]"], outputs); // the UI and log see the placeholder
        var everything = string.Concat(result.Messages.Select(m => m.Content))
                         + string.Concat(client.Requests.SelectMany(r => r.Messages).Select(m => m.Content));
        Assert.DoesNotContain("sk-live-abcdef123", everything); // the model never sees the value
        Assert.Equal(1, vault.Entry("API_KEY")?.UseCount);
    }

    [Fact]
    public async Task SecretsInToolOutputAreScrubbedEvenWithoutAPlaceholder()
    {
        // e.g. the agent types out a .env file that holds a vault value.
        var vault = Vault();
        vault.Add("DB_PASSWORD", "correct-horse-battery");
        var (engine, _) = MakeEngine(vault, new RecordingTool(), [new ToolCall("1", "record", """{"text":"DB=correct-horse-battery"}""")]);
        var result = await engine.RunAsync([], "go");
        Assert.Contains(result.Messages, m => m.Content == "ran with: DB=[vault:DB_PASSWORD]");
    }

    [Fact]
    public async Task AccessRules()
    {
        var vault = Vault();
        vault.Add("NEVER", "never-value", access: VaultAccess.Never);
        vault.Add("ASK", "ask-value", access: VaultAccess.Ask);
        var tool = new RecordingTool();

        // Unknown and withheld credentials refuse the call; the tool never runs.
        var (e1, _) = MakeEngine(vault, tool,
        [
            new ToolCall("1", "record", """{"text":"{{vault:MISSING}}"}"""),
            new ToolCall("2", "record", """{"text":"{{vault:NEVER}}"}"""),
        ]);
        var r1 = await e1.RunAsync([], "go");
        Assert.Empty(tool.Seen);
        Assert.Contains(r1.Messages, m => m.Content?.Contains("no credential named MISSING") == true);
        Assert.Contains(r1.Messages, m => m.Content?.Contains("unavailable to the agent") == true);

        // "Ask first" asks once per chat.
        var asked = 0;
        var grants = new VaultGrants();
        var call = new ToolCall("3", "record", """{"text":"{{vault:ASK}}"}""");
        var (e2, _) = MakeEngine(vault, tool, [call, call], (_, _, detail) =>
        {
            Assert.Contains("ASK", detail);
            Interlocked.Increment(ref asked);
            return Task.FromResult(true);
        }, grants);
        await e2.RunAsync([], "go");
        Assert.Equal(1, asked);
        Assert.Equal(2, tool.Seen.Count);

        // Declined: refused, not run.
        var (e3, _) = MakeEngine(vault, tool, [call], (_, _, _) => Task.FromResult(false));
        var r3 = await e3.RunAsync([], "go");
        Assert.Equal(2, tool.Seen.Count);
        Assert.Contains(r3.Messages, m => m.Content?.StartsWith("Permission denied", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task NonExecutingToolsKeepThePlaceholder()
    {
        // A subagent prompt or a queued task would carry the secret to a model or to disk.
        var vault = Vault();
        vault.Add("API_KEY", "sk-live-abcdef123");
        var seen = new List<string>();
        var queue = new QueueAddTool((title, details, _, _) =>
        {
            seen.Add(details);
            return Task.FromResult("queued");
        });
        var client = new ScriptedClient(
            Turn.Calling(new ToolCall("1", "queue_task", """{"title":"t","details":"use {{vault:API_KEY}}"}""")),
            new Turn("done"));
        var engine = new Engine(client, new ToolRegistry([queue]), "s", new EngineConfig("m"), _root.Path,
            new PermissionPolicy(PermissionPreset.FullAccess, _root.Path), Gates.Allow) { Vault = vault };
        await engine.RunAsync([], "go");
        Assert.Equal(["use {{vault:API_KEY}}"], seen);
        Assert.Equal(0, vault.Entry("API_KEY")?.UseCount);
    }
}
