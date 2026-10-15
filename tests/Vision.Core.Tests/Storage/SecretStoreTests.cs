namespace Vision.Core.Tests;

/// <summary>New: the secret store in memory-only mode (a process-wide switch, so serial and restored).
/// On Windows the real store is Credential Manager, which these tests never touch.</summary>
[Collection(SerialStaticState.Name)]
public sealed class SecretStoreTests : IDisposable
{
    private readonly bool _previous = SecretStore.UseMemoryOnly;
    private readonly string _target = SecretStore.ProviderTarget($"test-{Guid.NewGuid():N}|model");

    public SecretStoreTests() => SecretStore.UseMemoryOnly = true;

    public void Dispose()
    {
        SecretStore.Delete(_target);
        SecretStore.UseMemoryOnly = _previous;
    }

    [Fact]
    public void WriteReadOverwriteAndDelete()
    {
        Assert.Null(SecretStore.Read(_target));

        SecretStore.Write(_target, "sk-first");
        Assert.Equal("sk-first", SecretStore.Read(_target));

        SecretStore.Write(_target, "sk-second");
        Assert.Equal("sk-second", SecretStore.Read(_target));

        SecretStore.Delete(_target);
        Assert.Null(SecretStore.Read(_target));
        SecretStore.Delete(_target); // deleting twice is fine
    }

    [Fact]
    public void AnEmptySecretDeletesTheEntry()
    {
        SecretStore.Write(_target, "sk-live");
        SecretStore.Write(_target, "");
        Assert.Null(SecretStore.Read(_target));
    }

    [Fact]
    public void SecretsAreKeptVerbatim()
    {
        const string secret = "  p@ss wörd 😀 \"quoted\"  ";
        SecretStore.Write(_target, secret, userName: "someone");
        Assert.Equal(secret, SecretStore.Read(_target));
    }

    [Fact]
    public void TargetsAreNamespacedUnderVision()
    {
        Assert.Equal("Vision/provider/Spark|qwen", SecretStore.ProviderTarget("Spark|qwen"));
        Assert.Equal("Vision/spark/login", SecretStore.SparkTarget);
        Assert.Equal("Vision/swapper/box-1", SecretStore.SwapperTarget("box-1"));
        Assert.Equal("Spark|qwen", new ProviderProfile(ProviderKind.OpenAI, "Spark", "https://x/v1", "qwen").RouteId);
    }

    [Fact]
    public void LegacyTargetsAreTheSameNamesUnderDsh()
    {
        // Legacy: DSH, Vision's former name, kept its entries under DSH/….
        Assert.Equal("DSH/provider/Spark|qwen", SecretStore.LegacyTarget(SecretStore.ProviderTarget("Spark|qwen")));
        Assert.Equal("DSH/spark/login", SecretStore.LegacyTarget(SecretStore.SparkTarget));
        Assert.Equal("DSH/swapper/box-1", SecretStore.LegacyTarget(SecretStore.SwapperTarget("box-1")));
        Assert.Null(SecretStore.LegacyTarget("SomeoneElse/provider/x"));
    }
}

/// <summary>New: bringing DSH's (Vision's former name) Credential Manager entries over to Vision's names — against a
/// fake store that records every call and can be made to fail, so nothing touches the real Credential Manager.</summary>
[Collection(SerialStaticState.Name)]
public sealed class SecretStoreLegacyTests : IDisposable
{
    private readonly FakeCredentialStore _store = new();
    private readonly ICredentialStore? _previous = SecretStore.StoreOverride;

    public SecretStoreLegacyTests() => SecretStore.StoreOverride = _store;

    public void Dispose() => SecretStore.StoreOverride = _previous;

    private const string Target = "Vision/provider/Spark|qwen";
    private const string Legacy = "DSH/provider/Spark|qwen";

