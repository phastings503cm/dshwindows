using Vision.Core;

namespace Vision.Core.Tests;

/// <summary>Runs only when a real claude CLI is named in VISION_CLAUDE_CLI and a stand-in Messages API (one that streams
/// scripted replies, like the one the fixtures were captured with) in VISION_CLAUDE_API.</summary>
public sealed class ClaudeLiveFactAttribute : FactAttribute
{
    public ClaudeLiveFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("VISION_CLAUDE_CLI")) ||
            string.IsNullOrEmpty(Environment.GetEnvironmentVariable("VISION_CLAUDE_API")))
            Skip = "Set VISION_CLAUDE_CLI (a claude CLI) and VISION_CLAUDE_API (a stand-in Messages API) to run against the real CLI.";
    }
}

[CollectionDefinition("claude-live", DisableParallelization = true)]
public sealed class ClaudeLiveCollection;

/// <summary>Vision's runner driving the REAL claude CLI process — its spawning, UTF-8 stdin, the stream-json both ways,
/// the control protocol and the interrupt — with the model replaced by a local stand-in, so no account is involved and
/// the CLI's config folder is a scratch one. The far end of the pipe the fixtures stand in for everywhere else.</summary>
[Collection("claude-live")]
public sealed class ClaudeCodeLiveTests : IDisposable
{
    private readonly TempDirectory _home = new("vision-claude-home");
    private readonly TempDirectory _project = new("vision-claude-proj");
    private readonly ClaudeCodeLaunch _launch;

    public ClaudeCodeLiveTests()
    {
        _launch = ClaudeCodeLaunch.Direct(Environment.GetEnvironmentVariable("VISION_CLAUDE_CLI") ?? "claude");
        ClaudeCodeEnvironment.OverridesForTesting = new Dictionary<string, string?>
        {
            ["HOME"] = _home.Path,
            ["CLAUDE_CONFIG_DIR"] = System.IO.Path.Combine(_home.Path, ".claude"),
            ["ANTHROPIC_BASE_URL"] = Environment.GetEnvironmentVariable("VISION_CLAUDE_API"),
            ["ANTHROPIC_API_KEY"] = "fixture-not-a-real-key",
            ["DISABLE_AUTOUPDATER"] = "1",
            ["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"] = "1",
            ["DISABLE_TELEMETRY"] = "1",
        };
    }

    public void Dispose()
    {
        ClaudeCodeEnvironment.OverridesForTesting = null;
        _home.Dispose();
        _project.Dispose();
    }

    private async Task<(ClaudeCodeTurnResult Result, List<EngineEvent> Events)> Turn(string prompt, ClaudeCodeTurnOptions options,
        Func<ClaudeCodePermissionRequest, ClaudeCodePermissionDecision>? decide = null, CancellationToken ct = default)
    {
        var events = new List<EngineEvent>();
        var stream = new ClaudeCodeStream(e =>
        {
            lock (events) events.Add(e);
        }, _project.Path);
        var result = await ClaudeCodeRunner.RunAsync(_launch, options, _project.Path, ClaudeCodeCommand.UserMessage(prompt), stream,
            (q, _) => Task.FromResult(decide?.Invoke(q) ?? new ClaudeCodePermissionDecision.Allow()), ct);
        return (result, events);
    }

    [ClaudeLiveFact]
    public async Task ItFindsTheCliAndSaysHowItIsSignedIn()
    {
        // With only the stand-in's key in its environment, the real CLI says it is signed in with an API key — and Vision says
        // that is billed to the key, not a Claude plan.
        var status = await ClaudeCodeCli.CheckAsync(new ClaudeCodeSearch { ExplicitPath = _launch.Program, IgnoreCase = false });
        var ready = Assert.IsType<ClaudeCodeStatus.Ready>(status);
        Assert.NotNull(ClaudeCodeCli.ParseVersion(ready.Version));
        Assert.Equal("api_key", ready.Auth!.Method);
        Assert.Contains("billed to that key", ready.Summary);

        // Without it, an empty config folder is signed out.
        ClaudeCodeEnvironment.OverridesForTesting = ClaudeCodeEnvironment.OverridesForTesting!
            .Where(kv => kv.Key != "ANTHROPIC_API_KEY").Append(new("ANTHROPIC_API_KEY", null)).ToDictionary(kv => kv.Key, kv => kv.Value);
        Assert.IsType<ClaudeCodeStatus.SignedOut>(await ClaudeCodeCli.CheckAsync(new ClaudeCodeSearch { ExplicitPath = _launch.Program, IgnoreCase = false }));
    }

