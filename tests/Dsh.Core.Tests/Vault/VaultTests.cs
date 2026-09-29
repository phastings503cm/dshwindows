using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dsh.Core.Tests;

/// <summary>New: the rest of the vault's contract — access and lookups, redaction order, use counts,
/// change notification, thread safety, file handling, the system-prompt section and the Windows
/// details (shell-specific examples, DPAPI).</summary>
public sealed class VaultTests : IDisposable
{
    private readonly TempDirectory _dir = new("dsh-vault");

    public void Dispose() => _dir.Dispose();

    private CredentialVault Vault(ISecretBlobStore? store = null) => new(_dir.Path, store ?? new MemoryBlobStore());

    /// <summary>A store that counts loads and can be told to fail.</summary>
    private sealed class ProbeStore : ISecretBlobStore
    {
        private readonly MemoryBlobStore _inner = new();
        public int Loads;
        public bool Fail;

        public byte[]? Load()
        {
            Interlocked.Increment(ref Loads);
            if (Fail) throw new IOException("disk on fire");
            return _inner.Load();
        }

        public void Save(byte[] data)
        {
            if (Fail) throw new IOException("disk on fire");
            _inner.Save(data);
        }
    }

    // MARK: - Lookup and access

    [Fact]
    public void LookupReportsAccessAndNeverPrintsTheSecret()
    {
        var v = Vault();
        v.Add("NEVER", "never-value", access: VaultAccess.Never);
        v.Add("ASK", "ask-value", access: VaultAccess.Ask);
        Assert.Equal(new VaultLookup.Value("never-value", VaultAccess.Never), v.Lookup("never"));
        Assert.Equal(new VaultLookup.Value("ask-value", VaultAccess.Ask), v.Lookup("ASK"));
        Assert.Equal(new VaultLookup.Missing(), v.Lookup("MISSING"));
        Assert.DoesNotContain("ask-value", v.Lookup("ASK").ToString());
    }

    [Fact]
    public void GrantsAreCaseInsensitive()
    {
        var grants = new VaultGrants();
        Assert.False(grants.Has("api_key"));
        grants.Grant("API_KEY");
        Assert.True(grants.Has("api_key"));
        Assert.False(new VaultGrants().Has("API_KEY")); // per chat
    }

    [Fact]
    public void TagsAreCleanedAndBlankDetailsDropped()
    {
        var v = Vault();
        var e = v.Add("  SPACED  ", "value-1", tags: [" Prod ", "", "prod", "EU"], username: "  ", url: " https://x.test ");
        Assert.Equal("SPACED", e.Name);
        Assert.Equal(["prod", "eu"], e.Tags);
        Assert.Null(e.Username);
        Assert.Equal("https://x.test", e.Url);
        Assert.Equal("{{vault:SPACED}}", e.Placeholder);
    }

    [Theory]
    [InlineData("OPENAI_API_KEY", true)]
    [InlineData("stripe.test-2", true)]
    [InlineData("", false)]
    [InlineData("has space", false)]
    [InlineData("clé", false)] // a placeholder can't spell it
    [InlineData("a{b}", false)]
    public void NamesArePlaceholderSafe(string name, bool valid) => Assert.Equal(valid, CredentialVault.IsValidName(name));

    [Fact]
    public void NamesAreAtMostEightyCharacters()
    {
        Assert.True(CredentialVault.IsValidName(new string('A', 80)));
        Assert.False(CredentialVault.IsValidName(new string('A', 81)));
    }

    [Fact]
    public void FingerprintIsTheStartOfTheSha256()
    {
        // printf 'abc' | sha256sum → ba7816bf8f01cfea…
        Assert.Equal("sha256:ba7816bf8f01", CredentialVault.Fingerprint("abc"));
    }

    // MARK: - Updates

    [Fact]
    public void UpdateRejectsUnknownDuplicateAndEmpty()
    {
        var v = Vault();
        var a = v.Add("A_KEY", "value-a");
        v.Add("B_KEY", "value-b");
        Assert.Equal(VaultErrorKind.NotFound, Assert.Throws<VaultException>(() => v.Update(a with { Id = "nope" })).Kind);
        Assert.Equal(VaultErrorKind.DuplicateName, Assert.Throws<VaultException>(() => v.Update(a with { Name = "b_key" })).Kind);
        Assert.Equal(VaultErrorKind.EmptyValue, Assert.Throws<VaultException>(() => v.Update(a, "")).Kind);
        Assert.Equal(VaultErrorKind.InvalidName, Assert.Throws<VaultException>(() => v.Update(a with { Name = "no way" })).Kind);
        v.Update(a with { Name = "a_key" }); // renaming to its own name in another case is fine
        Assert.Equal("a_key", v.Entry("A_KEY")?.Name);
    }

