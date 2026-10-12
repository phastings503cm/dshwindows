using System.Text.Json.Nodes;

namespace Dsh.Core.Tests;

public sealed class SubscriptionPlansTests
{
    [Fact]
    public void TheCatalogHoldsThePlansInOrder()
    {
        Assert.Equal(["zai-coding", "zai-api", "kimi-code", "moonshot-api", "openai", "qwen-coding", "qwen-api"],
            SubscriptionPlans.All.Select(p => p.Id));
        Assert.Equal(["Z.ai — GLM Coding Plan", "Z.ai — pay-as-you-go API", "Kimi Code (subscription)", "Moonshot (Kimi) — pay-as-you-go API",
                      "OpenAI API", "Qwen — Alibaba Cloud Coding Plan", "Qwen — Model Studio pay-as-you-go"],
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
    [InlineData("zai-coding", "intl", "https://api.z.ai/api/coding/paas/v4", "https://z.ai/manage-apikey/apikey-list", "glm-5.3")]
    [InlineData("zai-coding", "china", "https://open.bigmodel.cn/api/coding/paas/v4", "https://open.bigmodel.cn/usercenter/apikeys", "glm-5.3")]
    [InlineData("zai-api", "intl", "https://api.z.ai/api/paas/v4", "https://z.ai/manage-apikey/apikey-list", "glm-5.3")]
    [InlineData("zai-api", "china", "https://open.bigmodel.cn/api/paas/v4", "https://open.bigmodel.cn/usercenter/apikeys", "glm-5.3")]
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
        Assert.Equal(1_000_000, SubscriptionPlans.Find("zai-coding")!.Recommended.Context);
        Assert.False(SubscriptionPlans.Find("kimi-code")!.ListsModels);
        Assert.True(SubscriptionPlans.Find("openai")!.ListsModels);
        Assert.True(SubscriptionPlans.Find("moonshot-api")!.ListsModels);
        Assert.True(SubscriptionPlans.Find("qwen-api")!.ListsModels);
        Assert.Equal("Z.ai offers the Coding Plan for use in coding tools.", SubscriptionPlans.Find("zai-coding")!.TermsNote);
        Assert.Equal("A ChatGPT Plus or Pro subscription doesn't include API access — the API is billed separately at platform.openai.com.",
            SubscriptionPlans.Find("openai")!.Note);
        Assert.Equal("https://z.ai/subscribe", SubscriptionPlans.Find("zai-coding")!.SubscribeUrl);
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
        Assert.Equal(1_000_000, SubscriptionPlans.Find("zai-coding")!.Route("intl", "glm-5.3").ContextWindow);
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

    // MARK: - GLM-5 thinking

    private static JsonObject Body(string model, ThinkingLevel? level, string baseUrl = "https://api.z.ai/api/coding/paas/v4")
    {
        var client = new OpenAiClient(new ProviderProfile(ProviderKind.OpenAICompat, "z", baseUrl, model));
        return JsonNode.Parse(client.MakeBody(new LlmRequest("sys", [LlmMessage.User("hi")], [], model, Thinking: level)))!.AsObject();
    }

    [Theory]
    [InlineData(ThinkingLevel.Off, "low")]
    [InlineData(ThinkingLevel.Low, "low")]
    [InlineData(ThinkingLevel.Medium, "high")]
    [InlineData(ThinkingLevel.High, "high")]
    [InlineData(ThinkingLevel.Max, "max")]
    public void Glm5AlwaysThinksAndTakesItsOwnEffortWords(ThinkingLevel level, string effort)
    {
        var body = Body("glm-5.3", level);
        Assert.Equal(effort, body["reasoning_effort"]!.GetValue<string>());
        Assert.Null(body["chat_template_kwargs"]); // never "enable_thinking: false"
        Assert.Null(body["thinking"]);
        // The same on a server of your own.
        var local = Body("glm-5.3", level, "http://192.168.1.42:8000/v1");
        Assert.Equal(effort, local["reasoning_effort"]!.GetValue<string>());
        Assert.Null(local["chat_template_kwargs"]);
    }

    [Fact]
    public void OtherHostedModelsKeepTheUsualThinkingParameters()
    {
        var off = Body("kimi-for-coding", ThinkingLevel.Off, "https://api.kimi.ai/coding/v1");
        Assert.Null(off["reasoning_effort"]);
        Assert.Null(off["chat_template_kwargs"]); // hosted: never template kwargs
        Assert.Equal("high", Body("kimi-for-coding", ThinkingLevel.High, "https://api.kimi.ai/coding/v1")["reasoning_effort"]!.GetValue<string>());
        Assert.False(SubscriptionPlans.AlwaysThinks("glm-4.6"));
        Assert.True(SubscriptionPlans.AlwaysThinks("zai/glm-5.3"));
    }

    [Fact]
    public void DshSaysWhoItIsHonestly()
    {
        Assert.Matches(@"^DSH/[0-9A-Za-z.\-]+ \(Windows\)$", OpenAiClient.UserAgent);
        Assert.DoesNotContain("claude", OpenAiClient.UserAgent, StringComparison.OrdinalIgnoreCase);
    }

    // MARK: - Friendly errors

    [Fact]
    public void RefusalsAreExplainedInPlainWords()
    {
        var kimi = SubscriptionPlans.Find("kimi-code")!;
        var zai = SubscriptionPlans.Find("zai-coding")!;
        Assert.Equal("The key wasn't accepted — check it's a Z.ai — GLM Coding Plan key for the China (BigModel) region.",
            SubscriptionPlans.ExplainError(zai, zai.Region("china"), 401, "{}"));
        Assert.Equal("The plan's usage limit is used up for now. Try again later, or check your plan's usage page.",
            SubscriptionPlans.ExplainError(zai, zai.DefaultRegion, 429, ""));
        var agents = SubscriptionPlans.ExplainError(kimi, kimi.DefaultRegion, 403, """{"error":{"message":"Kimi For Coding is only available for Coding Agents"}}""");
        Assert.Contains("only works with certain coding agents", agents);
        Assert.Contains("Moonshot (Kimi) pay-as-you-go API", agents);
    }
}
