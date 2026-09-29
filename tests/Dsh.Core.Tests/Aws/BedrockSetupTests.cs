using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Dsh.Core.Tests;

public sealed class BedrockSetupTests
{
    // MARK: - Fixtures (shapes from botocore's bedrock/2023-04-20 model, as the CLI prints them with --output json)

    private static string Model(string id, string name, string provider, string inference, string lifecycle = "ACTIVE",
                                bool streaming = true, string output = "\"TEXT\"", string input = "\"TEXT\"") => $$"""
        {
            "modelArn": "arn:aws:bedrock:us-east-1::foundation-model/{{id}}",
            "modelId": "{{id}}",
            "modelName": "{{name}}",
            "providerName": "{{provider}}",
            "inputModalities": [{{input}}],
            "outputModalities": [{{output}}],
            "responseStreamingSupported": {{(streaming ? "true" : "false")}},
            "customizationsSupported": [],
            "inferenceTypesSupported": [{{inference}}],
            "modelLifecycle": {"status": "{{lifecycle}}", "startOfLifeTime": "2025-09-29T00:00:00+00:00"}
        }
        """;

    private static readonly string FoundationModels = "{\"modelSummaries\": [" + string.Join(",\n",
        Model("anthropic.claude-sonnet-4-5-20250929-v1:0", "Claude Sonnet 4.5", "Anthropic", "\"INFERENCE_PROFILE\"", input: "\"TEXT\", \"IMAGE\""),
        Model("anthropic.claude-sonnet-4-20250514-v1:0", "Claude Sonnet 4", "Anthropic", "\"INFERENCE_PROFILE\"", input: "\"TEXT\", \"IMAGE\""),
        Model("anthropic.claude-3-7-sonnet-20250219-v1:0", "Claude 3.7 Sonnet", "Anthropic", "\"INFERENCE_PROFILE\"", input: "\"TEXT\", \"IMAGE\""),
        Model("anthropic.claude-opus-4-1-20250805-v1:0", "Claude Opus 4.1", "Anthropic", "\"INFERENCE_PROFILE\""),
        Model("anthropic.claude-opus-4-20250514-v1:0", "Claude Opus 4", "Anthropic", "\"INFERENCE_PROFILE\""),
        Model("anthropic.claude-haiku-4-5-20251001-v1:0", "Claude Haiku 4.5", "Anthropic", "\"INFERENCE_PROFILE\""),
        Model("anthropic.claude-3-haiku-20240307-v1:0", "Claude 3 Haiku", "Anthropic", "\"ON_DEMAND\""),
        Model("anthropic.claude-3-sonnet-20240229-v1:0:28k", "Claude 3 Sonnet", "Anthropic", "\"PROVISIONED\""),
        Model("anthropic.claude-v2:1", "Claude", "Anthropic", "\"ON_DEMAND\"", lifecycle: "LEGACY"),
        Model("amazon.nova-pro-v1:0", "Nova Pro", "Amazon", "\"ON_DEMAND\", \"INFERENCE_PROFILE\""),
        Model("amazon.nova-lite-v1:0", "Nova Lite", "Amazon", "\"ON_DEMAND\""),
        Model("amazon.titan-embed-text-v2:0", "Titan Text Embeddings V2", "Amazon", "\"ON_DEMAND\"", output: "\"EMBEDDING\""),
        Model("meta.llama3-3-70b-instruct-v1:0", "Llama 3.3 70B Instruct", "Meta", "\"INFERENCE_PROFILE\""),
        Model("meta.llama4-maverick-17b-instruct-v1:0", "Llama 4 Maverick 17B Instruct", "Meta", "\"INFERENCE_PROFILE\""),
        Model("mistral.mistral-large-2402-v1:0", "Mistral Large (24.02)", "Mistral AI", "\"ON_DEMAND\""),
        Model("deepseek.r1-v1:0", "DeepSeek-R1", "DeepSeek", "\"INFERENCE_PROFILE\""),
        Model("qwen.qwen3-coder-480b-a35b-v1:0", "Qwen3 Coder 480B A35B Instruct", "Qwen", "\"ON_DEMAND\""),
        Model("openai.gpt-oss-20b-1:0", "gpt-oss-20b", "OpenAI", "\"ON_DEMAND\""),
        Model("openai.gpt-oss-120b-1:0", "gpt-oss-120b", "OpenAI", "\"ON_DEMAND\""),
        Model("cohere.command-r-plus-v1:0", "Command R+", "Cohere", "\"ON_DEMAND\""),
        Model("ai21.jamba-1-5-large-v1:0", "Jamba 1.5 Large", "AI21 Labs", "\"ON_DEMAND\"", streaming: false)) + "]}";

    private static string Profile(string id, string name, params string[] modelArns) => $$"""
        {
            "inferenceProfileName": "{{name}}",
            "description": "Routes requests to {{name}}.",
            "createdAt": "2025-09-29T00:00:00+00:00",
            "updatedAt": "2025-09-29T00:00:00+00:00",
            "inferenceProfileArn": "arn:aws:bedrock:us-east-1:111122223333:inference-profile/{{id}}",
            "models": [{{string.Join(", ", modelArns.Select(a => $"{{\"modelArn\": \"{a}\"}}"))}}],
            "inferenceProfileId": "{{id}}",
            "status": "ACTIVE",
            "type": "SYSTEM_DEFINED"
        }
        """;

    private static string Geo(string prefix, string modelId, params string[] regions) =>
        Profile($"{prefix}.{modelId}", $"{prefix.ToUpperInvariant()} {modelId}",
            regions.Select(r => $"arn:aws:bedrock:{r}::foundation-model/{modelId}").ToArray());

