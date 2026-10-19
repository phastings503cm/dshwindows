using System.Text.Json.Nodes;

namespace Vision.Core.Tests;

/// <summary>Settings › Import &amp; Export, the export half: Vision's skills, memory, keys (after a confirm) and model servers
/// into an OpenClaw on this PC — plus the clash choices both directions share.</summary>
public sealed class OpenClawExportTests : IDisposable
{
    private readonly OpenClawFixture _fx = new();
    private readonly TempDirectory _vision = new("vision-export");

    public void Dispose()
    {
        _fx.Dispose();
        _vision.Dispose();
    }

    private OpenClawInstall Install => new() { StateDir = _fx.State, ConfigPath = Path.Combine(_fx.State, "openclaw.json") };

    private Skill DeploySkill()
    {
        var file = _vision.Write("skills/deploy/SKILL.md", "---\nname: deploy\ndescription: Ship the service\n---\nRun the publish script.\n");
        _vision.Write("skills/deploy/scripts/publish.ps1", "dotnet publish\n");
        return new Skill { Path = file, Name = "deploy", Description = "Ship the service", Scope = SkillScope.User };
    }

    private MemoryStore Memory()
    {
        var memory = new MemoryStore(Path.Combine(_vision.Path, "memory"));
        memory.Import([
            new MemoryDraft { Title = "Build with dotnet 10", Body = "The repo pins SDK 10.0.100.", Kind = MemoryKinds.Fact, Source = "user" },
            new MemoryDraft { Title = "From OpenClaw before", Body = "Imported note", Source = "import:openclaw/MEMORY.md" },
        ], out _);
        return memory;
    }

    private CredentialVault Vault()
    {
        var vault = new CredentialVault(Path.Combine(_vision.Path, "vault"), new MemoryBlobStore());
        vault.Add("GITHUB_TOKEN", "ghp_fromvision_0123456789abcdefghij", VaultKind.Token);
        vault.Add("my-api.key", "value with \"quotes\" and \\ slash", VaultKind.ApiKey);
        return vault;
    }

    [Fact]
    public async Task SkillsMemoryAndConfirmedKeysLandWhereOpenClawReadsThem()
    {
        var envBefore = File.ReadAllText(Path.Combine(_fx.State, ".env"));
        var vault = Vault();
        var workspace = await OpenClawExporter.WorkspaceAsync(Install, _fx.Local());
        Assert.Equal(_fx.Workspace, workspace.TrimEnd('/', '\\'));

        var result = OpenClawExporter.Export(new OpenClawExportSelection
        {
            Skills = [DeploySkill()],
            Memory = true,
            Vault = new HashSet<string> { "GITHUB_TOKEN", "my-api.key" },
            VaultConfirmed = true,
        }, Install, workspace, Memory(), vault);

        Assert.Equal(["deploy"], result.Skills);
        Assert.True(File.Exists(Path.Combine(_fx.State, "skills", "deploy", "SKILL.md")));
        Assert.True(File.Exists(Path.Combine(_fx.State, "skills", "deploy", "scripts", "publish.ps1")));

        var memoryText = File.ReadAllText(Path.Combine(workspace, "MEMORY.md"));
        Assert.Contains("<!-- vision-export:begin MEMORY.md sha256:", memoryText);
        Assert.Contains("## From Vision — MEMORY.md", memoryText);
        Assert.Contains("### Build with dotnet 10", memoryText);
        Assert.DoesNotContain("Imported note", memoryText); // what came from OpenClaw doesn't go back
        Assert.Equal(1, result.Notes);

        var env = File.ReadAllText(Path.Combine(_fx.State, ".env"));
        Assert.StartsWith(envBefore.TrimEnd('\n'), env); // everything OpenClaw had is still there
        Assert.Contains("GITHUB_TOKEN=\"ghp_fromvision_0123456789abcdefghij\"", env);
        Assert.Contains("MY_API_KEY=\"value with \\\"quotes\\\" and \\\\ slash\"", env);
        Assert.Equal(["GITHUB_TOKEN", "MY_API_KEY"], result.Secrets.Order());
        Assert.Equal("value with \"quotes\" and \\ slash", OpenClawExporter.ParseEnvLine(env.Split('\n').First(l => l.StartsWith("MY_API_KEY")))!.Value.Value);
        Assert.Equal("1 skill, 1 memory note and 2 keys and passwords", result.Summary);

        // Again: the memory block is the same, and the keys are already there.
        var again = OpenClawExporter.Export(new OpenClawExportSelection
        {
            Memory = true, Vault = new HashSet<string> { "GITHUB_TOKEN" }, VaultConfirmed = true,
        }, Install, workspace, Memory(), vault);
        Assert.Equal(1, again.MemoryUnchanged);
        Assert.Equal("OpenClaw already has GITHUB_TOKEN with this value.", again.SecretsSkipped["GITHUB_TOKEN"]);
        Assert.Equal(memoryText, File.ReadAllText(Path.Combine(workspace, "MEMORY.md")));
    }

