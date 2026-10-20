using System.Net.Http.Headers;

namespace Vision.Core;

// MARK: - Guardian as a model route
//
// A Guardian route is an ordinary OpenAI-compatible route: base URL <address>/api/harness/v1, the Guardian session
// token as its key (in Credential Manager like any key), and x-guardian-org in its headers when an org is picked.
// So the chat client, the context probe, failover and the per-chat route switch all work on it unchanged — and the
// shared-ETA client finds Guardian from it (GuardianEndpoint.FromRoute reads the same URL and header).

public static class GuardianRoutes
{
    public const string ProxyPath = "/api/harness/v1";
    public const string OrgHeader = "x-guardian-org";
    /// <summary>The name new Guardian routes get (a second model on it becomes "Guardian" too: route ids are name|model).</summary>
    public const string RouteName = "Guardian";

    /// <summary>A route that goes through a Guardian's model proxy.</summary>
    public static bool IsGuardian(ProviderProfile route) =>
        route.Kind != ProviderKind.Bedrock && route.BaseUrl.Contains(ProxyPath, StringComparison.OrdinalIgnoreCase);

    /// <summary>The Guardian deployment a route goes through (the part before /api/harness/v1), or null.</summary>
    public static string? AddressOf(ProviderProfile route)
    {
        if (!IsGuardian(route)) return null;
        var at = route.BaseUrl.IndexOf(ProxyPath, StringComparison.OrdinalIgnoreCase);
        return GuardianClient.NormalizeAddress(route.BaseUrl[..at]);
    }

    /// <summary>The same deployment, compared without scheme case or a trailing slash.</summary>
    public static bool SameAddress(string? a, string? b) =>
        GuardianClient.NormalizeAddress(a) is { } x && GuardianClient.NormalizeAddress(b) is { } y && string.Equals(x, y, StringComparison.OrdinalIgnoreCase);

    /// <summary>A new route for <paramref name="model"/> on <paramref name="address"/>'s proxy. It isn't private (Guardian is
    /// an online service: the sensitive-data shield masks what goes to it), and an interactive-only model is marked so.</summary>
    public static ProviderProfile Route(string address, GuardianModel model, string? orgId)
    {
        var route = new ProviderProfile(ProviderKind.OpenAICompat, RouteName, (GuardianClient.NormalizeAddress(address) ?? address.TrimEnd('/')) + ProxyPath, model.Id)
        {
            ContextWindow = model.ContextWindow,
            InteractiveOnly = model.InteractiveOnly == true ? true : null,
        };
        SetOrg(route, orgId);
        return route;
    }

    /// <summary>Point a route at an org (null: the account's default org).</summary>
    public static void SetOrg(ProviderProfile route, string? orgId)
    {
        var headers = route.CustomHeaders is null ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(route.CustomHeaders, StringComparer.OrdinalIgnoreCase);
        headers.Remove(OrgHeader);
        if (orgId is { Length: > 0 }) headers[OrgHeader] = orgId;
        route.CustomHeaders = headers.Count == 0 ? null : headers;
    }

    public static string? OrgOf(ProviderProfile route) =>
        route.CustomHeaders?.FirstOrDefault(h => string.Equals(h.Key, OrgHeader, StringComparison.OrdinalIgnoreCase)).Value;

    // MARK: Guardian's models in every chat's model menu
    //
    // Signed in, the account's models are routes without being saved: each is "Guardian|<model id>" (the id a saved Guardian
    // route for that model has too, so a chat on one keeps working either way), made on demand from the model list with the
    // sign-in's token and org — a new sign-in renews every one of them at once, and signing out takes them all away.

    /// <summary>What a chat on a Guardian model says while Guardian is signed out.</summary>
    public const string SignInNeeded = "Sign in to Guardian to use this model.";

    /// <summary>The route id of Guardian's model <paramref name="modelId"/>.</summary>
    public static string LiveRouteId(string modelId) => $"{RouteName}|{modelId}";

