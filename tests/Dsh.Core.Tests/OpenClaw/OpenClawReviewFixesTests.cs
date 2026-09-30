namespace Dsh.Core.Tests;

/// <summary>What a careful read of the OpenClaw import turned up: nothing that looks like a credential is carried across
/// in a skill or a note, the same skill isn't copied twice, a login banner or a link can't misplace a file, one folder
/// that fails doesn't lose the rest, and odd input can't hang or crash the reader.</summary>
public sealed class OpenClawReviewFixesTests : IDisposable
{
    private readonly OpenClawFixture _fx = new();
    private readonly TempDirectory _dsh = new("dsh-target");

    public void Dispose()
    {
        _fx.Dispose();
        _dsh.Dispose();
    }

    private SkillLocations Skills() => new(Path.Combine(_dsh.Path, "home"), Path.Combine(_dsh.Path, "appdata"));

    private async Task<OpenClawBundle> Bundle(IFileSource? source = null)
    {
        source ??= _fx.Local();
        var install = (await OpenClawScanner.FindAsync(source, options: OpenClawReaderTests.NoRoaming)).Single();
        return await OpenClawReader.ReadAsync(source, install);
    }

    private static OpenClawSelection Only(OpenClawBundle bundle, params string[] skillNames) => new()
    {
        Skills = bundle.Skills.Where(s => skillNames.Contains(s.Name)).Select(s => s.Directory).ToHashSet(StringComparer.Ordinal),
    };

    // MARK: - Skills carry no credentials

    [Fact]
    public async Task CredentialFilesInASkillAreLeftOutAndSaid()
    {
        var skill = Path.Combine(_fx.Workspace, "skills", "deploy-tool");
        OpenClawFixture.Write(Path.Combine(skill, "SKILL.md"), "---\nname: deploy-tool\ndescription: Deploy the app\n---\nRun the deploy script.\n");
        OpenClawFixture.Write(Path.Combine(skill, ".env"), "AWS_SECRET_ACCESS_KEY=wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY\n");
        OpenClawFixture.Write(Path.Combine(skill, "id_rsa"), "-----BEGIN OPENSSH PRIVATE KEY-----\nabc\n");
        OpenClawFixture.Write(Path.Combine(skill, "notes.md"), "The staging server is called orion.\n");
        var locations = Skills();
        var bundle = await Bundle();

        var result = await OpenClawImporter.ApplyAsync(_fx.Local(), bundle, Only(bundle, "deploy-tool"), new OpenClawTargets(null, null, locations));

        Assert.Contains("deploy-tool", result.Skills.Select(Path.GetFileName).Concat(result.Skills));
        var landed = Path.Combine(locations.UserSkills, "deploy-tool");
        Assert.True(File.Exists(Path.Combine(landed, "SKILL.md")));
        Assert.True(File.Exists(Path.Combine(landed, "notes.md")));
        Assert.False(File.Exists(Path.Combine(landed, ".env")));
        Assert.False(File.Exists(Path.Combine(landed, "id_rsa")));
        Assert.Contains(result.Problems, p => p.Contains(".env") && p.Contains("credentials"));
    }

    [Fact]
    public async Task ATextFileWithAKeyInsideIsLeftOutButTheSkillStillComes()
    {
        var skill = Path.Combine(_fx.Workspace, "skills", "notifier");
        OpenClawFixture.Write(Path.Combine(skill, "SKILL.md"), "---\nname: notifier\ndescription: Send a notification\n---\nUse notify.md.\n");
        OpenClawFixture.Write(Path.Combine(skill, "notify.md"), "Call the API with DB_PASSWORD=hunter2hunter2 as the password.\n");
        var locations = Skills();
        var bundle = await Bundle();

        var result = await OpenClawImporter.ApplyAsync(_fx.Local(), bundle, Only(bundle, "notifier"), new OpenClawTargets(null, null, locations));

        Assert.False(File.Exists(Path.Combine(locations.UserSkills, "notifier", "notify.md")));
        Assert.True(File.Exists(Path.Combine(locations.UserSkills, "notifier", "SKILL.md")));
        Assert.Contains(result.Problems, p => p.Contains("notify.md") && p.Contains("key or a password"));
    }

