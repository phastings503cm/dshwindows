using System.Text.Json.Nodes;

namespace Dsh.Core.Tests;

public sealed class Json5Tests
{
    [Fact]
    public void ParsesWhatOpenClawWrites()
    {
        const string text = """
            // top comment
            {
              unquoted: 'single quoted',   /* inline */
              "quoted-key": [1, 2, 3,],
              nested: { yes: true, no: false, nothing: null, hex: 0xFF, neg: -5, frac: .5, plus: +3, },
              url: "http://x/y", // not a comment inside a string: http://z
            }
            """;
        var node = Json5.TryParse(text, out var error);
        Assert.Null(error);
        var obj = Assert.IsType<JsonObject>(node);
        Assert.Equal("single quoted", (string?)obj["unquoted"]);
        Assert.Equal(3, obj["quoted-key"]!.AsArray().Count);
        Assert.True((bool)obj["nested"]!["yes"]!);
        Assert.Null(obj["nested"]!["nothing"]);
        Assert.Equal(255, (int)obj["nested"]!["hex"]!);
        Assert.Equal(-5, (int)obj["nested"]!["neg"]!);
        Assert.Equal(0.5, (double)obj["nested"]!["frac"]!);
        Assert.Equal(3, (int)obj["nested"]!["plus"]!);
        Assert.Equal("http://x/y", (string?)obj["url"]);
    }

    [Fact]
    public void ReadsEscapesAndUnicode()
    {
        const string text = """
            { a: "line1\nline2 A \x42 \"q\"", b: 'it\'s', c: "tab\tend", d: "multi\
            line" }
            """;
        var obj = Json5.TryParse(text, out _)!.AsObject();
        Assert.Equal("line1\nline2 A B \"q\"", (string?)obj["a"]);
        Assert.Equal("it's", (string?)obj["b"]);
        Assert.Equal("tab\tend", (string?)obj["c"]);
        Assert.Equal("multiline", (string?)obj["d"]);
    }

    [Fact]
    public void StrictJsonIsJson5()
    {
        var node = Json5.TryParse("""{"a":[{"b":1.5e2},"x"],"c":{}}""", out var error);
        Assert.Null(error);
        Assert.Equal(150.0, (double)node!["a"]![0]!["b"]!);
    }

    [Fact]
    public void ABomAndTopLevelArraysAreFine()
    {
        Assert.IsType<JsonArray>(Json5.TryParse("﻿[1, 2]", out _));
        Assert.Equal(1, (int)Json5.TryParse("1", out _)!);
    }

    [Theory]
    [InlineData("{ a: ", "unexpected end")]
    [InlineData("{ a 1 }", "expected ':'")]
    [InlineData("{ a: 1 b: 2 }", "expected ','")]
    [InlineData("[1, 2", "unterminated array")]
    [InlineData("{ a: \"open", "unterminated string")]
    [InlineData("/* never closed", "unterminated comment")]
    [InlineData("{} extra", "after the value")]
    [InlineData("", "unexpected end")]
    public void BadInputReportsWhereInsteadOfThrowing(string text, string fragment)
    {
        Assert.Null(Json5.TryParse(text, out var error));
        Assert.Contains(fragment, error);
        Assert.Contains("line", error);
    }

    [Fact]
    public void DeepNestingIsRefusedNotStackOverflowed()
    {
        var text = new string('[', 5_000) + new string(']', 5_000);
        Assert.Null(Json5.TryParse(text, out var error));
        Assert.Contains("too deeply", error);
    }
}

public sealed class OpenClawScannerTests : IDisposable
{
    private readonly OpenClawFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [Fact]
    public async Task FindsTheInstallInTheHomeFolder()
    {
        var installs = await OpenClawScanner.FindAsync(_fx.Local());
        var install = Assert.Single(installs);
        Assert.Equal(_fx.State, install.StateDir);
        Assert.Equal(Path.Combine(_fx.State, "openclaw.json"), install.ConfigPath);
        Assert.Equal("openclaw", install.Flavor);
    }

