namespace Dsh.Core.Tests;

public sealed class OpenClawImportTests : IDisposable
{
    private readonly OpenClawFixture _fx = new();
    private readonly TempDirectory _dsh = new("dsh-target");

    public void Dispose()
    {
        _fx.Dispose();
        _dsh.Dispose();
    }

    private MemoryStore Memory() => new(Path.Combine(_dsh.Path, "memory"));
    private CredentialVault Vault() => new(Path.Combine(_dsh.Path, "vault"), new MemoryBlobStore());
    private SkillLocations Skills() => new(Path.Combine(_dsh.Path, "home"), Path.Combine(_dsh.Path, "appdata"));

    private async Task<OpenClawBundle> Bundle(IFileSource? source = null)
    {
        source ??= _fx.Local();
        var install = (await OpenClawScanner.FindAsync(source, options: OpenClawReaderTests.NoRoaming)).Single();
        return await OpenClawReader.ReadAsync(source, install);
    }

    [Fact]
    public async Task BringsInWhatWasTicked()
    {
        var bundle = await Bundle();
        var memory = Memory();
        var vault = Vault();
        var skills = Skills();
        var selection = OpenClawSelection.Suggested(bundle);
        var result = await OpenClawImporter.ApplyAsync(_fx.Local(), bundle, selection, new OpenClawTargets(memory, vault, skills));

        // Skills: the shadowed managed copy is not offered; scripts come along untouched and unrun.
        Assert.Equal(["onboarding", "pdf-tools", "translate", "weather", "weekly-report"], result.Skills.Order());
        Assert.Empty(result.SkillsSkipped);
        var pdf = Path.Combine(skills.UserSkills, "pdf-tools");
        Assert.True(File.Exists(Path.Combine(pdf, "SKILL.md")));
        Assert.True(File.Exists(Path.Combine(pdf, "scripts", "extract.py")));
        Assert.Contains("Extract text and tables", File.ReadAllText(Path.Combine(pdf, "SKILL.md")));
        Assert.Contains("weekly-report", Directory.GetDirectories(skills.UserSkills).Select(Path.GetFileName));

        // Memory: MEMORY.md, USER.md, and the 45 newest daily logs — not the persona or instructions.
        Assert.Equal(2 + 1 + 45, result.Notes); // Deploy+People, the About note, 45 logs
        Assert.Equal(result.Notes, memory.Count);
        Assert.Contains(memory.All(), n => n.Title == "Deploy" && n.Body.Contains("scripts/deploy.ps1"));
        Assert.DoesNotContain(memory.All(), n => n.Body.Contains("space lobster"));
        Assert.All(memory.All(), n => Assert.Contains("openclaw", n.Tags));

        // Credentials: one entry per distinct value, all "ask first".
        Assert.NotEmpty(result.Credentials);
        Assert.All(vault.All, e => Assert.Equal(VaultAccess.Ask, e.Access));
        Assert.All(vault.All, e => Assert.Contains("openclaw", e.Tags));
        Assert.Equal(OpenClawFixture.AnthropicKey, vault.Value(vault.Entry("ANTHROPIC_API_KEY")!.Id));
        Assert.Equal(OpenClawFixture.TelegramToken, vault.Value(vault.Entry("OPENCLAW_CHANNELS_TELEGRAM_BOTTOKEN")!.Id));

        // Model servers come back for the app to add: the two DSH can talk to.
        Assert.Equal(["ollama", "spark"], result.ModelServers.Select(s => s.Id).Order());
        Assert.True(result.BroughtAnything);
        Assert.Contains("5 skills", result.Summary);
        Assert.Contains("keys and passwords", result.Summary);
        Assert.Contains("2 model servers", result.Summary);
    }