    [Fact]
    public async Task ASkillWhoseManifestHoldsAKeyIsRefusedWithAReason()
    {
        var skill = Path.Combine(_fx.Workspace, "skills", "leaky");
        OpenClawFixture.Write(Path.Combine(skill, "SKILL.md"), "---\nname: leaky\ndescription: Talks to an API\n---\nUse the key sk-live-abcdefghijklmnopqrstuvwxyz0123456789 when calling.\n");
        var bundle = await Bundle();

        var result = await OpenClawImporter.ApplyAsync(_fx.Local(), bundle, Only(bundle, "leaky"), new OpenClawTargets(null, null, Skills()));

        Assert.Empty(result.Skills);
        Assert.Contains("key or a password", result.SkillsSkipped["leaky"]);
    }

    [Theory]
    [InlineData(".env", true)]
    [InlineData(".env.production", true)]
    [InlineData(".env.example", false)]
    [InlineData("config/id_ed25519", true)]
    [InlineData("certs/server.pem", true)]
    [InlineData("credentials.json", true)]
    [InlineData("scripts/run.ps1", false)]
    [InlineData("notes.md", false)]
    public void CredentialFilesAreRecognisedByName(string path, bool isCredential) =>
        Assert.Equal(isCredential, OpenClawImporter.IsCredentialFile(path));

    // MARK: - What a skill may hold

