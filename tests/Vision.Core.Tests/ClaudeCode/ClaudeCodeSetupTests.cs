using System.Text.Json.Nodes;
using Vision.Core;

namespace Vision.Core.Tests;

/// <summary>Everything around a Claude Code turn: the command line for each permission preset and session, what goes down
/// stdin, how permission questions are decided with the chat's own policy, finding the CLI and reading its version and
/// sign-in, starting it in WSL, the route it is stored as, and the recap a new session starts with.</summary>
public sealed class ClaudeCodeSetupTests
{
    // MARK: Command line

    [Fact]
    public void ATurnRunsHeadlessWithStreamJsonBothWaysAndPermissionQuestionsSentToVision()
    {
        var args = ClaudeCodeCommand.Args(new ClaudeCodeTurnOptions
        {
            Model = "sonnet",
            NewSession = "11111111-2222-3333-4444-555555555555",
            AppendSystemPrompt = "note",
            AddDirs = ["/attachments"],
        });
        Assert.Equal(
        [
            "-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose", "--include-partial-messages",
            "--model", "sonnet", "--permission-mode", "default", "--permission-prompt-tool", "stdio",
            "--session-id", "11111111-2222-3333-4444-555555555555", "--append-system-prompt", "note", "--add-dir", "/attachments",
        ], args);
    }

    [Theory]
    [InlineData(PermissionPreset.WorkspaceWrite, "default")]
    [InlineData(PermissionPreset.Plan, "plan")]
    [InlineData(PermissionPreset.FullAccess, "acceptEdits")]
    public void EachPresetMapsToAClaudeCodePermissionMode(PermissionPreset preset, string mode)
    {
        var args = ClaudeCodeCommand.Args(new ClaudeCodeTurnOptions { Model = "opus", Preset = preset });
        Assert.Equal(mode, args[args.ToList().IndexOf("--permission-mode") + 1]);
        Assert.DoesNotContain("--dangerously-skip-permissions", args);
    }

    [Fact]
    public void ALaterTurnResumesTheSessionAndThinkingBecomesEffortUnlessTheCliRefusedIt()
    {
        var args = ClaudeCodeCommand.Args(new ClaudeCodeTurnOptions { Model = "haiku", ResumeSession = "abc", NewSession = "ignored", Thinking = ThinkingLevel.High });
        Assert.Contains("--resume", args);
        Assert.Equal("abc", args[args.ToList().IndexOf("--resume") + 1]);
        Assert.DoesNotContain("--session-id", args);
        Assert.Equal("high", args[args.ToList().IndexOf("--effort") + 1]);
        Assert.Equal("low", ClaudeCodeCommand.Effort(ThinkingLevel.Off));
        Assert.Equal("max", ClaudeCodeCommand.Effort(ThinkingLevel.Max));

        var older = ClaudeCodeCommand.Args(new ClaudeCodeTurnOptions
        {
            Model = "haiku", Thinking = ThinkingLevel.High, PartialMessages = true,
            Unsupported = new HashSet<string> { "--effort", "--include-partial-messages" },
        });
        Assert.DoesNotContain("--effort", older);
        Assert.DoesNotContain("--include-partial-messages", older);
        Assert.Equal("--effort", ClaudeCodeCommand.UnknownOption("error: unknown option '--effort'\n(Did you mean --help?)"));
        Assert.Null(ClaudeCodeCommand.UnknownOption("Error: Session ID x is already in use."));
    }

    [Fact]
    public void ThePromptGoesAsOneLineWithPicturesAndPdfsAsContentBlocks()
    {
        var line = ClaudeCodeCommand.UserMessage("Look at this\nplease", [
            new AttachmentWirePart.Image("image/png", [1, 2, 3]),
            new AttachmentWirePart.Document("spec.pdf", [4, 5]),
            new AttachmentWirePart.Text("[Attached file: notes.md]\nhello"),
        ]);
        Assert.DoesNotContain('\n', line);
        var o = JsonNode.Parse(line)!;
        Assert.Equal("user", (string?)o["type"]);
        var content = o["message"]!["content"]!.AsArray();
        Assert.Equal(["text", "image", "document", "text"], content.Select(c => (string?)c!["type"]));
        Assert.Equal("Look at this\nplease", (string?)content[0]!["text"]);
        Assert.Equal(("base64", "image/png", "AQID"), ((string?)content[1]!["source"]!["type"], (string?)content[1]!["source"]!["media_type"], (string?)content[1]!["source"]!["data"]));
        Assert.Equal("application/pdf", (string?)content[2]!["source"]!["media_type"]);
        // The shape the real CLI took (bash-allow.sent.jsonl's first line).
        var sent = JsonNode.Parse(ClaudeFixtures.Lines("bash-allow.sent.jsonl")[0])!;
        Assert.Equal((string?)sent["type"], (string?)o["type"]);
        Assert.Equal((string?)sent["message"]!["role"], (string?)o["message"]!["role"]);
    }