    [Fact]
    public async Task FindsProfilesAndOlderNames()
    {
        OpenClawFixture.Write(Path.Combine(_fx.Home, ".openclaw-work", "openclaw.json"), "{}");
        OpenClawFixture.Write(Path.Combine(_fx.Home, ".clawdbot", "clawdbot.json"), "{}");
        OpenClawFixture.Write(Path.Combine(_fx.Home, ".moltbot", "moltbot.json"), "{}");
        var installs = await OpenClawScanner.FindAsync(_fx.Local());
        Assert.Equal(4, installs.Count);
        Assert.Contains(installs, i => i.StateDir.EndsWith(".openclaw-work") && i.Flavor == "openclaw");
        Assert.Contains(installs, i => i.Flavor == "clawdbot");
        Assert.Contains(installs, i => i.Flavor == "moltbot");
    }

    [Fact]
    public async Task HonoursTheStateDirectoryVariable()
    {
        var elsewhere = Path.Combine(_fx.Root.Path, "srv", "claw-state");
        OpenClawFixture.Write(Path.Combine(elsewhere, "openclaw.json"), "{}");
        var installs = await OpenClawScanner.FindAsync(_fx.Local(new Dictionary<string, string> { ["OPENCLAW_STATE_DIR"] = elsewhere }));
        Assert.Equal(elsewhere, installs[0].StateDir); // an explicit setting comes first
        Assert.Equal("OPENCLAW_STATE_DIR", installs[0].FoundBy);
        Assert.Equal(2, installs.Count);
    }

    [Fact]
    public async Task SearchesForAConfigFileInAnOddPlace()
    {
        var odd = Path.Combine(_fx.Home, "projects", "assistant", "data");
        OpenClawFixture.Write(Path.Combine(odd, "openclaw.json"), "{}");
        var installs = await OpenClawScanner.FindAsync(_fx.Local());
        Assert.Contains(installs, i => i.StateDir == odd && i.FoundBy == "search");
    }

    [Fact]
    public async Task ASearchSkipsHeavyFolders()
    {
        OpenClawFixture.Write(Path.Combine(_fx.Home, "code", "node_modules", "pkg", "openclaw.json"), "{}");
        var installs = await OpenClawScanner.FindAsync(_fx.Local());
        Assert.DoesNotContain(installs, i => i.StateDir.Contains("node_modules"));
    }

    [Fact]
    public async Task ANonInstallIsIgnoredAndAWorkspaceAloneIsAccepted()
    {
        Directory.CreateDirectory(Path.Combine(_fx.Home, "openclaw")); // empty: not an install
        Assert.DoesNotContain(await OpenClawScanner.FindAsync(_fx.Local()), i => i.StateDir.EndsWith(Path.DirectorySeparatorChar + "openclaw"));
        var install = await OpenClawScanner.InspectAsync(_fx.Local(), _fx.Workspace, "typed");
        Assert.NotNull(install); // a bare workspace (skills/ + MEMORY.md) can be imported too
        Assert.Null(await OpenClawScanner.InspectAsync(_fx.Local(), Path.Combine(_fx.Home, "nowhere"), "typed"));
    }

    [Fact]
    public async Task NothingFoundIsAnEmptyList()
    {
        using var empty = new TempDirectory("dsh-nothing");
        Assert.Empty(await OpenClawScanner.FindAsync(new LocalFileSource(empty.Path, new Dictionary<string, string>())));
    }

    [Fact]
    public async Task ReportsProgress()
    {
        // (Reported straight to the list: Progress<T> hands its messages over on the thread pool, a moment later.)
        var messages = new List<string>();
        await OpenClawScanner.FindAsync(_fx.Local(), new Reporting(messages.Add));
        Assert.Contains(messages, m => m.StartsWith("Found OpenClaw in"));
    }

    private sealed class Reporting(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
}

public sealed class OpenClawReaderTests : IDisposable
{
    private readonly OpenClawFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    private async Task<OpenClawBundle> Read(IFileSource? source = null)
    {
        source ??= _fx.Local();
        var install = (await OpenClawScanner.FindAsync(source, options: NoRoaming)).Single();
        return await OpenClawReader.ReadAsync(source, install);
    }

    /// <summary>Keep remote scans inside the fixture.</summary>
    internal static OpenClawScanOptions NoRoaming => new() { HomeParents = [], KnownFolders = [], SearchRoots = [] };

    // MARK: - Skills