    /// <summary>Records calls; <see cref="FailWrites"/> and <see cref="CorruptWrites"/> stand for Credential Manager
    /// refusing a write or keeping something else than what was written.</summary>
    private sealed class FakeCredentialStore : ICredentialStore
    {
        public readonly Dictionary<string, CredentialEntry> Entries = new(StringComparer.Ordinal);
        public readonly List<string> Calls = [];
        public bool FailWrites;
        public bool CorruptWrites;

        public CredentialEntry? Read(string target)
        {
            Calls.Add($"read {target}");
            return Entries.GetValueOrDefault(target);
        }

        public void Write(string target, string secret, string userName)
        {
            Calls.Add($"write {target}");
            if (FailWrites) throw new InvalidOperationException("Couldn't save to Credential Manager (error 1312).");
            Entries[target] = new CredentialEntry(CorruptWrites ? secret + "?" : secret, userName);
        }

        public void Delete(string target)
        {
            Calls.Add($"delete {target}");
            Entries.Remove(target);
        }
    }

    [Fact]
    public void ANewEntryIsReadAsItIsAndDshIsNotAsked()
    {
        _store.Entries[Target] = new CredentialEntry("sk-new", "pat");
        Assert.Equal("sk-new", SecretStore.Read(Target));
        Assert.Equal([$"read {Target}"], _store.Calls);
    }

    [Fact]
    public void TheLegacyEntryIsCopiedCheckedThenRemoved()
    {
        _store.Entries[Legacy] = new CredentialEntry("sk-legacy", "swapper-user");

        Assert.Equal("sk-legacy", SecretStore.Read(Target));

        Assert.Equal(new CredentialEntry("sk-legacy", "swapper-user"), _store.Entries[Target]); // the user name came too
        Assert.False(_store.Entries.ContainsKey(Legacy));
        // Read back before the old one goes, never the other way round.
        var write = _store.Calls.IndexOf($"write {Target}");
        var check = _store.Calls.LastIndexOf($"read {Target}");
        var delete = _store.Calls.IndexOf($"delete {Legacy}");
        Assert.True(write >= 0 && write < check && check < delete, string.Join(", ", _store.Calls));

        // Once over, DSH's name isn't asked again.
        _store.Calls.Clear();
        Assert.Equal("sk-legacy", SecretStore.Read(Target));
        Assert.Equal([$"read {Target}"], _store.Calls);
    }

    [Fact]
    public void AFailedCopyKeepsDshsEntryAndStillReturnsTheSecret()
    {
        _store.Entries[Legacy] = new CredentialEntry("sk-legacy", "pat");
        _store.FailWrites = true;

        Assert.Equal("sk-legacy", SecretStore.Read(Target));
        Assert.True(_store.Entries.ContainsKey(Legacy));
        Assert.DoesNotContain($"delete {Legacy}", _store.Calls);

        // The next read tries again, and this time it works.
        _store.FailWrites = false;
        Assert.Equal("sk-legacy", SecretStore.Read(Target));
        Assert.False(_store.Entries.ContainsKey(Legacy));
        Assert.Equal("sk-legacy", _store.Entries[Target].Secret);
    }

    [Fact]
    public void ACopyThatDoesntReadBackTheSameKeepsDshsEntry()
    {
        _store.Entries[Legacy] = new CredentialEntry("sk-legacy", "pat");
        _store.CorruptWrites = true;

        Assert.Equal("sk-legacy", SecretStore.Read(Target)); // the original, not the bad copy
        Assert.True(_store.Entries.ContainsKey(Legacy));
        Assert.False(_store.Entries.ContainsKey(Target)); // a copy that doesn't match is no copy
        Assert.Equal("sk-legacy", SecretStore.Read(Target)); // so the next read still gets the original
        Assert.Equal(0, SecretStore.BringOver([Target]));
    }

    [Fact]
    public void NoEntryUnderEitherNameIsNull()
    {
        Assert.Null(SecretStore.Read(Target));
        Assert.Empty(_store.Entries);
        Assert.DoesNotContain(_store.Calls, c => c.StartsWith("write", StringComparison.Ordinal));
    }