    [Fact]
    public void TheInterruptIsTheControlRequestTheRealCliAcknowledged()
    {
        var mine = JsonNode.Parse(ClaudeCodeCommand.Interrupt("vision-stop-1"))!;
        var sent = JsonNode.Parse(ClaudeFixtures.Lines("interrupt.sent.jsonl")[1])!;
        Assert.True(JsonNode.DeepEquals(sent, mine));
        // ...and the CLI answered it.
        Assert.Contains(ClaudeFixtures.Lines("interrupt.jsonl"), l => l.Contains("\"request_id\": \"vision-stop-1\"", StringComparison.Ordinal) || l.Contains("\"request_id\":\"vision-stop-1\"", StringComparison.Ordinal));
    }

    // MARK: Permissions

    private static ClaudeCodePermissionRequest Ask(string tool, string json, string? reasonType = null, bool? approvable = null, bool interaction = false) =>
        new("r1", tool, JsonNode.Parse(json), "t1") { DecisionReasonType = reasonType, ClassifierApprovable = approvable, RequiresUserInteraction = interaction };

    private static string Root => OperatingSystem.IsWindows() ? @"C:\work\proj" : "/work/proj";
    private static string Inside(string name) => Path.Combine(Root, name).Replace("\\", "\\\\");
    private static string Outside => (OperatingSystem.IsWindows() ? @"C:\elsewhere\x.txt" : "/elsewhere/x.txt").Replace("\\", "\\\\");

    [Fact]
    public void WorkspaceWriteLetsProjectEditsAndReadOnlyCommandsGoAheadAndAsksForTheRest()
    {
        var policy = new PermissionPolicy(PermissionPreset.WorkspaceWrite, Root);
        Assert.IsType<ClaudeCodePermissionDecision.Allow>(ClaudeCodePermissions.Decide(Ask("Edit", $$"""{"file_path":"{{Inside("a.cs")}}"}"""), policy));
        var outside = Assert.IsType<ClaudeCodePermissionDecision.Ask>(ClaudeCodePermissions.Decide(Ask("Write", $$"""{"file_path":"{{Outside}}"}"""), policy));
        Assert.Equal("write_file", outside.Name);
        Assert.StartsWith("Write outside the project: ", outside.Detail);
        Assert.IsType<ClaudeCodePermissionDecision.Allow>(ClaudeCodePermissions.Decide(Ask("Bash", """{"command":"git log --oneline"}"""), policy));
        var command = Assert.IsType<ClaudeCodePermissionDecision.Ask>(ClaudeCodePermissions.Decide(Ask("Bash", """{"command":"rm -rf build"}"""), policy));
        Assert.Equal(("run_shell_command", "Run command: rm -rf build"), (command.Name, command.Detail));
        Assert.IsType<ClaudeCodePermissionDecision.Allow>(ClaudeCodePermissions.Decide(Ask("WebFetch", """{"url":"https://example.com"}"""), policy));
        Assert.IsType<ClaudeCodePermissionDecision.Ask>(ClaudeCodePermissions.Decide(Ask("mcp__github__create_issue", """{"title":"x"}"""), policy));
    }