    [Fact]
    public async Task BringsOnlyWhatIsSelected()
    {
        var bundle = await Bundle();
        var memory = Memory();
        var vault = Vault();
        var selection = new OpenClawSelection
        {
            Skills = { bundle.Skills.Single(s => s.Name == "weather").Directory },
            NoteFiles = { bundle.NoteFiles.Single(f => f.Kind == OpenClawNoteKind.Persona && f.Name.EndsWith("SOUL.md")).Path },
            Credentials = { ["HF_TOKEN"] = VaultAccess.Allowed },
        };
        var result = await OpenClawImporter.ApplyAsync(_fx.Local(), bundle, selection, new OpenClawTargets(memory, vault, Skills()));

        Assert.Equal(["weather"], result.Skills);
        Assert.Single(memory.All(), n => n.Body.Contains("space lobster"));
        Assert.Equal(1, memory.Count);
        Assert.Equal(["HF_TOKEN"], vault.All.Select(e => e.Name));
        Assert.Equal(VaultAccess.Allowed, vault.Entry("HF_TOKEN")!.Access);
        Assert.Empty(result.ModelServers);
    }

    [Fact]
    public async Task SkillsCanWaitForApproval()
    {
        var bundle = await Bundle();
        var skills = Skills();
        var selection = new OpenClawSelection { Skills = { bundle.Skills.Single(s => s.Name == "pdf-tools" && !s.Shadowed).Directory }, SkillsAsDrafts = true };
        var result = await OpenClawImporter.ApplyAsync(_fx.Local(), bundle, selection, new OpenClawTargets(null, null, skills));
        Assert.Equal(["pdf-tools"], result.SkillDrafts);
        Assert.Empty(result.Skills);
        Assert.False(Directory.Exists(Path.Combine(skills.UserSkills, "pdf-tools"))); // not active until approved
        Assert.NotEmpty(SkillDrafts.List(skills));
    }

    [Fact]
    public async Task ImportingTwiceDoesNotDuplicate()
    {
        var bundle = await Bundle();
        var memory = Memory();
        var vault = Vault();
        var targets = new OpenClawTargets(memory, vault, null);
        var selection = OpenClawSelection.Suggested(bundle);
        var first = await OpenClawImporter.ApplyAsync(_fx.Local(), bundle, selection, targets);
        var notes = memory.Count;
        var keys = vault.All.Count;
        var second = await OpenClawImporter.ApplyAsync(_fx.Local(), bundle, selection, targets);

        Assert.Equal(notes, memory.Count);
        Assert.Equal(0, second.Notes);
        Assert.Equal(keys, vault.All.Count);
        Assert.Empty(second.Credentials);
        Assert.All(second.CredentialsSkipped.Values, reason => Assert.Contains("already in the vault", reason));
        Assert.Equal(first.Credentials.Count, second.CredentialsSkipped.Count);
    }

    [Fact]
    public async Task ANameTakenByADifferentValueGetsANewName()
    {
        var bundle = await Bundle();
        var vault = Vault();
        vault.Add("HF_TOKEN", "an-entirely-different-token-value", VaultKind.Token);
        var selection = new OpenClawSelection { Credentials = { ["HF_TOKEN"] = VaultAccess.Ask } };
        var result = await OpenClawImporter.ApplyAsync(_fx.Local(), bundle, selection, new OpenClawTargets(null, vault, null));

        Assert.Equal(["HF_TOKEN_OPENCLAW"], result.Credentials);
        Assert.Equal("an-entirely-different-token-value", vault.Value(vault.Entry("HF_TOKEN")!.Id)); // the user's own entry is untouched
        Assert.Equal(OpenClawFixture.HuggingFaceToken, vault.Value(vault.Entry("HF_TOKEN_OPENCLAW")!.Id));
    }

    [Fact]
    public async Task NullTargetsSkipThatKind()
    {
        var bundle = await Bundle();
        var result = await OpenClawImporter.ApplyAsync(_fx.Local(), bundle, OpenClawSelection.Suggested(bundle), new OpenClawTargets(null, null, null));
        Assert.Equal("nothing", result.Summary.Replace("2 model servers", "nothing"));
        Assert.Empty(result.Skills);
        Assert.Equal(0, result.Notes);
        Assert.Empty(result.Credentials);
    }

    [UnixFact]
    public async Task ImportingFromARemoteMachineFetchesTheFilesThroughItsShell()
    {
        var remote = _fx.Remote();
        var bundle = await Bundle(remote);
        var skills = Skills();
        var selection = new OpenClawSelection { Skills = { bundle.Skills.Single(s => s.Name == "pdf-tools" && !s.Shadowed).Directory } };
        var result = await OpenClawImporter.ApplyAsync(remote, bundle, selection, new OpenClawTargets(null, null, skills));
        Assert.Equal(["pdf-tools"], result.Skills);
        Assert.Equal("print('extracting')\n", File.ReadAllText(Path.Combine(skills.UserSkills, "pdf-tools", "scripts", "extract.py")));
    }