    [Fact]
    public async Task FindsSkillsInEveryPlaceOpenClawKeepsThem()
    {
        var bundle = await Read();
        var names = bundle.Skills.Where(s => !s.Shadowed).Select(s => s.Name).Order().ToList();
        Assert.Equal(["onboarding", "pdf-tools", "translate", "weather", "weekly-report"], names);

        var pdf = bundle.Skills.Single(s => s.Name == "pdf-tools" && !s.Shadowed);
        Assert.Equal("workspace", pdf.Location);
        Assert.Equal("Extract text and tables from PDF files", pdf.Description);
        Assert.True(pdf.HasScripts);
        Assert.Equal(["SKILL.md", "notes.txt", "scripts/extract.py"], pdf.Files);
        // The managed copy of the same name is hidden by the workspace one, as in OpenClaw.
        Assert.Single(bundle.Skills, s => s.Name == "pdf-tools" && s.Shadowed);
        Assert.Equal("managed", bundle.Skills.Single(s => s.Name == "weather").Location);
        Assert.Equal("extra", bundle.Skills.Single(s => s.Name == "translate").Location);
        Assert.False(bundle.Skills.Single(s => s.Name == "weekly-report").HasScripts);
        Assert.DoesNotContain(bundle.Skills, s => s.Directory.EndsWith("not-a-skill")); // no manifest
    }

    // MARK: - Memory

    [Fact]
    public async Task ReadsMemoryIntoNotesWithSensibleDefaults()
    {
        var bundle = await Read();
        var byKind = bundle.NoteFiles.GroupBy(f => f.Kind).ToDictionary(g => g.Key, g => g.ToList());
        Assert.Equal(["Deploy", "People"], byKind[OpenClawNoteKind.Memory].Single().Notes.Select(n => n.Title));
        Assert.All(byKind[OpenClawNoteKind.Memory].Single().Notes, n => Assert.Equal("import:openclaw/MEMORY.md", n.Source));
        Assert.True(byKind[OpenClawNoteKind.Memory].Single().SelectedByDefault);
        Assert.True(byKind[OpenClawNoteKind.About].Single().SelectedByDefault);
        Assert.False(byKind[OpenClawNoteKind.Persona].Single().SelectedByDefault);
        Assert.False(byKind[OpenClawNoteKind.Instructions].Single().SelectedByDefault);

        // Sixty daily logs are found; only the newest are ticked.
        var daily = byKind[OpenClawNoteKind.Daily];
        Assert.Equal(60, daily.Count);
        Assert.Equal("memory/2026-03-01.md", daily[0].Name);
        Assert.Equal(45, daily.Count(d => d.SelectedByDefault));
        Assert.All(daily.Take(45), d => Assert.True(d.SelectedByDefault));
        Assert.Contains("daily", daily[0].Notes[0].Tags);
        Assert.Equal(new DateOnly(2026, 3, 1), DateOnly.FromDateTime(daily[0].Notes[0].CreatedAt!.Value.LocalDateTime));
    }

    // MARK: - Credentials

    [Fact]
    public async Task FindsEveryKeyTokenAndPasswordWorthKeeping()
    {
        var bundle = await Read();
        var byName = bundle.Credentials.ToDictionary(c => c.Name);

        Assert.Equal(OpenClawFixture.AnthropicKey, byName["ANTHROPIC_API_KEY"].Secret.Reveal());
        Assert.Equal(VaultKind.ApiKey, byName["ANTHROPIC_API_KEY"].Kind);
        Assert.Equal("anthropic", byName["ANTHROPIC_API_KEY"].Provider);
        Assert.Equal(OpenClawFixture.OpenRouterKey, byName["OPENROUTER_API_KEY"].Secret.Reveal());
        Assert.Equal(OpenClawFixture.HuggingFaceToken, byName["HF_TOKEN"].Secret.Reveal());
        Assert.Equal(VaultKind.Token, byName["HF_TOKEN"].Kind);
        Assert.Equal("sk-proj-workworkworkworkwork1234", byName["OPENCLAW_OPENAI_WORK_TOKEN"].Secret.Reveal());
        Assert.Equal("BSA-brave-key-1234567890abcdef", byName["BRAVE_API_KEY"].Secret.Reveal());
        Assert.Equal(OpenClawFixture.TelegramToken, byName["OPENCLAW_CHANNELS_TELEGRAM_BOTTOKEN"].Secret.Reveal());
        Assert.Equal(OpenClawFixture.GatewayToken, byName["OPENCLAW_GATEWAY_TOKEN"].Secret.Reveal());
        Assert.Equal(OpenClawFixture.GithubToken, byName["OPENCLAW_SKILLS_ENTRIES_GITHUB_APIKEY"].Secret.Reveal());
        Assert.Equal("github", byName["OPENCLAW_SKILLS_ENTRIES_GITHUB_APIKEY"].Provider);
        // Every name is one the vault accepts.
        Assert.All(bundle.Credentials, c => Assert.True(CredentialVault.IsValidName(c.Name), c.Name));
    }

