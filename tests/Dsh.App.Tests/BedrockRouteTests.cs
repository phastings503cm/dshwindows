using System.IO;
using System.Windows.Threading;
using Dsh.App.Model;
using Dsh.Core;
using MessageRole = Dsh.App.Model.MessageRole;

namespace Dsh.App.Tests;

/// <summary>A Bedrock route whose AWS sign-in has run out: the chat says so and the banner offers to sign
/// the same profile in again.</summary>
public sealed class BedrockRouteTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"dsh-bedrock-{Guid.NewGuid():N}");
    private readonly FakeModelServer _server = new();

    public BedrockRouteTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "project"));
        _server.Reset((_, _) => new FakeReply.Text("unexpected"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class ExpiredSignIn(string profile) : IAwsCredentialSource
    {
        public int Calls;

        public Task<AwsCredentials> GetAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            throw new AwsSignInRequiredException(profile, "Your session has expired. Please reauthenticate.");
        }
    }

    private AgentHost MakeHost(string profile)
    {
        var config = new AppConfig(Path.Combine(_dir, "settings.json"));
        config.Activate(new ProviderProfile(ProviderKind.Bedrock, "Amazon Bedrock", "https://bedrock-runtime.us-west-2.amazonaws.com",
            "us.anthropic.claude-sonnet-4-5-20250929-v1:0")
        {
            AwsProfile = profile,
            AwsRegion = "us-west-2",
        });
        config.Preset = PermissionPreset.FullAccess.RawValue();
        config.ComputerToolsEnabled = false;
        return new AgentHost(config, new ConversationLog(Path.Combine(_dir, "log")), Dispatcher.CurrentDispatcher,
            queueFile: Path.Combine(_dir, "task-queue.json"),
            skillLocations: new SkillLocations(_dir, Path.Combine(_dir, "support")),
            vault: new CredentialVault(Path.Combine(_dir, "vault"), new MemoryBlobStore()),
            memory: new MemoryStore(Path.Combine(_dir, "memory")))
        {
            HttpHandlerForTesting = _server,
            RetryPolicy = new RetryPolicy { Delay = _ => TimeSpan.FromMilliseconds(50) },
        };
    }

    private static async Task WaitUntil(string what, Func<bool> condition, double seconds = 15)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail($"timed out waiting for: {what}");
            await Task.Delay(10);
        }
    }

    [Fact]
    public void ExpiredSignInStopsTheRunAndOffersToSignInAgain() => UiThread.Run(async () =>
    {
        var source = new ExpiredSignIn("work-sso");
        var previous = ProviderClients.AwsCredentials;
        ProviderClients.AwsCredentials = _ => source;
        var host = MakeHost("work-sso");
        try
        {
            host.AdoptProject(Path.Combine(_dir, "project"));
            var vm = host.NewSession(Path.Combine(_dir, "project"));
            host.Send("hello", vm.Id);
            await WaitUntil("run ended", () => !vm.Running && host.AwsSignInProfile is not null);

            Assert.Equal("work-sso", host.AwsSignInProfile);
            var error = vm.Entries.OfType<MessageEntryVM>().Last(m => m.Role == MessageRole.Error).Text;
            Assert.Contains("work-sso", error);
            Assert.Contains("expired", error);
            Assert.Equal(error, host.Banner);
            // No retry loop on a sign-in that can only be fixed by the user, and nothing was sent unsigned.
            Assert.Empty(_server.Seen);
            Assert.True(source.Calls <= 2, $"asked for credentials {source.Calls} times");

            // Any other banner (or dismissing it) drops the sign-in button.
            host.Banner = null;
            Assert.Null(host.AwsSignInProfile);
        }
        finally
        {
            ProviderClients.AwsCredentials = previous;
            host.StopAll();
            await Task.Delay(50);
        }
    });

    [Fact]
    public void OtherFailuresDoNotOfferAnAwsSignIn() => UiThread.Run(async () =>
    {
        var config = new AppConfig(Path.Combine(_dir, "settings.json"));
        config.Activate(new ProviderProfile(ProviderKind.OpenAICompat, "stub", "http://stub.test/v1", "stub-model"));
        config.Preset = PermissionPreset.FullAccess.RawValue();
        config.ComputerToolsEnabled = false;
        var host = new AgentHost(config, new ConversationLog(Path.Combine(_dir, "log")), Dispatcher.CurrentDispatcher,
            queueFile: Path.Combine(_dir, "task-queue.json"),
            skillLocations: new SkillLocations(_dir, Path.Combine(_dir, "support")),
            vault: new CredentialVault(Path.Combine(_dir, "vault"), new MemoryBlobStore()),
            memory: new MemoryStore(Path.Combine(_dir, "memory")))
        {
            HttpHandlerForTesting = _server,
            RetryPolicy = new RetryPolicy { Delay = _ => TimeSpan.FromMilliseconds(50) },
        };
        _server.Reset((_, _) => new FakeReply.Http(401, """{"error":{"message":"Incorrect API key provided"}}"""));
        try
        {
            host.AdoptProject(Path.Combine(_dir, "project"));
            var vm = host.NewSession(Path.Combine(_dir, "project"));
            host.Send("hello", vm.Id);
            await WaitUntil("run ended", () => !vm.Running && host.Banner is not null);
            Assert.Null(host.AwsSignInProfile);
        }
        finally
        {
            host.StopAll();
            await Task.Delay(50);
        }
    });
}