    [Fact]
    public async Task ASkillWhoseFilesEscapeItsFolderIsKeptInside()
    {
        var bundle = await Bundle();
        var weather = bundle.Skills.Single(s => s.Name == "weather") with { Files = ["SKILL.md", "../../../escape.txt", "..\\evil.txt", "/etc/passwd", "ok/../../up.txt", "bad:name.txt"] };
        var custom = bundle with { Skills = [weather] };
        var skills = Skills();
        var selection = new OpenClawSelection { Skills = { weather.Directory } };
        var result = await OpenClawImporter.ApplyAsync(_fx.Local(), custom, selection, new OpenClawTargets(null, null, skills));

        Assert.Equal(["weather"], result.Skills);
        Assert.False(File.Exists(Path.Combine(_fx.Root.Path, "escape.txt")));
        Assert.False(File.Exists(Path.Combine(_dsh.Path, "escape.txt")));
        Assert.Equal(["SKILL.md"], Directory.GetFiles(Path.Combine(skills.UserSkills, "weather"), "*", SearchOption.AllDirectories).Select(Path.GetFileName));
    }

    [Theory]
    [InlineData("SKILL.md", true)]
    [InlineData("scripts/run.py", true)]
    [InlineData("a/b/c.txt", true)]
    [InlineData("../x", false)]
    [InlineData("a/../../x", false)]
    [InlineData("/abs", false)]
    [InlineData("a/./b", false)]
    [InlineData("name.", false)]
    [InlineData("con:fig", false)]
    [InlineData("", false)]
    public void RelativePathSafety(string path, bool expected) =>
        Assert.Equal(expected, OpenClawImporter.SafeRelativePath(path, out _));

    [Fact]
    public async Task ASkillWithoutAReadableManifestIsSkippedWithAReason()
    {
        var bundle = await Bundle();
        var ghost = bundle.Skills.Single(s => s.Name == "weather") with { Directory = Path.Combine(_fx.Root.Path, "does-not-exist") };
        var result = await OpenClawImporter.ApplyAsync(_fx.Local(), bundle with { Skills = [ghost] },
            new OpenClawSelection { Skills = { ghost.Directory } }, new OpenClawTargets(null, null, Skills()));
        Assert.Empty(result.Skills);
        Assert.Contains("weather", result.SkillsSkipped.Keys);
    }

    [Fact]
    public async Task ProgressNeverMentionsAValue()
    {
        var bundle = await Bundle();
        var lines = new List<string>();
        await OpenClawImporter.ApplyAsync(_fx.Local(), bundle, OpenClawSelection.Suggested(bundle), new OpenClawTargets(Memory(), Vault(), Skills()),
            new SyncProgress(lines.Add));
        Assert.Contains(lines, l => l.StartsWith("Adding ANTHROPIC_API_KEY to the vault"));
        Assert.DoesNotContain(lines, l => l.Contains(OpenClawFixture.AnthropicKey) || l.Contains(OpenClawFixture.SparkKey));
    }

    private sealed class SyncProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
}

public sealed class RemoteFileSourceTests : IDisposable
{
    private readonly TempDirectory _root = new("dsh-remote");

    public void Dispose() => _root.Dispose();

    private RemoteFileSource Source(out LocalShell shell)
    {
        shell = new LocalShell(_root.Path);
        return new RemoteFileSource("sam@box", shell, null, "box");
    }

    [UnixFact]
    public async Task ReadsHomeAndEnvironment()
    {
        using var source = Source(out _);
        Assert.Equal(_root.Path, await source.HomeAsync(default));
        Assert.Null(await source.EnvironmentAsync("DSH_SURELY_NOT_SET_12345", default));
        Assert.Null(await source.EnvironmentAsync("BAD NAME; rm -rf /", default)); // refused, never run
        Assert.NotNull(await source.EnvironmentAsync("HOME", default));
    }

