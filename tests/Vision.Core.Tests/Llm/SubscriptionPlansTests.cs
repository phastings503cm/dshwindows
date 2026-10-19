using System.Text.Json.Nodes;

namespace Vision.Core.Tests;

public sealed class SubscriptionPlansTests
{
    [Fact]
    public void TheCatalogHoldsThePlansInOrder()
    {
        Assert.Equal(["zai", "kimi-code", "moonshot-api", "openai", "qwen-coding", "qwen-api", "deepseek", "anthropic"],
            SubscriptionPlans.All.Select(p => p.Id));
        Assert.Equal(["Z.ai (GLM)", "Kimi Code (subscription)", "Moonshot (Kimi) — pay-as-you-go API",
                      "OpenAI API", "Qwen — Alibaba Cloud Coding Plan", "Qwen — Model Studio pay-as-you-go",
                      "DeepSeek API", "Anthropic API (Claude, with an API key)"],
            SubscriptionPlans.All.Select(p => p.Name));
        Assert.All(SubscriptionPlans.All, p =>
        {
            Assert.NotEmpty(p.Models);
            Assert.NotEmpty(p.KeySteps);
            Assert.NotEmpty(p.Regions);
            Assert.All(p.Regions, r => Assert.StartsWith("https://", r.BaseUrl));
            Assert.All(p.Regions, r => Assert.StartsWith("https://", r.KeyPage));
        });
    }

    [Theory]
    [InlineData("zai", "coding", "https://api.z.ai/api/coding/paas/v4", "https://z.ai/manage-apikey/apikey-list", "glm-5.3")]
    [InlineData("zai", "chinaCoding", "https://open.bigmodel.cn/api/coding/paas/v4", "https://open.bigmodel.cn/usercenter/apikeys", "glm-5.3")]
    [InlineData("zai", "general", "https://api.z.ai/api/paas/v4", "https://z.ai/manage-apikey/apikey-list", "glm-5.3")]
    [InlineData("zai", "chinaGeneral", "https://open.bigmodel.cn/api/paas/v4", "https://open.bigmodel.cn/usercenter/apikeys", "glm-5.3")]
    [InlineData("kimi-code", "intl", "https://api.kimi.ai/coding/v1", "https://www.kimi.com/code/console", "kimi-for-coding")]
    [InlineData("kimi-code", "china", "https://api.kimi.com/coding/v1", "https://www.kimi.com/code/console", "kimi-for-coding")]
    [InlineData("moonshot-api", "intl", "https://api.moonshot.ai/v1", "https://platform.moonshot.ai/console/api-keys", "kimi-k2.7-code")]
    [InlineData("moonshot-api", "china", "https://api.moonshot.cn/v1", "https://platform.moonshot.cn/console/api-keys", "kimi-k2.7-code")]
    [InlineData("openai", "intl", "https://api.openai.com/v1", "https://platform.openai.com/api-keys", "gpt-5")]
    [InlineData("qwen-coding", "intl", "https://coding-intl.dashscope.aliyuncs.com/v1", "https://modelstudio.console.alibabacloud.com/", "qwen3.7-plus")]
    [InlineData("qwen-coding", "china", "https://coding.dashscope.aliyuncs.com/v1", "https://bailian.console.aliyun.com/cn-beijing/subscription/coding-plan", "qwen3.7-plus")]
    [InlineData("qwen-api", "intl", "https://dashscope-intl.aliyuncs.com/compatible-mode/v1", "https://modelstudio.console.alibabacloud.com/", "qwen3.7-plus")]
    [InlineData("qwen-api", "china", "https://dashscope.aliyuncs.com/compatible-mode/v1", "https://bailian.console.aliyun.com/", "qwen3.7-plus")]
    public void EachPlanHasItsAddressesKeyPageAndRecommendedModel(string id, string region, string baseUrl, string keyPage, string recommended)
    {
        var plan = SubscriptionPlans.Find(id)!;
        var where = plan.Region(region);
        Assert.Equal(region, where.Id);
        Assert.Equal(baseUrl, where.BaseUrl);
        Assert.Equal(keyPage, where.KeyPage);
        Assert.Equal(recommended, plan.Recommended.Id);
        Assert.Equal((id, region), (SubscriptionPlans.ForBaseUrl(baseUrl + "/")!.Value.Plan.Id, SubscriptionPlans.ForBaseUrl(baseUrl)!.Value.Region.Id));
    }