    [ClaudeLiveFact]
    public async Task ATurnWritesAFileAndTheNextOneResumesTheSameSession()
    {
        var path = System.IO.Path.Combine(_project.Path, "hello.txt");
        var session = Guid.NewGuid().ToString();
        var (first, events) = await Turn($"SCENARIO:write PATH={path} please", new ClaudeCodeTurnOptions
        {
            Model = "sonnet", Preset = PermissionPreset.FullAccess, NewSession = session, AppendSystemPrompt = "Vision live test.",
        });
        Assert.Null(first.Failure);
        Assert.Equal(session, first.Stream.SessionId);
        Assert.True(File.Exists(path));
        Assert.Contains(events, e => e is EngineEvent.FilesChanged f && f.Changes.Any(c => c.Kind == FileChangeKind.Created));
        Assert.Contains(events, e => e is EngineEvent.TextDelta);
        Assert.Equal("Done — the file is written.", first.Stream.LastReply);

        var (second, _) = await Turn("And now just say hello — héllo ✓ 漢字", new ClaudeCodeTurnOptions { Model = "sonnet", ResumeSession = session });
        Assert.Null(second.Failure);
        Assert.Equal(session, second.Stream.SessionId);
        Assert.Contains("héllo ✓ 漢字", second.Stream.LastReply); // UTF-8 down stdin and back
    }

    [ClaudeLiveFact]
    public async Task APermissionQuestionComesToVisionAndItsAnswerIsObeyed()
    {
        var asked = new List<string>();
        var (allowed, _) = await Turn("SCENARIO:bash\nCMD=mkdir -p made-by-vision", new ClaudeCodeTurnOptions { Model = "sonnet" }, q =>
        {
            asked.Add(q.ToolName);
            return ClaudeCodePermissions.Decide(q, new PermissionPolicy(PermissionPreset.FullAccess, _project.Path));
        });
        Assert.Null(allowed.Failure);
        Assert.Equal(["Bash"], asked);
        Assert.True(Directory.Exists(System.IO.Path.Combine(_project.Path, "made-by-vision")));

        var (denied, events) = await Turn("SCENARIO:bash\nCMD=mkdir -p refused-by-vision", new ClaudeCodeTurnOptions { Model = "sonnet" },
            _ => new ClaudeCodePermissionDecision.Deny("Not in this test."));
        Assert.False(Directory.Exists(System.IO.Path.Combine(_project.Path, "refused-by-vision")));
        Assert.Equal(1, denied.Result!.Denials);
        Assert.Contains(events, e => e is EngineEvent.ToolFinished { Ok: false, Summary: "Not in this test." });
    }

    [ClaudeLiveFact]
    public async Task StopInterruptsTheRealCli()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var started = DateTime.UtcNow;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Turn("SCENARIO:slow go", new ClaudeCodeTurnOptions { Model = "sonnet" }, ct: stop.Token));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(12));
    }

    [ClaudeLiveFact]
    public async Task AnUnknownSessionIsReportedAsMissing()
    {
        var (result, _) = await Turn("hi", new ClaudeCodeTurnOptions { Model = "sonnet", ResumeSession = Guid.NewGuid().ToString() });
        Assert.Equal(ClaudeCodeFailureKind.ResumeMissing, result.Failure!.Kind);
    }
}
