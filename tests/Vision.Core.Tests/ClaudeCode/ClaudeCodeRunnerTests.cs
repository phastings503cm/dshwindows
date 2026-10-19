using System.Text.Json.Nodes;
using Vision.Core;

namespace Vision.Core.Tests;

/// <summary>One turn on the claude CLI, driven against a stand-in that replays the real CLI's captures with the real CLI's
/// pipe behaviour: the prompt goes down stdin as one stream-json line, permission questions are answered with control
/// responses shaped like the ones the real CLI accepted, stdin closes once the result is in, Stop sends an interrupt, and a
/// CLI that fails before answering is explained.</summary>
public sealed class ClaudeCodeRunnerTests
{
    private static readonly ClaudeCodeLaunch Launch = ClaudeCodeLaunch.Direct("/usr/local/bin/claude");

    private static ClaudeCodeTurnOptions Options(string? resume = null) => new()
    {
        Model = "sonnet",
        ResumeSession = resume,
        NewSession = resume is null ? "11111111-2222-3333-4444-555555555555" : null,
    };

    private static Task<ClaudeCodeTurnResult> Run(ScriptedClaudeProcess process, List<EngineEvent>? events = null,
        Func<ClaudeCodePermissionRequest, ClaudeCodePermissionDecision>? decide = null, CancellationToken ct = default,
        ClaudeCodeTurnOptions? options = null)
    {
        events ??= [];
        var stream = new ClaudeCodeStream(e =>
        {
            lock (events) events.Add(e);
        }, "/work/proj2");
        return ClaudeCodeRunner.RunAsync(Launch, options ?? Options(), "/work/proj2", ClaudeCodeCommand.UserMessage("SCENARIO:write please"), stream,
            (q, _) => Task.FromResult(decide?.Invoke(q) ?? new ClaudeCodePermissionDecision.Allow()), ct, ScriptedClaudeProcess.Starter(process));
    }

    [Fact]
    public async Task ATurnSendsThePromptReadsEveryLineAndClosesStdinOnceTheResultIsIn()
    {
        var process = ScriptedClaudeProcess.Replay(ClaudeFixtures.Lines("write-partial.jsonl"));
        var events = new List<EngineEvent>();
        var outcome = await Run(process, events);

        Assert.Null(outcome.Failure);
        Assert.Equal("1bbd318c-7964-4079-9816-4fbe5b039d85", outcome.Stream.SessionId);
        Assert.Equal(new LlmUsage(11000, 84, 8000), outcome.Usage);
        Assert.True(process.InputClosed);
        // Exactly one line went down stdin: the prompt.
        var prompt = JsonNode.Parse(Assert.Single(process.Written))!;
        Assert.Equal("user", (string?)prompt["type"]);
        Assert.Equal("SCENARIO:write please", (string?)prompt["message"]!["content"]![0]!["text"]);
        Assert.Equal("/work/proj2", process.WorkingDirectory);
        Assert.Equal(ClaudeCodeCommand.Args(Options()), process.Args);
        Assert.Contains(events, e => e is EngineEvent.FilesChanged);
    }

    [Fact]
    public async Task APermissionQuestionIsAnsweredTheWayTheRealCliAcceptedIt()
    {
        var process = ScriptedClaudeProcess.Replay(ClaudeFixtures.Lines("bash-allow.jsonl"));
        ClaudeCodePermissionRequest? asked = null;
        var outcome = await Run(process, decide: q =>
        {
            asked = q;
            return new ClaudeCodePermissionDecision.Allow();
        });
        Assert.Null(outcome.Failure);
        Assert.Equal("Bash", asked!.ToolName);
        var written = JsonNode.Parse(process.Written[1])!;
        // Same shape as what the driver wrote to the real CLI (bash-allow.sent.jsonl), plus the tool use id.
        var accepted = JsonNode.Parse(ClaudeFixtures.Lines("bash-allow.sent.jsonl")[1])!;
        Assert.Equal((string?)accepted["type"], (string?)written["type"]);
        Assert.Equal((string?)accepted["response"]!["subtype"], (string?)written["response"]!["subtype"]);
        Assert.Equal((string?)accepted["response"]!["request_id"], (string?)written["response"]!["request_id"]);
        Assert.Equal("allow", (string?)written["response"]!["response"]!["behavior"]);
        Assert.True(JsonNode.DeepEquals(accepted["response"]!["response"]!["updatedInput"], written["response"]!["response"]!["updatedInput"]));
        Assert.Equal("toolu_fixture_1_1", (string?)written["response"]!["response"]!["toolUseID"]);
    }