    [Fact]
    public void UpdateFromAStaleCopyKeepsTheVaultsCountsAndFingerprint()
    {
        var v = Vault();
        var stale = v.Add("TOKEN", "token-value");
        v.NoteUse("TOKEN");
        v.NoteUse("token");
        v.Update(stale with { Description = "edited", UseCount = 0, Fingerprint = "sha256:forged", Access = VaultAccess.Ask });
        var now = v.Entry("TOKEN")!;
        Assert.Equal("edited", now.Description);
        Assert.Equal(VaultAccess.Ask, now.Access);
        Assert.Equal(2, now.UseCount);
        Assert.NotNull(now.LastUsedAt);
        Assert.Equal(stale.Fingerprint, now.Fingerprint);
        Assert.Equal(stale.CreatedAt, now.CreatedAt);
    }

    [Fact]
    public void UsesAreCountedAndPersisted()
    {
        var store = new MemoryBlobStore();
        var v = Vault(store);
        v.Add("DEPLOY_TOKEN", "deploy-secret");
        v.NoteUse("deploy_token");
        v.NoteUse("UNKNOWN"); // ignored
        var reopened = Vault(store);
        Assert.Equal(1, reopened.Entry("DEPLOY_TOKEN")?.UseCount);
        Assert.NotNull(reopened.Entry("DEPLOY_TOKEN")?.LastUsedAt);
        Assert.Equal(v.Entry("DEPLOY_TOKEN"), reopened.Entry("DEPLOY_TOKEN"));
    }

    [Fact]
    public void EveryChangeBumpsTheRevisionAndRaisesChanged()
    {
        var v = Vault();
        var raised = 0;
        var consistent = true;
        v.Changed += (sender, _) =>
        {
            raised++;
            // Raised outside the lock: a handler can read the vault.
            consistent &= ReferenceEquals(sender, v) && v.All.Count >= 0;
        };
        var e = v.Add("K1", "value-1");
        v.Update(e with { Description = "d" });
        v.NoteUse("K1");
        v.Delete(e.Id);
        v.Delete(e.Id); // already gone: no change
        v.NoteUse("K1"); // unknown: no change
        Assert.Throws<VaultException>(() => v.Add("bad name", "x"));
        Assert.Equal(4, raised);
        Assert.Equal(4, v.Revision);
        Assert.True(consistent);
    }

    // MARK: - Redaction

    [Fact]
    public void ValuesForRedactionAreLongestFirstAndSkipShortOnes()
    {
        var v = Vault();
        v.Add("SHORT", "abc");
        v.Add("BASE", "abcd1234");
        v.Add("LONG", "abcd1234-extended");
        Assert.Equal([("LONG", "abcd1234-extended"), ("BASE", "abcd1234")], v.ValuesForRedaction());
        Assert.Equal("x=[vault:LONG] y=[vault:BASE] z=abc",
                     VaultPlaceholders.Redact("x=abcd1234-extended y=abcd1234 z=abc", v.ValuesForRedaction()));
    }

    [Fact]
    public void AnEmptyVaultNeverTouchesTheStore()
    {
        var store = new ProbeStore();
        var v = Vault(store);
        Assert.Empty(v.ValuesForRedaction());
        Assert.Equal(new VaultLookup.Missing(), v.Lookup("ANY"));
        Assert.Equal(0, store.Loads);
    }

    [Fact]
    public void RedactIgnoresEmptyValues() =>
        Assert.Equal("unchanged", VaultPlaceholders.Redact("unchanged", [("EMPTY", "")]));

    // MARK: - Store failures