    [UnixFact]
    public async Task ASkillWhoseManifestIsALinkToAnotherFileIsNotBroughtIn()
    {
        var secret = Path.Combine(_fx.Home, ".docker", "config.json");
        OpenClawFixture.Write(secret, "{\"auths\":{\"registry.example\":{\"auth\":\"c2VjcmV0\"}}}");
        var skill = Path.Combine(_fx.Workspace, "skills", "linked");
        Directory.CreateDirectory(skill);
        File.CreateSymbolicLink(Path.Combine(skill, "SKILL.md"), secret); // read through the link, the "skill" would be that file
        var locations = Skills();
        var bundle = await Bundle();

        var result = await OpenClawImporter.ApplyAsync(_fx.Local(), bundle, new OpenClawSelection { Skills = bundle.Skills.Select(s => s.Directory).Where(d => d.EndsWith("linked", StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal) },
            new OpenClawTargets(null, null, locations));

        Assert.Empty(result.Skills);
        Assert.Contains("link", Assert.Single(result.SkillsSkipped).Value);
        Assert.False(Directory.Exists(Path.Combine(locations.UserSkills, "linked")));
    }

    [Fact]
    public async Task FilesLeftOutForBeingTooBigOrTooManyAreSaid()
    {
        var skill = Path.Combine(_fx.Workspace, "skills", "heavy");
        OpenClawFixture.Write(Path.Combine(skill, "SKILL.md"), "---\nname: heavy\ndescription: Has a lot\n---\nSee data.\n");
        File.WriteAllBytes(Path.Combine(skill, "huge.bin.txt"), new byte[1_600_000]);
        for (var i = 0; i < 310; i++) OpenClawFixture.Write(Path.Combine(skill, "parts", $"p{i:000}.md"), "x\n");
        var locations = Skills();
        var bundle = await Bundle();

        var result = await OpenClawImporter.ApplyAsync(_fx.Local(), bundle, Only(bundle, "heavy"), new OpenClawTargets(null, null, locations));

        Assert.Single(result.Skills);
        Assert.Contains(result.Problems, p => p.Contains("300 files or more") && p.Contains("first 300"));
        Assert.Contains(result.Problems, p => p.Contains("huge.bin.txt") && p.Contains("left out"));
    }

    [Theory]
    [InlineData("null")] // an entry with nothing in it
    [InlineData("""{"Hash":null,"Slug":null}""")] // an entry with nothing in it
    [InlineData("""{"Hash":"x"}""")] // half an entry
    [InlineData("7")] // the wrong kind of thing
    [InlineData("not json at all")] // the whole file
    public async Task ADamagedImportRecordDoesNotStopAnImport(string entry)
    {
        var locations = Skills();
        var bundle = await Bundle();
        var weather = bundle.Skills.Single(s => s.Name == "weather");
        Directory.CreateDirectory(locations.AppSupport);
        // (Damage in the very entry that this skill's import looks up.)
        var ledger = entry == "not json at all" ? entry : "{" + System.Text.Json.JsonSerializer.Serialize("This PC|" + weather.Directory) + ":" + entry + "}";
        File.WriteAllText(Path.Combine(locations.AppSupport, "openclaw-imports.json"), ledger);

        var result = await OpenClawImporter.ApplyAsync(_fx.Local(), bundle, Only(bundle, "weather"), new OpenClawTargets(null, null, locations));

        Assert.Single(result.Skills);
        // And what was brought in is written down again, so the next import knows.
        var again = await OpenClawImporter.ApplyAsync(_fx.Local(), bundle, Only(bundle, "weather"), new OpenClawTargets(null, null, locations));
        Assert.Empty(again.Skills);
    }

    [UnixFact]
    public async Task ASkillWithThousandsOfFilesStillListsItsManifest()
    {
        // find lists in the order the disk keeps things, and the list is cut before it is sorted: the files right in the folder
        // are asked for on their own first. (A stand-in find puts 300 deep files ahead of SKILL.md.)
        _fx.Root.Write("home/sam/big/SKILL.md", "---\nname: big\n---\nx\n");
        var bin = StandIn("find", """
            case " $* " in
              *" -maxdepth 1 "*) exec /usr/bin/find "$@" ;;
              *) root=$2; i=0; while [ $i -lt 300 ]; do echo "$root/data/f$i.txt"; i=$((i+1)); done; echo "$root/SKILL.md" ;;
            esac
            """);
        using var source = new RemoteFileSource("sam@box", new PathShim(new LocalShell(_fx.Home), bin), null, "box");

        var files = await source.ListFilesAsync(Path.Combine(_fx.Home, "big"), 4, 2, default); // (cuts the deep list at 40 lines)

        Assert.Contains("SKILL.md", files);
    }

    [Fact]
    public void APasswordCutOffFromItsNameBetweenTwoPiecesOfAFileIsLeftOut()
    {
        var store = new MemoryStore(Path.Combine(_dsh.Path, "memory-split"));
        var pieces = new[]
        {
            new MemoryDraft { Title = "Database", Body = "Notes about the production database. The database password is", Source = "import:openclaw/MEMORY.md" },
            new MemoryDraft { Title = "Database 2", Body = "hunter2hunter2 and it is rotated monthly by the platform team.", Source = "import:openclaw/MEMORY.md" },
            new MemoryDraft { Title = "Style", Body = "Prefer tabs over spaces in this repository.", Source = "import:openclaw/MEMORY.md" },
        };

        var added = store.Import(pieces, out var skipped);

        Assert.Equal(2, skipped);
        Assert.Equal(1, added);
        Assert.DoesNotContain(store.All(), n => n.Body.Contains("hunter2hunter2") || n.Body.Contains("database password is"));
        Assert.Contains(store.All(), n => n.Title == "Style");
    }

    // MARK: - Once is enough

    [Fact]
    public async Task ASkillBroughtInTwiceIsNotCopiedAgain()
    {
        var bundle = await Bundle();
        var locations = Skills();
        var selection = Only(bundle, "weekly-report", "weather");
        var targets = new OpenClawTargets(null, null, locations);

        var first = await OpenClawImporter.ApplyAsync(_fx.Local(), bundle, selection, targets);
        var folders = Directory.GetDirectories(locations.UserSkills).Select(Path.GetFileName).OrderBy(n => n).ToList();
        var second = await OpenClawImporter.ApplyAsync(_fx.Local(), bundle, selection, targets);

        Assert.Equal(2, first.Skills.Count);
        Assert.Empty(second.Skills);
        Assert.All(second.SkillsSkipped.Values, reason => Assert.Contains("Already in DSH", reason));
        Assert.Equal(folders, Directory.GetDirectories(locations.UserSkills).Select(Path.GetFileName).OrderBy(n => n).ToList()); // no "-2"
    }

    [Fact]
    public async Task ASkillThatChangedAtItsSourceComesInAgain()
    {
        var bundle = await Bundle();
        var locations = Skills();
        var targets = new OpenClawTargets(null, null, locations);
        var selection = Only(bundle, "weather");
        await OpenClawImporter.ApplyAsync(_fx.Local(), bundle, selection, targets);

        OpenClawFixture.Write(Path.Combine(_fx.State, "skills", "weather", "SKILL.md"), "---\nname: weather\ndescription: Look up the forecast\n---\nUse the NEW weather API.\n");
        var again = await OpenClawImporter.ApplyAsync(_fx.Local(), bundle, selection, targets);

        Assert.Single(again.Skills); // kept beside the first under a new name rather than overwriting it
    }

    [Fact]
    public async Task ASkillWithNoNameOfItsOwnTakesItsFolderName()
    {
        OpenClawFixture.Write(Path.Combine(_fx.Workspace, "skills", "Weekly_Digest", "SKILL.md"), "---\ndescription: Write the weekly digest\n---\nCollect the highlights.\n");
        var bundle = await Bundle();
        var skill = bundle.Skills.Single(s => s.Directory.EndsWith("Weekly_Digest", StringComparison.Ordinal));

        var result = await OpenClawImporter.ApplyAsync(_fx.Local(), bundle,
            new OpenClawSelection { Skills = new HashSet<string>([skill.Directory], StringComparer.Ordinal) }, new OpenClawTargets(null, null, Skills()));

        Assert.Single(result.Skills);
        Assert.Empty(result.SkillsSkipped);
    }

    // MARK: - Notes

    [Fact]
    public async Task NotesThatLookLikeCredentialsAreLeftOutAndSaid()
    {
        OpenClawFixture.Write(Path.Combine(_fx.Workspace, "memory", "2026-03-02.md"),
            "Log for 2026-03-02.\n\nprod DB login DB_PASSWORD=hunter2hunter2 keep it safe\n\nAlso: the standup moved to 10:00.\n");
        var bundle = await Bundle();
        var memory = new MemoryStore(Path.Combine(_dsh.Path, "memory"));
        var selection = new OpenClawSelection { NoteFiles = bundle.NoteFiles.Where(f => f.Name.Contains("2026-03-02")).Select(f => f.Path).ToHashSet(StringComparer.Ordinal) };

        var result = await OpenClawImporter.ApplyAsync(_fx.Local(), bundle, selection, new OpenClawTargets(memory, null, null));

        Assert.DoesNotContain(memory.All(), n => n.Body.Contains("hunter2hunter2"));
        Assert.Contains(result.Problems, p => p.Contains("key or a password"));
    }

    // MARK: - A remote shell that talks

    /// <summary>A shell whose login script prints a banner before every command and a goodbye after it.</summary>
    private sealed class BannerShell(IRemoteShell inner) : IRemoteShell
    {
        public async Task<RemoteResult> RunAsync(string command, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var result = await inner.RunAsync(command, timeout, cancellationToken);
            return result with { Output = "Welcome to spark-3!\nLast login: yesterday from 10.0.0.2\n" + result.Output + "logout\n" };
        }

        public void Dispose() => inner.Dispose();
    }

    /// <summary>A machine whose <c>ls</c> is GNU's: given a link with nothing at the end of it (and -L, which the listing
    /// uses) it lists everything and then exits 1, "minor problems". BSD ls, which these tests otherwise meet on a Mac,
    /// exits 0 — so the difference is put in by hand: an <c>ls</c> earlier on the PATH that runs the real one and exits 1.</summary>
    [UnixFact]
    public async Task AFolderStillListsWhenLsExitsWithMinorProblems()
    {
        _fx.Root.Write("home/sam/box/a.txt", "AAAA");
        Directory.CreateDirectory(Path.Combine(_fx.Home, "box", "sub"));
        var bin = StandIn("ls", "/bin/ls \"$@\"\nexit 1");
        using var source = new RemoteFileSource("sam@box", new PathShim(new LocalShell(_fx.Home), bin), null, "box");

        var entries = await source.ListAsync(Path.Combine(_fx.Home, "box"), default);

        Assert.NotNull(entries);
        Assert.Contains(entries!, e => e.Name == "a.txt" && !e.IsDirectory);
        Assert.Contains(entries!, e => e.Name == "sub" && e.IsDirectory);
        Assert.Null(await source.ListAsync(Path.Combine(_fx.Home, "no-such-folder"), default)); // nothing printed: not a folder we can read
    }

    // MARK: - A machine that is not quite the ordinary one

    /// <summary>Puts a folder of stand-in programs first on the PATH the command sees (an <c>ls</c> that exits 1, a <c>base64</c>
    /// that is not installed...).</summary>
    private sealed class PathShim(IRemoteShell inner, string binFolder) : IRemoteShell
    {
        public Task<RemoteResult> RunAsync(string command, TimeSpan timeout, CancellationToken cancellationToken) =>
            inner.RunAsync($"PATH='{binFolder}':\"$PATH\"; export PATH\n{command}", timeout, cancellationToken);

        public void Dispose() => inner.Dispose();
    }

    private string StandIn(string name, string script)
    {
        var bin = Path.Combine(_fx.Root.Path, "stand-ins");
        Directory.CreateDirectory(bin);
        var path = Path.Combine(bin, name);
        File.WriteAllText(path, "#!/bin/sh\n" + script + "\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return bin;
    }

    [UnixFact]
    public async Task AMachineWithoutBase64GivesNoFilesNotEmptyOnes()
    {
        // "base64" prints nothing and fails; the tail of the pipeline still says 0. An empty result decodes without complaint
        // to an empty file — a skill with an empty SKILL.md, a note that vanished.
        _fx.Root.Write("home/sam/box/a.txt", "AAAA");
        _fx.Root.Write("home/sam/box/empty.txt", "");
        var bin = StandIn("base64", "exit 1");
        using var source = new RemoteFileSource("sam@box", new PathShim(new LocalShell(_fx.Home), bin), null, "box");
        var box = Path.Combine(_fx.Home, "box");

        Assert.Null(await source.ReadAsync($"{box}/a.txt", 1_000, default));
        var many = await source.ReadManyAsync([$"{box}/a.txt", $"{box}/empty.txt"], 1_000, default);
        Assert.Null(many[0]);
        Assert.Empty(many[1]!); // a file that really is empty needs no base64, and stays empty
    }

    [UnixFact]
    public async Task EveryCommandIsHandedToShWhateverTheAccountsOwnShellIs()
    {
        // fish, for one, has no `rc=$?` and no `if …; fi`: a command line that isn't handed to sh never runs there.
        _fx.Root.Write("home/sam/box/a.txt", "AAAA");
        using var source = new RemoteFileSource("sam@box", new FishLikeShell(new LocalShell(_fx.Home)), null, "box");
        Assert.Equal(_fx.Home, await source.HomeAsync(default));
        Assert.Equal("AAAA", Text(await source.ReadAsync(Path.Combine(_fx.Home, "box", "a.txt"), 1_000, default)));
        var entries = await source.ListAsync(Path.Combine(_fx.Home, "box"), default);
        Assert.Contains(entries!, e => e.Name == "a.txt");
    }

    private sealed class FishLikeShell(IRemoteShell inner) : IRemoteShell
    {
        public Task<RemoteResult> RunAsync(string command, TimeSpan timeout, CancellationToken cancellationToken) =>
            command.StartsWith("sh -c ", StringComparison.Ordinal)
                ? inner.RunAsync(command, timeout, cancellationToken)
                : Task.FromResult(new RemoteResult(127, "", "fish: Unknown command"));

        public void Dispose() => inner.Dispose();
    }

    [Fact]
    public async Task ASilentShellThatOnlyComplainsIsReportedWithItsComplaint()
    {
        using var source = new RemoteFileSource("sam@box", new ComplainingShell("This account can only use SFTP."), null, "box");
        var error = await Assert.ThrowsAsync<IOException>(() => source.HomeAsync(default));
        Assert.Contains("only use SFTP", error.Message);
    }

    private sealed class ComplainingShell(string complaint) : IRemoteShell
    {
        public Task<RemoteResult> RunAsync(string command, TimeSpan timeout, CancellationToken cancellationToken) =>
            Task.FromResult(new RemoteResult(1, "", complaint));

        public void Dispose()
        {
        }
    }

    /// <summary>An SFTP channel that never answers a listing (a hung mount).</summary>
    private sealed class WedgedFiles : IRemoteFiles
    {
        public int Asked;

        public Task<IReadOnlyList<FileEntry>?> ListAsync(string directory, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Asked);
            return new TaskCompletionSource<IReadOnlyList<FileEntry>?>().Task;
        }

        public Task<byte[]?> ReadAsync(string path, long maxBytes, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Asked);
            return new TaskCompletionSource<byte[]?>().Task;
        }

        public void Dispose()
        {
        }
    }

