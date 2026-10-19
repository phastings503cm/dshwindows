using System.Text.Json.Nodes;
using Vision.Core;

namespace Vision.Core.Tests;

/// <summary>What the review of the Claude Code engine fixed, each at the point where it matters: a folder's own Claude Code
/// settings load only when the person trusted it (headless claude -p runs a cloned repository's hooks otherwise — seen on
/// the real CLI 2.1.299), reads outside the project and commands naming private files ask, what Vision hands over goes
/// through the shield, attachments stay within one request's limits, and a one-off's long system prompt goes in a file.</summary>
public sealed class ClaudeCodeReviewTests
{
    // MARK: Trust

    private const string Config = """
        {
          "oauthAccount": { "emailAddress": "not-read@example.com" },
          "projects": {
            "C:/Users/pat/code/trusted": { "hasTrustDialogAccepted": true, "lastSessionId": "s-1" },
            "C:/Users/pat/code/untrusted": { "hasTrustDialogAccepted": false },
            "C:/Users/pat": { "hasTrustDialogAccepted": true },
            "D:/work/": { "hasTrustDialogAccepted": true },
            "/home/pat/repos": { "hasTrustDialogAccepted": true }
          }
        }
        """;

    [Fact]
    public void TrustIsReadFromClaudeCodesProjectsOnlyAndAFolderAboveCountsButNotTheHomeFolderOrARoot()
    {
        var projects = ClaudeCodeTrust.ParseProjects(Config, ignoreCase: true);
        Assert.True(ClaudeCodeTrust.IsTrusted(@"C:\Users\pat\code\trusted", projects, home: @"C:\Users\pat"));
        Assert.True(ClaudeCodeTrust.IsTrusted(@"c:\users\pat\code\trusted\sub\deeper", projects, home: @"C:\Users\pat"));
        Assert.False(ClaudeCodeTrust.IsTrusted(@"C:\Users\pat\code\untrusted", projects, home: @"C:\Users\pat"));
        // Trusting the home folder (or a drive's root) doesn't trust everything under it.
        Assert.False(ClaudeCodeTrust.IsTrusted(@"C:\Users\pat\Downloads\cloned", projects, home: @"C:\Users\pat"));
        Assert.True(ClaudeCodeTrust.IsTrusted("/home/pat/repos/app", projects, home: "/home/pat"));
        Assert.False(ClaudeCodeTrust.IsTrusted("/home/pat/other", projects, home: "/home/pat"));
        Assert.Equal("s-1", ClaudeCodeTrust.Record(@"C:\Users\pat\code\trusted\", projects)?.LastSessionId);
        Assert.Null(ClaudeCodeTrust.Record(@"C:\Users\pat\code\nowhere", projects));
        // Not JSON (being written) or no file: nothing is trusted.
        Assert.Empty(ClaudeCodeTrust.ParseProjects("{ not json"));
        Assert.Empty(ClaudeCodeTrust.ReadProjects(Path.Combine(Path.GetTempPath(), $"absent-{Guid.NewGuid():N}.json")));
        Assert.Equal(Path.Combine("cfg", ".claude.json"), ClaudeCodeTrust.ConfigFile(configDir: "cfg"));
    }

    [Fact]
    public void AnUntrustedFolderWithSettingsOfItsOwnRunsOnThePersonsAloneOrIsRefusedWhenTheCliCantBeToldTo()
    {
        using var folder = new TempDirectory("vision-cc-trust");
        // No settings of its own: nothing to leave out, nothing to say — even on a CLI that can't be told to.
        var bare = ClaudeCodeTrust.Settings(folder.Path, trusted: false, canSkip: false);
        Assert.Equal(ClaudeCodeFolderTrust.UserOnly, bare.Trust);
        Assert.Empty(bare.Skipped);
        folder.Write(".claude/settings.json", """{"hooks":{}}""");
        folder.Write(".mcp.json", "{}");
        var userOnly = ClaudeCodeTrust.Settings(folder.Path, trusted: false);
        Assert.Equal(ClaudeCodeFolderTrust.UserOnly, userOnly.Trust);
        Assert.Equal([".claude/settings.json", ".mcp.json"], userOnly.Skipped);
        Assert.Contains("(.claude/settings.json, .mcp.json)", ClaudeCodeTrust.SkippedNotice(userOnly.Skipped));
        Assert.Equal(ClaudeCodeFolderTrust.CantSkip, ClaudeCodeTrust.Settings(folder.Path, trusted: false, canSkip: false).Trust);
        Assert.True(ClaudeCodeTrust.Settings(folder.Path, trusted: true).LoadsProjectSettings);
        Assert.True(new ClaudeCodeFailure(ClaudeCodeFailureKind.UntrustedFolder, "x").Permanent);
        Assert.True(new ClaudeCodeFailure(ClaudeCodeFailureKind.NotInstalled, "x").Permanent);
        Assert.False(new ClaudeCodeFailure(ClaudeCodeFailureKind.ResumeMissing, "x").Permanent);
    }

    // MARK: Permissions

    private static ClaudeCodePermissionRequest Ask(string tool, JsonObject input) => new("r1", tool, input, "t1");

    [Fact]
    public void AReadOutsideTheProjectAsksUnlessItIsWhereVisionSavedAnAttachmentOrFullAccess()
    {
        using var project = new TempDirectory("vision-cc-proj");
        using var store = new TempDirectory("vision-cc-store");
        var outside = Path.Combine(Path.GetTempPath(), "vision-cc-elsewhere", "id_rsa");
        var policy = new PermissionPolicy(PermissionPreset.WorkspaceWrite, project.Path);
        Assert.IsType<ClaudeCodePermissionDecision.Allow>(ClaudeCodePermissions.Decide(Ask("Read", new() { ["file_path"] = project["src/a.cs"] }), policy));
        var asked = Assert.IsType<ClaudeCodePermissionDecision.Ask>(ClaudeCodePermissions.Decide(Ask("Read", new() { ["file_path"] = outside }), policy));
        Assert.Equal(("read_file", "Read outside the project: " + outside), (asked.Name, asked.Detail));
        Assert.IsType<ClaudeCodePermissionDecision.Allow>(ClaudeCodePermissions.Decide(Ask("Read", new() { ["file_path"] = store["notes.txt"] }), policy, [store.Path]));
        Assert.IsType<ClaudeCodePermissionDecision.Ask>(ClaudeCodePermissions.Decide(Ask("Grep", new() { ["pattern"] = "key", ["path"] = Path.GetDirectoryName(outside)! }), policy));
        // A Glob with an absolute pattern reads the folder it starts in.
        Assert.IsType<ClaudeCodePermissionDecision.Ask>(ClaudeCodePermissions.Decide(Ask("Glob", new() { ["pattern"] = Path.GetDirectoryName(outside)!.Replace('\\', '/') + "/*" }), policy));
        Assert.IsType<ClaudeCodePermissionDecision.Allow>(ClaudeCodePermissions.Decide(Ask("Glob", new() { ["pattern"] = "**/*.cs" }), policy));
        Assert.IsType<ClaudeCodePermissionDecision.Ask>(ClaudeCodePermissions.Decide(Ask("Read", new() { ["file_path"] = outside }), new PermissionPolicy(PermissionPreset.Plan, project.Path)));
        Assert.IsType<ClaudeCodePermissionDecision.Allow>(ClaudeCodePermissions.Decide(Ask("Read", new() { ["file_path"] = outside }), new PermissionPolicy(PermissionPreset.FullAccess, project.Path)));
        Assert.IsType<ClaudeCodePermissionDecision.Allow>(ClaudeCodePermissions.Decide(Ask("WebFetch", new() { ["url"] = "https://example.com" }), policy));
    }

    [Fact]
    public void ALinkInTheProjectThatPointsOutOfItIsOutside()
    {
        using var project = new TempDirectory("vision-cc-proj");
        using var elsewhere = new TempDirectory("vision-cc-elsewhere");
        var link = project["linked"];
        try
        {
            Directory.CreateSymbolicLink(link, elsewhere.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return; // Windows without the right to make links: nothing to check.
        }
        var policy = new PermissionPolicy(PermissionPreset.WorkspaceWrite, project.Path);
        Assert.IsType<ClaudeCodePermissionDecision.Ask>(ClaudeCodePermissions.Decide(Ask("Read", new() { ["file_path"] = Path.Combine(link, "x") }), policy));
    }

    [Fact]
    public void ACommandThatNamesAFileInTheHomeFolderOutsideTheProjectAsksEvenWhenItOnlyReads()
    {
        var home = PermissionPolicy.HomeDirectory;
        var project = Path.Combine(home, $"vision-cc-never-created-{Guid.NewGuid():N}");
        var policy = new PermissionPolicy(PermissionPreset.WorkspaceWrite, project);
        ClaudeCodePermissionDecision Run(string command) => ClaudeCodePermissions.Decide(Ask("Bash", new() { ["command"] = command }), policy);
        Assert.IsType<ClaudeCodePermissionDecision.Ask>(Run("cat ~/.ssh/id_ed25519"));
        Assert.IsType<ClaudeCodePermissionDecision.Ask>(Run("cat $HOME/.aws/credentials"));
        Assert.IsType<ClaudeCodePermissionDecision.Ask>(Run("type %USERPROFILE%\\.aws\\credentials"));
        Assert.IsType<ClaudeCodePermissionDecision.Ask>(Run("Get-Content $env:USERPROFILE\\.ssh\\id_rsa"));
        Assert.IsType<ClaudeCodePermissionDecision.Ask>(Run("cat ../notes.txt"));
        Assert.IsType<ClaudeCodePermissionDecision.Ask>(Run($"head \"{Path.Combine(home, ".netrc")}\""));
        // Inside the project, or not a path at all: a read-only command goes ahead as before.
        Assert.IsType<ClaudeCodePermissionDecision.Allow>(Run("cat src/main.cs"));
        Assert.IsType<ClaudeCodePermissionDecision.Allow>(Run($"cat {Path.Combine(project, "README.md")}"));
        Assert.IsType<ClaudeCodePermissionDecision.Allow>(Run("git log --oneline -5"));
        // Full access: yes.
        Assert.IsType<ClaudeCodePermissionDecision.Allow>(ClaudeCodePermissions.Decide(Ask("Bash", new() { ["command"] = "cat ~/.ssh/id_ed25519" }),
            new PermissionPolicy(PermissionPreset.FullAccess, project)));
    }

    [Fact]
    public void InsideWslTheCliNamesMountedPathsWhichAreJudgedAsTheWindowsPathsTheyAre()
    {
        Assert.Equal(@"C:\proj\a.cs", ClaudeCodeCli.FromWslPath("/mnt/c/proj/a.cs"));
        if (!OperatingSystem.IsWindows()) return; // C:\ paths only mean something on Windows
        var policy = new PermissionPolicy(PermissionPreset.WorkspaceWrite, @"C:\proj");
        Assert.IsType<ClaudeCodePermissionDecision.Allow>(ClaudeCodePermissions.Decide(Ask("Edit", new() { ["file_path"] = "/mnt/c/proj/a.cs" }), policy,
            mapPath: ClaudeCodeCli.FromWslPath));
        Assert.IsType<ClaudeCodePermissionDecision.Ask>(ClaudeCodePermissions.Decide(Ask("Edit", new() { ["file_path"] = "/mnt/c/other/a.cs" }), policy,
            mapPath: ClaudeCodeCli.FromWslPath));
    }

    // MARK: The shield

    [Fact]
    public void TheRecapIsMaskedLikeAnyRequestAndTheMessageOnlyForSecrets()
    {
        var shield = new ShieldOptions { Categories = SensitiveCategories.Default, VaultSecrets = ["vault-value-123456"] };
        var recap = ClaudeCodeRecap.Build([LlmMessage.User("mail pat@example.com, key AKIAIOSFODNN7EXAMPLE"), LlmMessage.Assistant("noted")])!;
        var masked = ClaudeCodeShield.Mask(recap, "use AKIAIOSFODNN7EXAMPLE and vault-value-123456 to write to pat@example.com",
            [new AttachmentWirePart.Text("config: AKIAIOSFODNN7EXAMPLE"), new AttachmentWirePart.Image("image/png", [1, 2, 3])], shield);
        Assert.StartsWith(ClaudeCodeRecap.Header + "\n" + ClaudeCodeShield.RecapNote, masked.Recap);
        Assert.DoesNotContain("pat@example.com", masked.Recap);
        Assert.DoesNotContain("AKIAIOSFODNN7EXAMPLE", masked.Recap);
        // One numbering: the key is the same placeholder in the recap, the message and the attachment's text.
        var key = SensitiveKinds.Placeholder(SensitiveKind.Secret, 1);
        Assert.Contains(key, masked.Recap);
        Assert.Contains(key, masked.Message);
        Assert.Equal("config: " + key, Assert.IsType<AttachmentWirePart.Text>(masked.Parts[0]).Value);
        Assert.DoesNotContain("vault-value-123456", masked.Message);
        // Claude Code would write a placeholder into a file as it is: the message keeps what isn't a secret.
        Assert.Contains("pat@example.com", masked.Message);
        Assert.Equal(1, masked.Unchecked);
        Assert.True(masked.Counts[SensitiveKind.Email] >= 1);

        var off = ClaudeCodeShield.Mask(recap, "AKIAIOSFODNN7EXAMPLE", [], null);
        Assert.Equal((recap, "AKIAIOSFODNN7EXAMPLE"), (off.Recap, off.Message));
        var clean = ClaudeCodeShield.Mask("[Earlier] hello", "hi", [], shield);
        Assert.Equal("[Earlier] hello", clean.Recap);

        // A recap with no secret in it is still masked for everything else (a map looks at a text only once).
        var plain = ClaudeCodeRecap.Build([LlmMessage.User("my address is pat@example.com"), LlmMessage.Assistant("noted")])!;
        var onlyEmail = ClaudeCodeShield.Mask(plain, "carry on with AKIAIOSFODNN7EXAMPLE", [], shield);
        Assert.StartsWith(ClaudeCodeRecap.Header + "\n" + ClaudeCodeShield.RecapNote, onlyEmail.Recap);
        Assert.Contains(SensitiveKinds.Placeholder(SensitiveKind.Email, 1), onlyEmail.Recap);
        Assert.DoesNotContain("pat@example.com", onlyEmail.Recap);
        Assert.Equal("carry on with " + SensitiveKinds.Placeholder(SensitiveKind.Secret, 1), onlyEmail.Message);
        Assert.Equal(1, onlyEmail.Counts[SensitiveKind.Secret]);
        Assert.Equal(1, onlyEmail.Counts[SensitiveKind.Email]);
    }

    // MARK: Attachments

    [Fact]
    public void PicturesAndPdfsStayWithinOneRequestAndThePictureLimitCountsTheirBase64()
    {
        Assert.Equal(3_750_000, ClaudeCodeCommand.Attachments.MaxImageBytes);
        var pdf = new byte[8 * 1024 * 1024];
        "%PDF-1.4"u8.CopyTo(pdf);
        var parts = ClaudeCodeCommand.AttachmentParts(
        [
            new MessageAttachment(AttachmentKind.File, "a.pdf", pdf),
            new MessageAttachment(AttachmentKind.File, "b.pdf", pdf),
            new MessageAttachment(AttachmentKind.File, "c.pdf", pdf),
        ]);
        Assert.Equal(2, parts.Count(p => p is AttachmentWirePart.Document));
        Assert.IsType<AttachmentWirePart.Text>(parts[2]);
        // A 4 MB picture would be over 5 MB as base64: it goes as text.
        var big = ClaudeCodeCommand.AttachmentParts([new MessageAttachment(AttachmentKind.Image, "big.png", new byte[4_000_000])]);
        Assert.IsType<AttachmentWirePart.Text>(Assert.Single(big));
    }

    // MARK: The stream

    [Fact]
    public void AWithdrawnPermissionQuestionIsReportedForTheLineThatSaidSo()
    {
        var stream = new ClaudeCodeStream(_ => { });
        stream.Feed("""{"type":"control_cancel_request","request_id":"req-9"}""");
        Assert.Equal("req-9", stream.CancelledRequest);
        stream.Feed("""{"type":"system","subtype":"status","status":"requesting"}""");
        Assert.Null(stream.CancelledRequest);
    }
}

/// <summary>A turn's process and its permission questions: nothing a card shows can hold a turn once its answer can't reach
/// Claude Code, a program that keeps stdout open after Claude Code exits can't either, and Vision closing stops every
/// claude process. (Its own collection: it changes the runner's static grace periods and stops every running turn.)</summary>
[Collection(ClaudeCodeProcessCollection.Name)]
public sealed class ClaudeCodeProcessTests
{
    private static readonly ClaudeCodeLaunch Launch = ClaudeCodeLaunch.Direct("claude");

    private const string Init = """{"type":"system","subtype":"init","session_id":"S","model":"claude-sonnet-5-5","apiKeySource":"none","permissionMode":"default"}""";
    private const string Question = """{"type":"control_request","request_id":"req-1","request":{"subtype":"can_use_tool","tool_name":"Bash","input":{"command":"rm -rf build"},"tool_use_id":"toolu_1"}}""";
    private const string Reply = """{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"done"}],"usage":{"input_tokens":10,"output_tokens":5}},"parent_tool_use_id":null,"session_id":"S","uuid":"a1"}""";
    private const string Result = """{"type":"result","subtype":"success","is_error":false,"result":"done","session_id":"S","total_cost_usd":0.01,"usage":{"input_tokens":10,"output_tokens":5},"num_turns":1}""";

    private static Task<ClaudeCodeTurnResult> Run(ScriptedClaudeProcess process,
        Func<ClaudeCodePermissionRequest, CancellationToken, Task<ClaudeCodePermissionDecision>> decide, CancellationToken ct = default) =>
        ClaudeCodeRunner.RunAsync(Launch, new ClaudeCodeTurnOptions { Model = "sonnet" }, "/work", ClaudeCodeCommand.UserMessage("go"),
            new ClaudeCodeStream(_ => { }), decide, ct, ScriptedClaudeProcess.Starter(process));

    /// <summary>A card nobody answers: completes only when its token says the answer can't be used.</summary>
    private static Func<ClaudeCodePermissionRequest, CancellationToken, Task<ClaudeCodePermissionDecision>> NeverAnswered(List<CancellationToken> tokens) =>
        (_, token) =>
        {
            lock (tokens) tokens.Add(token);
            return new TaskCompletionSource<ClaudeCodePermissionDecision>().Task;
        };

    [Fact]
    public async Task AQuestionTheCliWithdrawsIsTakenDownAndNotAnswered()
    {
        var process = new ScriptedClaudeProcess(
        [
            new(Init), new(Question), new("""{"type":"control_cancel_request","request_id":"req-1"}""", Unanswered: true), new(Reply), new(Result),
        ]);
        var tokens = new List<CancellationToken>();
        var outcome = await Run(process, NeverAnswered(tokens)).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Null(outcome.Failure);
        Assert.True(Assert.Single(tokens).IsCancellationRequested);
        Assert.DoesNotContain(process.Written, w => w.Contains("req-1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AQuestionStillUpWhenTheTurnEndsDoesntHoldIt()
    {
        var process = new ScriptedClaudeProcess([new(Init), new(Question), new(Result, Unanswered: true)]);
        var tokens = new List<CancellationToken>();
        var outcome = await Run(process, NeverAnswered(tokens)).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Null(outcome.Failure);
        Assert.True(Assert.Single(tokens).IsCancellationRequested);
    }

    [Fact]
    public async Task AQuestionAfterTheReplyIsInIsNotPutToAnyone()
    {
        var process = new ScriptedClaudeProcess([new(Init), new(Reply), new(Result), new(Question)]);
        var asked = 0;
        await Run(process, (_, _) =>
        {
            Interlocked.Increment(ref asked);
            return Task.FromResult<ClaudeCodePermissionDecision>(new ClaudeCodePermissionDecision.Allow());
        }).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(0, asked);
    }

    [Fact]
    public async Task AQuestionThatCantBePutIsAnsweredNo()
    {
        var process = new ScriptedClaudeProcess([new(Init), new(Question), new(Reply), new(Result)]);
        var outcome = await Run(process, (_, _) => throw new InvalidOperationException("no window")).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Null(outcome.Failure);
        var answer = JsonNode.Parse(process.Written.Single(w => w.Contains("req-1", StringComparison.Ordinal)))!;
        Assert.Equal("deny", (string?)answer["response"]!["response"]!["behavior"]);
        Assert.Equal(ClaudeCodePermissions.Declined, (string?)answer["response"]!["response"]!["message"]);
    }

    [Fact]
    public async Task AStopBeforeTheTurnStartsStartsNothing()
    {
        var started = false;
        using var stopped = new CancellationTokenSource();
        stopped.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ClaudeCodeRunner.RunAsync(Launch, new ClaudeCodeTurnOptions { Model = "sonnet" }, "/work",
            ClaudeCodeCommand.UserMessage("go"), new ClaudeCodeStream(_ => { }),
            (_, _) => Task.FromResult<ClaudeCodePermissionDecision>(new ClaudeCodePermissionDecision.Allow()), stopped.Token,
            (_, _, _) =>
            {
                started = true;
                throw new InvalidOperationException("must not start");
            }));
        Assert.False(started);
    }

    [Fact]
    public async Task AChildThatKeepsStdoutOpenAfterClaudeCodeExitsDoesntHoldTheTurn()
    {
        var old = ClaudeCodeRunner.DrainGrace;
        ClaudeCodeRunner.DrainGrace = TimeSpan.FromMilliseconds(200);
        try
        {
            var process = new ScriptedClaudeProcess([new(Init), new(Reply), new(Result)], holdStdoutAfterExit: true);
            var outcome = await Run(process, (_, _) => Task.FromResult<ClaudeCodePermissionDecision>(new ClaudeCodePermissionDecision.Allow()))
                .WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Null(outcome.Failure);
            Assert.Equal("done", outcome.Stream.LastReply);
        }
        finally
        {
            ClaudeCodeRunner.DrainGrace = old;
        }
    }

    [Fact]
    public async Task VisionClosingInterruptsEveryTurnAndKillsWhatDoesntStop()
    {
        // A CLI that never gets to its next line until it is killed.
        var process = new ScriptedClaudeProcess([new(Init), new(Reply, WaitFor: _ => false)]);
        var run = Run(process, (_, _) => Task.FromResult<ClaudeCodePermissionDecision>(new ClaudeCodePermissionDecision.Allow()));
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (ClaudeCodeRunner.RunningCount == 0 || process.Written.Count == 0)
        {
            if (DateTime.UtcNow > deadline) Assert.Fail("the turn didn't start");
            await Task.Delay(10);
        }
        ClaudeCodeRunner.StopAll(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.True(process.Killed);
        Assert.Contains(process.Written, w => w.Contains("\"interrupt\"", StringComparison.Ordinal));
        Assert.Equal(0, ClaudeCodeRunner.RunningCount);
    }

    [Fact]
    public async Task AOneOffsLongSystemPromptGoesInAFileThatIsDeletedAfterwards()
    {
        var process = ScriptedClaudeProcess.Replay(ClaudeFixtures.Lines("resume.jsonl"));
        var start = ScriptedClaudeProcess.Starter(process);
        string? seen = null;
        ClaudeCodeClient.LaunchForTesting = Launch;
        ClaudeCodeClient.StarterForTesting = (launch, args, cwd) =>
        {
            // What the CLI would read when it starts.
            var at = args.ToList().IndexOf("--system-prompt-file");
            if (at >= 0 && File.Exists(args[at + 1])) seen = File.ReadAllText(args[at + 1]);
            return start(launch, args, cwd);
        };
        try
        {
            var client = new ClaudeCodeClient(ClaudeCodeRoutes.Route("haiku"));
            var system = new string('s', ClaudeCodeCommand.SystemPromptFileThreshold + 1);
            await foreach (var e in client.StreamAsync(new LlmRequest(system, [LlmMessage.User("hi")], [], "haiku"))) _ = e;
            var used = process.Args.ToList();
            Assert.DoesNotContain("--system-prompt", used);
            Assert.Equal(system, seen);
            Assert.False(File.Exists(used[used.IndexOf("--system-prompt-file") + 1]));
            Assert.Contains("--strict-mcp-config", used);
        }
        finally
        {
            ClaudeCodeClient.LaunchForTesting = null;
            ClaudeCodeClient.StarterForTesting = null;
        }
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ClaudeCodeProcessCollection
{
    public const string Name = "Claude Code processes";
}