    [Fact]
    public void PlanOnlyShowsThePlanAndChangesNothingAndFullAccessLetsEverythingGoBarSafetyChecks()
    {
        var plan = new PermissionPolicy(PermissionPreset.Plan, Root);
        var shown = Assert.IsType<ClaudeCodePermissionDecision.Deny>(ClaudeCodePermissions.Decide(Ask("ExitPlanMode", "{}", interaction: true), plan));
        Assert.Equal(ClaudeCodePermissions.PlanShown, shown.Message);
        Assert.IsType<ClaudeCodePermissionDecision.Ask>(ClaudeCodePermissions.Decide(Ask("Edit", $$"""{"file_path":"{{Inside("a.cs")}}"}"""), plan));

        var full = new PermissionPolicy(PermissionPreset.FullAccess, Root);
        Assert.IsType<ClaudeCodePermissionDecision.Allow>(ClaudeCodePermissions.Decide(Ask("Bash", """{"command":"rm -rf build"}"""), full));
        Assert.IsType<ClaudeCodePermissionDecision.Allow>(ClaudeCodePermissions.Decide(Ask("mcp__x__y", "{}"), full));
        Assert.IsType<ClaudeCodePermissionDecision.Allow>(ClaudeCodePermissions.Decide(Ask("ExitPlanMode", "{}", interaction: true), full));
        // A safety check Claude Code says needs a person asks, whatever the preset.
        Assert.IsType<ClaudeCodePermissionDecision.Ask>(ClaudeCodePermissions.Decide(Ask("Bash", """{"command":"rm -rf /"}""", "safetyCheck", false), full));
        Assert.IsType<ClaudeCodePermissionDecision.Deny>(ClaudeCodePermissions.Decide(Ask("AskUserQuestion", "{}", interaction: true), full));
    }

    [Fact]
    public void TheRealCliPermissionQuestionParses()
    {
        var line = ClaudeFixtures.Lines("read-edit.jsonl").Single(l => l.Contains("can_use_tool", StringComparison.Ordinal));
        var request = ClaudeCodePermissionRequest.From(JsonNode.Parse(line))!;
        Assert.Equal(("Edit", "toolu_fixture_2_0", "hello.txt"), (request.ToolName, request.ToolUseId, request.Description));
        Assert.Null(ClaudeCodePermissionRequest.From(JsonNode.Parse("""{"type":"control_request","request_id":"x","request":{"subtype":"interrupt"}}""")));
    }

    // MARK: Finding it

    [Fact]
    public void TheVersionAndSignInAreReadFromTheCli()
    {
        Assert.Equal("2.1.299", ClaudeCodeCli.ParseVersionText(ClaudeFixtures.Text("version.txt")));
        Assert.Null(ClaudeCodeCli.ParseVersionText("command not found"));
        var signedOut = ClaudeCodeCli.ParseAuth(ClaudeFixtures.Text("auth-status-logged-out.json"))!;
        Assert.Equal((false, "none", "firstParty"), (signedOut.LoggedIn, signedOut.Method, signedOut.Provider));
        var max = ClaudeCodeCli.ParseAuth("""{"loggedIn":true,"authMethod":"claude.ai","apiProvider":"firstParty","account":{"email":"pat@503cm.com","subscriptionType":"max"}}""")!;
        Assert.Equal("your Claude Max plan", max.Describe());
        Assert.Equal("pat@503cm.com", max.Email);
        var key = ClaudeCodeCli.ParseAuth(ClaudeFixtures.Text("auth-status-api-key.json"))!; // the real CLI with ANTHROPIC_API_KEY set
        Assert.Equal((true, "api_key"), (key.LoggedIn, key.Method));
        Assert.Contains("billed to that key", key.Describe());
        Assert.Null(ClaudeCodeCli.ParseAuth("Logged in as pat"));
    }