    [UnixFact]
    public async Task AListingThatSftpNeverAnswersFallsBackToTheShellAndSftpIsNotAskedAgain()
    {
        _fx.Root.Write("home/sam/box/a.txt", "AAAA");
        var wedged = new WedgedFiles();
        using var source = new RemoteFileSource("sam@box", new LocalShell(_fx.Home), wedged, "box") { SftpReadTimeout = TimeSpan.FromMilliseconds(250) };
        var box = Path.Combine(_fx.Home, "box");

        var listed = await source.ListAsync(box, default);
        Assert.Contains(listed!, e => e.Name == "a.txt"); // the shell answered
        Assert.Equal("AAAA", Text(await source.ReadAsync($"{box}/a.txt", 1_000, default)));
        Assert.Equal(1, wedged.Asked); // and the channel was given up on after its first silence
    }

    [WindowsFact]
    public void ASlashSeparatedNameIsJoinedTheWayThisPcSpellsPaths()
    {
        // "~/.openclaw/workspace" from a config file and the same folder found by listing are one folder, not two.
        var joined = new LocalFileSource(@"C:\Users\sam").Combine(@"C:\Users\sam", ".openclaw/workspace");
        Assert.Equal(@"C:\Users\sam\.openclaw\workspace", joined);
    }

    [UnixFact]
    public async Task ALoginBannerDoesNotMoveOneFilesTextOntoAnother()
    {
        _fx.Root.Write("home/sam/box/a.txt", "AAAA");
        _fx.Root.Write("home/sam/box/b.txt", "BBBB");
        _fx.Root.Write("home/sam/box/c.txt", "CCCC");
        using var source = new RemoteFileSource("sam@box", new BannerShell(new LocalShell(_fx.Home)), null, "box");
        var box = Path.Combine(_fx.Home, "box");

        var read = await source.ReadManyAsync([$"{box}/a.txt", $"{box}/missing.txt", $"{box}/b.txt", $"{box}/c.txt"], 1_000, default);

        Assert.Equal("AAAA", Text(read[0]));
        Assert.Null(read[1]);
        Assert.Equal("BBBB", Text(read[2]));
        Assert.Equal("CCCC", Text(read[3]));
    }