    [Fact]
    public void ThePresetModelListsMatchTheResearch()
    {
        Assert.Equal(["kimi-for-coding", "k3", "k3-256k", "kimi-for-coding-highspeed"], SubscriptionPlans.Find("kimi-code")!.Models.Select(m => m.Id));
        Assert.Equal(["qwen3.7-plus", "qwen3.6-plus", "qwen3-coder-plus", "qwen3-coder-next", "qwen3.5-plus", "kimi-k2.5", "glm-5", "MiniMax-M2.5"],
            SubscriptionPlans.Find("qwen-coding")!.Models.Select(m => m.Id));
        Assert.Equal(1_000_000, SubscriptionPlans.Find("zai")!.Recommended.Context);
        Assert.Equal(["glm-5.3", "glm-5.2", "glm-5.1", "glm-5", "glm-4.7", "glm-4.6", "glm-5.3-flash"], SubscriptionPlans.Find("zai")!.Models.Select(m => m.Id));
        Assert.True(SubscriptionPlans.Find("zai")!.ListsModels);
        Assert.True(SubscriptionPlans.Find("zai")!.ChecksWithChat);
        Assert.Equal("Endpoint", SubscriptionPlans.Find("zai")!.RegionLabel);
        Assert.False(SubscriptionPlans.Find("kimi-code")!.ListsModels);
        Assert.True(SubscriptionPlans.Find("openai")!.ListsModels);
        Assert.True(SubscriptionPlans.Find("moonshot-api")!.ListsModels);
        Assert.True(SubscriptionPlans.Find("qwen-api")!.ListsModels);
        Assert.Equal("Z.ai offers the GLM Coding Plan for use in coding tools; the General API is billed per token.", SubscriptionPlans.Find("zai")!.TermsNote);
        Assert.Equal("A ChatGPT Plus or Pro subscription doesn't include API access — the API is billed separately at platform.openai.com.",
            SubscriptionPlans.Find("openai")!.Note);
        Assert.Equal("https://z.ai/subscribe", SubscriptionPlans.Find("zai")!.SubscribeUrl);
    }

    [Fact]
    public void EveryPlanIsAHostedServiceAndNotPrivate()
    {
        foreach (var plan in SubscriptionPlans.All)
            foreach (var region in plan.Regions)
            {
                var route = plan.Route(region.Id, plan.Recommended.Id);
                Assert.False(route.IsSelfHosted, region.BaseUrl);
                Assert.False(route.IsPrivateRoute, region.BaseUrl);
                Assert.Equal(region.BaseUrl, route.BaseUrl);
                Assert.True(route.NeedsApiKey);
            }
        Assert.Equal(ProviderKind.OpenAI, SubscriptionPlans.Find("openai")!.Route(null, "gpt-5").Kind);
    }

    [Fact]
    public void TheQwenCodingPlanIsForInteractiveUseOnly()
    {
        var plan = SubscriptionPlans.Find("qwen-coding")!;
        Assert.True(plan.InteractiveOnly);
        var route = plan.Route("intl", "qwen3.7-plus");
        Assert.True(route.IsInteractiveOnly);
        Assert.Equal("Qwen — Alibaba Cloud Coding Plan", route.Name);
        Assert.Equal("Qwen — Alibaba Cloud Coding Plan (China)", plan.Route("china", "qwen3.7-plus").Name);
        Assert.Null(SubscriptionPlans.Find("qwen-api")!.Route(null, "qwen3.7-plus").InteractiveOnly);
    }

    [Fact]
    public void ARouteKeepsAKnownContextWindow()
    {
        Assert.Equal(1_000_000, SubscriptionPlans.Find("zai")!.Route("coding", "glm-5.3").ContextWindow);
        Assert.Equal(262_144, SubscriptionPlans.Find("kimi-code")!.Route("intl", "k3-256k").ContextWindow);
        Assert.Null(SubscriptionPlans.Find("kimi-code")!.Route("intl", "k3").ContextWindow);
    }

    [Theory]
    [InlineData("qwen-coding", "sk-sp-abc123", null)]
    [InlineData("qwen-coding", "sk-abc123", "pay-as-you-go")]
    [InlineData("qwen-coding", "nope", "start with sk-sp-")]
    [InlineData("qwen-api", "sk-sp-abc123", "Coding Plan key")]
    [InlineData("qwen-api", "sk-abc123", null)]
    [InlineData("openai", "sk-proj abc", "space")]
    [InlineData("openai", "", null)]
    public void RecognisableKeyMistakesGetAHint(string id, string key, string? hint)
    {
        var mistake = SubscriptionPlans.Find(id)!.KeyMistake(key);
        if (hint is null) Assert.Null(mistake);
        else Assert.Contains(hint, mistake);
    }

    [Fact]
    public void TheNewestGptChatModelIsRecommended()
    {
        Assert.Equal("gpt-5.2", SubscriptionPlans.RecommendOpenAiModel(
            ["gpt-4o", "gpt-4o-mini", "gpt-5", "gpt-5.1", "gpt-5.2-mini", "gpt-5.2", "gpt-5.2-2025-12-11", "gpt-5.2-pro", "gpt-image-1",
             "gpt-4o-realtime-preview", "o3", "text-embedding-3-large", "gpt-5.3-codex", "gpt-oss-120b", "whisper-1"]));
        Assert.Equal("gpt-4.1", SubscriptionPlans.RecommendOpenAiModel(["gpt-4o", "gpt-4.1-mini", "gpt-4.1", "gpt-3.5-turbo"]));
        Assert.Equal("gpt-5.2-2025-12-11", SubscriptionPlans.RecommendOpenAiModel(["gpt-5.2-2025-12-11", "gpt-5.2-mini"]));
        Assert.Null(SubscriptionPlans.RecommendOpenAiModel(["o3", "whisper-1", "dall-e-3"]));
    }