    [Fact]
    public void KeysAreNeverWrittenWithoutTheConfirm()
    {
        var env = Path.Combine(_fx.State, ".env");
        var before = File.ReadAllText(env);
        var result = OpenClawExporter.Export(new OpenClawExportSelection { Vault = new HashSet<string> { "GITHUB_TOKEN" } }, Install, _fx.Workspace, null, Vault());
        Assert.Empty(result.Secrets);
        Assert.StartsWith("Not confirmed", result.SecretsSkipped["GITHUB_TOKEN"]);
        Assert.Equal(before, File.ReadAllText(env));
    }

    [Theory]
    [InlineData(OpenClawConflict.Skip, "SPARK_KEY=spk-live-4f8a1c9d2e7b6a305d", null)]
    [InlineData(OpenClawConflict.Replace, "SPARK_KEY=\"spk-from-vision-value-1234\"", null)]
    [InlineData(OpenClawConflict.KeepBoth, "SPARK_KEY=spk-live-4f8a1c9d2e7b6a305d", "SPARK_KEY_VISION=\"spk-from-vision-value-1234\"")]
    public void AKeyOpenClawAlreadyHasFollowsTheClashChoice(OpenClawConflict conflict, string expectedLine, string? addedLine)
    {
        var vault = new CredentialVault(Path.Combine(_vision.Path, "v-" + conflict), new MemoryBlobStore());
        vault.Add("SPARK_KEY", "spk-from-vision-value-1234");
        var result = OpenClawExporter.Export(new OpenClawExportSelection { Vault = new HashSet<string> { "SPARK_KEY" }, VaultConfirmed = true, Conflict = conflict },
            Install, _fx.Workspace, null, vault);
        var lines = File.ReadAllLines(Path.Combine(_fx.State, ".env"));
        Assert.Contains(expectedLine, lines);
        if (addedLine is not null) Assert.Contains(addedLine, lines);
        if (conflict == OpenClawConflict.Skip) Assert.Contains("kept that one", result.SecretsSkipped["SPARK_KEY"]);
        Assert.DoesNotContain("spk-from-vision", string.Join(" ", result.Problems) + string.Join(" ", result.SecretsSkipped.Values));
    }

    [Fact]
    public void AConfigWithCommentsIsNeverRewrittenTheServersGoBesideIt()
    {
        var config = Path.Combine(_fx.State, "openclaw.json");
        var before = File.ReadAllText(config);
        var result = OpenClawExporter.Export(new OpenClawExportSelection
        {
            Servers = [new OpenClawExportServer("DGX Spark", "https://spark.local:8443/v1", "qwen3-coder-30b", 262_144, "spark-route-key-123")],
            VaultConfirmed = true,
        }, Install, _fx.Workspace, null, null);

        Assert.Equal(before, File.ReadAllText(config));
        Assert.Equal(Path.Combine(_fx.State, OpenClawExporter.ServersSnippetName), result.ServersSnippet);
        var snippet = File.ReadAllText(result.ServersSnippet!);
        Assert.StartsWith("// Model servers from Vision.", snippet);
        var parsed = Json5.TryParse(snippet, out _) as JsonObject;
        var provider = parsed!["models"]!["providers"]!["vision-dgx-spark"]!;
        Assert.Equal("https://spark.local:8443/v1", provider["baseUrl"]!.GetValue<string>());
        Assert.Equal("openai-completions", provider["api"]!.GetValue<string>());
        Assert.Equal("${VISION_DGX_SPARK_API_KEY}", provider["apiKey"]!.GetValue<string>());
        Assert.Equal(262_144, provider["models"]![0]!["contextWindow"]!.GetValue<int>());
        Assert.DoesNotContain("spark-route-key-123", snippet);
        Assert.Contains("VISION_DGX_SPARK_API_KEY=\"spark-route-key-123\"", File.ReadAllText(Path.Combine(_fx.State, ".env")));
    }