    private static string? Text(byte[]? bytes) => bytes is null ? null : System.Text.Encoding.UTF8.GetString(bytes);

    [UnixFact]
    public async Task HomeEnvironmentAndSingleReadsSurviveABanner()
    {
        _fx.Root.Write("home/sam/one.txt", "hello");
        using var source = new RemoteFileSource("sam@box", new BannerShell(new LocalShell(_fx.Home)), null, "box");
        Assert.Equal(_fx.Home, await source.HomeAsync(default));
        Assert.Equal("hello", Text(await source.ReadAsync(Path.Combine(_fx.Home, "one.txt"), 1_000, default)));
        Assert.Null(await source.ReadAsync(Path.Combine(_fx.Home, "nope.txt"), 1_000, default));
        var entries = await source.ListAsync(_fx.Home, default);
        Assert.Contains(entries!, e => e.Name == "one.txt" && !e.IsDirectory);
        Assert.DoesNotContain(entries!, e => e.Name.Contains("Welcome") || e.Name == "logout");
    }

    [UnixFact]
    public async Task ASkillFolderThatIsALinkStillListsItsFiles()
    {
        var real = Path.Combine(_fx.Home, "git", "my-skill");
        OpenClawFixture.Write(Path.Combine(real, "SKILL.md"), "---\nname: my-skill\ndescription: d\n---\nbody\n");
        OpenClawFixture.Write(Path.Combine(real, "extra", "more.md"), "more\n");
        var link = Path.Combine(_fx.Workspace, "skills", "my-skill");
        Directory.CreateSymbolicLink(link, real);
        using var source = _fx.Remote();

        var files = await source.ListFilesAsync(link, 4, 100, default);

        Assert.Contains("SKILL.md", files);
        Assert.Contains("extra/more.md", files);
    }