    private static readonly string InferenceProfiles = "{\"inferenceProfileSummaries\": [" + string.Join(",\n",
        Geo("us", "anthropic.claude-sonnet-4-5-20250929-v1:0", "us-east-1", "us-east-2", "us-west-2"),
        Geo("eu", "anthropic.claude-sonnet-4-5-20250929-v1:0", "eu-central-1", "eu-west-1", "eu-west-3"),
        Geo("jp", "anthropic.claude-sonnet-4-5-20250929-v1:0", "ap-northeast-1", "ap-northeast-3"),
        Geo("apac", "anthropic.claude-sonnet-4-5-20250929-v1:0", "ap-northeast-1", "ap-south-1", "ap-southeast-2"),
        Geo("au", "anthropic.claude-sonnet-4-5-20250929-v1:0", "ap-southeast-2", "ap-southeast-4"),
        Geo("global", "anthropic.claude-sonnet-4-5-20250929-v1:0", ""),
        Geo("us", "anthropic.claude-sonnet-4-20250514-v1:0", "us-east-1", "us-west-2"),
        Geo("us", "anthropic.claude-3-7-sonnet-20250219-v1:0", "us-east-1", "us-west-2"),
        Geo("us", "anthropic.claude-opus-4-1-20250805-v1:0", "us-east-1", "us-west-2"),
        Geo("global", "anthropic.claude-haiku-4-5-20251001-v1:0", ""),
        Geo("us", "anthropic.claude-haiku-4-5-20251001-v1:0", "us-east-1", "us-west-2"),
        Geo("us", "meta.llama3-3-70b-instruct-v1:0", "us-east-1", "us-west-2"),
        Geo("us", "meta.llama4-maverick-17b-instruct-v1:0", "us-east-1", "us-west-2"),
        Geo("us", "deepseek.r1-v1:0", "us-east-1", "us-west-2"),
        Geo("us", "amazon.nova-pro-v1:0", "us-east-1", "us-west-2"),
        // A profile the user made themselves: not a system route, ignored.
        """
        {"inferenceProfileName": "mine", "inferenceProfileId": "abcd1234efgh", "inferenceProfileArn": "arn:aws:bedrock:us-east-1:111122223333:application-inference-profile/abcd1234efgh",
         "models": [{"modelArn": "arn:aws:bedrock:us-east-1::foundation-model/anthropic.claude-opus-4-20250514-v1:0"}], "status": "ACTIVE", "type": "APPLICATION"}
        """) + "]}";

    private static IReadOnlyList<BedrockModel> Build(string region) =>
        BedrockModels.Build(JsonNode.Parse(FoundationModels), JsonNode.Parse(InferenceProfiles), region);

    // MARK: - Models and the id to call them by

    [Fact]
    public void KeepsActiveStreamingTextModelsOnly()
    {
        var ids = Build("us-east-1").Select(m => m.ModelId).ToList();
        Assert.DoesNotContain("anthropic.claude-3-sonnet-20240229-v1:0:28k", ids); // provisioned throughput only
        Assert.DoesNotContain("anthropic.claude-v2:1", ids);                        // legacy
        Assert.DoesNotContain("amazon.titan-embed-text-v2:0", ids);                 // embeddings
        Assert.DoesNotContain("ai21.jamba-1-5-large-v1:0", ids);                    // no streaming
        Assert.Equal(17, ids.Count);
    }

    [Theory]
    [InlineData("us-east-1", "us.anthropic.claude-sonnet-4-5-20250929-v1:0")]
    [InlineData("us-west-2", "us.anthropic.claude-sonnet-4-5-20250929-v1:0")]
    [InlineData("eu-central-1", "eu.anthropic.claude-sonnet-4-5-20250929-v1:0")]
    [InlineData("ap-northeast-1", "jp.anthropic.claude-sonnet-4-5-20250929-v1:0")]
    [InlineData("ap-southeast-2", "au.anthropic.claude-sonnet-4-5-20250929-v1:0")]
    [InlineData("ap-south-1", "apac.anthropic.claude-sonnet-4-5-20250929-v1:0")]
    [InlineData("sa-east-1", "global.anthropic.claude-sonnet-4-5-20250929-v1:0")]
    public void PicksTheClosestInferenceProfile(string region, string invokeId)
    {
        var sonnet = Build(region).Single(m => m.ModelId == "anthropic.claude-sonnet-4-5-20250929-v1:0");
        Assert.Equal(invokeId, sonnet.InvokeId);
        Assert.True(sonnet.UsesInferenceProfile);
        Assert.True(sonnet.AcceptsImages);
        Assert.True(sonnet.IsAnthropic);
        Assert.Equal("Claude Sonnet 4.5 (Anthropic)", sonnet.DisplayName);
    }

    [Fact]
    public void OnDemandModelsAreCalledByTheirOwnId()
    {
        var models = Build("us-east-1");
        var nova = models.Single(m => m.ModelId == "amazon.nova-pro-v1:0");
        Assert.Equal("amazon.nova-pro-v1:0", nova.InvokeId); // on demand wins over a profile
        Assert.False(nova.UsesInferenceProfile);
        Assert.Equal("anthropic.claude-3-haiku-20240307-v1:0", models.Single(m => m.ModelId == "anthropic.claude-3-haiku-20240307-v1:0").InvokeId);
    }

