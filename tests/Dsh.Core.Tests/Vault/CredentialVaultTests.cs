using System.Text.Json;

namespace Dsh.Core.Tests;

/// <summary>Ported from CredentialVaultTests.swift: the store, placeholders, JSON escaping and the
/// search tool. The engine-level cases (substitution into a tool call, redaction of results, access
/// rules) are ported with the engine tests.</summary>
public sealed class CredentialVaultTests : IDisposable
{
    private readonly TempDirectory _dir = new("dsh-vault");

    public void Dispose() => _dir.Dispose();

    private CredentialVault Vault(MemoryBlobStore? store = null) => new(_dir.Path, store ?? new MemoryBlobStore());

    // MARK: - Store

    [Fact]
    public void AddSearchUpdateDelete()
    {
        var v = Vault();
        var openai = v.Add("OPENAI_API_KEY", "sk-live-123456", description: "OpenAI production",
                           tags: ["AI", "openai", "ai"], url: "https://api.openai.com");
        v.Add("STRIPE_TEST", "sk_test_abcdef", VaultKind.Token, "Stripe sandbox", ["payments"]);
        Assert.Equal(["ai", "openai"], openai.Tags);
        Assert.StartsWith("sha256:", openai.Fingerprint);
        Assert.Equal("sha256:".Length + 12, openai.Fingerprint.Length);
        Assert.Equal(["OPENAI_API_KEY"], v.Search("openai").Select(e => e.Name));
        Assert.Equal(["STRIPE_TEST"], v.Search("stripe sandbox").Select(e => e.Name));
        Assert.Equal(2, v.Search("").Count);
        Assert.Equal(["OPENAI_API_KEY"], v.Search(openai.Fingerprint).Select(e => e.Name));
        Assert.Equal("sk-live-123456", v.Value(openai.Id));

        var edited = openai with { Description = "OpenAI (rotated)" };
        v.Update(edited, "sk-live-999999");
        Assert.Equal("sk-live-999999", v.Value(openai.Id));
        Assert.NotEqual(openai.Fingerprint, v.Entry("openai_api_key")?.Fingerprint);
        v.Update(v.Entry("OPENAI_API_KEY")!); // no value → unchanged
        Assert.Equal("sk-live-999999", v.Value(openai.Id));

        v.Delete(openai.Id);
        Assert.Null(v.Entry("OPENAI_API_KEY"));
        Assert.Null(v.Value(openai.Id));
    }

    [Fact]
    public void NamesAreValidatedAndUnique()
    {
        var v = Vault();
        var invalid = Assert.Throws<VaultException>(() => v.Add("has space", "x"));
        Assert.Equal((VaultErrorKind.InvalidName, "has space"), (invalid.Kind, invalid.Name));
        Assert.Equal(VaultErrorKind.EmptyValue, Assert.Throws<VaultException>(() => v.Add("OK", "")).Kind);
        v.Add("DB_PASSWORD", "hunter22");
        var duplicate = Assert.Throws<VaultException>(() => v.Add("db_password", "other"));
        Assert.Equal((VaultErrorKind.DuplicateName, "db_password"), (duplicate.Kind, duplicate.Name));
    }

    [Fact]
    public void ValuesNeverTouchTheMetadataFile()
    {
        var store = new MemoryBlobStore();
        var v = Vault(store);
        v.Add("GH_TOKEN", "ghp_supersecretvalue", description: "GitHub");
        var file = File.ReadAllText(_dir[CredentialVault.MetadataFileName]);
        Assert.Contains("GH_TOKEN", file);
        Assert.DoesNotContain("ghp_supersecretvalue", file);
        // A new vault over the same folder + store sees both again.
        var reopened = Vault(store);
        Assert.Equal(new VaultLookup.Value("ghp_supersecretvalue", VaultAccess.Allowed), reopened.Lookup("gh_token"));
    }

    // MARK: - Placeholders

    [Fact]
    public void PlaceholderSubstitutionKeepsJsonValid()
    {
        Assert.Equal(["A", "B"], VaultPlaceholders.Names("""{"command":"curl -H 'k: {{vault:A}}' {{ vault:B }} {{vault:A}}"}"""));
        const string tricky = "p\"a\\ss\nword/";
        const string raw = """{"text":"secret={{vault:PW}}"}""";
        var output = VaultPlaceholders.Substitute(raw, new Dictionary<string, string> { ["PW"] = tricky });
        var decoded = JsonSerializer.Deserialize<Dictionary<string, string>>(output);
        Assert.Equal("secret=" + tricky, decoded?["text"]);
        Assert.Equal("key is [vault:K] and [vault:K]",
                     VaultPlaceholders.Redact("key is sk-123456 and sk-123456", [("K", "sk-123456")]));
    }

    // MARK: - Search tool

    [Fact]
    public async Task SearchToolListsPlaceholdersNotValues()
    {
        var v = Vault();
        v.Add("OPENAI_API_KEY", "sk-verysecret", description: "OpenAI", tags: ["ai"]);
        v.Add("ROOT_PW", "pw-verysecret", VaultKind.Password, access: VaultAccess.Never);
        using var tools = new ToolTestContext();
        var all = await tools.Output(new VaultSearchTool(v), "{}");
        Assert.Contains("{{vault:OPENAI_API_KEY}}", all);
        Assert.Contains("NOT available to the agent", all);
        Assert.DoesNotContain("verysecret", all);
        var none = await tools.Output(new VaultSearchTool(v), """{"query":"stripe"}""");
        Assert.Contains("No credential matches", none);
        var blank = new CredentialVault(_dir["other"], new MemoryBlobStore());
        var empty = await tools.Output(new VaultSearchTool(blank), "{}");
        Assert.Contains("vault is empty", empty);
    }
}