    [UnixFact]
    public async Task ASkillWithMoreFilesThanTheCapStillListsItsManifest()
    {
        var skill = Path.Combine(_fx.Home, "big-skill");
        OpenClawFixture.Write(Path.Combine(skill, "SKILL.md"), "---\nname: big\n---\nx\n");
        for (var i = 0; i < 60; i++) OpenClawFixture.Write(Path.Combine(skill, "data", $"f{i:00}.txt"), "x");
        using var source = _fx.Remote();

        var files = await source.ListFilesAsync(skill, 4, 10, default);

        Assert.Equal(10, files.Count);
        Assert.Contains("SKILL.md", files); // shallow files first, so the manifest is never the one cut
    }

    [UnixFact]
    public async Task ALocalSkillDoesNotFollowALinkedFileOutsideItself()
    {
        var secret = Path.Combine(_fx.Home, ".ssh", "id_test");
        OpenClawFixture.Write(secret, "private key material");
        var skill = Path.Combine(_fx.Workspace, "skills", "linker");
        OpenClawFixture.Write(Path.Combine(skill, "SKILL.md"), "---\nname: linker\n---\nx\n");
        File.CreateSymbolicLink(Path.Combine(skill, "notes.md"), secret);

        var files = await _fx.Local().ListFilesAsync(skill, 4, 100, default);

        Assert.Contains("SKILL.md", files);
        Assert.DoesNotContain("notes.md", files);
    }

    // MARK: - One failure is not the end

    private sealed class FlakySource(IFileSource inner) : IFileSource
    {
        public string Label => inner.Label;
        public bool IsRemote => false;
        public string? NetworkHost => null;
        public Task<string> HomeAsync(CancellationToken ct) => inner.HomeAsync(ct);
        public Task<string?> EnvironmentAsync(string name, CancellationToken ct) =>
            Task.FromResult(name == "OPENCLAW_STATE_DIR" ? "/boom" : null);

