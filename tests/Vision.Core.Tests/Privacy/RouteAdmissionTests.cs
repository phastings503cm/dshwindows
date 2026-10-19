namespace Vision.Core.Tests;

/// <summary>The sensitivity policy (10-19 #20): which routes a Sensitive chat may use, and how.</summary>
public sealed class RouteAdmissionTests
{
    private static ProviderProfile Route(ProviderKind kind, string name, string url, string model = "m", bool? isPrivate = null) =>
        new(kind, name, url, model) { IsPrivate = isPrivate };

    private static readonly ProviderProfile Spark = Route(ProviderKind.OpenAICompat, "DGX Spark", "http://192.168.1.40:8002/v1");
    private static readonly ProviderProfile Ollama = Route(ProviderKind.Ollama, "Ollama", "http://127.0.0.1:11434/v1");
    private static readonly ProviderProfile LmStudio = Route(ProviderKind.LmStudio, "LM Studio", "http://localhost:1234/v1");
    private static readonly ProviderProfile Tailnet = Route(ProviderKind.OpenAICompat, "Evo", "http://evo-x2.tail1234.ts.net:8000/v1");
    private static readonly ProviderProfile Bedrock = Route(ProviderKind.Bedrock, "Amazon Bedrock", "https://bedrock-runtime.us-east-1.amazonaws.com") with { AwsProfile = "vision-bedrock" };
    private static readonly ProviderProfile Guardian = Route(ProviderKind.OpenAICompat, "Guardian", "https://guardian.503cm.com/api/harness/v1");
    private static readonly ProviderProfile DeepSeek = Route(ProviderKind.OpenAICompat, "DeepSeek", "https://api.deepseek.com/v1");
    private static readonly ProviderProfile OpenAi = Route(ProviderKind.OpenAI, "OpenAI", "https://api.openai.com/v1");

    [Fact]
    public void RoutesAreClassedByWhereTheySendWhatTheyAreGiven()
    {
        Assert.Equal(RouteReach.OwnMachine, RouteAdmission.Reach(Spark));
        Assert.Equal(RouteReach.OwnMachine, RouteAdmission.Reach(Ollama));
        Assert.Equal(RouteReach.OwnMachine, RouteAdmission.Reach(LmStudio));
        Assert.Equal(RouteReach.OwnMachine, RouteAdmission.Reach(Tailnet));
        Assert.Equal(RouteReach.OwnCloud, RouteAdmission.Reach(Bedrock));
        Assert.Equal(RouteReach.Guardian, RouteAdmission.Reach(Guardian));
        Assert.Equal(RouteReach.Online, RouteAdmission.Reach(DeepSeek));
        Assert.Equal(RouteReach.Online, RouteAdmission.Reach(OpenAi));
        // The person's own word wins: an online address they marked private is treated as theirs.
        Assert.Equal(RouteReach.OwnMachine, RouteAdmission.Reach(OpenAi with { IsPrivate = true }));
        Assert.Equal(RouteReach.Online, RouteAdmission.Reach(Spark with { IsPrivate = false }));
    }

    [Fact]
    public void AStandardChatMayUseEveryRoute()
    {
        foreach (var route in new[] { Spark, Bedrock, Guardian, DeepSeek, OpenAi })
            Assert.True(RouteAdmission.Admit(route, sensitive: false).Allowed);
        Assert.False(RouteAdmission.Admit(Spark, sensitive: false).Masked);
        Assert.True(RouteAdmission.Admit(DeepSeek, sensitive: false).Masked);
    }

    [Fact]
    public void ASensitiveChatUsesPrivateRoutesAsTheyAreAndOnlineOnesOnlyMasked()
    {
        foreach (var route in new[] { Spark, Ollama, LmStudio, Tailnet })
        {
            var admission = RouteAdmission.Admit(route, sensitive: true);
            Assert.True(admission.Allowed, route.Name);
            Assert.False(admission.Masked, route.Name);
        }
        Assert.True(RouteAdmission.Admit(Bedrock, sensitive: true).Allowed);
        Assert.True(RouteAdmission.Admit(Guardian, sensitive: true).Allowed);
        var online = RouteAdmission.Admit(DeepSeek, sensitive: true);
        Assert.True(online.Allowed);
        Assert.True(online.Masked);
        Assert.Equal(RouteReach.Online, online.Reach);
    }

    [Fact]
    public void WithMaskedOnlineUseOffASensitiveChatRefusesOnlineRoutesButKeepsPrivateOnes()
    {
        var refused = RouteAdmission.Admit(OpenAi, sensitive: true, allowShielded: false);
        Assert.False(refused.Allowed);
        Assert.Contains("private models only", refused.Refusal);
        Assert.Contains("OpenAI", refused.Refusal);
        Assert.True(RouteAdmission.Admit(Spark, sensitive: true, allowShielded: false).Allowed);
        Assert.True(RouteAdmission.Admit(Bedrock, sensitive: true, allowShielded: false).Allowed);
        Assert.True(RouteAdmission.Admit(Guardian, sensitive: true, allowShielded: false).Allowed);
        Assert.True(RouteAdmission.Admit(OpenAi, sensitive: false, allowShielded: false).Allowed);
    }

    [Fact]
    public void AnEngineThatReadsTheFilesItselfIsRefusedForASensitiveChatWithTheReason()
    {
        // The Claude Code engine is a route kind of its own; matched by name so this policy holds whatever build has it.
        Assert.True(RouteAdmission.IsDirectFileKindName("ClaudeCode"));
        Assert.True(RouteAdmission.IsDirectFileKindName("codex"));
        Assert.False(RouteAdmission.IsDirectFileKindName("OpenAI"));
        var refusal = RouteAdmission.DirectEngineRefusal("Claude Code");
        Assert.Contains("can't use Claude Code", refusal);
        Assert.Contains("reads your project's files itself", refusal);
        Assert.Contains("turn Sensitive off", refusal);
    }

    [Fact]
    public void TheHeaderSaysThePolicyAndWhatTheChatsModelDoesUnderIt()
    {
        Assert.Equal("Sensitive", RouteAdmission.Label(true));
        Assert.Equal("Standard", RouteAdmission.Label(false));
        var sensitive = RouteAdmission.Describe(true, RouteAdmission.Admit(DeepSeek, true));
        Assert.StartsWith("Sensitive: private models only", sensitive);
        Assert.EndsWith("sensitive details are masked before anything is sent.", sensitive);
        Assert.EndsWith("runs on your own machine.", RouteAdmission.Describe(true, RouteAdmission.Admit(Spark, true)));
        Assert.Contains("can't be used while it is Sensitive", RouteAdmission.Describe(true, RouteAdmission.Admit(OpenAi, true, allowShielded: false), allowShielded: false));
        Assert.StartsWith("Standard: any model", RouteAdmission.Describe(false, null));
    }
}