    /// <summary>A route id that names one of Guardian's models ("Guardian|guardian/default").</summary>
    public static bool IsLiveRouteId(string? routeId) =>
        routeId is { } id && id.StartsWith(RouteName + "|", StringComparison.Ordinal) && id.Length > RouteName.Length + 1;

    /// <summary>The model id in a Guardian route id, or null.</summary>
    public static string? ModelOfRouteId(string? routeId) => IsLiveRouteId(routeId) ? routeId![(RouteName.Length + 1)..] : null;

    /// <summary>The route for Guardian's model <paramref name="routeId"/> on <paramref name="address"/>, with the sign-in's
    /// <paramref name="token"/> as its key. Null when the id isn't a Guardian model id, or — once the account's models are known
    /// (<paramref name="known"/>, with <paramref name="models"/> not null) — names one the account no longer offers. Before the
    /// list arrives (just after launch or a sign-in) any Guardian model id resolves, so a chat on one doesn't fall back to
    /// another model while the list loads; one in the list kept from last time (<paramref name="models"/>) keeps that entry's
    /// facts meanwhile — its window, and whether it is interactive-only, so a task list resumed at launch can't run on it
    /// unattended before Guardian answers.</summary>
    /// <param name="subagents">Guardian's models enlisted for subagents (model id → subagents at a time, 0 = the default):
    /// such a model's route is a subagent server.</param>
    public static ProviderProfile? LiveRoute(string? routeId, string address, IReadOnlyList<GuardianModel>? models, string? orgId, string token,
        bool known = true, IReadOnlyDictionary<string, int>? subagents = null)
    {
        if (ModelOfRouteId(routeId) is not { } id) return null;
        var model = models?.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.Ordinal))
            ?? (known && models is not null ? null : new GuardianModel(id));
        if (model is null) return null;
        var route = Route(address, model, orgId);
        route.ApiKey = token;
        if (subagents is not null && subagents.TryGetValue(id, out var parallel))
        {
            route.SubagentWorker = true;
            route.SubagentParallel = parallel > 0 ? parallel : null;
        }
        return route;
    }

    /// <summary>The menu's name for a Guardian model: its label with the id when they differ ("Claude Opus 5.5
    /// (claude-opus-5-5)"), else its id — as Vision for macOS names it.</summary>
    public static string MenuTitle(GuardianModel model) => model.Title;
}

/// <summary>Refusals from Guardian's model proxy, said in Guardian's words. The chat shows the sentence (never "HTTP 402"),
/// and <see cref="Reported"/> lets the app show it as a dialog too.</summary>
public static class GuardianProxyRefusals
{
    /// <summary>Raised (on whatever thread the call ran on) for every 401/403/402/429 from a Guardian route.</summary>
    public static event Action<ProviderProfile, GuardianError>? Reported;

    /// <summary>The refusal a Guardian route's error reply carries, or null when the route isn't Guardian's or the status
    /// isn't one of the four. Reports it.</summary>
    public static GuardianError? From(ProviderProfile route, int status, string body, HttpResponseHeaders? headers)
    {
        if (status is not (401 or 402 or 403 or 429) || !GuardianRoutes.IsGuardian(route)) return null;
        var error = GuardianError.FromResponse(status, body, headers);
        try
        {
            Reported?.Invoke(route, error);
        }
        catch (Exception)
        {
            // A listener's failure is not the chat's.
        }
        return error;
    }

    /// <summary>What the chat client throws for it: Guardian's sentence; retried only when Guardian asked Vision to slow
    /// down (429 rate_limited) — a budget, a missing permission or a revoked sign-in can't be waited out.</summary>
    public static LlmException ToLlmException(GuardianError error, string body)
    {
        // The Mac's one line: "Guardian spend limit reached. <Guardian's sentence> Spend today: $5.01 of $5.00 — resets …".
        var message = GuardianRefusalText.For(error).OneLine();
        var retryable = error.Status == 429 && error.Code is null or "rate_limited";
        return LlmException.Rejected(error.Status, message, body, permanent: !retryable);
    }
}
