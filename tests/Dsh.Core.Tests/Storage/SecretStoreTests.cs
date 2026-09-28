namespace Dsh.Core.Tests;

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
    public void TargetsAreNamespacedUnderDsh()
    {
        Assert.Equal("DSH/provider/Spark|qwen", SecretStore.ProviderTarget("Spark|qwen"));
        Assert.Equal("DSH/spark/login", SecretStore.SparkTarget);
        Assert.Equal("Spark|qwen", new ProviderProfile(ProviderKind.OpenAI, "Spark", "https://x/v1", "qwen").RouteId);
    }
}

/// <summary>New: where DSH keeps its files; DSH_HOME redirects everything (tests, portable installs).</summary>
[Collection(SerialStaticState.Name)]
public sealed class AppPathsTests : IDisposable
{
    private readonly TempDirectory _home = new("dsh-home");
    private readonly string? _previous = Environment.GetEnvironmentVariable("DSH_HOME");

    public AppPathsTests() => Environment.SetEnvironmentVariable("DSH_HOME", _home.Path);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("DSH_HOME", _previous);
        _home.Dispose();
    }

    [Fact]
    public void DshHomeRedirectsEveryPath()
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
    public void BlankDshHomeFallsBackToAppData()
    {
        Environment.SetEnvironmentVariable("DSH_HOME", "  ");
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DSH"), AppPaths.Root);
    }
}