    [Fact]
    public async Task LeavesOutWhatIsNotASecret()
    {
        var bundle = await Read();
        var values = bundle.Credentials.Select(c => c.Secret.Reveal()).ToList();
        Assert.DoesNotContain("debug", values);            // LOG_LEVEL
        Assert.DoesNotContain("18789", values);            // PORT
        Assert.DoesNotContain("ollama-local", values);     // a local server's dummy key is not a secret worth vaulting
        Assert.DoesNotContain("not-a-secret", values);
        Assert.DoesNotContain("cl100k_base", values);
        Assert.DoesNotContain("${DISCORD_TOKEN}", values);
        Assert.DoesNotContain(bundle.Credentials, c => c.Name.Contains("MAXTOKENS") || c.Name.Contains("TOKENLIMIT"));
        Assert.DoesNotContain(bundle.Credentials, c => c.Origin.Contains("oauth", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AValueUnderTwoNamesIsStoredOnce()
    {
        var bundle = await Read();
        // The Anthropic key is in auth-profiles.json and in the (incompatible) provider block.
        var anthropic = bundle.Credentials.Where(c => c.Secret.Reveal() == OpenClawFixture.AnthropicKey).ToList();
        var single = Assert.Single(anthropic);
        Assert.Contains("OPENCLAW_MODELS_PROVIDERS_CLAUDE_APIKEY", single.AlsoNamed);
        Assert.Equal(bundle.Credentials.Count, bundle.Credentials.Select(c => c.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public async Task ReferencesToTheEnvFileAreResolvedAndUnknownOnesAreReported()
    {
        var bundle = await Read();
        // ${SPARK_KEY} resolves from .env, and travels with the model server rather than the vault.
        Assert.Equal(OpenClawFixture.SparkKey, bundle.ModelServers.Single(s => s.Id == "spark").ApiKey!.Reveal());
        Assert.DoesNotContain(bundle.Credentials, c => c.Secret.Reveal() == OpenClawFixture.SparkKey && c.Name.Contains("PROVIDERS"));
        // ${DISCORD_TOKEN} has no value anywhere.
        Assert.Contains(bundle.Left, l => l.Contains("${DISCORD_TOKEN}"));
    }

    [Fact]
    public async Task SecretsNeverAppearInPrintedForms()
    {
        var bundle = await Read();
        var printed = bundle.ToString() + string.Join("\n", bundle.Credentials.Select(c => c.ToString()))
                      + string.Join("\n", bundle.ModelServers.Select(s => s.ToString())) + string.Join("\n", bundle.Left) + string.Join("\n", bundle.Problems);
        foreach (var secret in new[] { OpenClawFixture.SparkKey, OpenClawFixture.OpenRouterKey, OpenClawFixture.AnthropicKey,
                     OpenClawFixture.TelegramToken, OpenClawFixture.GatewayToken, OpenClawFixture.GithubToken })
            Assert.DoesNotContain(secret, printed);
        var credential = bundle.Credentials.First();
        Assert.StartsWith("[secret sha256:", credential.Secret.ToString());
        Assert.DoesNotContain(credential.Secret.Reveal()[6..], credential.Secret.Hint);
    }

    // MARK: - Model servers

    [Fact]
    public async Task ReadsModelServersAndFlagsWhatDshCannotSpeak()
    {
        var bundle = await Read();
        var spark = bundle.ModelServers.Single(s => s.Id == "spark");
        Assert.True(spark.Compatible);
        Assert.Equal("http://127.0.0.1:8002/v1", spark.BaseUrl); // read on this PC: localhost is this PC
        Assert.Equal(["qwen3-coder", "qwen3-32b"], spark.Models);
        Assert.Equal(131072, spark.ContextWindow);
        Assert.Equal("qwen3-coder", spark.DefaultModel);
        Assert.False(bundle.ModelServers.Single(s => s.Id == "claude").Compatible);
        Assert.Contains("anthropic-messages", bundle.ModelServers.Single(s => s.Id == "claude").Note);
        Assert.True(bundle.ModelServers.Single(s => s.Id == "ollama").Compatible);
        Assert.Null(bundle.ModelServers.Single(s => s.Id == "ollama").DefaultModel);
    }

    [UnixFact]
    public async Task ALocalhostServerOnAnotherMachineIsRewrittenToThatMachine()
    {
        var bundle = await Read(_fx.Remote(host: "spark-3.local"));
        var spark = bundle.ModelServers.Single(s => s.Id == "spark");
        Assert.Equal("http://spark-3.local:8002/v1", spark.BaseUrl);
        Assert.Contains("localhost on spark-3.local", spark.Note);
        // A LAN address is already reachable and stays as written.
        Assert.Equal("http://192.168.1.50:11434/v1", bundle.ModelServers.Single(s => s.Id == "ollama").BaseUrl);
    }

    // MARK: - What isn't brought over

    [Fact]
    public async Task SaysPlainlyWhatWasLeftBehind()
    {
        var bundle = await Read();
        Assert.Contains(bundle.Left, l => l.StartsWith("1 OAuth sign-in") && l.Contains("openai-codex"));
        Assert.Contains(bundle.Left, l => l.StartsWith("Chat channels (telegram, discord)"));
        Assert.Contains(bundle.Left, l => l.StartsWith("2 scheduled jobs"));
        Assert.Contains(bundle.Left, l => l.StartsWith("2 saved conversations"));
        Assert.Empty(bundle.Problems);
    }

    [Fact]
    public async Task ABrokenConfigStillYieldsSkillsAndMemory()
    {
        File.WriteAllText(Path.Combine(_fx.State, "openclaw.json"), "{ agents: { defaults: ");
        var bundle = await Read();
        Assert.Single(bundle.Problems, p => p.Contains("Couldn't make sense of openclaw.json"));
        Assert.NotEmpty(bundle.NoteFiles);            // the default workspace is still found by its folder name
        Assert.NotEmpty(bundle.Skills);
        Assert.Contains(bundle.Credentials, c => c.Name == "ANTHROPIC_API_KEY"); // auth-profiles.json is independent of the config
        Assert.Empty(bundle.ModelServers);
    }

    [Fact]
    public async Task AnotherWorkspaceNamedInTheConfigIsRead()
    {
        var other = Path.Combine(_fx.Root.Path, "elsewhere", "work-ws");
        OpenClawFixture.Write(Path.Combine(other, "MEMORY.md"), "## Other\nThe work workspace remembers other things entirely.\n");
        File.WriteAllText(Path.Combine(_fx.State, "openclaw.json"), $$"""{ agents: { list: [ { id: "work", workspace: "{{other.Replace("\\", "\\\\")}}" } ] } }""");
        var bundle = await Read();
        Assert.Contains(other, bundle.Workspaces);
        // With more than one workspace the notes' source says which.
        Assert.Contains(bundle.NoteFiles, f => f.Notes.Any(n => n.Source == "import:openclaw/work-ws/MEMORY.md"));
        Assert.Contains(bundle.NoteFiles, f => f.Notes.Any(n => n.Source == "import:openclaw/workspace/MEMORY.md"));
    }

    // MARK: - The remote path reads the same things through a real shell

    [UnixFact]
    public async Task ReadingThroughAShellFindsTheSameThings()
    {
        var local = await Read();
        var remote = await Read(_fx.Remote());
        Assert.Equal(local.Skills.Select(s => (s.Name, s.Shadowed, s.HasScripts)), remote.Skills.Select(s => (s.Name, s.Shadowed, s.HasScripts)));
        Assert.Equal(local.NoteFiles.Select(f => (f.Name, f.Notes.Count)), remote.NoteFiles.Select(f => (f.Name, f.Notes.Count)));
        Assert.Equal(local.Credentials.Select(c => (c.Name, c.Secret.Reveal())), remote.Credentials.Select(c => (c.Name, c.Secret.Reveal())));
        Assert.Equal(local.Left, remote.Left);
    }

    [UnixFact]
    public async Task ReadingWithSftpUsesItAndFallsBackWhenItFails()
    {
        var files = new LocalFiles();
        using var withSftp = new RemoteFileSource("sam@x", new LocalShell(_fx.Home), files, "x");
        var install = (await OpenClawScanner.FindAsync(withSftp, options: NoRoaming)).Single();
        var bundle = await OpenClawReader.ReadAsync(withSftp, install);
        Assert.True(files.Reads > 10);
        Assert.NotEmpty(bundle.Credentials);
    }
}