    [Fact]
    public async Task ANoGoesBackWithItsReason()
    {
        var process = ScriptedClaudeProcess.Replay(ClaudeFixtures.Lines("bash-deny.jsonl"));
        await Run(process, decide: _ => new ClaudeCodePermissionDecision.Deny("Not today."));
        var written = JsonNode.Parse(process.Written[1])!["response"]!["response"]!;
        Assert.Equal("deny", (string?)written["behavior"]);
        Assert.Equal("Not today.", (string?)written["message"]);
    }

    [Fact]
    public async Task StopInterruptsTheTurnAndSurfacesAsCancelled()
    {
        var lines = ClaudeFixtures.Lines("interrupt.jsonl");
        // Hold everything after the first text delta back until Vision has asked for the interrupt.
        var cut = lines.ToList().FindIndex(l => l.Contains("content_block_delta", StringComparison.Ordinal)) + 1;
        bool Interrupted(IReadOnlyList<string> w) => w.Any(x => x.Contains("\"interrupt\"", StringComparison.Ordinal));
        var process = new ScriptedClaudeProcess(lines.Select((l, i) => new ScriptedClaudeProcess.Step(l, i == cut ? Interrupted : null)));
        using var stop = new CancellationTokenSource();
        var events = new List<EngineEvent>();
        var run = Run(process, events, ct: stop.Token);
        await WaitUntil(() => events.Any(e => e is EngineEvent.TextDelta));
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        var interrupt = JsonNode.Parse(process.Written.Single(w => w.Contains("interrupt", StringComparison.Ordinal)))!;
        Assert.Equal("control_request", (string?)interrupt["type"]);
        Assert.Equal("interrupt", (string?)interrupt["request"]!["subtype"]);
        Assert.False(process.Killed); // it ended the turn by itself, within the grace period
    }

    [Fact]
    public async Task ACliThatIgnoresTheInterruptIsKilled()
    {
        var old = ClaudeCodeRunner.InterruptGrace;
        ClaudeCodeRunner.InterruptGrace = TimeSpan.FromMilliseconds(100);
        try
        {
            var lines = ClaudeFixtures.Lines("write-partial.jsonl");
            var process = new ScriptedClaudeProcess(lines.Select((l, i) => new ScriptedClaudeProcess.Step(l, i == 5 ? _ => false : null)));
            using var stop = new CancellationTokenSource();
            var run = Run(process, ct: stop.Token);
            await WaitUntil(() => process.Written.Count > 0);
            await Task.Delay(50);
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
            Assert.True(process.Killed);
        }
        finally
        {
            ClaudeCodeRunner.InterruptGrace = old;
        }
    }

    [Fact]
    public async Task ACliThatFailsBeforeAnsweringIsExplained()
    {
        var unknown = await Run(new ScriptedClaudeProcess([], "error: unknown option '--effort'\n", 1, exitWithoutInputClose: true));
        Assert.Equal((ClaudeCodeFailureKind.UnknownOption, "--effort"), (unknown.Failure!.Kind, unknown.Failure.Detail));

        var inUse = await Run(new ScriptedClaudeProcess([], "Error: Session ID 11111111-2222-3333-4444-555555555555 is already in use.\n", 1, exitWithoutInputClose: true));
        Assert.Equal(ClaudeCodeFailureKind.SessionInUse, inUse.Failure!.Kind);

        var missing = await Run(ScriptedClaudeProcess.Replay(ClaudeFixtures.Lines("resume-missing.jsonl")), options: Options("11111111-2222-3333-4444-555555555555"));
        Assert.Equal(ClaudeCodeFailureKind.ResumeMissing, missing.Failure!.Kind);

        var notSignedIn = await Run(ScriptedClaudeProcess.Replay(ClaudeFixtures.Lines("not-signed-in.jsonl")));
        Assert.Equal(ClaudeCodeFailureKind.NotSignedIn, notSignedIn.Failure!.Kind);
    }

    [Fact]
    public async Task ControlRequestsVisionDoesNotHandleGetAnErrorSoTheCliNeverWaits()
    {
        var process = ScriptedClaudeProcess.Replay([
            """{"type":"control_request","request_id":"hook-1","request":{"subtype":"hook_callback","callback_id":"c"}}""",
            .. ClaudeFixtures.Lines("resume.jsonl"),
        ]);
        await Run(process);
        var answer = JsonNode.Parse(process.Written.Single(w => w.Contains("hook-1", StringComparison.Ordinal)))!;
        Assert.Equal("error", (string?)answer["response"]!["subtype"]);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException();
            await Task.Delay(5);
        }
    }
}