    [Fact]
    public void StoreFailuresSurfaceAsVaultErrors()
    {
        var store = new ProbeStore { Fail = true };
        var v = Vault(store);
        var error = Assert.Throws<VaultException>(() => v.Add("KEY", "value-1"));
        Assert.Equal(VaultErrorKind.Storage, error.Kind);
        Assert.IsType<IOException>(error.InnerException);
        Assert.True(v.IsEmpty);
        Assert.False(File.Exists(_dir[CredentialVault.MetadataFileName]));

        store.Fail = false;
        var reopened = Vault(store);
        var e = reopened.Add("KEY", "value-1");
        store.Fail = true;
        var locked = Vault(store); // a fresh vault that can't read the store
        Assert.Equal(new VaultLookup.Missing(), locked.Lookup("KEY"));
        Assert.Empty(locked.ValuesForRedaction());
        Assert.Equal(VaultErrorKind.Storage, Assert.Throws<VaultException>(() => locked.Value(e.Id)).Kind);
    }

    [Fact]
    public void ADamagedMetadataFileIsKeptAside()
    {
        File.WriteAllText(_dir[CredentialVault.MetadataFileName], "{ not json");
        var v = Vault();
        Assert.True(v.IsEmpty);
        Assert.Equal("{ not json", File.ReadAllText(_dir[CredentialVault.MetadataFileName + ".damaged"]));
    }

    [Fact]
    public void WritesLeaveNoTemporaryFiles()
    {
        var v = Vault();
        for (var i = 0; i < 5; i++) v.Add($"KEY_{i}", $"value-{i}");
        Assert.Equal([CredentialVault.MetadataFileName],
                     Directory.EnumerateFileSystemEntries(_dir.Path).Select(Path.GetFileName));
        var stored = JsonNode.Parse(File.ReadAllText(_dir[CredentialVault.MetadataFileName]))!.AsArray();
        Assert.Equal(5, stored.Count);
        Assert.Equal("apiKey", stored[0]!["kind"]!.GetValue<string>());
        Assert.Equal("allowed", stored[0]!["access"]!.GetValue<string>());
    }

    [Fact]
    public async Task ConcurrentUseFromManyThreadsIsSafe()
    {
        var v = Vault();
        v.Add("SHARED", "shared-value");
        var tasks = Enumerable.Range(0, 8).Select(t => Task.Run(() =>
        {
            v.Add($"KEY_{t}", $"value-{t}");
            for (var i = 0; i < 25; i++)
            {
                v.NoteUse("SHARED");
                Assert.IsType<VaultLookup.Value>(v.Lookup("SHARED"));
                _ = v.ValuesForRedaction();
                _ = v.Search("key");
            }
        })).ToArray();
        await Task.WhenAll(tasks);
        Assert.Equal(200, v.Entry("SHARED")?.UseCount);
        Assert.Equal(9, v.All.Count);
        Assert.Equal(9, Vault().All.Count); // what's on disk agrees
    }

    // MARK: - Placeholders

    [Fact]
    public void SubstituteMatchesNamesCaseInsensitivelyAndLeavesUnknownOnes()
    {
        var output = VaultPlaceholders.Substitute("""{"command":"echo {{vault:api_key}} {{vault:OTHER}}"}""",
                                                  new Dictionary<string, string> { ["API_KEY"] = "sk-1" });
        Assert.Equal("""{"command":"echo sk-1 {{vault:OTHER}}"}""", output);
        Assert.Equal(["api_key", "OTHER"], VaultPlaceholders.Names(output.Replace("sk-1", "{{vault:api_key}}") + "{{vault:API_KEY}}"));
    }

    [Fact]
    public void EscapingCoversControlCharactersAndKeepsUnicode()
    {
        Assert.Equal("a\\u0001b\\tc\\\\d/é😀", VaultPlaceholders.JsonEscaped("a\u0001b\tc\\d/é😀"));
        const string value = "\u0000\b\f\r\n\"'<>&";
        var json = VaultPlaceholders.Substitute("""{"v":"{{vault:X}}"}""", new Dictionary<string, string> { ["X"] = value });
        Assert.Equal(value, JsonArgs.String(json, "v"));
    }

    [Fact]
    public void OnlyToolsThatExecuteTheirArgumentsGetValues()
    {
        Assert.True(VaultPlaceholders.SubstitutesInto("run_shell_command"));
        Assert.True(VaultPlaceholders.SubstitutesInto("write_file"));
        foreach (var name in new[] { "agent", "queue_task", "propose_skill", "todo_write", "use_skill", "vault_search",
                                     "agent_status", "agent_stop", "exit_plan_mode" })
            Assert.False(VaultPlaceholders.SubstitutesInto(name), name);
    }