    [UnixTheory]
    [InlineData("plain")]
    [InlineData("with space")]
    [InlineData("it's quoted")]
    [InlineData("dollar $HOME and `tick`")]
    [InlineData("semi; colon & amp | pipe")]
    [InlineData("unicode-ünï-日本語")]
    [InlineData("--dashes-first")]
    public async Task OddFileNamesSurviveTheShell(string name)
    {
        var path = _root.Write("dir/" + name + "/note.txt", "hello " + name);
        using var source = Source(out _);
        var listing = await source.ListAsync(Path.Combine(_root.Path, "dir"), default);
        Assert.Equal(name, Assert.Single(listing!).Name);
        Assert.True(listing![0].IsDirectory);
        Assert.Equal("hello " + name, System.Text.Encoding.UTF8.GetString((await source.ReadAsync(path, 1_000, default))!));
    }

    [UnixFact]
    public async Task ReadsBinaryAndEmptyFilesAndRefusesBigOnes()
    {
        var bytes = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        File.WriteAllBytes(Path.Combine(_root.Path, "bin"), bytes);
        File.WriteAllBytes(Path.Combine(_root.Path, "empty"), []);
        File.WriteAllBytes(Path.Combine(_root.Path, "big"), new byte[5_000]);
        using var source = Source(out _);
        Assert.Equal(bytes, await source.ReadAsync(Path.Combine(_root.Path, "bin"), 1_000, default));
        Assert.Empty((await source.ReadAsync(Path.Combine(_root.Path, "empty"), 1_000, default))!);
        Assert.Null(await source.ReadAsync(Path.Combine(_root.Path, "big"), 1_000, default));
        Assert.Null(await source.ReadAsync(Path.Combine(_root.Path, "missing"), 1_000, default));
        Assert.Null(await source.ReadAsync(_root.Path, 1_000, default)); // a folder
    }

    [UnixFact]
    public async Task ReadsManyFilesInOneCommand()
    {
        _root.Write("a.txt", "AAA");
        _root.Write("b.txt", "BBB");
        _root.Write("c.txt", new string('c', 3_000));
        using var source = Source(out var shell);
        var paths = new[] { "a.txt", "missing.txt", "b.txt", "c.txt", "it's.txt" }.Select(n => Path.Combine(_root.Path, n)).ToList();
        _root.Write("it's.txt", "quoted");
        var results = await source.ReadManyAsync(paths, 1_000, default);

        Assert.Equal("AAA", System.Text.Encoding.UTF8.GetString(results[0]!));
        Assert.Null(results[1]);
        Assert.Equal("BBB", System.Text.Encoding.UTF8.GetString(results[2]!));
        Assert.Null(results[3]);                                   // over the limit
        Assert.Equal("quoted", System.Text.Encoding.UTF8.GetString(results[4]!));
        Assert.Single(shell.Commands);                             // one round trip for all five
    }

    [UnixFact]
    public async Task ReadsMoreThanOneBatch()
    {
        for (var i = 0; i < 95; i++) _root.Write($"f{i:000}.txt", $"file {i}");
        using var source = Source(out var shell);
        var results = await source.ReadManyAsync(Enumerable.Range(0, 95).Select(i => Path.Combine(_root.Path, $"f{i:000}.txt")).ToList(), 1_000, default);
        Assert.Equal(Enumerable.Range(0, 95).Select(i => $"file {i}"), results.Select(r => System.Text.Encoding.UTF8.GetString(r!)));
        Assert.Equal(3, shell.Commands.Count);
    }

    [UnixFact]
    public async Task ListsFilesRecursivelyWithoutTheJunk()
    {
        _root.Write("skill/SKILL.md", "x");
        _root.Write("skill/scripts/run.py", "x");
        _root.Write("skill/deep/er/file.txt", "x");
        _root.Write("skill/node_modules/pkg/index.js", "x");
        _root.Write("skill/.git/config", "x");
        _root.Write("skill/__pycache__/a.pyc", "x");
        using var source = Source(out _);
        var files = await source.ListFilesAsync(Path.Combine(_root.Path, "skill"), 4, 100, default);
        Assert.Equal(["SKILL.md", "deep/er/file.txt", "scripts/run.py"], files);
        var local = await new LocalFileSource(_root.Path).ListFilesAsync(Path.Combine(_root.Path, "skill"), 4, 100, default);
        Assert.Equal(files, local); // both sources agree
        Assert.Single(await source.ListFilesAsync(Path.Combine(_root.Path, "skill"), 1, 100, default));
        Assert.Empty(await source.ListFilesAsync(Path.Combine(_root.Path, "nothing"), 4, 100, default));
    }