    [Fact]
    public void CandidatesComeExplicitFirstThenPathThenInstallersThenTheDesktopAppsNewestCopy()
    {
        var files = new HashSet<string>(StringComparer.Ordinal)
        {
            Full("/opt/mine/claude"), Full("/usr/bin/claude"), Full("/home/u/.local/bin/claude"),
            Full("/bundled/2.1.295/abc/claude"), Full("/bundled/2.1.299/def/claude"),
        };
        var dirs = new Dictionary<string, string[]>
        {
            [Full("/bundled")] = [Full("/bundled/2.1.295"), Full("/bundled/2.1.299"), Full("/bundled/notes")],
            [Full("/bundled/2.1.295")] = [Full("/bundled/2.1.295/abc")],
            [Full("/bundled/2.1.299")] = [Full("/bundled/2.1.299/def")],
        };
        var search = new ClaudeCodeSearch
        {
            ExplicitPath = Full("/opt/mine/claude"),
            PathDirectories = [Full("/nowhere"), Full("/usr/bin")],
            FileNames = ["claude"],
            KnownLocations = [Full("/home/u/.local/bin/claude")],
            BundledRoots = [Full("/bundled")],
            FileExists = files.Contains,
            Directories = d => dirs.GetValueOrDefault(d) ?? [],
            IgnoreCase = false,
        };
        Assert.Equal([Full("/opt/mine/claude"), Full("/usr/bin/claude"), Full("/home/u/.local/bin/claude"), Full("/bundled/2.1.299/def/claude"),
                      Full("/bundled/2.1.295/abc/claude")], ClaudeCodeCli.Candidates(search));
    }

    private static string Full(string path) => Path.GetFullPath(path);

    [Fact]
    public void AnNpmShimRunsAsNodeWithItsScriptAndAnythingElseDirectly()
    {
        var dir = Path.GetFullPath("/npm");
        var script = Path.Combine(dir, "node_modules", "@anthropic-ai", "claude-code", "cli.js");
        var shim = ClaudeCodeCli.LaunchFor(Path.Combine(dir, "claude.cmd"), p => p == script);
        Assert.Equal(("node", script), (shim.Program, shim.Prefix.Single()));
        var cmd = ClaudeCodeCli.LaunchFor(Path.Combine(dir, "claude.cmd"), _ => false);
        Assert.Equal(["/d", "/s", "/c", Path.Combine(dir, "claude.cmd")], cmd.Prefix);
        Assert.Empty(ClaudeCodeCli.LaunchFor("/usr/bin/claude").Prefix);
    }

    [Fact]
    public async Task TheCheckSaysMissingBrokenSignedOutOrReady()
    {
        var search = new ClaudeCodeSearch { ExplicitPath = Full("/c/claude"), FileExists = _ => true, IgnoreCase = false };
        ClaudeCodeCommandRunner Runner(string version, string auth, int authExit = 0) => (_, args, _, _) =>
            Task.FromResult(args[0] == "--version" ? new ClaudeCodeCommandResult(0, version, "", false) : new ClaudeCodeCommandResult(authExit, auth, "", false));

        Assert.IsType<ClaudeCodeStatus.Missing>(await ClaudeCodeCli.CheckAsync(new ClaudeCodeSearch { FileExists = _ => false }, Runner("", "")));
        Assert.IsType<ClaudeCodeStatus.Broken>(await ClaudeCodeCli.CheckAsync(search, Runner("", "")));
        var signedOut = Assert.IsType<ClaudeCodeStatus.SignedOut>(await ClaudeCodeCli.CheckAsync(search, Runner("2.1.299 (Claude Code)", ClaudeFixtures.Text("auth-status-logged-out.json"))));
        Assert.Equal("2.1.299", signedOut.Version);
        Assert.Contains("claude auth login", signedOut.Summary);
        var ready = Assert.IsType<ClaudeCodeStatus.Ready>(await ClaudeCodeCli.CheckAsync(search, Runner("2.1.299 (Claude Code)", """{"loggedIn":true,"authMethod":"claude.ai"}""")));
        Assert.Equal(Full("/c/claude"), ready.Launch.Program);
        Assert.Contains("your Claude subscription", ready.Summary);
        // An older CLI without `auth status`: ready, the first turn will say whether it's signed in.
        var older = Assert.IsType<ClaudeCodeStatus.Ready>(await ClaudeCodeCli.CheckAsync(search, Runner("1.0.88 (Claude Code)", "error: unknown command 'auth'", 1)));
        Assert.Null(older.Auth);
    }

    [Fact]
    public void InWslItRunsThroughALoginShellInTheSameFolder()
    {
        var info = ClaudeCodeLaunch.InWsl("Ubuntu-24.04").StartInfo(["-p", "--model", "sonnet"], @"C:\work\proj");
        Assert.Equal("wsl.exe", info.FileName);
        Assert.Equal(["-d", "Ubuntu-24.04", "--cd", @"C:\work\proj", "--", "bash", "-lc", "exec claude \"$@\"", "claude", "-p", "--model", "sonnet"], info.ArgumentList);
        Assert.Equal(@"C:\Users\pat\a.txt", ClaudeCodeCli.FromWslPath("/mnt/c/Users/pat/a.txt"));
    }