    // MARK: - Search tool

    [Fact]
    public void SearchToolParametersAreAJsonObject()
    {
        var spec = new VaultSearchTool(Vault()).Spec;
        Assert.Equal("vault_search", spec.Name);
        Assert.IsType<JsonObject>(JsonNode.Parse(spec.Parameters));
    }

    [Fact]
    public async Task SearchToolShowsDetailsAndAnExampleForTheChatsShell()
    {
        var v = Vault();
        v.Add("GH_TOKEN", "ghp_value", VaultKind.Token, "GitHub", ["git", "ci"], "octocat", "https://api.github.com",
              VaultAccess.Ask);
        using var tools = new ToolTestContext();
        var tool = new VaultSearchTool(v);
        var powershell = (await tool.ExecuteAsync("""{"query":"github"}""",
            tools.Context with { Shell = new AgentShell(ShellKind.PowerShell, "pwsh", "pwsh") }, CancellationToken.None)).Output;
        var fingerprint = v.Entry("GH_TOKEN")!.Fingerprint;
        Assert.Equal(
            "1 credential:\n" +
            $"- {{{{vault:GH_TOKEN}}}} — Token: GitHub (user octocat; https://api.github.com; tags git, ci; {fingerprint}; asks the user before first use)\n" +
            "Use one by writing its placeholder, e.g. $env:API_KEY = '{{vault:GH_TOKEN}}' in a shell command.",
            powershell);
        var bash = (await tool.ExecuteAsync("{}", tools.Context with { Shell = new AgentShell(ShellKind.Bash, "/bin/bash", "bash") },
            CancellationToken.None)).Output;
        Assert.EndsWith("e.g. export API_KEY={{vault:GH_TOKEN}} in a shell command.", bash);
        var cmd = (await tool.ExecuteAsync("{}", tools.Context with { Shell = new AgentShell(ShellKind.Cmd, "cmd.exe", "cmd") },
            CancellationToken.None)).Output;
        Assert.EndsWith("e.g. set \"API_KEY={{vault:GH_TOKEN}}\" in a shell command.", cmd);
        Assert.DoesNotContain("ghp_value", powershell + bash + cmd);
    }

    [Fact]
    public async Task SearchToolListsAtMostFifty()
    {
        var v = Vault();
        for (var i = 0; i < 55; i++) v.Add($"KEY_{i:00}", $"value-{i}");
        using var tools = new ToolTestContext();
        var output = await tools.Output(new VaultSearchTool(v), """{"query":"key"}""");
        Assert.StartsWith("55 credentials:\n- {{vault:KEY_00}}", output);
        Assert.Contains("… 5 more; narrow the query.", output);
        Assert.DoesNotContain("KEY_50}}", output);
    }

    [Fact]
    public async Task SearchToolPointsAnEmptyVaultAtTheWindowsShortcut()
    {
        using var tools = new ToolTestContext();
        var output = await tools.Output(new VaultSearchTool(Vault()), """{"query":"anything"}""");
        Assert.Equal("The vault is empty. Ask the user to add the credential in the Credentials Vault (Ctrl+Shift+K) — never ask them to paste it into chat.", output);
    }

    // MARK: - System prompt

    [Fact]
    public void PromptSectionForAnEmptyVault()
    {
        Assert.Equal(
            "--- Credential vault ---\nThe user keeps API keys, tokens and passwords in a credential vault (empty right now). If a task needs one, " +
            "ask them to add it in the Credentials Vault (Ctrl+Shift+K) — never ask them to paste a secret into the chat.",
            VaultPrompt.Section([]));
    }

