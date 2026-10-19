using System.Text;

namespace Vision.Core.Tests;

/// <summary>Credential Manager's 2,560-byte limit (long secrets in parts), the Guardian sign-in on disk, and secrets held
/// for this session only.</summary>
[Collection(SerialStaticState.Name)]
public sealed class GuardianStorageTests : IDisposable
{
    /// <summary>Credential Manager as it is: an entry holds at most 2,560 bytes of UTF-16.</summary>
    private sealed class LimitedStore : ICredentialStore
    {
        public readonly Dictionary<string, CredentialEntry> Entries = new(StringComparer.Ordinal);

        public CredentialEntry? Read(string target) => Entries.GetValueOrDefault(target);

        public void Write(string target, string secret, string userName)
        {
            if (Encoding.Unicode.GetByteCount(secret) > 2_560) throw new ArgumentException("That secret is too long to store in Credential Manager.");
            Entries[target] = new CredentialEntry(secret, userName);
        }

        public void Delete(string target) => Entries.Remove(target);
    }

    private readonly LimitedStore _store = new();
    private readonly ICredentialStore? _previous = SecretStore.StoreOverride;

    public GuardianStorageTests() => SecretStore.StoreOverride = _store;

    public void Dispose() => SecretStore.StoreOverride = _previous;

    [Fact]
    public void ASecretLongerThanOneEntryIsKeptInPartsAndComesBackWhole()
    {
        const string target = "Vision/guardian/token";
        var token = "eyJ" + string.Concat(Enumerable.Range(0, 3_000).Select(i => (char)('a' + i % 26))) + "😀end";
        SecretStore.Write(target, token, "pat@503cm.com");

        Assert.Equal(token, SecretStore.Read(target));
        Assert.StartsWith("vision-parts:", _store.Entries[target].Secret);
        Assert.Equal(4, _store.Entries.Count); // the marker and three parts
        Assert.All(_store.Entries.Values, e => Assert.True(Encoding.Unicode.GetByteCount(e.Secret) <= 2_560));

        // A shorter one replaces it and the parts go.
        SecretStore.Write(target, "short-token");
        Assert.Equal("short-token", SecretStore.Read(target));
        Assert.Single(_store.Entries);

        SecretStore.Write(target, token);
        SecretStore.Delete(target);
        Assert.Empty(_store.Entries);
        Assert.Null(SecretStore.Read(target));
    }

    [Fact]
    public void APartsNeverSplitASurrogatePair()
    {
        var text = new string('a', SecretStore.PartChars - 1) + "😀" + new string('b', 10);
        var parts = SecretStore.Parts(text);
        Assert.Equal(text, string.Concat(parts));
        Assert.All(parts, p => Assert.False(char.IsHighSurrogate(p[^1])));
    }

    [Fact]
    public void AMissingPartReadsAsNoSecretNotAWrongOne()
    {
        const string target = "Vision/provider/Guardian|guardian/default";
        SecretStore.Write(target, new string('k', 5_000));
        _store.Entries.Remove(SecretStore.PartTarget(target, 2));
        Assert.Null(SecretStore.Read(target));
    }

    [Fact]
    public void TheAccountIsKeptWithoutItsTokenOnDisk()
    {
        using var folder = new TempDirectory();
        var accounts = new GuardianAccountStore(folder.Path);
        Assert.Null(accounts.Load());

        accounts.Save(new GuardianAccount { Address = "https://guardian.503cm.com", Email = "pat@503cm.com", Name = "Pat", OrgId = "o1", OrgName = "503CM", Method = "browser" },
            "tok-secret-value");

        var loaded = accounts.Load()!.Value;
        Assert.Equal(("pat@503cm.com", "o1", "browser", "tok-secret-value"), (loaded.Account.Email, loaded.Account.OrgId, loaded.Account.Method, loaded.Token));
        Assert.DoesNotContain("tok-secret-value", File.ReadAllText(accounts.Path));
        Assert.Equal("Pat (pat@503cm.com)", loaded.Account.Label);

        accounts.SignOut();
        Assert.Null(accounts.Load());
        Assert.Equal("pat@503cm.com", accounts.LastAccount()!.Email); // the form remembers who to sign in as
    }

    [Fact]
    public void ASessionSecretIsUsableButNeverWritten()
    {
        using var folder = new TempDirectory();
        var blobs = new MemoryBlobStore();
        var vault = new CredentialVault(folder.Path, blobs);
        vault.Add("GITHUB_TOKEN", "ghp-saved", VaultKind.ApiKey);
        var savedMetadata = File.ReadAllText(vault.MetadataPath);

        var held = vault.HoldForSession("FRESHDESK_API_KEY", "fd-from-guardian", description: "From Guardian", tags: ["guardian"]);
        var renamed = vault.HoldForSession("GITHUB_TOKEN", "ghp-from-guardian", tags: ["guardian"]);

        Assert.Equal("GITHUB_TOKEN_GUARDIAN", renamed.Name);
        Assert.True(vault.IsSessionOnly(held.Id));
        Assert.Equal(new VaultLookup.Value("fd-from-guardian", VaultAccess.Ask), vault.Lookup("freshdesk_api_key"));
        Assert.Equal(3, vault.All.Count);
        Assert.Contains(vault.ValuesForRedaction(), p => p.Value == "fd-from-guardian");
        // Nothing of it reached the files.
        Assert.Equal(savedMetadata, File.ReadAllText(vault.MetadataPath));
        Assert.DoesNotContain("fd-from-guardian", Encoding.UTF8.GetString(blobs.Load() ?? []));
        Assert.DoesNotContain("FRESHDESK", File.ReadAllText(vault.MetadataPath));

        // Holding the same name again replaces the value; a reopened vault never sees it.
        vault.HoldForSession("FRESHDESK_API_KEY", "fd-rotated", tags: ["guardian"]);
        Assert.Equal("fd-rotated", vault.Value(held.Id));
        Assert.Single(new CredentialVault(folder.Path, blobs).All);

        Assert.Equal(2, vault.ForgetSession("guardian"));
        Assert.IsType<VaultLookup.Missing>(vault.Lookup("FRESHDESK_API_KEY"));
        Assert.Single(vault.All);
    }
}