    [Fact]
    public void APlainJsonConfigGetsTheServersWithABackupAndTheClashChoice()
    {
        var config = Path.Combine(_fx.State, "openclaw.json");
        File.WriteAllText(config, """{"models":{"providers":{"vision-spark":{"baseUrl":"http://old:8002/v1","models":[{"id":"x"}]}}},"gateway":{"port":18789}}""");
        var servers = new[] { new OpenClawExportServer("Spark", "http://10.0.0.5:8002/v1", "qwen3", null, null) };

        var skipped = OpenClawExporter.Export(new OpenClawExportSelection { Servers = servers, Conflict = OpenClawConflict.Skip }, Install, _fx.Workspace, null, null);
        Assert.Empty(skipped.Servers);
        Assert.Contains("kept that one", skipped.ServersSkipped["Spark"]);

        var both = OpenClawExporter.Export(new OpenClawExportSelection { Servers = servers, Conflict = OpenClawConflict.KeepBoth }, Install, _fx.Workspace, null, null);
        Assert.Equal(["vision-spark-2"], both.Servers);
        var json = JsonNode.Parse(File.ReadAllText(config))!;
        Assert.Equal("http://old:8002/v1", json["models"]!["providers"]!["vision-spark"]!["baseUrl"]!.GetValue<string>());
        Assert.Equal("http://10.0.0.5:8002/v1", json["models"]!["providers"]!["vision-spark-2"]!["baseUrl"]!.GetValue<string>());
        Assert.Null(json["models"]!["providers"]!["vision-spark-2"]!["apiKey"]); // no key without the confirm
        Assert.Equal(18789, json["gateway"]!["port"]!.GetValue<int>());
        Assert.True(File.Exists(config + ".vision-backup"));
        Assert.Null(both.ServersSnippet);
    }

    [Fact]
    public void ASkillOpenClawAlreadyHasIsKeptUnlessReplaceIsChosen()
    {
        OpenClawFixture.Write(Path.Combine(_fx.State, "skills", "deploy", "SKILL.md"), "---\nname: deploy\n---\nOpenClaw's own.\n");
        var kept = OpenClawExporter.Export(new OpenClawExportSelection { Skills = [DeploySkill()] }, Install, _fx.Workspace, null, null);
        Assert.Contains("kept that one", kept.SkillsSkipped["deploy"]);
        Assert.Contains("OpenClaw's own.", File.ReadAllText(Path.Combine(_fx.State, "skills", "deploy", "SKILL.md")));

        var replaced = OpenClawExporter.Export(new OpenClawExportSelection { Skills = [DeploySkill()], Conflict = OpenClawConflict.Replace }, Install, _fx.Workspace, null, null);
        Assert.Equal(["deploy"], replaced.Skills);
        Assert.Contains("Run the publish script.", File.ReadAllText(Path.Combine(_fx.State, "skills", "deploy", "SKILL.md")));
    }

    [Fact]
    public void ANoteThatLooksLikeASecretStaysBehind()
    {
        var memory = new MemoryStore(Path.Combine(_vision.Path, "memory-secret"));
        memory.Import([new MemoryDraft { Title = "Deploy", Body = "Use dotnet publish -c Release", Source = "user" }], out _);
        // (The store itself refuses secrets; one that slipped in by an older build is still kept out of the export.)
        var (body, count, leftOut) = OpenClawExporter.MemoryBody(memory);
        Assert.Equal((1, 0), (count, leftOut));
        Assert.Contains("Use dotnet publish", body);
    }