    [Fact]
    public void FallsBackToGlobalThenToNothing()
    {
        var models = Build("eu-west-1");
        Assert.Equal("global.anthropic.claude-haiku-4-5-20251001-v1:0", models.Single(m => m.ModelId == "anthropic.claude-haiku-4-5-20251001-v1:0").InvokeId);
        // Opus 4 has no system profile (only the user's own application profile): it can't be called.
        var opus4 = models.Single(m => m.ModelId == "anthropic.claude-opus-4-20250514-v1:0");
        Assert.Null(opus4.InvokeId);
        Assert.False(opus4.CanInvoke);
        Assert.Equal(opus4, models[^1]); // listed last
        // A US-only profile is still better than nothing from a European Region (profiles listed there are callable there).
        Assert.Equal("us.meta.llama4-maverick-17b-instruct-v1:0", models.Single(m => m.ModelId == "meta.llama4-maverick-17b-instruct-v1:0").InvokeId);
    }

    [Fact]
    public void RecommendsTheNewestOfEachFamilyInOrder()
    {
        var models = Build("us-east-1");
        var recommended = models.Where(m => m.Recommended).Select(m => m.ModelId).ToList();
        Assert.Equal(
        [
            "anthropic.claude-sonnet-4-5-20250929-v1:0",
            "anthropic.claude-opus-4-1-20250805-v1:0",
            "anthropic.claude-haiku-4-5-20251001-v1:0",
            "amazon.nova-pro-v1:0",
            "meta.llama4-maverick-17b-instruct-v1:0",
            "mistral.mistral-large-2402-v1:0",
            "deepseek.r1-v1:0",
            "qwen.qwen3-coder-480b-a35b-v1:0",
            "openai.gpt-oss-120b-1:0",
        ], recommended);
        Assert.Equal(recommended, models.Take(recommended.Count).Select(m => m.ModelId));
        // Then the rest of the families, newest first, then everything else.
        Assert.Equal(["anthropic.claude-sonnet-4-20250514-v1:0", "anthropic.claude-3-7-sonnet-20250219-v1:0"],
            models.Skip(recommended.Count).Take(2).Select(m => m.ModelId));
        Assert.Equal("Claude Sonnet", BedrockModels.FamilyOf("anthropic.claude-3-7-sonnet-20250219-v1:0"));
        Assert.Null(BedrockModels.FamilyOf("cohere.command-r-plus-v1:0"));
    }

    [Theory]
    [InlineData("anthropic.claude-sonnet-4-5-20250929-v1:0", "anthropic.claude-sonnet-4-20250514-v1:0")]
    [InlineData("anthropic.claude-sonnet-4-20250514-v1:0", "anthropic.claude-3-7-sonnet-20250219-v1:0")]
    [InlineData("anthropic.claude-3-5-sonnet-20241022-v2:0", "anthropic.claude-3-5-sonnet-20240620-v1:0")]
    [InlineData("anthropic.claude-opus-4-1-20250805-v1:0", "anthropic.claude-opus-4-20250514-v1:0")]
    [InlineData("meta.llama4-scout-17b-instruct-v1:0", "meta.llama3-3-70b-instruct-v1:0")]
    [InlineData("openai.gpt-oss-120b-1:0", "openai.gpt-oss-20b-1:0")]
    public void KnowsWhichModelIsNewer(string newer, string older) =>
        Assert.True(BedrockModels.RecencyComparer.Instance.Compare(newer, older) > 0);

    [Theory]
    [InlineData("us-east-1", "us,global")]
    [InlineData("us-gov-west-1", "us-gov")]
    [InlineData("ca-central-1", "ca,us,global")]
    [InlineData("eu-west-3", "eu,global")]
    [InlineData("ap-northeast-3", "jp,apac,global")]
    [InlineData("ap-southeast-4", "au,apac,global")]
    [InlineData("ap-southeast-1", "apac,global")]
    [InlineData("me-central-1", "global")]
    public void ProfilePrefixesFollowGeography(string region, string prefixes) =>
        Assert.Equal(prefixes.Split(','), BedrockRegions.ProfilePrefixes(region));

    [Fact]
    public async Task ListsModelsThroughTheCli()
    {
        var page1 = """{"inferenceProfileSummaries": [""" + Geo("us", "anthropic.claude-sonnet-4-5-20250929-v1:0", "us-east-1") + """], "NextToken": "eyJuZXh0IjogMn0="}""";
        var page2 = """{"inferenceProfileSummaries": [""" + Geo("global", "anthropic.claude-haiku-4-5-20251001-v1:0", "") + "]}";
        var runner = new FakeAwsCliRunner()
            .On(["bedrock", "list-foundation-models"], FoundationModels)
            .OnSequence(["bedrock", "list-inference-profiles"], new AwsCliResult(0, page1, ""), new AwsCliResult(0, page2, ""));
        var setup = new BedrockSetup(new AwsCli(runner), "dsh-bedrock", "us-east-1");
        var models = await setup.ListModelsAsync();

        Assert.Equal("us.anthropic.claude-sonnet-4-5-20250929-v1:0", models.Single(m => m.ModelId.Contains("sonnet-4-5")).InvokeId);
        Assert.Equal("global.anthropic.claude-haiku-4-5-20251001-v1:0", models.Single(m => m.ModelId.Contains("haiku-4-5")).InvokeId);
        Assert.Equal(["bedrock", "list-foundation-models", "--by-output-modality", "TEXT", "--profile", "dsh-bedrock", "--region", "us-east-1", "--output", "json"],
            runner.Calls[0].Args);
        var profileCalls = runner.CallsTo("bedrock", "list-inference-profiles");
        Assert.Equal(2, profileCalls.Count);
        Assert.Equal(["bedrock", "list-inference-profiles", "--type-equals", "SYSTEM_DEFINED", "--profile", "dsh-bedrock", "--region", "us-east-1", "--output", "json"],
            profileCalls[0].Args);
        Assert.True(profileCalls[1].HasPair("--starting-token", "eyJuZXh0IjogMn0="));
    }