    [Fact]
    public void DeletingAlsoRemovesDshsCopySoAnOldKeyCantComeBack()
    {
        _store.Entries[Legacy] = new CredentialEntry("sk-old", "pat");
        SecretStore.Delete(Target);
        Assert.Empty(_store.Entries);
        Assert.Null(SecretStore.Read(Target));
    }

    [Fact]
    public void WritingReplacesDshsCopyToo()
    {
        _store.Entries[Legacy] = new CredentialEntry("sk-old", "pat");
        SecretStore.Write(Target, "sk-new", "pat");
        Assert.Equal("sk-new", _store.Entries[Target].Secret);
        Assert.False(_store.Entries.ContainsKey(Legacy));

        // An empty secret still means delete — both names.
        _store.Entries[Legacy] = new CredentialEntry("sk-old", "pat");
        SecretStore.Write(Target, "");
        Assert.Empty(_store.Entries);
    }

    [Fact]
    public void BringingOverEveryRouteAtLaunchCountsWhatMoved()
    {
        _store.Entries["DSH/provider/a|m"] = new CredentialEntry("ka", "pat");
        _store.Entries["DSH/swapper/box"] = new CredentialEntry("token", "swapper");
        _store.Entries["Vision/provider/b|m"] = new CredentialEntry("kb", "pat");

        var moved = SecretStore.BringOver(["Vision/provider/a|m", "Vision/provider/b|m", "Vision/swapper/box", "Vision/provider/none|m"]);

        Assert.Equal(2, moved);
        Assert.Equal(["Vision/provider/a|m", "Vision/provider/b|m", "Vision/swapper/box"], _store.Entries.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("swapper", _store.Entries["Vision/swapper/box"].UserName);
        Assert.Equal(0, SecretStore.BringOver(["Vision/provider/a|m"])); // idempotent
    }

    [Fact]
    public void AFailureNeverPutsTheSecretInWhatIsReported()
    {
        _store.Entries[Legacy] = new CredentialEntry("sk-very-secret-value", "pat");
        _store.FailWrites = true;
        Assert.Equal(0, SecretStore.BringOver([Target]));
        Assert.DoesNotContain(_store.Calls, c => c.Contains("sk-very-secret-value", StringComparison.Ordinal));
    }

    [Fact]
    public void ConcurrentFirstReadsAllGetTheSecret()
    {
        // Two threads asking for a key at once (a subagent and the UI): neither may find DSH's entry gone and the
        // new one not there yet.
        for (var round = 0; round < 50; round++)
        {
            var store = new MemoryCredentialStore();
            store.Write(Legacy, "sk-legacy", "pat");
            SecretStore.StoreOverride = store;
            var results = new string?[8];
            Parallel.For(0, results.Length, i => results[i] = SecretStore.Read(Target));
            Assert.All(results, r => Assert.Equal("sk-legacy", r));
            Assert.Null(store.Read(Legacy));
        }
    }
}

/// <summary>New: where Vision keeps its files; VISION_HOME redirects everything (tests, the self-test).</summary>
[Collection(SerialStaticState.Name)]
public sealed class AppPathsTests : IDisposable
{
    private readonly TempDirectory _home = new("vision-home");
    private readonly string? _previous = Environment.GetEnvironmentVariable("VISION_HOME");

    public AppPathsTests() => Environment.SetEnvironmentVariable("VISION_HOME", _home.Path);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("VISION_HOME", _previous);
        _home.Dispose();
    }

    [Fact]
    public void VisionHomeRedirectsEveryPath()
    {
        Assert.Equal(_home.Path, AppPaths.Root);
        Assert.Equal(Path.Combine(_home.Path, "settings.json"), AppPaths.Settings);
        Assert.Equal(Path.Combine(_home.Path, "conversations"), AppPaths.Conversations);
        Assert.Equal(Path.Combine(_home.Path, "plugins"), AppPaths.Plugins);
        Assert.Equal(Path.Combine(_home.Path, "logs"), AppPaths.Logs);
        Assert.Equal(Path.Combine(_home.Path, "skills"), SkillLocations.Standard.UserSkills);
    }