    [Fact]
    public void PromptSectionListsUsableCredentialsOnly()
    {
        var v = Vault();
        v.Add("OPENAI_API_KEY", "sk-secret-1", description: new string('d', 100));
        v.Add("ASK_ME", "ask-secret", VaultKind.Password, access: VaultAccess.Ask);
        v.Add("ROOT_PW", "root-secret", VaultKind.Password, access: VaultAccess.Never);
        var section = VaultPrompt.Section(v.All);
        Assert.StartsWith("--- Credential vault ---\nThe user's credentials are in a vault. You never see their values.", section);
        Assert.Contains("a shell command (`$env:OPENAI_API_KEY = '{{vault:OPENAI_API_KEY}}'; …`)", section);
        Assert.Contains("write {{vault:NAME}} to refer to it", section);
        Assert.EndsWith(
            "\nAvailable:\n- {{vault:ASK_ME}} (Password; asks the user first)\n" +
            $"- {{{{vault:OPENAI_API_KEY}}}} (API key: {new string('d', 80)})",
            section);
        Assert.DoesNotContain("ROOT_PW", section);
        Assert.DoesNotContain("-secret", section);
        Assert.Contains("(`export OPENAI_API_KEY={{vault:OPENAI_API_KEY}}; …`)", VaultPrompt.Section(v.All, ShellKind.Bash));
    }

    [Fact]
    public void PromptSectionWhenNothingIsUsableOrThereAreMany()
    {
        var v = Vault();
        v.Add("ROOT_PW", "root-secret", access: VaultAccess.Never);
        Assert.EndsWith("\nNo credential is currently available to you.", VaultPrompt.Section(v.All));
        for (var i = 0; i < 32; i++) v.Add($"KEY_{i:00}", $"value-{i}");
        var section = VaultPrompt.Section(v.All);
        Assert.EndsWith("- {{vault:KEY_29}} (API key)\n… and 2 more (vault_search).", section);
    }

    // MARK: - DPAPI (Windows)

    [WindowsFact]
    public void DpapiRoundTripsAndNeverWritesPlaintext()
    {
        var path = _dir["vault.bin"];
        var store = new DpapiBlobStore(path);
        Assert.Null(store.Load()); // nothing saved yet
        var plain = Encoding.UTF8.GetBytes("""{"id":"sk-dpapi-plaintext"}""");
        store.Save(plain);
        Assert.Equal(plain, store.Load());
        Assert.Equal(plain, new DpapiBlobStore(path).Load());
        Assert.DoesNotContain("sk-dpapi-plaintext", Encoding.UTF8.GetString(File.ReadAllBytes(path)));
        // Different entropy: DPAPI refuses, reported as a vault error.
        var wrong = new DpapiBlobStore(path, Encoding.UTF8.GetBytes("someone else"));
        Assert.Equal(VaultErrorKind.Storage, Assert.Throws<VaultException>(() => wrong.Load()).Kind);
    }

    [WindowsFact]
    public void TheDefaultStoreIsDpapiNextToTheMetadata()
    {
        var v = new CredentialVault(_dir.Path);
        var e = v.Add("WIN_TOKEN", "win-secret-value");
        Assert.True(File.Exists(_dir[DpapiBlobStore.DefaultFileName]));
        Assert.DoesNotContain("win-secret-value", Encoding.UTF8.GetString(File.ReadAllBytes(_dir[DpapiBlobStore.DefaultFileName])));
        Assert.Equal("win-secret-value", new CredentialVault(_dir.Path).Value(e.Id));
    }

    [UnixFact]
    public void OffWindowsTheDefaultStoreRefusesCleanly()
    {
        var v = new CredentialVault(_dir.Path);
        var error = Assert.Throws<VaultException>(() => v.Add("KEY", "value-1"));
        Assert.Equal(VaultErrorKind.Storage, error.Kind);
        Assert.IsType<PlatformNotSupportedException>(error.InnerException);
        Assert.True(v.IsEmpty);
    }
}

/// <summary>New: the vault lives in DSH's own folder (DSH_HOME redirects it, so serial).</summary>
[Collection(SerialStaticState.Name)]
public sealed class VaultLocationTests : IDisposable
{
    private readonly TempDirectory _home = new("dsh-home");
    private readonly string? _previous = Environment.GetEnvironmentVariable("DSH_HOME");

    public VaultLocationTests() => Environment.SetEnvironmentVariable("DSH_HOME", _home.Path);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("DSH_HOME", _previous);
        _home.Dispose();
    }

    [Fact]
    public void TheVaultDefaultsToTheAppFolder()
    {
        var vault = new CredentialVault();
        Assert.Equal(_home.Path, vault.StorageDirectory);
        Assert.Equal(Path.Combine(_home.Path, "vault.json"), vault.MetadataPath);
        Assert.True(vault.IsEmpty);
        Assert.False(File.Exists(vault.MetadataPath)); // nothing is written until something changes
    }
}
