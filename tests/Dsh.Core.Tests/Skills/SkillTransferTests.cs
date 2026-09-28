using System.IO.Compression;
using System.Net;
using System.Text;

namespace Dsh.Core.Tests;

public sealed class SkillTransferTests : IDisposable
{
    private readonly SkillFixture _fx = new();

    public void Dispose()
    {
        SkillImporter.HttpHandler = null;
        _fx.Dispose();
    }

    /// <summary>A repo laid out the way people actually publish skills.</summary>
    private string MakeRepo()
    {
        var repo = Path.Combine(_fx.Base, "repo");
        Directory.CreateDirectory(repo);
        _fx.Skill("skills/pdf-tools", "pdf-tools", "Use when working with PDFs.", root: repo);
        _fx.Write("skills/pdf-tools/scripts/extract.py", "print('hi')", repo);
        _fx.Write("skills/pdf-tools/reference.md", "Reference", repo);
        _fx.Skill(".claude/skills/review", "review", "Use when reviewing code.", root: repo);
        _fx.Write(".claude/commands/deploy.md", "---\ndescription: Ship it\n---\nShip $ARGUMENTS", repo);
        _fx.Write(".claude/agents/reviewer.md", "---\nname: reviewer\n---\nAgent", repo);
        _fx.Write(".cursor/rules/lore.mdc", "---\ndescription: Canon\nglobs:\n  - \"docs/**\"\nalwaysApply: false\n---\nCanon.", repo);
        _fx.Write(".cursorrules", "Old style Cursor rules.", repo);
        _fx.Write("CLAUDE.md", "# Project\nBe careful.", repo);
        _fx.Write("node_modules/junk/SKILL.md", "---\nname: junk\ndescription: should be skipped\n---\nx", repo);
        return repo;
    }

    [Fact]
    public void ScanFindsSkillsRulesCommandsAndKnowledgeButNotJunk()
    {
        var plan = SkillImporter.Scan(MakeRepo());
        var byName = plan.Candidates.ToDictionary(c => c.Name);
        foreach (var name in new[] { "pdf-tools", "review", "deploy", "lore", "cursorrules", "claude-md" }) Assert.Contains(name, byName.Keys);
        Assert.DoesNotContain("junk", byName.Keys); // dependency folders are skipped
        Assert.True(byName["pdf-tools"].HasScripts);
        Assert.Equal(3, byName["pdf-tools"].FileCount);
        Assert.False(byName["review"].HasScripts);
        Assert.Equal(SkillOrigin.Claude, byName["review"].Origin);
        Assert.Equal(SkillKind.Command, byName["deploy"].Kind);
        Assert.True(byName["cursorrules"].IsInstructionFile && byName["cursorrules"].Skill.AlwaysApply);
        Assert.Contains(plan.Notes, n => n.Contains("subagent", StringComparison.Ordinal));
        Assert.Null(plan.TemporaryRoot);
    }

    [Fact]
    public void ImportActiveAndAsDraft()
    {
        var plan = SkillImporter.Scan(MakeRepo());
        var pick = plan.Candidates.Where(c => c.Name is "pdf-tools" or "lore" or "deploy").Select(c => c.Id).ToHashSet();
        var active = SkillImporter.Perform(plan, pick, SkillScope.Project, _fx.Project, asDraft: false, locations: _fx.Locations);
        Assert.Equal(3, active.Imported.Count);
        var live = SkillCatalog.Load(_fx.Project, _fx.Locations).ToDictionary(s => s.Name);
        Assert.Equal(SkillOrigin.Dsh, live["pdf-tools"].Origin);
        Assert.True(File.Exists(Path.Combine(live["pdf-tools"].Directory, "scripts", "extract.py")), "bundled files come along");
        Assert.Equal(new[] { "docs/**" }, live["lore"].Globs);
        Assert.False(live["deploy"].ModelInvocable);

        // Same import again: conflicts are renamed, not overwritten.
        var again = SkillImporter.Perform(plan, pick, SkillScope.Project, _fx.Project, asDraft: false, conflict: ConflictPolicy.Rename,
                                          locations: _fx.Locations);
        Assert.Equal(3, again.Imported.Count);
        Assert.Contains(again.Imported, p => p.Contains("pdf-tools-2", StringComparison.Ordinal));
        var strict = SkillImporter.Perform(plan, pick, SkillScope.Project, _fx.Project, asDraft: false, conflict: ConflictPolicy.Fail,
                                           locations: _fx.Locations);
        Assert.Equal(3, strict.Skipped.Count);

        // As drafts, nothing becomes active.
        var before = SkillCatalog.Load(_fx.Project, _fx.Locations).Count;
        var staged = SkillImporter.Perform(plan, plan.Candidates.Select(c => c.Id), SkillScope.User, null, asDraft: true,
                                           locations: _fx.Locations);
        Assert.Equal(plan.Candidates.Count, staged.Drafts.Count);
        Assert.Equal(before, SkillCatalog.Load(_fx.Project, _fx.Locations).Count);
        Assert.All(staged.Drafts, d =>
        {
            Assert.Equal("import", d.Source);
            Assert.Contains("Imported from", d.Note);
        });
    }