    [Fact]
    public async Task ListingNeedsPermission()
    {
        var runner = new FakeAwsCliRunner().Fail(["bedrock", "list-foundation-models"],
            "aws: [ERROR]: An error occurred (AccessDeniedException) when calling the ListFoundationModels operation: User: arn:aws:iam::111122223333:user/alice is not authorized to perform: bedrock:ListFoundationModels because no identity-based policy allows the bedrock:ListFoundationModels action");
        var ex = await Assert.ThrowsAsync<AwsCliException>(() => new BedrockSetup(new AwsCli(runner), "p", "us-east-1").ListModelsAsync());
        Assert.Equal(AwsCliFailure.AccessDenied, ex.Kind);
        Assert.Equal("bedrock:ListFoundationModels", ex.Error.Permission);
    }

    // MARK: - Access

    private static string Availability(string agreement = "AVAILABLE", string authorization = "AUTHORIZED", string entitlement = "AVAILABLE",
                                       string region = "AVAILABLE", string? error = null) => $$"""
        {
            "modelId": "x",
            "agreementAvailability": {"status": "{{agreement}}"{{(error is null ? "" : $", \"errorMessage\": \"{error}\"")}}},
            "authorizationStatus": "{{authorization}}",
            "entitlementAvailability": "{{entitlement}}",
            "regionAvailability": "{{region}}"
        }
        """;

    private const string Offers = """
        {
            "modelId": "cohere.command-r-plus-v1:0",
            "offers": [
                {
                    "offerId": "offer-3k2l5j6h7g8f",
                    "offerToken": "eyJvZmZlcklkIjoib2ZmZXItM2syIn0=",
                    "termDetails": {
                        "usageBasedPricingTerm": {
                            "rateCard": [
                                {"dimension": "InputTokenCount", "price": "0.0030", "description": "Input tokens (per 1K)", "unit": "Units"},
                                {"dimension": "OutputTokenCount", "price": "0.0150", "unit": "1K tokens"}
                            ]
                        },
                        "legalTerm": {"url": "https://aws-mp-legal-docs.s3.amazonaws.com/eula/cohere.pdf"},
                        "supportTerm": {"refundPolicyDescription": "No refunds for usage already billed."},
                        "validityTerm": {"agreementDuration": "P1Y"}
                    }
                }
            ]
        }
        """;

    private const string UseCaseNotFound = "aws: [ERROR]: An error occurred (ResourceNotFoundException) when calling the GetUseCaseForModelAccess operation: Use case form not found.";

    private static BedrockModel Cohere => new("cohere.command-r-plus-v1:0", "Command R+", "Cohere", ["TEXT"], true, "ACTIVE", ["ON_DEMAND"], "cohere.command-r-plus-v1:0");
    private static BedrockModel Sonnet => new("anthropic.claude-sonnet-4-5-20250929-v1:0", "Claude Sonnet 4.5", "Anthropic", ["TEXT", "IMAGE"], true, "ACTIVE",
        ["INFERENCE_PROFILE"], "us.anthropic.claude-sonnet-4-5-20250929-v1:0", true);

    private static (BedrockSetup Setup, FakeAwsCliRunner Runner) Setup(Action<FakeAwsCliRunner> script, TimeProvider? time = null)
    {
        var runner = new FakeAwsCliRunner();
        script(runner);
        return (new BedrockSetup(new AwsCli(runner), "dsh-bedrock", "us-east-1", time), runner);
    }

    [Theory]
    [InlineData("AVAILABLE", "AUTHORIZED", "AVAILABLE", "AVAILABLE", ModelAccessState.Ready)]
    [InlineData("AVAILABLE", "AUTHORIZED", "AVAILABLE", "NOT_AVAILABLE", ModelAccessState.NotInRegion)]
    [InlineData("AVAILABLE", "NOT_AUTHORIZED", "AVAILABLE", "AVAILABLE", ModelAccessState.NotAuthorized)]
    [InlineData("PENDING", "AUTHORIZED", "NOT_AVAILABLE", "AVAILABLE", ModelAccessState.Pending)]
    [InlineData("AVAILABLE", "AUTHORIZED", "NOT_AVAILABLE", "AVAILABLE", ModelAccessState.Pending)]
    [InlineData("NOT_AVAILABLE", "AUTHORIZED", "NOT_AVAILABLE", "AVAILABLE", ModelAccessState.NeedsAgreement)]
    [InlineData("ERROR", "AUTHORIZED", "NOT_AVAILABLE", "AVAILABLE", ModelAccessState.Error)]
    [InlineData("NOT_AVAILABLE", "NOT_AUTHORIZED", "NOT_AVAILABLE", "NOT_AVAILABLE", ModelAccessState.NotInRegion)]
    public async Task SummarizesAvailability(string agreement, string authorization, string entitlement, string region, ModelAccessState state)
    {
        var (setup, runner) = Setup(r => r
            .On(["bedrock", "get-foundation-model-availability"], Availability(agreement, authorization, entitlement, region, "Payment instrument declined"))
            .On(["bedrock", "list-foundation-model-agreement-offers"], Offers));
        var access = await setup.CheckAccessAsync(Cohere);
        Assert.Equal(state, access.State);
        Assert.Equal(state == ModelAccessState.Ready, access.IsReady);
        Assert.Equal("cohere.command-r-plus-v1:0", access.ModelId);
        Assert.False(string.IsNullOrWhiteSpace(access.Message));
        Assert.Equal(agreement, access.Availability!.Agreement);
        Assert.Empty(runner.CallsTo("bedrock", "get-use-case-for-model-access")); // not an Anthropic model
        if (state == ModelAccessState.NotAuthorized) Assert.Equal(BedrockPermissions.InvokeActions, access.MissingPermissions);
        if (state == ModelAccessState.Error) Assert.Contains("Payment instrument declined", access.Message);
        if (state == ModelAccessState.NotInRegion) Assert.Contains("US East (N. Virginia)", access.Message);
    }

