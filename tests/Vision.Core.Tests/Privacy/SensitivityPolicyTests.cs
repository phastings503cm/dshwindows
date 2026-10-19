using Vision.Core;

namespace Vision.Core.Tests;

/// <summary>Which models a Sensitive chat may use — the same decisions and words as Vision for macOS: private routes as
/// they are, online ones only behind the shield, and engines that read files themselves (Claude Code) never.</summary>
public sealed class SensitivityPolicyTests
{
    [Fact]
    public void ClaudeCodeIsRefusedWithTheReason()
    {
        var route = ClaudeCodeRoutes.Route("sonnet");
        foreach (var shieldOn in new[] { true, false })
        {
            var admission = SensitivityPolicy.Admission(route, shieldOn);
            Assert.Equal("refused", admission.Code);
            Assert.Equal("Claude Code reads your files itself and sends them to its own service, so the sensitive-data shield can't mask " +
                         "anything on the way. Sensitive chats can't use it — choose a private model (your own hardware, Guardian, Amazon " +
                         "Bedrock in your AWS account), or turn Sensitive off for this chat.", admission.Refusal);
        }
        Assert.StartsWith("This chat is Sensitive. Claude Code reads your files itself", SensitivityPolicy.RefusalMessage(route, shieldOn: true));
        Assert.Equal("Sensitive — Claude Code can't be used in this chat. Choose a private model.", SensitivityPolicy.HeaderLine(route, true));
    }

    [Fact]
    public void PrivateRoutesAreAllowedAndOnlineOnesOnlyBehindTheShield()
    {
        var spark = new ProviderProfile(ProviderKind.OpenAICompat, "Spark", "http://192.168.1.20:8002/v1", "qwen");
        var guardian = GuardianRoutes.Route("https://guardian.503cm.com", new GuardianModel("guardian/default", "Default", null, null), null);
        var bedrock = new ProviderProfile(ProviderKind.Bedrock, "Amazon Bedrock", "https://bedrock-runtime.us-east-1.amazonaws.com", "claude");
        var openai = new ProviderProfile(ProviderKind.OpenAI, "OpenAI", "https://api.openai.com/v1", "gpt-5");
        Assert.Equal(new RouteAdmission.Private("your own hardware"), SensitivityPolicy.Admission(spark, false));
        Assert.Equal(new RouteAdmission.Private("Guardian, your organisation's own server"), SensitivityPolicy.Admission(guardian, false));
        Assert.Equal(new RouteAdmission.Private("Amazon Bedrock in your own AWS account"), SensitivityPolicy.Admission(bedrock, false));
        Assert.Equal("masked", SensitivityPolicy.Admission(openai, shieldOn: true).Code);
        Assert.Equal("refused", SensitivityPolicy.Admission(openai, shieldOn: false).Code);
        Assert.Null(SensitivityPolicy.RefusalMessage(openai, shieldOn: true));
        Assert.Equal([spark, openai], SensitivityPolicy.AdmittedBackups([spark, ClaudeCodeRoutes.Route("opus"), openai], shieldOn: true));
    }
}