    [Fact]
    public void ImportFromSingleFilesAndSkillFolders()
    {
        var mdc = _fx.Write("loose/style.mdc", "---\ndescription: Style\nalwaysApply: true\n---\nUse tabs.");
        var only = Assert.Single(SkillImporter.Scan(mdc).Candidates);
        Assert.Equal(SkillKind.Rule, only.Kind);
        Assert.True(only.Skill.AlwaysApply);

        var cursorrules = _fx.Write("loose2/.cursorrules", "Legacy rules.");
        Assert.True(SkillImporter.Scan(cursorrules).Candidates[0].IsInstructionFile);

        _fx.Skill("one/my-skill", "my-skill", "Use when solo.");
        Assert.Equal(new[] { "my-skill" },
                     SkillImporter.Scan(Path.Combine(_fx.Base, "project", "one", "my-skill", "SKILL.md")).Candidates.Select(c => c.Name));
        Assert.Equal(SkillErrorKind.NotFound, Assert.Throws<SkillException>(() => SkillImporter.Scan(Path.Combine(_fx.Base, "missing"))).Kind);
        Assert.Equal(SkillErrorKind.Unsupported, Assert.Throws<SkillException>(() => SkillImporter.Scan(_fx.Write("x.txt", "hello"))).Kind);
    }

    [Fact]
    public void SymlinksAreNeverFollowedIntoAnImport()
    {
        var repo = Path.Combine(_fx.Base, "repo2");
        _fx.Skill("s", "s", "Use when linked.", root: repo);
        var secret = _fx.Write("secret.txt", "TOP SECRET");
        if (!TryLink(() => File.CreateSymbolicLink(Path.Combine(repo, "s", "leak.txt"), secret))) return; // no symlink rights (Windows)
        var plan = SkillImporter.Scan(Path.Combine(repo, "s"));
        SkillImporter.Perform(plan, plan.Candidates.Select(c => c.Id), SkillScope.User, null, asDraft: false, locations: _fx.Locations);
        Assert.False(File.Exists(Path.Combine(_fx.Locations.UserSkills, "s", "leak.txt")));
        Assert.True(File.Exists(Path.Combine(_fx.Locations.UserSkills, "s", "SKILL.md")));
    }

    [Fact]
    public void LinkedManifestsAndFoldersAreNotRead()
    {
        var repo = Path.Combine(_fx.Base, "repo3");
        _fx.Skill("skills/fine", "fine", "Use when nothing is linked.", root: repo);
        var secret = _fx.Write("secret.md", "---\nname: stolen\ndescription: Use when stealing.\n---\nTOP SECRET");
        _fx.Skill(".dsh/skills/elsewhere", "elsewhere", "Use when outside the import.");
        Directory.CreateDirectory(Path.Combine(repo, "skills", "evil"));
        if (!TryLink(() => File.CreateSymbolicLink(Path.Combine(repo, "skills", "evil", "SKILL.md"), secret))) return;
        if (!TryLink(() => Directory.CreateSymbolicLink(Path.Combine(repo, "skills", "linked"), Path.Combine(_fx.Project, ".dsh", "skills")))) return;
        var plan = SkillImporter.Scan(repo);
        Assert.Equal(new[] { "fine" }, plan.Candidates.Select(c => c.Name));
    }