    [Fact]
    public async Task ShowsTheTermsToAccept()
    {
        var (setup, runner) = Setup(r => r
            .On(["bedrock", "get-foundation-model-availability"], Availability("NOT_AVAILABLE", entitlement: "NOT_AVAILABLE"))
            .On(["bedrock", "list-foundation-model-agreement-offers"], Offers));
        var access = await setup.CheckAccessAsync(Cohere);
        Assert.Equal(ModelAccessState.NeedsAgreement, access.State);
        Assert.True(access.CanAcceptInDsh);
        var offer = access.Offer!;
        Assert.Equal("eyJvZmZlcklkIjoib2ZmZXItM2syIn0=", offer.OfferToken);
        Assert.Equal("offer-3k2l5j6h7g8f", offer.OfferId);
        Assert.Equal("https://aws-mp-legal-docs.s3.amazonaws.com/eula/cohere.pdf", offer.LegalTermsUrl);
        Assert.Equal("Input tokens (per 1K): US$0.0030; OutputTokenCount: US$0.0150 per 1K tokens", offer.PricingSummary);
        Assert.Equal("No refunds for usage already billed.", offer.RefundPolicy);
        Assert.Equal("1 year", offer.DurationText);
        Assert.Equal(["bedrock", "list-foundation-model-agreement-offers", "--model-id", "cohere.command-r-plus-v1:0", "--profile", "dsh-bedrock", "--region", "us-east-1", "--output", "json"],
            runner.CallsTo("bedrock", "list-foundation-model-agreement-offers")[0].Args);
    }

    [Fact]
    public async Task NoOfferMeansAwsSubscribesOnFirstUse()
    {
        var (setup, _) = Setup(r => r
            .On(["bedrock", "get-foundation-model-availability"], Availability("NOT_AVAILABLE"))
            .On(["bedrock", "list-foundation-model-agreement-offers"], """{"modelId": "x", "offers": []}"""));
        var access = await setup.CheckAccessAsync(Cohere);
        Assert.Equal(ModelAccessState.NeedsAgreement, access.State);
        Assert.Null(access.Offer);
        Assert.False(access.CanAcceptInDsh);
        Assert.Contains("first time", access.Message);
    }

    [Fact]
    public async Task OffersThatCantBeReadNameThePermission()
    {
        var (setup, _) = Setup(r => r
            .On(["bedrock", "get-foundation-model-availability"], Availability("NOT_AVAILABLE"))
            .Fail(["bedrock", "list-foundation-model-agreement-offers"],
                "aws: [ERROR]: An error occurred (AccessDeniedException) when calling the ListFoundationModelAgreementOffers operation: Access denied"));
        var access = await setup.CheckAccessAsync(Cohere);
        Assert.Equal(ModelAccessState.NeedsAgreement, access.State);
        Assert.Equal(["bedrock:ListFoundationModelAgreementOffers"], access.MissingPermissions);
    }

    [Fact]
    public async Task AnthropicModelsNeedTheUseCaseFormFirst()
    {
        var (setup, runner) = Setup(r => r
            .On(["bedrock", "get-foundation-model-availability"], Availability("NOT_AVAILABLE", entitlement: "NOT_AVAILABLE"))
            .Fail(["bedrock", "get-use-case-for-model-access"], UseCaseNotFound));
        var access = await setup.CheckAccessAsync(Sonnet);
        Assert.Equal(ModelAccessState.NeedsUseCaseForm, access.State);
        Assert.False(access.UseCaseSubmitted);
        Assert.Contains("Anthropic", access.Message);
        // Checked by the base model id, never the inference profile id.
        Assert.True(runner.CallsTo("bedrock", "get-foundation-model-availability")[0].HasPair("--model-id", "anthropic.claude-sonnet-4-5-20250929-v1:0"));
        Assert.Empty(runner.CallsTo("bedrock", "list-foundation-model-agreement-offers"));
    }

    [Fact]
    public async Task AnthropicModelsWithTheFormOnFileAreReady()
    {
        var form = new UseCaseForm("Acme", "https://acme.example", "Technology", "Coding").ToBase64();
        var (setup, _) = Setup(r => r
            .On(["bedrock", "get-foundation-model-availability"], Availability())
            .On(["bedrock", "get-use-case-for-model-access"], $$"""{"formData": "{{form}}"}"""));
        var access = await setup.CheckAccessAsync(Sonnet);
        Assert.Equal(ModelAccessState.Ready, access.State);
        Assert.True(access.UseCaseSubmitted);
    }

    [Fact]
    public async Task AFormThatCantBeCheckedDoesntBlock()
    {
        var (setup, _) = Setup(r => r
            .On(["bedrock", "get-foundation-model-availability"], Availability())
            .Fail(["bedrock", "get-use-case-for-model-access"],
                "aws: [ERROR]: An error occurred (AccessDeniedException) when calling the GetUseCaseForModelAccess operation: denied"));
        var access = await setup.CheckAccessAsync(Sonnet);
        Assert.Equal(ModelAccessState.Ready, access.State);
        Assert.Null(access.UseCaseSubmitted);
    }