        public Task<IReadOnlyList<FileEntry>?> ListAsync(string directory, CancellationToken ct) =>
            directory.StartsWith("/boom", StringComparison.Ordinal) ? throw new IOException("the link dropped") : inner.ListAsync(directory, ct);

        public Task<byte[]?> ReadAsync(string path, long maxBytes, CancellationToken ct) => inner.ReadAsync(path, maxBytes, ct);
        public Task<IReadOnlyList<byte[]?>> ReadManyAsync(IReadOnlyList<string> paths, long maxBytesEach, CancellationToken ct) => inner.ReadManyAsync(paths, maxBytesEach, ct);
        public Task<IReadOnlyList<string>> ListFilesAsync(string root, int maxDepth, int maxFiles, CancellationToken ct) => inner.ListFilesAsync(root, maxDepth, maxFiles, ct);
        public Task<IReadOnlyList<string>> FindAsync(IReadOnlyList<string> roots, IReadOnlyList<string> fileNames, int maxDepth, CancellationToken ct) =>
            throw new IOException("the search timed out");
        public string Combine(string directory, string name) => inner.Combine(directory, name);
        public string? Parent(string path) => inner.Parent(path);
        public void Dispose() => inner.Dispose();
    }

    [Fact]
    public async Task OneFolderOrSearchThatFailsDoesNotLoseTheInstallAlreadyFound()
    {
        var messages = new List<string>();
        var installs = await OpenClawScanner.FindAsync(new FlakySource(_fx.Local()), new SyncProgress(messages.Add), options: OpenClawReaderTests.NoRoaming);

        Assert.Equal(_fx.State, Assert.Single(installs).StateDir);
        Assert.Contains(messages, m => m.Contains("the link dropped"));
        Assert.Contains(messages, m => m.Contains("search didn't finish"));
    }

    /// <summary>A machine that answered for the first look around (its home folder) and whose link then dropped.</summary>
    private sealed class DroppedLinkSource : IFileSource
    {
        public string Label => "sam@box";
        public bool IsRemote => true;
        public string? NetworkHost => "box";
        public Task<string> HomeAsync(CancellationToken ct) => Task.FromResult("/home/sam");
        public Task<string?> EnvironmentAsync(string name, CancellationToken ct) => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<FileEntry>?> ListAsync(string directory, CancellationToken ct) =>
            directory == "/home/sam" ? Task.FromResult<IReadOnlyList<FileEntry>?>([]) : throw new IOException("Lost touch with the machine: connection reset");
        public Task<byte[]?> ReadAsync(string path, long maxBytes, CancellationToken ct) => throw new IOException("Lost touch with the machine: connection reset");
        public Task<IReadOnlyList<byte[]?>> ReadManyAsync(IReadOnlyList<string> paths, long maxBytesEach, CancellationToken ct) => throw new IOException("Lost touch with the machine: connection reset");
        public Task<IReadOnlyList<string>> ListFilesAsync(string root, int maxDepth, int maxFiles, CancellationToken ct) => throw new IOException("Lost touch with the machine: connection reset");
        public Task<IReadOnlyList<string>> FindAsync(IReadOnlyList<string> roots, IReadOnlyList<string> fileNames, int maxDepth, CancellationToken ct) => throw new IOException("Lost touch with the machine: connection reset");
        public string Combine(string directory, string name) => directory + "/" + name;
        public string? Parent(string path) => "/";
        public void Dispose()
        {
        }
    }

    [Fact]
    public async Task IfEveryStepFailsBecauseTheLinkDroppedTheScanSaysSoInsteadOfNotFound()
    {
        var error = await Assert.ThrowsAsync<IOException>(() => OpenClawScanner.FindAsync(new DroppedLinkSource(), options: OpenClawReaderTests.NoRoaming));
        Assert.Contains("Lost touch", error.Message);
    }

