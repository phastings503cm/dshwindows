using System.IO;
using System.Text.Json.Nodes;
using Vision.App.Model;
using Vision.Core;

namespace Vision.App.Tests;

/// <summary>New: settings that DSH (Vision's former name) wrote, brought over by <see cref="DataFolderMigration"/> and
/// then read by <see cref="AppConfig"/> itself — the skill switches and pinned skills that name files in the data
/// folder follow it, and a Bedrock route that relied on DSH's default AWS profile keeps using it. The tests seed
/// legacy DSH data on purpose.</summary>
public sealed class LegacySettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"vision-legacy-settings-{Guid.NewGuid():N}");

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

    /// <summary>Settings as DSH wrote them: this version's file without the flag DSH never wrote.</summary>
    private static void WriteAsLegacy(AppConfig config, string path)
    {
        config.SaveNow();
        var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.True(json.Remove("legacyAwsProfilePinned"));
        File.WriteAllText(path, json.ToJsonString());
    }

    [Fact]
    public void SettingsDshWroteAreReadWithTheirPathsMovedAndItsBedrockSignInKept()
    {
        var legacy = Path.Combine(_dir, "DSH");
        var root = Path.Combine(_dir, "Vision");
        var route = $"Bedrock {Guid.NewGuid():N}"; // unique, so no real Credential Manager entry can match
        var old = new AppConfig(Path.Combine(legacy, "settings.json"));
        old.Store(new ProviderProfile(ProviderKind.Bedrock, route, "https://bedrock-runtime.us-east-1.amazonaws.com", "m") { AwsRegion = "us-east-1" });
        old.Store(new ProviderProfile(ProviderKind.Bedrock, route + " sso", "https://bedrock-runtime.us-east-1.amazonaws.com", "m") { AwsProfile = "work-sso" });
        old.SetSkillEnabled(Path.Combine(legacy, "skills", "off", "SKILL.md"), false);
        old.SetSkillEnabled(@"C:\code\app\.claude\skills\x\SKILL.md", false);
        old.SetSkillsFor("chat-1", new SessionSkillSelection([Path.Combine(legacy, "skills", "pinned", "SKILL.md")], Auto: false));
        WriteAsLegacy(old, Path.Combine(legacy, "settings.json"));

        Assert.Equal(DataMigrationResult.Moved, new DataFolderMigration(legacy, root).Run().Result);
        var config = new AppConfig(Path.Combine(root, "settings.json"));

        Assert.Contains(Path.Combine(root, "skills", "off", "SKILL.md"), config.DisabledSkills);
        Assert.Contains(@"C:\code\app\.claude\skills\x\SKILL.md", config.DisabledSkills);
        Assert.DoesNotContain(config.DisabledSkills, s => s.StartsWith(legacy, StringComparison.OrdinalIgnoreCase));
        Assert.Equal([Path.Combine(root, "skills", "pinned", "SKILL.md")], config.SkillsFor("chat-1").Pinned);
        Assert.False(config.SkillsFor("chat-1").Auto);
        // The route signed in with DSH's default profile; it names it now, so the new default changes nothing for it.
        Assert.Equal(AwsSignIn.LegacyDefaultProfile, config.Providers.Single(p => p.Name == route).AwsProfile);
        Assert.Equal("work-sso", config.Providers.Single(p => p.Name == route + " sso").AwsProfile);

        // Done once: a route added later without a profile gets the new default, not DSH's.
        config.Store(new ProviderProfile(ProviderKind.Bedrock, route + " new", "https://bedrock-runtime.us-east-1.amazonaws.com", "m"));
        var reloaded = new AppConfig(Path.Combine(root, "settings.json"));
        Assert.Null(reloaded.Providers.Single(p => p.Name == route + " new").AwsProfile);
        Assert.Equal(AwsSignIn.LegacyDefaultProfile, reloaded.Providers.Single(p => p.Name == route).AwsProfile);
    }

    [Fact]
    public void NewSettingsHaveNothingPinned()
    {
        var path = Path.Combine(_dir, "fresh", "settings.json");
        var config = new AppConfig(path);
        config.Store(new ProviderProfile(ProviderKind.Bedrock, $"Bedrock {Guid.NewGuid():N}", "https://bedrock-runtime.us-east-1.amazonaws.com", "m"));
        var reloaded = new AppConfig(path);
        Assert.All(reloaded.Providers.Where(p => p.Kind == ProviderKind.Bedrock), p => Assert.NotEqual(AwsSignIn.LegacyDefaultProfile, p.AwsProfile));
    }
}
