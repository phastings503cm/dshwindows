namespace Vision.Core.Tests;

/// <summary>Which routes a Sensitive chat may use (10-19 #20): private ones as they are, online ones only through the shield,
/// engines that read files themselves never — the same cases as Vision for macOS's SensitivityPolicyTests.</summary>
public sealed class RouteAdmissionTests
{
    private static ProviderProfile Route(ProviderKind kind, string name, string url, bool? isPrivate = null) =>
        new(kind, name, url, "m") { IsPrivate = isPrivate };

    [Fact]
    public void YourOwnHardwareIsPrivate()
    {
        foreach (var url in new[] { "http://spark.local:8002/v1", "http://127.0.0.1:11434/v1", "http://192.168.1.50:8002/v1", "http://halo:8080/v1", "http://evo.tail1234.ts.net/v1" })
            Assert.Equal(RouteAdmission.Private("your own hardware"), SensitivityPolicy.Admission(Route(ProviderKind.OpenAICompat, "Spark", url), shieldOn: false));
        Assert.Equal("private", SensitivityPolicy.Admission(Route(ProviderKind.Ollama, "Ollama", "http://127.0.0.1:11434/v1"), shieldOn: true).Code);
        Assert.Equal("private", SensitivityPolicy.Admission(Route(ProviderKind.LmStudio, "LM Studio", "http://localhost:1234/v1"), shieldOn: true).Code);
    }

    [Fact]
    public void GuardianAndBedrockInYourAccountArePrivate()
    {
        // Windows reaches Guardian through its model proxy (an OpenAI-compatible route under /api/harness/).
        Assert.Equal(RouteAdmission.Private("Guardian, your organisation's own server"),
            SensitivityPolicy.Admission(Route(ProviderKind.OpenAICompat, "Guardian", "https://guardian.503cm.com/api/harness/v1"), shieldOn: false));
        // Bedrock isn't "private" for the shield (it still masks), but it is the person's own AWS account.
        var bedrock = Route(ProviderKind.Bedrock, "Amazon Bedrock", "https://bedrock-runtime.us-east-1.amazonaws.com");
        Assert.False(bedrock.IsPrivateRoute);
        Assert.Equal(RouteAdmission.Private("Amazon Bedrock in your own AWS account"), SensitivityPolicy.Admission(bedrock, shieldOn: false));
        Assert.Equal("private", SensitivityPolicy.Admission("guardian", "Guardian", isPrivate: false, shieldOn: false).Code);
    }

    [Fact]
    public void AnOnlineServiceIsAllowedOnlyThroughTheShield()
    {
        var kimi = Route(ProviderKind.OpenAICompat, "Kimi Code", "https://api.kimi.com/coding/v1");
        Assert.Equal(RouteAdmission.Masked, SensitivityPolicy.Admission(kimi, shieldOn: true));
        var off = SensitivityPolicy.Admission(kimi, shieldOn: false);
        Assert.False(off.IsAllowed);
        Assert.Equal("refused", off.Code);
        Assert.Contains("the sensitive-data shield is switched off", off.Refusal);
        Assert.Contains("Settings › AI Models › Sensitive data", off.Refusal);
        Assert.Equal(RouteAdmission.Masked, SensitivityPolicy.Admission(Route(ProviderKind.OpenAICompat, "DeepSeek", "https://api.deepseek.com/v1"), shieldOn: true));
    }

    [Fact]
    public void ThePersonsPrivateSwitchWinsBothWays()
    {
        Assert.Equal("private", SensitivityPolicy.Admission(Route(ProviderKind.OpenAICompat, "My box", "https://box.example.com/v1", isPrivate: true), shieldOn: false).Code);
        Assert.Equal(RouteAdmission.Masked, SensitivityPolicy.Admission(Route(ProviderKind.OpenAICompat, "Shared", "http://10.0.0.5:8000/v1", isPrivate: false), shieldOn: true));
    }

    [Fact]
    public void EnginesThatReadFilesThemselvesAreRefusedWhateverTheShield()
    {
        foreach (var kind in new[] { "claudeCode", "ClaudeCode", "codex" })
        {
            foreach (var shield in new[] { true, false })
            {
                var admission = SensitivityPolicy.Admission(kind, "Claude Code", isPrivate: false, shieldOn: shield);
                Assert.False(admission.IsAllowed, kind);
                Assert.StartsWith("Claude Code reads your files itself", admission.Refusal);
            }
            // Even marked private: what it reads goes to its own service.
            Assert.False(SensitivityPolicy.Admission(kind, "Claude Code", isPrivate: true, shieldOn: true).IsAllowed);
        }
        Assert.False(SensitivityPolicy.ReadsFilesDirectly(Route(ProviderKind.OpenAI, "OpenAI", "https://api.openai.com/v1")));
        // Windows' Claude Code route: an OpenAI-compatible kind at a claude-code:// address.
        var claude = Route(ProviderKind.OpenAICompat, "Claude Code", "claude-code://local");
        Assert.True(SensitivityPolicy.ReadsFilesDirectly(claude));
        Assert.StartsWith("Claude Code reads your files itself", SensitivityPolicy.Admission(claude, shieldOn: true).Refusal);
    }

    [Fact]
    public void TheRefusalTheChatShowsAndTheHeaderLine()
    {
        var kimi = Route(ProviderKind.OpenAICompat, "Kimi Code", "https://api.kimi.com/coding/v1");
        Assert.Null(SensitivityPolicy.RefusalMessage(kimi, shieldOn: true));
        Assert.StartsWith("This chat is Sensitive. Kimi Code is an online service", SensitivityPolicy.RefusalMessage(kimi, shieldOn: false));
        Assert.Equal("Sensitive — Kimi Code isn't private: what the shield detects is masked before sending.", SensitivityPolicy.HeaderLine(kimi, shieldOn: true));
        Assert.Equal("Sensitive — Kimi Code can't be used in this chat. Choose a private model.", SensitivityPolicy.HeaderLine(kimi, shieldOn: false));
        Assert.Equal("Sensitive — Spark is private (your own hardware).", SensitivityPolicy.HeaderLine(Route(ProviderKind.OpenAICompat, "Spark", "http://spark.local/v1"), shieldOn: false));
        Assert.Equal("Sensitive — only private models, or others through the shield.", SensitivityPolicy.HeaderLine(null, shieldOn: true));
        Assert.Equal("gpt-5", SensitivityPolicy.Label(new ProviderProfile(ProviderKind.OpenAI, "", "https://api.openai.com/v1", "gpt-5")));
        Assert.Equal(("Sensitive", "Not sensitive"), (SensitivityPolicy.Label(true), SensitivityPolicy.Label(false)));
    }

    [Fact]
    public void BackupsAndSubagentsFollowThePolicy()
    {
        var spark = Route(ProviderKind.OpenAICompat, "Spark", "http://spark.local/v1");
        var kimi = Route(ProviderKind.OpenAICompat, "Kimi Code", "https://api.kimi.com/coding/v1");
        var bedrock = Route(ProviderKind.Bedrock, "Bedrock", "");
        Assert.Equal(["Spark", "Bedrock"], SensitivityPolicy.AdmittedBackups([spark, kimi, bedrock], shieldOn: false).Select(r => r.Name));
        Assert.Equal(["Spark", "Kimi Code", "Bedrock"], SensitivityPolicy.AdmittedBackups([spark, kimi, bedrock], shieldOn: true).Select(r => r.Name));
        Assert.True(SensitivityPolicy.SubagentsPrivateOnly(shieldOn: false));
        Assert.False(SensitivityPolicy.SubagentsPrivateOnly(shieldOn: true));
    }
}