    [Fact]
    public void ItStartsWithoutTheVariablesOfAParentClaudeCodeSession()
    {
        var env = new Dictionary<string, string?> { ["CLAUDECODE"] = "1", ["CLAUDE_CODE_ENTRYPOINT"] = "cli", ["PATH"] = "/bin", ["CLAUDE_CODE_USE_BEDROCK"] = "1" };
        ClaudeCodeEnvironment.Prepare(env);
        Assert.False(env.ContainsKey("CLAUDECODE"));
        Assert.False(env.ContainsKey("CLAUDE_CODE_ENTRYPOINT"));
        Assert.Equal("/bin", env["PATH"]);
        Assert.Equal("1", env["CLAUDE_CODE_USE_BEDROCK"]); // the person's own Claude Code settings stay
    }

    // MARK: The route

    [Fact]
    public void AClaudeCodeRouteIsAnOrdinaryRouteNobodyCallsOverHttp()
    {
        var route = ClaudeCodeRoutes.Route("sonnet");
        Assert.Equal(("Claude Code|sonnet", "claude-code://local"), (route.RouteId, route.BaseUrl));
        Assert.Equal(ProviderKind.OpenAICompat, route.Kind); // older builds can still read the settings file
        Assert.True(ClaudeCodeRoutes.IsClaudeCode(route));
        Assert.False(route.IsPrivateRoute);
        Assert.False(route.NeedsApiKey);
        Assert.False(ClaudeCodeRoutes.UsesWsl(route));
        var wsl = ClaudeCodeRoutes.Route("opus", wsl: true, distro: "Ubuntu 24.04");
        Assert.True(ClaudeCodeRoutes.UsesWsl(wsl));
        Assert.Equal("Ubuntu 24.04", ClaudeCodeRoutes.WslDistro(wsl));
        Assert.Null(ClaudeCodeRoutes.WslDistro(ClaudeCodeRoutes.Route("opus", wsl: true)));
        Assert.False(ClaudeCodeRoutes.IsClaudeCode(new ProviderProfile(ProviderKind.OpenAICompat, "Spark", "http://spark:8002/v1", "qwen")));
    }

    [Fact]
    public async Task ItsClientReportsTheWindowTheCliLastReportedAndRefusesRawModelCalls()
    {
        var client = Assert.IsType<ClaudeCodeClient>(ProviderClients.Create(ClaudeCodeRoutes.Route("haiku")));
        Assert.Equal(ClaudeCodeRoutes.DefaultContextWindow, (await client.ModelInfoAsync()).ContextWindow);
        ClaudeCodeRoutes.LearnWindow("haiku", 1_000_000);
        Assert.Equal(1_000_000, (await client.ModelInfoAsync()).ContextWindow);
        var error = await Assert.ThrowsAsync<LlmException>(async () =>
        {
            await foreach (var _ in client.StreamAsync(new LlmRequest("", [LlmMessage.User("hi")], [], "haiku"))) { }
        });
        Assert.Contains("Claude Code answers chats only", error.Message);
    }

    // MARK: Recap

    [Fact]
    public void ANewSessionHearsTheMostRecentHistoryThatFits()
    {
        Assert.Null(ClaudeCodeRecap.Build([]));
        var history = new List<LlmMessage>
        {
            LlmMessage.User("first question " + new string('a', 5000)),
            LlmMessage.Assistant("first answer"),
            LlmMessage.User("second question"),
            LlmMessage.Assistant("second answer"),
        };
        var recap = ClaudeCodeRecap.Build(history, budget: 1000)!;
        Assert.StartsWith(ClaudeCodeRecap.Header, recap);
        Assert.Contains("Person: second question", recap);
        Assert.Contains("Assistant: second answer", recap);
        Assert.True(recap.IndexOf("second question", StringComparison.Ordinal) < recap.IndexOf("second answer", StringComparison.Ordinal));
        Assert.True(recap.Length < 1600);
        Assert.EndsWith("The person's new message follows.]", recap);
    }
}