    [Fact]
    public void PlanHostsAreHostedForThinkingAndFleetGrouping()
    {
        foreach (var url in new[] { "https://api.z.ai/x", "https://open.bigmodel.cn/x", "https://api.kimi.ai/x", "https://www.kimi.com/x",
                     "https://api.moonshot.ai/x", "https://api.moonshot.cn/x", "https://dashscope.aliyuncs.com/x", "https://x.alibabacloud.com/y" })
            Assert.False(new ProviderProfile(ProviderKind.OpenAICompat, "p", url, "m").IsSelfHosted, url);
        // The ones that were there before still are.
        Assert.False(new ProviderProfile(ProviderKind.OpenAI, "p", "https://api.openai.com/v1", "m").IsSelfHosted);
        Assert.True(new ProviderProfile(ProviderKind.OpenAICompat, "p", "https://192.168.1.42:11443/v1", "m").IsSelfHosted);
    }

    // MARK: - Z.ai, one plan with four endpoints

    [Fact]
    public void ZaiIsOnePlanWithFourEndpoints()
    {
        var zai = SubscriptionPlans.Find("zai")!;
        Assert.Same(zai, SubscriptionPlans.Find("zai-coding")); // the ids of earlier builds still find it
        Assert.Same(zai, SubscriptionPlans.Find("zai-api"));
        Assert.Equal(["Coding Plan", "General API", "China — Coding Plan", "China — General API"], zai.Regions.Select(r => r.Label));
        Assert.Equal(["GLM Coding Plan", "Z.ai API", "GLM Coding Plan (China)", "Z.ai API (China)"],
            zai.Regions.Select(r => zai.Route(r.Id, "glm-5.3").Name));
        Assert.Equal([true, false, true, false], zai.Regions.Select(r => r.IsSubscription == true));
        Assert.Equal("For a GLM Coding Plan subscription — Coding Plan keys work only here.", zai.DefaultRegion.Detail);
        Assert.Equal("coding", zai.DefaultRegion.Id);
        Assert.Contains("don't start with sk-", zai.KeyMistake("sk-abc123"));
        Assert.Null(zai.KeyMistake("0123abcd.EfGh"));
    }

    [Fact]
    public void ZaisModelListKeepsTheChatModels()
    {
        var zai = SubscriptionPlans.Find("zai")!;
        Assert.Equal(["glm-5.3", "glm-4.6", "glm-5.3-flash"],
            SubscriptionPlans.ChatModels(zai, ["glm-5.3", "embedding-3", "glm-4.6", "cogview-4", "glm-asr-2", "glm-5.3-flash", "glm-tts"]));
        Assert.Equal(zai.Models.Select(m => m.Id), SubscriptionPlans.ChatModels(zai, ["cogview-4"]));
        var openai = SubscriptionPlans.Find("openai")!;
        Assert.Equal(["a", "b"], SubscriptionPlans.ChatModels(openai, ["a", "b"]));
    }

    [Fact]
    public void VisionSaysWhoItIsHonestly()
    {
        Assert.Matches(@"^Vision/[0-9A-Za-z.\-]+ \(Windows\)$", OpenAiClient.UserAgent);
        Assert.DoesNotContain("claude", OpenAiClient.UserAgent, StringComparison.OrdinalIgnoreCase);
    }

    // MARK: - Friendly errors

    [Fact]
    public void RefusalsAreExplainedInPlainWords()
    {
        var kimi = SubscriptionPlans.Find("kimi-code")!;
        var zai = SubscriptionPlans.Find("zai")!;
        Assert.Equal("Z.ai didn't accept the key (HTTP 401).", SubscriptionPlans.ExplainError(zai, zai.Region("chinaCoding"), 401, "{}"));
        // A Coding Plan key on the General API: the 429 that used to read "usage limit used up".
        Assert.StartsWith("Z.ai says this account has no balance on the General API (1113). If this is a GLM Coding Plan key, switch the endpoint to Coding Plan",
            SubscriptionPlans.ExplainError(zai, zai.Region("general"), 429, """{"error":{"code":"1113","message":"Insufficient balance or no resource package. Please recharge."}}"""));
        Assert.Equal("The plan's usage limit is used up for now. Try again later, or check your plan's usage page.",
            SubscriptionPlans.ExplainError(zai, zai.DefaultRegion, 429, ""));
        var agents = SubscriptionPlans.ExplainError(kimi, kimi.DefaultRegion, 403, """{"error":{"message":"Kimi For Coding is only available for Coding Agents"}}""");
        Assert.Contains("only works with certain coding agents", agents);
        Assert.Contains("Moonshot (Kimi) pay-as-you-go API", agents);
    }
}