    [Fact]
    public async Task CheckFailuresAreStatesNotCrashes()
    {
        var denied = await Setup(r => r.Fail(["bedrock", "get-foundation-model-availability"],
            "aws: [ERROR]: An error occurred (AccessDeniedException) when calling the GetFoundationModelAvailability operation: User: arn:aws:iam::1:user/a is not authorized to perform: bedrock:GetFoundationModelAvailability"))
            .Setup.CheckAccessAsync(Cohere);
        Assert.Equal(ModelAccessState.NotAuthorized, denied.State);
        Assert.Equal(["bedrock:GetFoundationModelAvailability"], denied.MissingPermissions);
        Assert.Contains("try the model", denied.Message);

        var unknown = await Setup(r => r.Fail(["bedrock", "get-foundation-model-availability"],
            "aws: [ERROR]: An error occurred (ResourceNotFoundException) when calling the GetFoundationModelAvailability operation: Model not found."))
            .Setup.CheckAccessAsync(Cohere);
        Assert.Equal(ModelAccessState.NotInRegion, unknown.State);

        var broken = await Setup(r => r.Fail(["bedrock", "get-foundation-model-availability"],
            "aws: [ERROR]: An error occurred (InternalServerException) when calling the GetFoundationModelAvailability operation: Internal error."))
            .Setup.CheckAccessAsync(Cohere);
        Assert.Equal(ModelAccessState.Error, broken.State);
        Assert.Equal("Internal error.", broken.Message);

        await Assert.ThrowsAsync<AwsSignInRequiredException>(() => Setup(r => r.Fail(["bedrock", "get-foundation-model-availability"],
            "aws: [ERROR]: Error when retrieving token from sso: Token has expired and refresh failed", 255)).Setup.CheckAccessAsync(Cohere));
    }

    // MARK: - Anthropic's use-case form

    private static readonly UseCaseForm Acme = new("Acme Robotics", "https://acme.example", "Technology", "Coding assistant for our developers");