    [UnixFact]
    public async Task FindsFilesByNameAndSkipsNodeModules()
    {
        _root.Write("a/b/openclaw.json", "{}");
        _root.Write("node_modules/x/openclaw.json", "{}");
        _root.Write("deep/1/2/3/4/5/6/7/openclaw.json", "{}");
        using var source = Source(out _);
        var found = await source.FindAsync([_root.Path], ["openclaw.json", "clawdbot.json"], 4, default);
        Assert.Equal([Path.Combine(_root.Path, "a", "b", "openclaw.json")], found);
    }

    [UnixFact]
    public async Task UnsafePathsAreNeverSentToTheShell()
    {
        using var source = Source(out var shell);
        Assert.Null(await source.ListAsync("/tmp/x\nrm -rf /", default));
        Assert.Null(await source.ReadAsync("/tmp/x\0y", 10, default));
        Assert.Empty(await source.ListFilesAsync("/tmp/a\nb", 3, 10, default));
        Assert.Empty(shell.Commands);
    }

    [Fact]
    public void QuotingEscapesSingleQuotes()
    {
        Assert.Equal("'plain'", RemoteFileSource.Quote("plain"));
        Assert.Equal("'it'\\''s'", RemoteFileSource.Quote("it's"));
        Assert.Equal("'$(rm -rf /)'", RemoteFileSource.Quote("$(rm -rf /)")); // inert inside single quotes
    }

    [UnixFact]
    public void ParentAndCombineAreForwardSlash()
    {
        using var source = Source(out _);
        Assert.Equal("/home/sam/.openclaw", source.Combine("/home/sam", ".openclaw"));
        Assert.Equal("/home/sam", source.Parent("/home/sam/.openclaw"));
        Assert.Equal("/", source.Parent("/home"));
        Assert.Null(source.Parent("relative"));
    }
}

public sealed class SshConfigTests
{
    private const string Config = """
        # my machines
        Host spark-2 spark-3
            HostName 192.168.1.42
            User sam
            Port 2222
            IdentityFile ~/.ssh/spark_ed25519

        Host build-*
            User builder

        Host *
            User fallback
            IdentityFile ~/.ssh/id_default
            Port 22
        """;

    [Fact]
    public void ResolvesAnAliasTheWaySshDoes()
    {
        var entry = SshConfigFile.Resolve(Config, "spark-2")!;
        Assert.Equal("192.168.1.42", entry.HostName);
        Assert.Equal("sam", entry.User);           // the first value wins over the "Host *" default
        Assert.Equal(2222, entry.Port);
        Assert.Equal("~/.ssh/spark_ed25519", entry.IdentityFile);
    }

    [Fact]
    public void WildcardBlocksAndDefaultsApply()
    {
        var build = SshConfigFile.Resolve(Config, "build-7")!;
        Assert.Equal("build-7", build.HostName);   // no HostName: the alias is the host
        Assert.Equal("builder", build.User);
        Assert.Equal(22, build.Port);              // from Host *
        Assert.Equal("~/.ssh/id_default", build.IdentityFile);
    }

    [Fact]
    public void AnUnknownHostIsNotResolvedJustByTheDefaultBlock()
    {
        Assert.Null(SshConfigFile.Resolve(Config, "unknown-box"));
        Assert.Null(SshConfigFile.Resolve("", "anything"));
    }

    [Fact]
    public void ListsThePlainAliases()
    {
        Assert.Equal(["spark-2", "spark-3"], SshConfigFile.Aliases(Config));
    }

    [Fact]
    public void ToleratesEqualsSignsQuotesAndWindowsLineEndings()
    {
        var entry = SshConfigFile.Resolve("Host box\r\n  HostName=\"10.0.0.5\"\r\n  User = root\r\n", "box")!;
        Assert.Equal("10.0.0.5", entry.HostName);
        Assert.Equal("root", entry.User);
    }
}