    private sealed class SyncProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }

    [Fact]
    public async Task TwoFoldersThatDifferOnlyInCaseDoNotEndTheScan()
    {
        var entries = new List<FileEntry> { new("Skills", "/x/Skills", true), new("skills", "/x/skills", true), new("openclaw.json", "/x/openclaw.json", false) };
        var source = new ListingSource(entries);
        var install = await OpenClawScanner.InspectAsync(source, "/x", "test");
        Assert.NotNull(install);
    }

    private sealed class ListingSource(IReadOnlyList<FileEntry> entries) : IFileSource
    {
        public string Label => "test";
        public bool IsRemote => false;
        public string? NetworkHost => null;
        public Task<string> HomeAsync(CancellationToken ct) => Task.FromResult("/");
        public Task<string?> EnvironmentAsync(string name, CancellationToken ct) => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<FileEntry>?> ListAsync(string directory, CancellationToken ct) => Task.FromResult<IReadOnlyList<FileEntry>?>(entries);
        public Task<byte[]?> ReadAsync(string path, long maxBytes, CancellationToken ct) => Task.FromResult<byte[]?>(null);
        public Task<IReadOnlyList<byte[]?>> ReadManyAsync(IReadOnlyList<string> paths, long maxBytesEach, CancellationToken ct) => Task.FromResult<IReadOnlyList<byte[]?>>([]);
        public Task<IReadOnlyList<string>> ListFilesAsync(string root, int maxDepth, int maxFiles, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<IReadOnlyList<string>> FindAsync(IReadOnlyList<string> roots, IReadOnlyList<string> fileNames, int maxDepth, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>([]);
        public string Combine(string directory, string name) => directory + "/" + name;
        public string? Parent(string path) => "/";
        public void Dispose()
        {
        }
    }

    // MARK: - Odd input

    [Fact]
    public void ATooLargeHexNumberIsAFailureNotACrash()
    {
        Assert.Null(Json5.TryParse("{ a: 0xFFFFFFFFFFFFFFFFFFFFFF }", out var error));
        Assert.Contains("too large", error);
    }

    [Fact]
    public void AnUnquotedWordIsNotEchoedBackInTheError()
    {
        Assert.Null(Json5.TryParse("{ apiKey: hunter2secretvalue }", out var error));
        Assert.DoesNotContain("hunter2", error);
    }

    [Fact]
    public async Task LongCredentialNamesThatCollideStillGetUniqueNames()
    {
        var stem = new string('A', 90);
        OpenClawFixture.Write(Path.Combine(_fx.State, ".env"), $"{stem}_TOKEN_1=tok_9f8e7d6c5b4a39281706f5e4d3c2b1a0\n{stem}_TOKEN_2=tok_0a1b2c3d4e5f60718293a4b5c6d7e8f9\n");
        // (This used to loop for ever.)
        var bundleTask = Task.Run(async () => await Bundle());
        Assert.True(await Task.WhenAny(bundleTask, Task.Delay(TimeSpan.FromSeconds(10))) == bundleTask, "the reader hung on colliding names");
        var bundle = await bundleTask;

        var long90 = bundle.Credentials.Where(c => c.Name.StartsWith("AAAA", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, long90.Count);
        Assert.Equal(2, long90.Select(c => c.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(long90, c => Assert.True(c.Name.Length <= CredentialVault.MaxNameLength));
    }

    [Fact]
    public async Task AContextWindowOfZeroIsNotAContextWindow()
    {
        OpenClawFixture.Write(Path.Combine(_fx.State, "openclaw.json"), """
            { models: { providers: { local: { baseUrl: "http://10.0.0.5:8000/v1", models: [ { id: "m", contextWindow: Infinity } ] } } } }
            """);
        var bundle = await Bundle();
        Assert.Null(Assert.Single(bundle.ModelServers).ContextWindow);
    }

    [UnixFact]
    public async Task ALocalhostModelOverPlainHttpIsFlaggedAsCrossingTheNetwork()
    {
        OpenClawFixture.Write(Path.Combine(_fx.State, "openclaw.json"), """
            { models: { providers: { local: { baseUrl: "http://127.0.0.1:8000/v1", apiKey: "vllm-local-key-12345", models: [ { id: "m" } ] } } } }
            """);
        var source = _fx.Remote();
        var install = (await OpenClawScanner.FindAsync(source, options: OpenClawReaderTests.NoRoaming)).Single();
        var bundle = await OpenClawReader.ReadAsync(source, install);
        var server = Assert.Single(bundle.ModelServers);
        Assert.StartsWith("http://spark-3.local:8000", server.BaseUrl);
        Assert.Contains("unencrypted", server.Note);
    }

    [Fact]
    public void ALoginNeverPrintsItsPasswordOrPassphrase()
    {
        var login = new SshLogin { Host = "box", Username = "sam", Password = "correct-horse-battery", Passphrase = "staple-9000" };
        var text = login.ToString();
        Assert.DoesNotContain("correct-horse", text);
        Assert.DoesNotContain("staple", text);
        Assert.Contains("box", text);
        Assert.Contains("sam", text);
    }
}