    [Fact]
    public void TheFormIsTheJsonAwsExpects()
    {
        const string json = """{"companyName":"Acme Robotics","companyWebsite":"https://acme.example","intendedUsers":"0","industryOption":"Technology","otherIndustryOption":"","useCases":"Coding assistant for our developers"}""";
        Assert.Equal(json, Acme.ToJson());
        Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes(json)), Acme.ToBase64());
        Assert.Equal("eyJjb21wYW55TmFtZSI6IkFjbWUgUm9ib3RpY3MiLCJjb21wYW55V2Vic2l0ZSI6Imh0dHBzOi8vYWNtZS5leGFtcGxlIiwiaW50ZW5kZWRVc2VycyI6IjAiLCJpbmR1c3RyeU9wdGlvbiI6IlRlY2hub2xvZ3kiLCJvdGhlckluZHVzdHJ5T3B0aW9uIjoiIiwidXNlQ2FzZXMiOiJDb2RpbmcgYXNzaXN0YW50IGZvciBvdXIgZGV2ZWxvcGVycyJ9",
            Acme.ToBase64());
        Assert.Null(Acme.Problem);
    }

    [Fact]
    public void TheFormKeepsTextAsTyped()
    {
        var form = new UseCaseForm("  Café \"Zoë\" & Co ", "zoe.example", "Other", "Refactoring <legacy> code\nand tests", UseCaseAudience.Both, "Hospitality");
        var parsed = JsonNode.Parse(form.ToJson())!.AsObject();
        Assert.Equal("Café \"Zoë\" & Co", parsed["companyName"]!.GetValue<string>());
        Assert.Equal("https://zoe.example", parsed["companyWebsite"]!.GetValue<string>());
        Assert.Equal("2", parsed["intendedUsers"]!.GetValue<string>());
        Assert.Equal("Other", parsed["industryOption"]!.GetValue<string>());
        Assert.Equal("Hospitality", parsed["otherIndustryOption"]!.GetValue<string>());
        Assert.Equal("Refactoring <legacy> code\nand tests", parsed["useCases"]!.GetValue<string>());
        Assert.Contains("Café", form.ToJson()); // not \u-escaped
        Assert.Equal("", JsonNode.Parse((Acme with { OtherIndustry = "ignored" }).ToJson())!["otherIndustryOption"]!.GetValue<string>());
        Assert.Equal("1", JsonNode.Parse((Acme with { IntendedUsers = UseCaseAudience.External }).ToJson())!["intendedUsers"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("", "https://acme.example", "Technology", "x", "", "company")]
    [InlineData("Acme", "not a website", "Technology", "x", "", "website")]
    [InlineData("Acme", "ftp://acme.example", "Technology", "x", "", "website")]
    [InlineData("Acme", "https://acme.example", "", "x", "", "industry")]
    [InlineData("Acme", "https://acme.example", "Other", "x", "", "industry")]
    [InlineData("Acme", "https://acme.example", "Technology", " ", "", "use")]
    public void IncompleteFormsSayWhatsMissing(string company, string site, string industry, string useCases, string other, string mentions)
    {
        var problem = new UseCaseForm(company, site, industry, useCases, OtherIndustry: other).Problem;
        Assert.NotNull(problem);
        Assert.Contains(mentions, problem, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadsTheFormBack()
    {
        Assert.Equal(Acme, UseCaseForm.FromFormData(Acme.ToBase64()));
        // AWS may hand back the stored value base64-encoded once more.
        Assert.Equal(Acme, UseCaseForm.FromFormData(Convert.ToBase64String(Encoding.UTF8.GetBytes(Acme.ToBase64()))));
        Assert.Null(UseCaseForm.FromFormData("not base64 at all!"));
        Assert.Contains("Other", UseCaseForm.Industries);
    }

    [Fact]
    public async Task SubmitsTheFormAsBase64()
    {
        var (setup, runner) = Setup(r => r
            .Fail(["bedrock", "get-use-case-for-model-access"], UseCaseNotFound)
            .On(["bedrock", "put-use-case-for-model-access"], ""));
        Assert.Equal(UseCaseSubmission.Submitted, await setup.SubmitUseCaseAsync(Acme));
        var put = Assert.Single(runner.CallsTo("bedrock", "put-use-case-for-model-access"));
        Assert.Equal(["bedrock", "put-use-case-for-model-access", "--form-data", Acme.ToBase64(), "--cli-binary-format", "base64",
                      "--profile", "dsh-bedrock", "--region", "us-east-1", "--output", "json"], put.Args);
        Assert.Equal(Acme.ToJson(), Encoding.UTF8.GetString(Convert.FromBase64String(put.Args[3])));
    }

    [Fact]
    public async Task AFormAlreadyOnFileIsntSentAgain()
    {
        var (setup, runner) = Setup(r => r.On(["bedrock", "get-use-case-for-model-access"], $$"""{"formData": "{{Acme.ToBase64()}}"}"""));
        Assert.Equal(UseCaseSubmission.AlreadyOnFile, await setup.SubmitUseCaseAsync(Acme));
        Assert.Empty(runner.CallsTo("bedrock", "put-use-case-for-model-access"));
        Assert.Equal(Acme, await setup.GetUseCaseAsync());
    }

    [Fact]
    public async Task AnIncompleteFormIsntSent()
    {
        var (setup, runner) = Setup(_ => { });
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => setup.SubmitUseCaseAsync(Acme with { CompanyWebsite = "" }));
        Assert.Contains("website", ex.Message);
        Assert.Empty(runner.Calls);
    }

    // MARK: - Agreements and waiting

    [Fact]
    public async Task AcceptsAnOffer()
    {
        var (setup, runner) = Setup(r => r.On(["bedrock", "create-foundation-model-agreement"], """{"modelId": "cohere.command-r-plus-v1:0"}"""));
        await setup.AcceptAgreementAsync("cohere.command-r-plus-v1:0", "eyJvZmZlcklkIjoib2ZmZXItM2syIn0=");
        Assert.Equal(["bedrock", "create-foundation-model-agreement", "--model-id", "cohere.command-r-plus-v1:0", "--offer-token", "eyJvZmZlcklkIjoib2ZmZXItM2syIn0=",
                      "--profile", "dsh-bedrock", "--region", "us-east-1", "--output", "json"], Assert.Single(runner.Calls).Args);
    }

    [Fact]
    public async Task AnAgreementInProgressCountsAsAccepted()
    {
        var (conflict, _) = Setup(r => r.Fail(["bedrock", "create-foundation-model-agreement"],
            "aws: [ERROR]: An error occurred (ConflictException) when calling the CreateFoundationModelAgreement operation: Agreement already exists."));
        await conflict.AcceptAgreementAsync("cohere.command-r-plus-v1:0", "token");

        var (denied, _) = Setup(r => r.Fail(["bedrock", "create-foundation-model-agreement"],
            "aws: [ERROR]: An error occurred (AccessDeniedException) when calling the CreateFoundationModelAgreement operation: User: arn:aws:iam::1:user/a is not authorized to perform: aws-marketplace:Subscribe"));
        var ex = await Assert.ThrowsAsync<AwsCliException>(() => denied.AcceptAgreementAsync("cohere.command-r-plus-v1:0", "token"));
        Assert.Equal("aws-marketplace:Subscribe", ex.Error.Permission);
    }

    [Fact]
    public async Task WaitsUntilTheSubscriptionIsReady()
    {
        var clock = new JumpingClock();
        var start = clock.GetUtcNow();
        var (setup, runner) = Setup(r => r.OnSequence(["bedrock", "get-foundation-model-availability"],
            new AwsCliResult(0, Availability("NOT_AVAILABLE", entitlement: "NOT_AVAILABLE"), ""), // right after accepting
            new AwsCliResult(0, Availability("PENDING", entitlement: "NOT_AVAILABLE"), ""),
            new AwsCliResult(0, Availability("PENDING", entitlement: "NOT_AVAILABLE"), ""),
            new AwsCliResult(0, Availability(), ""))
            .On(["bedrock", "list-foundation-model-agreement-offers"], Offers), clock);
        var progress = new ListProgress<ModelAccess>();

        var access = await setup.WaitUntilReadyAsync(Cohere, progress, pollInterval: TimeSpan.FromSeconds(15));

        Assert.Equal(ModelAccessState.Ready, access.State);
        Assert.Equal([ModelAccessState.NeedsAgreement, ModelAccessState.Pending, ModelAccessState.Pending, ModelAccessState.Ready],
            progress.Items.Select(a => a.State));
        Assert.Equal(4, runner.CallsTo("bedrock", "get-foundation-model-availability").Count);
        Assert.Equal(TimeSpan.FromSeconds(45), clock.GetUtcNow() - start);
    }

    [Fact]
    public async Task StopsWaitingAtTheTimeout()
    {
        var clock = new JumpingClock();
        var start = clock.GetUtcNow();
        var (setup, runner) = Setup(r => r.On(["bedrock", "get-foundation-model-availability"], Availability("PENDING", entitlement: "NOT_AVAILABLE")), clock);
        var access = await setup.WaitUntilReadyAsync(Cohere, timeout: TimeSpan.FromSeconds(60), pollInterval: TimeSpan.FromSeconds(20));
        Assert.Equal(ModelAccessState.Pending, access.State);
        Assert.Equal(4, runner.Calls.Count); // at 0, 20, 40 and 60 seconds
        Assert.Equal(TimeSpan.FromSeconds(60), clock.GetUtcNow() - start);
        Assert.All(clock.Delays, d => Assert.Equal(TimeSpan.FromSeconds(20), d));
    }

    [Fact]
    public async Task StopsWaitingWhenTheUserMustAct()
    {
        var clock = new JumpingClock();
        var (setup, runner) = Setup(r => r.On(["bedrock", "get-foundation-model-availability"], Availability(authorization: "NOT_AUTHORIZED")), clock);
        var access = await setup.WaitUntilReadyAsync(Cohere);
        Assert.Equal(ModelAccessState.NotAuthorized, access.State);
        Assert.Single(runner.Calls);
        Assert.Empty(clock.Delays);
    }

    [Fact]
    public async Task WaitingCanBeCancelled()
    {
        using var cts = new CancellationTokenSource();
        var (setup, _) = Setup(r => r.On(["bedrock", "get-foundation-model-availability"], Availability("PENDING")), new JumpingClock());
        var progress = new ListProgress<ModelAccess> { OnReport = _ => cts.Cancel() };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => setup.WaitUntilReadyAsync(Cohere, progress, cancellationToken: cts.Token));
    }

    [Theory]
    [InlineData("P1Y", "1 year")]
    [InlineData("P12M", "12 months")]
    [InlineData("P30D", "30 days")]
    [InlineData("P1Y6M", "1 year, 6 months")]
    [InlineData("Until cancelled", "Until cancelled")]
    public void DurationsReadNaturally(string iso, string text) => Assert.Equal(text, BedrockSetup.FriendlyDuration(iso));

    // MARK: - Regions and permissions

    [Fact]
    public void OffersCommonRegions()
    {
        var regions = BedrockSetup.CommonRegions;
        Assert.Equal("us-east-1", BedrockSetup.DefaultRegion);
        Assert.Contains(regions, r => r.Code == BedrockRegions.Default);
        Assert.Equal(regions.Count, regions.Select(r => r.Code).Distinct().Count());
        foreach (var code in new[] { "us-east-1", "us-west-2", "us-east-2", "eu-central-1", "eu-west-1", "eu-west-3", "ap-northeast-1", "ap-southeast-2", "ap-south-1", "ca-central-1", "sa-east-1" })
            Assert.Contains(regions, r => r.Code == code);
        Assert.Equal("US West (Oregon) — us-west-2", regions.Single(r => r.Code == "us-west-2").Display);
        Assert.Equal("Europe (Frankfurt)", BedrockRegions.NameOf("eu-central-1"));
        Assert.Equal("il-central-1", BedrockRegions.NameOf("il-central-1"));
    }

    [Fact]
    public void ThePolicyIsValidIamJson()
    {
        using var doc = JsonDocument.Parse(BedrockPermissions.PolicyJson);
        var root = doc.RootElement;
        Assert.Equal("2012-10-17", root.GetProperty("Version").GetString());
        var actions = new List<string>();
        var sids = new HashSet<string>();
        foreach (var statement in root.GetProperty("Statement").EnumerateArray())
        {
            Assert.Equal("Allow", statement.GetProperty("Effect").GetString());
            Assert.True(sids.Add(statement.GetProperty("Sid").GetString()!));
            Assert.Matches("^[A-Za-z0-9]+$", statement.GetProperty("Sid").GetString());
            Assert.True(statement.TryGetProperty("Resource", out _));
            var action = statement.GetProperty("Action");
            actions.AddRange(action.ValueKind == JsonValueKind.Array ? action.EnumerateArray().Select(a => a.GetString()!) : [action.GetString()!]);
        }
        Assert.All(actions, a => Assert.Matches(new Regex("^[a-z0-9-]+:[A-Za-z0-9]+$"), a));
        foreach (var needed in new[]
                 {
                     "bedrock:InvokeModel", "bedrock:InvokeModelWithResponseStream", "bedrock:ListFoundationModels", "bedrock:GetFoundationModelAvailability",
                     "bedrock:ListInferenceProfiles", "bedrock:GetInferenceProfile", "bedrock:ListFoundationModelAgreementOffers",
                     "bedrock:CreateFoundationModelAgreement", "bedrock:GetUseCaseForModelAccess", "bedrock:PutUseCaseForModelAccess",
                     "bedrock:CallWithBearerToken", "aws-marketplace:Subscribe", "aws-marketplace:ViewSubscriptions", "aws-marketplace:Unsubscribe",
                     "signin:AuthorizeOAuth2Access", "signin:CreateOAuth2Token",
                 })
            Assert.Contains(needed, actions);
        Assert.Equal(actions.Count, actions.Distinct().Count());
        var marketplace = root.GetProperty("Statement").EnumerateArray().Single(s => s.GetProperty("Sid").GetString()!.Contains("Marketplace"));
        Assert.Equal("bedrock.amazonaws.com", marketplace.GetProperty("Condition").GetProperty("StringEquals").GetProperty("aws:CalledViaLast").GetString());
        Assert.Contains("whoever manages your AWS account", BedrockPermissions.Explanation);
        Assert.Contains(BedrockPermissions.SignInManagedPolicy, BedrockPermissions.Explanation);
    }
}
