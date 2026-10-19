namespace Vision.Core.Tests;

/// <summary>Guardian's models as routes nobody saved: their ids, the route a chat on one resolves to (the sign-in's token as
/// its key, the org in its header, Guardian's proxy as its address), and what happens before and after the model list
/// arrives. The same cases as GuardianLiveRoutesTests.swift in Vision for macOS.</summary>
public sealed class GuardianLiveRoutesTests
{
    private const string Address = "https://guardian.example.test";

    private static readonly IReadOnlyList<GuardianModel> Models =
    [
        new("guardian/default", "Guardian default", 1_000_000),
        new("us.anthropic.claude-opus-5-5", "Claude Opus 5.5", 1_000_000, 48_000, InteractiveOnly: true),
    ];

    [Fact]
    public void AGuardianModelsRouteIdIsTheOneASavedRouteForItHas()
    {
        Assert.Equal("Guardian|guardian/default", GuardianRoutes.LiveRouteId("guardian/default"));
        Assert.Equal(GuardianRoutes.Route(Address, new GuardianModel("guardian/default"), null).RouteId, GuardianRoutes.LiveRouteId("guardian/default"));
        Assert.True(GuardianRoutes.IsLiveRouteId("Guardian|guardian/fast"));
        Assert.False(GuardianRoutes.IsLiveRouteId("Guardian|"));
        Assert.False(GuardianRoutes.IsLiveRouteId("GLM Coding Plan|glm-5.3"));
        Assert.False(GuardianRoutes.IsLiveRouteId(null));
        Assert.Equal("us.anthropic.claude-opus-5-5", GuardianRoutes.ModelOfRouteId("Guardian|us.anthropic.claude-opus-5-5"));
        Assert.Null(GuardianRoutes.ModelOfRouteId("Other|guardian/default"));
    }

    [Fact]
    public void ALiveRouteCarriesTheSignInsTokenOrgAndTheModelsFacts()
    {
        var route = GuardianRoutes.LiveRoute("Guardian|us.anthropic.claude-opus-5-5", Address + "/", Models, "org-503", "tok-1")!;
        Assert.Equal(ProviderKind.OpenAICompat, route.Kind);
        Assert.Equal(Address + "/api/harness/v1", route.BaseUrl);
        Assert.Equal("us.anthropic.claude-opus-5-5", route.Model);
        Assert.Equal("tok-1", route.ApiKey);
        Assert.Equal("org-503", GuardianRoutes.OrgOf(route));
        Assert.Equal(1_000_000, route.ContextWindow);
        Assert.True(route.IsInteractiveOnly);
        Assert.True(GuardianRoutes.IsGuardian(route));
        Assert.False(route.IsPrivateRoute); // masked by the shield, like any online service
        // A new sign-in is a new token on the very same route.
        Assert.Equal("tok-2", GuardianRoutes.LiveRoute(route.RouteId, Address, Models, "org-503", "tok-2")!.ApiKey);
    }

    [Fact]
    public void BeforeTheListArrivesAnyGuardianModelResolvesAfterwardsOnlyOfferedOnes()
    {
        Assert.NotNull(GuardianRoutes.LiveRoute("Guardian|guardian/fast", Address, null, null, "tok"));
        Assert.Null(GuardianRoutes.LiveRoute("Guardian|guardian/fast", Address, Models, null, "tok"));
        Assert.Null(GuardianRoutes.LiveRoute("Spark|qwen3.8", Address, null, null, "tok"));
        Assert.Null(GuardianRoutes.OrgOf(GuardianRoutes.LiveRoute("Guardian|guardian/default", Address, Models, null, "tok")!));
    }

    [Fact]
    public void BeforeTheListArrivesAModelInTheKeptListKeepsItsFacts()
    {
        // Just after launch (or a sign-in), the list kept from last time stands in: an interactive-only model stays one, so a
        // task list resumed at launch can't run on it unattended before Guardian answers.
        var kept = GuardianRoutes.LiveRoute("Guardian|us.anthropic.claude-opus-5-5", Address, Models, null, "tok", known: false)!;
        Assert.True(kept.IsInteractiveOnly);
        Assert.Equal(1_000_000, kept.ContextWindow);
        // An id the kept list doesn't have still resolves (bare) until the list arrives — and not afterwards.
        var bare = GuardianRoutes.LiveRoute("Guardian|guardian/fast", Address, Models, null, "tok", known: false)!;
        Assert.False(bare.IsInteractiveOnly);
        Assert.Null(bare.ContextWindow);
        Assert.Null(GuardianRoutes.LiveRoute("Guardian|guardian/fast", Address, Models, null, "tok", known: true));
        Assert.NotNull(GuardianRoutes.LiveRoute("Guardian|guardian/fast", Address, [], null, "tok", known: false));
    }

    [Fact]
    public void TheMenuNamesAModelByItsLabel()
    {
        Assert.Equal("Claude Opus 5.5 (us.anthropic.claude-opus-5-5)", GuardianRoutes.MenuTitle(Models[1]));
        Assert.Equal("guardian/fast", GuardianRoutes.MenuTitle(new GuardianModel("guardian/fast")));
        Assert.Equal("Sign in to Guardian to use this model.", GuardianRoutes.SignInNeeded);
    }
}