    [Fact]
    public void MarkdownBlocksUpdateInPlaceAndStripCleanly()
    {
        var (first, added) = MarkdownBlocks.Upsert("vision-export", "MEMORY.md", "From Vision — MEMORY.md", "one", "# Mine\n\nKeep this.\n");
        Assert.Equal(MarkdownBlocks.Outcome.Added, added);
        var (second, updated) = MarkdownBlocks.Upsert("vision-export", "MEMORY.md", "From Vision — MEMORY.md", "two", first + "\nAfter.\n");
        Assert.Equal(MarkdownBlocks.Outcome.Updated, updated);
        Assert.Contains("two", second);
        Assert.DoesNotContain("\none\n", second);
        Assert.Contains("After.", second);
        Assert.Equal(MarkdownBlocks.Outcome.Unchanged, MarkdownBlocks.Upsert("vision-export", "MEMORY.md", "t", "two", second).Outcome);
        var stripped = MarkdownBlocks.Strip("vision-export", second);
        Assert.Contains("Keep this.", stripped);
        Assert.DoesNotContain("vision-export", stripped);
    }

    // MARK: The import side's clash choices

    private async Task<OpenClawBundle> Bundle()
    {
        var source = _fx.Local();
        var install = (await OpenClawScanner.FindAsync(source, options: OpenClawReaderTests.NoRoaming)).Single();
        return await OpenClawReader.ReadAsync(source, install);
    }

    [Theory]
    [InlineData(OpenClawConflict.Skip, "an-entirely-different-token-value", false)]
    [InlineData(OpenClawConflict.Replace, OpenClawFixture.HuggingFaceToken, false)]
    [InlineData(OpenClawConflict.KeepBoth, "an-entirely-different-token-value", true)]
    public async Task AKeyVisionAlreadyHasFollowsTheClashChoice(OpenClawConflict conflict, string expected, bool renamed)
    {
        var bundle = await Bundle();
        var vault = new CredentialVault(Path.Combine(_vision.Path, "import-" + conflict), new MemoryBlobStore());
        vault.Add("HF_TOKEN", "an-entirely-different-token-value", VaultKind.Token);
        var selection = new OpenClawSelection { Credentials = { ["HF_TOKEN"] = VaultAccess.Ask }, CredentialConflict = conflict };
        var result = await OpenClawImporter.ApplyAsync(_fx.Local(), bundle, selection, new OpenClawTargets(null, vault, null));
        Assert.Equal(expected, vault.Value(vault.Entry("HF_TOKEN")!.Id));
        Assert.Equal(renamed, vault.Entry("HF_TOKEN_OPENCLAW") is not null);
        if (conflict == OpenClawConflict.Skip) Assert.Contains("kept that one", result.CredentialsSkipped["HF_TOKEN"]);
    }

    [Fact]
    public async Task ASkillVisionAlreadyHasIsKeptWhenSkipIsChosen()
    {
        var bundle = await Bundle();
        var skills = new SkillLocations(Path.Combine(_vision.Path, "home"), Path.Combine(_vision.Path, "appdata"));
        var first = bundle.Skills.First(s => !s.Shadowed);
        var mine = Path.Combine(skills.UserSkills, SkillNaming.Slug(first.Name), "SKILL.md");
        Directory.CreateDirectory(Path.GetDirectoryName(mine)!);
        File.WriteAllText(mine, $"---\nname: {first.Name}\ndescription: mine\n---\nMy own.\n");

        var selection = new OpenClawSelection { Skills = { first.Directory }, SkillConflict = OpenClawConflict.Skip };
        var result = await OpenClawImporter.ApplyAsync(_fx.Local(), bundle, selection, new OpenClawTargets(null, null, skills));
        Assert.Empty(result.Skills);
        Assert.Contains("kept that one", result.SkillsSkipped[first.Name]);
        Assert.Contains("My own.", File.ReadAllText(mine));
    }
}