    private static bool TryLink(Action create)
    {
        try
        {
            create();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    [Fact]
    public void OversizedSkillFolderIsRefused()
    {
        var repo = Path.Combine(_fx.Base, "big");
        _fx.Skill("huge", "huge", "Use when huge.", root: repo);
        File.WriteAllBytes(Path.Combine(repo, "huge", "blob.bin"), new byte[SkillFiles.MaxFileBytes + 1]);
        var plan = SkillImporter.Scan(repo);
        var r = SkillImporter.Perform(plan, plan.Candidates.Select(c => c.Id), SkillScope.User, null, asDraft: false, locations: _fx.Locations);
        Assert.Single(r.Skipped);
        Assert.Contains("MB", r.Skipped["huge"]);
        Assert.False(Path.Exists(Path.Combine(_fx.Locations.UserSkills, "huge")), "nothing half-copied");
    }

    // MARK: Zip

    [Fact]
    public void ExportPortableZipThenImportItBack()
    {
        _fx.Skill(".dsh/skills/alpha", "alpha", "Use when alpha.", body: "ALPHA");
        _fx.Write(".dsh/skills/alpha/ref/notes.md", "notes");
        _fx.Write(".cursor/rules/beta.mdc", "---\ndescription: Beta rule\nglobs: [\"b/**\"]\n---\nBETA");
        var skills = SkillCatalog.Load(_fx.Project, _fx.Locations);
        var zip = Path.Combine(_fx.Base, "out.zip");
        SkillExporter.Zip(skills, ExportFormat.Portable, zip);
        var listing = EntryNames(zip);
        Assert.Contains("alpha/SKILL.md", listing);
        Assert.Contains("alpha/ref/notes.md", listing);
        Assert.Contains("beta/SKILL.md", listing);
        Assert.DoesNotContain("README.txt", listing);

        var plan = SkillImporter.Scan(zip);
        try
        {
            Assert.Equal(new[] { "alpha", "beta" }, plan.Candidates.Select(c => c.Name).Order(StringComparer.Ordinal));
            var beta = plan.Candidates.First(c => c.Name == "beta");
            Assert.Equal(new[] { "b/**" }, beta.Skill.Globs); // rule metadata survives the round trip
            Assert.Equal(zip, plan.Source);
            Assert.True(Directory.Exists(plan.TemporaryRoot));
        }
        finally
        {
            SkillImporter.Dispose(plan);
        }
        Assert.False(Directory.Exists(plan.TemporaryRoot));
    }

    [Fact]
    public void LayoutZipsHoldTheirDotFoldersAndAReadme()
    {
        _fx.Skill(".dsh/skills/alpha", "alpha", "Use when alpha.", body: "ALPHA");
        _fx.Write(".dsh/skills/alpha/ref/notes.md", "notes");
        var zip = Path.Combine(_fx.Base, "claude.zip");
        File.WriteAllText(zip, "an older export"); // replaced, not appended to
        SkillExporter.Zip(SkillCatalog.Load(_fx.Project, _fx.Locations), ExportFormat.Claude, zip);
        var listing = EntryNames(zip);
        Assert.Contains(".claude/skills/alpha/SKILL.md", listing);
        Assert.Contains(".claude/skills/alpha/ref/notes.md", listing);
        Assert.Contains("README.txt", listing);
        Assert.DoesNotContain(listing, n => n.Contains('\\'));
        using (var archive = ZipFile.OpenRead(zip))
        using (var reader = new StreamReader(archive.GetEntry("README.txt")!.Open()))
        {
            Assert.StartsWith("Skills exported from DSH (Claude Code (.claude/skills)).\n\nUnzip this into a project folder", reader.ReadToEnd());
        }

        // A folder given as the zip path is an error, never something to delete.
        var folder = Path.Combine(_fx.Base, "not-a-zip");
        _fx.Write("keep.txt", "keep", folder);
        var error = Assert.Throws<SkillException>(() => SkillExporter.Zip(SkillCatalog.Load(_fx.Project, _fx.Locations), ExportFormat.Claude, folder));
        Assert.Equal(SkillErrorKind.Io, error.Kind);
        Assert.StartsWith("Couldn't create the zip:", error.Message);
        Assert.True(File.Exists(Path.Combine(folder, "keep.txt")));
    }

    [Fact]
    public void HostileZipPathsAreRefused()
    {
        foreach (var evil in new[]
                 {
                     "../evil/SKILL.md", "/abs/SKILL.md", "ok/../../escape.txt", "../evil.txt", "C:/evil.txt", "c:evil.txt",
                     "..\\evil.txt", "ok\\..\\..\\escape.txt", "\\abs\\evil.txt",
                 })
        {
            var zip = Path.Combine(_fx.Base, "evil.zip");
            File.WriteAllBytes(zip, ZipBytes((evil, "---\nname: evil\ndescription: x\n---\nbody")));
            var error = Assert.Throws<SkillException>(() => SkillImporter.Scan(zip));
            Assert.Contains("unsafe", error.Message);
        }
        Assert.False(File.Exists(Path.Combine(Path.GetTempPath(), "evil.txt")));

        var good = Path.Combine(_fx.Base, "good.zip");
        File.WriteAllBytes(good, ZipBytes(("my-skill/SKILL.md", "---\nname: my-skill\ndescription: Use when zipped by hand.\n---\nbody")));
        var plan = SkillImporter.Scan(good);
        Assert.Equal(new[] { "my-skill" }, plan.Candidates.Select(c => c.Name));
        SkillImporter.Dispose(plan);
        Assert.Equal(SkillErrorKind.Io, Assert.Throws<SkillException>(() => SkillImporter.Scan(_fx.Write("notazip.zip", "not a zip"))).Kind);
    }

    [Fact]
    public void OversizedArchivesAreRefused()
    {
        var many = Path.Combine(_fx.Base, "many.zip");
        File.WriteAllBytes(many, ZipBytes(Enumerable.Range(0, SkillImporter.MaxZipEntries + 1).Select(i => ($"s/{i}.md", "")).ToArray()));
        Assert.Contains("too many files", Assert.Throws<SkillException>(() => SkillImporter.Scan(many)).Message);

        var bomb = Path.Combine(_fx.Base, "bomb.zip");
        using (var stream = File.Create(bomb))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        using (var entry = archive.CreateEntry("big/blob.bin", CompressionLevel.Fastest).Open())
        {
            var zeros = new byte[1 << 20];
            for (var i = 0; i < 151; i++) entry.Write(zeros);
        }
        var error = Assert.Throws<SkillException>(() => SkillImporter.Scan(bomb));
        Assert.Equal(SkillErrorKind.TooLarge, error.Kind);
        Assert.Contains("MB", error.Message);
    }

    [Fact]
    public void MacJunkAndLinksInArchivesAreSkipped()
    {
        var zip = Path.Combine(_fx.Base, "mac.zip");
        using (var stream = File.Create(zip))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            AddEntry(archive, "__MACOSX/my-skill/._SKILL.md", "junk");
            AddEntry(archive, "my-skill/SKILL.md", "---\nname: my-skill\ndescription: Use when zipped on a Mac.\n---\nbody");
            var link = AddEntry(archive, "my-skill/passwd", "/etc/passwd");
            link.ExternalAttributes = unchecked((int)0xA1FF_0000u); // S_IFLNK | 0777: a Unix symlink
        }
        var plan = SkillImporter.Scan(zip);
        try
        {
            Assert.Equal(new[] { "my-skill" }, plan.Candidates.Select(c => c.Name));
            var root = plan.TemporaryRoot!;
            Assert.False(Directory.Exists(Path.Combine(root, "__MACOSX")));
            Assert.True(File.Exists(Path.Combine(root, "my-skill", "SKILL.md")));
            Assert.False(Path.Exists(Path.Combine(root, "my-skill", "passwd")));
        }
        finally
        {
            SkillImporter.Dispose(plan);
        }
    }

    // MARK: Remote

    [Fact]
    public async Task RemoteScanRejectsNonHttps()
    {
        foreach (var bad in new[] { "http://example.com/x.zip", "file:///etc/passwd", "ext::sh -c id", "git@github.com:a/b.git", "" })
        {
            var error = await Assert.ThrowsAsync<SkillException>(() => SkillImporter.ScanRemoteAsync(bad));
            Assert.Equal(SkillErrorKind.Invalid, error.Kind);
        }
    }

    private const string FooSkill = "---\nname: foo\ndescription: Use when testing codeload downloads.\n---\n\nFoo steps.\n";

    /// <summary>What codeload.github.com serves: everything under one "&lt;repo&gt;-&lt;ref&gt;/" folder.</summary>
    private static byte[] RepoZip() => ZipBytes(
        ("repo-main/", ""),
        ("repo-main/README.md", "# Repo\n"),
        ("repo-main/skills/", ""),
        ("repo-main/skills/foo/", ""),
        ("repo-main/skills/foo/SKILL.md", FooSkill),
        ("repo-main/skills/foo/notes.md", "notes"));

    [Fact]
    public async Task GitHubRepositoryLinkDownloadsTheCodeloadArchive()
    {
        var http = new FakeHttp(url => url == "https://codeload.github.com/owner/repo/zip/HEAD" ? Ok(RepoZip()) : Status(HttpStatusCode.NotFound));
        SkillImporter.HttpHandler = http;
        var plan = await SkillImporter.ScanRemoteAsync("  https://github.com/owner/repo  ");
        try
        {
            Assert.Equal(new[] { "https://codeload.github.com/owner/repo/zip/HEAD" }, http.Requests);
            var foo = Assert.Single(plan.Candidates);
            Assert.Equal("foo", foo.Name);
            Assert.Equal(2, foo.FileCount);
            Assert.Equal("https://github.com/owner/repo", plan.Source);
            Assert.True(Directory.Exists(plan.TemporaryRoot));
            var result = SkillImporter.Perform(plan, [foo.Id], SkillScope.User, null, asDraft: false, locations: _fx.Locations);
            Assert.Single(result.Imported);
            Assert.True(File.Exists(Path.Combine(_fx.Locations.UserSkills, "foo", "notes.md")));
        }
        finally
        {
            SkillImporter.Dispose(plan);
        }
        Assert.False(Directory.Exists(plan.TemporaryRoot));

        // A clone URL names the same repository.
        http.Requests.Clear();
        var dotGit = await SkillImporter.ScanRemoteAsync("https://github.com/owner/repo.git");
        SkillImporter.Dispose(dotGit);
        Assert.Equal(new[] { "https://codeload.github.com/owner/repo/zip/HEAD" }, http.Requests);
    }

    [Fact]
    public async Task GitHubTreeLinkImportsJustThatFolder()
    {
        var http = new FakeHttp(url => url == "https://codeload.github.com/owner/repo/zip/refs/heads/main" ? Ok(RepoZip()) : Status(HttpStatusCode.NotFound));
        SkillImporter.HttpHandler = http;
        var plan = await SkillImporter.ScanRemoteAsync("https://github.com/owner/repo/tree/main/skills");
        try
        {
            Assert.Equal(new[] { "https://codeload.github.com/owner/repo/zip/refs/heads/main" }, http.Requests);
            Assert.Equal(new[] { "foo" }, plan.Candidates.Select(c => c.Name));
            Assert.Equal("https://github.com/owner/repo/tree/main/skills", plan.Source);
            Assert.StartsWith(SkillFiles.Normalize(plan.TemporaryRoot!), plan.Candidates[0].Id);
        }
        finally
        {
            SkillImporter.Dispose(plan);
        }

        var missing = await Assert.ThrowsAsync<SkillException>(() => SkillImporter.ScanRemoteAsync("https://github.com/owner/repo/tree/main/nope"));
        Assert.Equal(SkillErrorKind.NotFound, missing.Kind);
        Assert.Equal("The folder “nope” isn't in that repository.", missing.Message);

        var escape = await Assert.ThrowsAsync<SkillException>(() => SkillImporter.ScanRemoteAsync("https://github.com/owner/repo/tree/main/..%2F..%2Fetc"));
        Assert.Equal(SkillErrorKind.Invalid, escape.Kind);
    }

    [Fact]
    public async Task GitHubTreeLinkFallsBackToATagAndReportsMissingRepositories()
    {
        var http = new FakeHttp(url => url == "https://codeload.github.com/owner/repo/zip/refs/tags/v1.0" ? Ok(RepoZip()) : Status(HttpStatusCode.NotFound));
        SkillImporter.HttpHandler = http;
        var plan = await SkillImporter.ScanRemoteAsync("https://github.com/owner/repo/tree/v1.0/skills/foo");
        SkillImporter.Dispose(plan);
        Assert.Equal(new[] { "foo" }, plan.Candidates.Select(c => c.Name));
        Assert.Equal(new[]
        {
            "https://codeload.github.com/owner/repo/zip/refs/heads/v1.0",
            "https://codeload.github.com/owner/repo/zip/refs/tags/v1.0",
        }, http.Requests);

        var gone = await Assert.ThrowsAsync<SkillException>(() => SkillImporter.ScanRemoteAsync("https://github.com/owner/missing"));
        Assert.Equal(SkillErrorKind.Io, gone.Kind);
        Assert.Contains("GitHub has no owner/missing", gone.Message);
        Assert.Equal(SkillErrorKind.Invalid,
                     (await Assert.ThrowsAsync<SkillException>(() => SkillImporter.ScanRemoteAsync("https://github.com/owner"))).Kind);
    }

    [Fact]
    public async Task GitHubArchiveWithoutATopFolderIsScannedAsIs()
    {
        SkillImporter.HttpHandler = new FakeHttp(_ => Ok(ZipBytes(("skills/foo/SKILL.md", FooSkill), ("NOTES.md", "notes"))));
        var plan = await SkillImporter.ScanRemoteAsync("https://github.com/owner/flat");
        SkillImporter.Dispose(plan);
        Assert.Equal(new[] { "foo" }, plan.Candidates.Select(c => c.Name));
    }

    [Fact]
    public async Task FileLinksAreDownloaded()
    {
        var http = new FakeHttp(url => url switch
        {
            "https://example.com/rules/style.mdc?raw=1" => Ok(Encoding.UTF8.GetBytes("---\ndescription: Style\nalwaysApply: true\n---\nUse tabs.")),
            "https://example.com/downloads/skills.zip" => Ok(ZipBytes(("my-skill/SKILL.md", "---\nname: my-skill\ndescription: Use when downloaded.\n---\nbody"))),
            "https://example.com/huge.zip" => Ok([1, 2, 3], contentLength: 70_000_000),
            _ => Status(HttpStatusCode.InternalServerError),
        });
        SkillImporter.HttpHandler = http;

        var rule = await SkillImporter.ScanRemoteAsync("https://example.com/rules/style.mdc?raw=1#section");
        try
        {
            var only = Assert.Single(rule.Candidates);
            Assert.Equal(SkillKind.Rule, only.Kind);
            Assert.Equal(SkillOrigin.Cursor, only.Origin);
            Assert.True(only.Skill.AlwaysApply);
            Assert.Equal("https://example.com/rules/style.mdc?raw=1", rule.Source);
            Assert.True(Directory.Exists(rule.TemporaryRoot));
        }
        finally
        {
            SkillImporter.Dispose(rule);
        }
        Assert.False(Directory.Exists(rule.TemporaryRoot));

        var zipped = await SkillImporter.ScanRemoteAsync("https://example.com/downloads/skills.zip");
        SkillImporter.Dispose(zipped);
        Assert.Equal(new[] { "my-skill" }, zipped.Candidates.Select(c => c.Name));

        var failed = await Assert.ThrowsAsync<SkillException>(() => SkillImporter.ScanRemoteAsync("https://example.com/x.md"));
        Assert.Equal("The server replied 500.", failed.Message);
        var huge = await Assert.ThrowsAsync<SkillException>(() => SkillImporter.ScanRemoteAsync("https://example.com/huge.zip"));
        Assert.Equal(SkillErrorKind.TooLarge, huge.Kind);
    }

    // MARK: Export layouts

    [Fact]
    public void ExportLayoutsAreReadableByTheirTools()
    {
        _fx.Skill(".dsh/skills/alpha", "alpha", "Use when alpha.", body: "ALPHA");
        _fx.Skill(".dsh/skills/withfiles", "withfiles", "Use when files.", body: "F");
        _fx.Write(".dsh/skills/withfiles/scripts/run.sh", "echo");
        _fx.Write(".claude/commands/ship.md", "---\ndescription: Ship\n---\nShip it");
        var skills = SkillCatalog.Load(_fx.Project, _fx.Locations);

        // Claude: lands where Claude Code looks, and DSH reads it back.
        var claudeRoot = Path.Combine(_fx.Base, "out-claude");
        SkillExporter.Write(skills, ExportFormat.Claude, claudeRoot);
        var back = SkillCatalog.Load(claudeRoot, new SkillLocations(Path.Combine(_fx.Base, "nohome"), Path.Combine(_fx.Base, "nosupport")));
        Assert.Equal(new[] { "alpha", "ship", "withfiles" }, back.Select(s => s.Name).Order(StringComparer.Ordinal));
        Assert.All(back, s => Assert.Equal(SkillOrigin.Claude, s.Origin));
        Assert.False(back.First(s => s.Name == "ship").ModelInvocable);

        // Cursor: plain skills become .mdc rules; skills with bundled files stay folders.
        var cursorRoot = Path.Combine(_fx.Base, "out-cursor");
        SkillExporter.Write(skills, ExportFormat.Cursor, cursorRoot);
        Assert.True(File.Exists(Path.Combine(cursorRoot, ".cursor", "rules", "alpha.mdc")));
        Assert.True(File.Exists(Path.Combine(cursorRoot, ".cursor", "skills", "withfiles", "scripts", "run.sh")));
        var mdc = SkillDocument.Parse(File.ReadAllText(Path.Combine(cursorRoot, ".cursor", "rules", "alpha.mdc")));
        Assert.Equal("Use when alpha.", mdc["description"]);
        Assert.False(mdc.Bool("alwaysApply"));
        Assert.Null(mdc["name"]); // Cursor rules carry no name field

        foreach (var (format, sub) in new[]
                 {
                     (ExportFormat.Agents, ".agents/skills/alpha/SKILL.md"), (ExportFormat.Dsh, ".dsh/skills/alpha/SKILL.md"),
                     (ExportFormat.Portable, "alpha/SKILL.md"),
                 })
        {
            var root = Path.Combine(_fx.Base, $"out-{format.RawValue()}");
            SkillExporter.Write(skills, format, root);
            Assert.True(File.Exists(Path.Combine([root, .. sub.Split('/')])), format.ToString());
        }
    }

    [Fact]
    public void ExportConflictPolicies()
    {
        _fx.Skill(".dsh/skills/alpha", "alpha", "Use when alpha.");
        var skills = SkillCatalog.Load(_fx.Project, _fx.Locations);
        var root = Path.Combine(_fx.Base, "target");
        SkillExporter.Write(skills, ExportFormat.Claude, root);
        var conflict = Assert.Throws<SkillException>(() => SkillExporter.Write(skills, ExportFormat.Claude, root, ConflictPolicy.Fail));
        Assert.Equal(SkillErrorKind.Exists, conflict.Kind);
        SkillExporter.Write(skills, ExportFormat.Claude, root, ConflictPolicy.Replace);
        var renamed = SkillExporter.Write(skills, ExportFormat.Claude, root, ConflictPolicy.Rename);
        Assert.EndsWith("alpha-2", renamed[0]);
        SkillExporter.Write(skills, ExportFormat.Cursor, root);
        Assert.Throws<SkillException>(() => SkillExporter.Write(skills, ExportFormat.Cursor, root, ConflictPolicy.Fail));
        var again = SkillExporter.Write(skills, ExportFormat.Cursor, root, ConflictPolicy.Rename);
        Assert.EndsWith("alpha-2.mdc", again[0]);
    }

    [Fact]
    public void ExportFormatsDescribeThemselves()
    {
        Assert.Equal(5, ExportFormats.All.Count);
        Assert.Equal(new[] { "portable", "claude", "cursor", "agents", "dsh" }, ExportFormats.All.Select(f => f.RawValue()));
        Assert.All(ExportFormats.All, f =>
        {
            Assert.NotEmpty(f.Label());
            Assert.NotEmpty(f.Detail());
        });
        Assert.Equal("Cursor (.cursor/rules, .mdc)", ExportFormat.Cursor.Label());
    }

    // MARK: Helpers

    private static List<string> EntryNames(string zip)
    {
        using var archive = ZipFile.OpenRead(zip);
        return archive.Entries.Select(e => e.FullName).ToList();
    }

    /// <summary>An archive with exactly these entry names (names ending in "/" are folders) — including
    /// hostile ones a zip tool would refuse to write.</summary>
    private static byte[] ZipBytes(params (string Name, string Content)[] entries)
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                if (name.EndsWith('/')) archive.CreateEntry(name);
                else AddEntry(archive, name, content);
            }
        }
        return memory.ToArray();
    }

    private static ZipArchiveEntry AddEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);
        return entry;
    }

    private static HttpResponseMessage Ok(byte[] body, long? contentLength = null)
    {
        var content = new ByteArrayContent(body);
        if (contentLength is { } length) content.Headers.ContentLength = length;
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private static HttpResponseMessage Status(HttpStatusCode code) => new(code) { Content = new ByteArrayContent([]) };

    /// <summary>Serves canned replies by URL and records what was asked for — no network.</summary>
    private sealed class FakeHttp(Func<string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            lock (Requests) Requests.Add(url);
            return Task.FromResult(respond(url));
        }
    }
}