    [Fact]
    public void EnsureCreatesTheFolder()
    {
        var logs = AppPaths.Ensure(AppPaths.Logs);
        Assert.True(Directory.Exists(logs));
    }

    [Fact]
    public void ConversationLogDefaultsToTheConversationsFolder()
    {
        var log = new ConversationLog();
        Assert.Equal(AppPaths.Conversations, log.StorageDirectory);
        Assert.True(Directory.Exists(AppPaths.Conversations));
    }

    [Fact]
    public void BlankHomeFallsBackToAppData()
    {
        Environment.SetEnvironmentVariable("VISION_HOME", "  ");
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Vision"), AppPaths.Root);
        Assert.Equal(AppPaths.DefaultRoot, AppPaths.Root);
    }

    [Fact]
    public void TheLegacyDshHomeStillWorksAndVisionHomeWins()
    {
        // Legacy: DSH_HOME, the variable of DSH (Vision's former name), keeps pointing the app at the same folder.
        var previousLegacy = Environment.GetEnvironmentVariable("DSH_HOME");
        using var legacy = new TempDirectory("vision-legacy-home");
        try
        {
            Environment.SetEnvironmentVariable("DSH_HOME", legacy.Path);
            Assert.Equal(_home.Path, AppPaths.Root); // VISION_HOME is set: it wins
            Environment.SetEnvironmentVariable("VISION_HOME", null);
            Assert.Equal(legacy.Path, AppPaths.Root);
            Assert.Equal(legacy.Path, AppPaths.Overridden);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DSH_HOME", previousLegacy);
        }
    }

    [Fact]
    public void TheLegacyDataFolderIsDshUnderAppData() =>
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DSH"), AppPaths.LegacyDefaultRoot);

    [Fact]
    public void ASessionRootWinsForThisSessionOnly()
    {
        using var other = new TempDirectory("vision-session-root");
        AppPaths.SessionRoot = other.Path;
        try
        {
            Assert.Equal(other.Path, AppPaths.Root);
            Assert.Equal(Path.Combine(other.Path, "settings.json"), AppPaths.Settings);
        }
        finally
        {
            AppPaths.SessionRoot = null;
        }
        Assert.Equal(_home.Path, AppPaths.Root);
    }

    [Fact]
    public void APortableCopyFindsAPortableDshBesideIt()
    {
        using var drive = new TempDirectory("vision-usb");
        var vision = drive.Write("Vision/portable", "");
        var visionFolder = Path.GetDirectoryName(vision)!;
        Assert.Equal(Path.Combine(visionFolder, "data"), AppPaths.PortableRootFor(visionFolder));
        Assert.Null(AppPaths.LegacyPortableRootFor(visionFolder)); // no DSH beside it

        // Legacy: the zip used to unpack to a folder named DSH.
        drive.Write("DSH/portable", "");
        Assert.Null(AppPaths.LegacyPortableRootFor(visionFolder)); // a DSH copy without data has nothing to bring
        drive.Write("DSH/data/settings.json", "{}");
        Assert.Equal(Path.Combine(drive.Path, "DSH", "data"), AppPaths.LegacyPortableRootFor(visionFolder));
        Assert.Equal(Path.Combine(drive.Path, "DSH", "data"), AppPaths.LegacyPortableRootFor(visionFolder + Path.DirectorySeparatorChar));

        // Unpacked over the old folder itself: its data is already beside it.
        Assert.Null(AppPaths.LegacyPortableRootFor(Path.Combine(drive.Path, "DSH")));

        // A folder named DSH that isn't a portable copy (no marker) isn't one.
        File.Delete(Path.Combine(drive.Path, "DSH", "portable"));
        Assert.Null(AppPaths.LegacyPortableRootFor(visionFolder));
    }
}
