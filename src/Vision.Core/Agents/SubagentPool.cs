using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vision.Core;

// MARK: - A chat's subagent pool
//
// The model a chat uses is that chat's *primary*: it leads, and it may hand side tasks to every other route the person
// enlisted ("Subagent" in Settings › AI Models) — a second Spark, Amazon Bedrock, a plan, a Guardian model, Claude Code —
// except what runs on the chat's own machine, what a Sensitive chat may not use, and what an unattended run may not use.
// Build works that out from plain facts about each route (so Vision for macOS and the shared fixtures decide exactly the
// same way); Describe is the stable catalogue the agent tool's description carries (no load, so the prompt cache holds);
// LoadLine is how busy each server is now, which goes in tool results instead.
//
// Pure values and functions, the same words as Vision for macOS (SubagentPool.swift; tests/…/Fixtures/SubagentPool).

/// <summary>What kind of route, in words both Vision apps share (the fixtures use them).</summary>
[JsonConverter(typeof(SubagentKindJsonConverter))]
public enum SubagentKind { OpenAICompatible, Ollama, LmStudio, OpenAI, OpenRouter, Bedrock, Guardian, Anthropic, ClaudeCode }

/// <summary>What a server costs: your own box, a subscription, per token, or your organisation's Guardian account.</summary>
public enum SubagentTier { Local, Subscription, Api, Org }

/// <summary>A name-based hint at how quick a model is (not measured).</summary>
public enum SubagentSpeed { Fast, Standard, Thorough }

public static class SubagentWords
{
    public static string Word(this SubagentKind kind) => kind switch
    {
        SubagentKind.OpenAICompatible => "openai-compatible",
        SubagentKind.Ollama => "ollama",
        SubagentKind.LmStudio => "lmstudio",
        SubagentKind.OpenAI => "openai",
        SubagentKind.OpenRouter => "openrouter",
        SubagentKind.Bedrock => "bedrock",
        SubagentKind.Guardian => "guardian",
        SubagentKind.Anthropic => "anthropic",
        _ => "claude-code",
    };

    public static SubagentKind? KindOf(string? word) =>
        Enum.GetValues<SubagentKind>().Cast<SubagentKind?>().FirstOrDefault(k => string.Equals(k!.Value.Word(), word, StringComparison.Ordinal));

    public static string Word(this SubagentTier tier) => tier.ToString().ToLowerInvariant();

    public static string Word(this SubagentSpeed speed) => speed.ToString().ToLowerInvariant();

    /// <summary>The pool's word for a configured route: Claude Code and Guardian by their address, the rest by kind.</summary>
    public static SubagentKind KindOf(ProviderProfile route)
    {
        if (ClaudeCodeRoutes.IsClaudeCode(route)) return SubagentKind.ClaudeCode;
        if (GuardianRoutes.IsGuardian(route)) return SubagentKind.Guardian;
        return route.Kind switch
        {
            ProviderKind.Bedrock => SubagentKind.Bedrock,
            ProviderKind.Anthropic => SubagentKind.Anthropic,
            ProviderKind.Ollama => SubagentKind.Ollama,
            ProviderKind.LmStudio => SubagentKind.LmStudio,
            ProviderKind.OpenAI => SubagentKind.OpenAI,
            ProviderKind.OpenRouter => SubagentKind.OpenRouter,
            _ => SubagentKind.OpenAICompatible,
        };
    }
}

public sealed class SubagentKindJsonConverter : JsonConverter<SubagentKind>
{
    public override SubagentKind Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        SubagentWords.KindOf(reader.GetString()) ?? throw new JsonException($"Unknown route kind “{reader.GetString()}”.");

    public override void Write(Utf8JsonWriter writer, SubagentKind value, JsonSerializerOptions options) => writer.WriteStringValue(value.Word());
}

/// <summary>One route as the pool sees it: every fact the decision needs, already worked out by the app.</summary>
public sealed record SubagentRouteFacts
{
    private readonly string? _routeId;

    /// <summary>The route's identity ("Name|model" — its configured model, not the one a server serves now).</summary>
    [JsonPropertyName("routeID")]
    public string RouteId { get => _routeId ?? Name + "|" + Model; init => _routeId = value; }
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    /// <summary>The model it serves: the route's, or the last one its server reported (sticky).</summary>
    [JsonPropertyName("model")] public string Model { get; init; } = "";
    [JsonPropertyName("kind")] public SubagentKind Kind { get; init; }
    [JsonPropertyName("baseURL")] public string BaseUrl { get; init; } = "";
    /// <summary>Switched on and flagged "Use for subagents" (a Guardian model: ticked in the Guardian map).</summary>
    [JsonPropertyName("enlisted")] public bool Enlisted { get; init; } = true;
    /// <summary>The route's Private switch (else its address): your own hardware. Guardian and Bedrock count as private
    /// whatever this says; Claude Code never does.</summary>
    [JsonPropertyName("private")] public bool Private { get; init; }
    [JsonPropertyName("interactiveOnly")] public bool InteractiveOnly { get; init; }
    /// <summary>Subagents at a time (null = the default: 2 for a subagent server, 4 for the chat's own model).</summary>
    [JsonPropertyName("parallel")] public int? Parallel { get; init; }
    /// <summary>Its context window in tokens (override, else probed, else the built-in table), null when unknown.</summary>
    [JsonPropertyName("context")] public int? Context { get; init; }
    [JsonPropertyName("images")] public bool Images { get; init; } = true;
    /// <summary>The subscription plan's provider when the base URL is a known plan's ("Kimi", "Z.ai", "DeepSeek" …).</summary>
    [JsonPropertyName("planProvider")] public string? PlanProvider { get; init; }
    /// <summary>That plan (its region, else the plan itself) is a monthly subscription; null when no plan is recognised.</summary>
    [JsonPropertyName("planSubscription")] public bool? PlanSubscription { get; init; }
    /// <summary>The base URL's host is your own (LAN, loopback, Tailscale …).</summary>
    [JsonPropertyName("privateAddress")] public bool PrivateAddress { get; init; }
    /// <summary>The host is not a known hosted API (a server you run yourself).</summary>
    [JsonPropertyName("selfHosted")] public bool SelfHosted { get; init; }

    /// <summary>The configured model (the part of the identity after the name).</summary>
    [JsonIgnore]
    public string ConfiguredModel => RouteId.IndexOf('|') is var bar and >= 0 ? RouteId[(bar + 1)..] : Model;

    /// <summary>How the route is named: its name, else its configured model.</summary>
    [JsonIgnore]
    public string Label => Name.Length == 0 ? ConfiguredModel : Name;

    /// <summary>The facts about a configured route (or one of Guardian's live models). <paramref name="model"/>,
    /// <paramref name="contextWindow"/> and <paramref name="images"/> are what the app knows now: the model its server last
    /// reported, the window it settled on, whether it takes pictures.</summary>
    public static SubagentRouteFacts From(ProviderProfile route, bool enlisted, string? model = null, int? contextWindow = null, bool images = true)
    {
        var kind = SubagentWords.KindOf(route);
        var plan = SubscriptionPlans.ForBaseUrl(route.BaseUrl);
        return new SubagentRouteFacts
        {
            RouteId = route.RouteId,
            Name = route.Name,
            Model = string.IsNullOrEmpty(model) ? route.Model : model,
            Kind = kind,
            BaseUrl = route.BaseUrl,
            Enlisted = enlisted,
            Private = route.IsPrivateRoute,
            InteractiveOnly = route.IsInteractiveOnly,
            Parallel = route.SubagentParallel,
            Context = contextWindow,
            Images = kind != SubagentKind.ClaudeCode && images,
            PlanProvider = plan?.Plan.Provider,
            PlanSubscription = plan is { } known ? known.Region.IsSubscription ?? known.Plan.IsSubscription : null,
            PrivateAddress = RoutePrivacy.IsPrivateAddress(route.BaseUrl),
            SelfHosted = route.IsSelfHosted,
        };
    }
}

/// <summary>A chat's subagent servers: the routes its own model (the primary) may hand side tasks to, then itself.</summary>
public sealed class SubagentPool
{
    /// <summary>One server a chat's model may hand a task to — what the catalogue says about it. No load numbers: those
    /// change all the time and would break the prompt cache (see <see cref="LoadLine"/>).</summary>
    public sealed record Entry
    {
        /// <summary>What the model calls it: the handle ("dgx-spark"), or "primary" for the chat's own model.</summary>
        public required string Id { get; init; }
        public required string RouteId { get; init; }
        /// <summary>The route's name ("DGX Spark").</summary>
        public required string Label { get; init; }
        public required string Provider { get; init; }
        public required string Model { get; init; }
        /// <summary>"256K", "1M"; null when unknown.</summary>
        public string? Context { get; init; }
        public bool IsPrivate { get; init; }
        public SubagentTier Tier { get; init; }
        public SubagentSpeed Speed { get; init; }
        public bool Images { get; init; }
        /// <summary>It runs its own agent (Claude Code: one `claude -p` job in the chat's folder).</summary>
        public bool OwnAgent { get; init; }
        public int Parallel { get; init; }
        public required string Machine { get; init; }
        public SubagentKind Kind { get; init; }
        public bool InteractiveOnly { get; init; }
        public bool IsPrimary { get; init; }

        public bool IsGuardian => Kind == SubagentKind.Guardian;
        public bool IsClaudeCode => Kind == SubagentKind.ClaudeCode;
    }

    /// <summary>An enlisted route this chat can't use, and why ("this chat's own machine").</summary>
    public sealed record DroppedRoute(string Id, string RouteId, string Label, string Reason);

    /// <summary>How busy one server is now.</summary>
    public sealed record Load(int InFlight, bool Healthy = true, double? RetryInSeconds = null);

    /// <summary>The servers, in Settings order, then the chat's own model ("primary") last.</summary>
    public IReadOnlyList<Entry> Entries { get; }
    /// <summary>Enlisted routes left out of this chat's pool.</summary>
    public IReadOnlyList<DroppedRoute> Dropped { get; }
    /// <summary>Subagents may use the chat's own model when the others are busy.</summary>
    public bool Overflow { get; }
    /// <summary>The chat runs on Claude Code, which uses its own subagents: no pool.</summary>
    public bool ChatOwnAgent { get; }

    public SubagentPool(IReadOnlyList<Entry>? entries = null, IReadOnlyList<DroppedRoute>? dropped = null, bool overflow = true, bool chatOwnAgent = false)
    {
        Entries = entries ?? [];
        Dropped = dropped ?? [];
        Overflow = overflow;
        ChatOwnAgent = chatOwnAgent;
        WorkerRouteIds = new HashSet<string>(Entries.Where(e => !e.IsPrimary).Select(e => e.RouteId), StringComparer.Ordinal);
    }

    public IReadOnlyList<Entry> Workers => Entries.Where(e => !e.IsPrimary).ToList();
    public Entry? Primary => Entries.FirstOrDefault(e => e.IsPrimary);
    public bool HasWorkers => Entries.Any(e => !e.IsPrimary);

    /// <summary>The route ids of the servers besides the chat's own (what the fleet may lease for this chat).</summary>
    public IReadOnlySet<string> WorkerRouteIds { get; }

    /// <summary>Changes exactly when what the model is told about the pool (or what it may pick) changes — never with load —
    /// so the chat's engine, and the prompt cache with it, is rebuilt only then.</summary>
    public string Signature
    {
        get
        {
            var text = new StringBuilder();
            text.Append(Overflow ? "o" : "-").Append(ChatOwnAgent ? "c" : "-");
            foreach (var e in Entries)
                text.Append('\n').Append(string.Join("|", e.Id, e.RouteId, e.Label, e.Provider, e.Model, e.Context, e.IsPrivate, e.Tier, e.Speed,
                    e.Images, e.OwnAgent, e.Parallel, e.Machine, e.Kind, e.InteractiveOnly, e.IsPrimary));
            foreach (var d in Dropped) text.Append("\nx|").Append(d.Id).Append('|').Append(d.RouteId).Append('|').Append(d.Reason);
            return text.ToString();
        }
    }

    // MARK: Building a chat's pool

    /// <summary>Why an enlisted route is left out (the same words as the Mac; "&lt;handle&gt; is &lt;reason&gt;" reads as a sentence).</summary>
    public static class Reason
    {
        public const string OwnMachine = "this chat's own machine";
        public const string GuardianSignedOut = "on Guardian, which is signed out";
        public const string Sensitive = "not available in a Sensitive chat";
        public const string Unattended = "for interactive use only, and this run is unattended";
        public static string SameMachine(string handle) => $"the same machine as {handle}";
    }

    /// <summary>The handle of the chat's own model.</summary>
    public const string PrimaryId = "primary";

    /// <summary>The pool of a chat on <paramref name="chat"/>: the enlisted <paramref name="routes"/> (every route in Settings
    /// order, then Guardian's live models in Guardian's order — handles are worked out over all of them, so a handle is the
    /// same in every chat) that this chat may use, then its own model as "primary". A chat on Claude Code has none (it runs
    /// Claude Code's own agent).</summary>
    public static SubagentPool Build(IReadOnlyList<SubagentRouteFacts> routes, SubagentRouteFacts? chat, bool guardianSignedIn, bool sensitive,
                                     bool shieldOn, bool unattended, bool overflow)
    {
        if (chat is null) return new SubagentPool(overflow: overflow);
        if (chat.Kind == SubagentKind.ClaudeCode) return new SubagentPool(overflow: overflow, chatOwnAgent: true);
        var handles = Handles(routes);
        var chatMachine = Machine(chat);
        var kept = new List<Entry>();
        var dropped = new List<DroppedRoute>();
        var machines = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < routes.Count; index++)
        {
            var route = routes[index];
            if (!route.Enlisted) continue;
            var handle = handles[index];
            var where = Machine(route);
            string? reason;
            if (where == chatMachine) reason = Reason.OwnMachine;
            else if (route.Kind == SubagentKind.Guardian && !guardianSignedIn) reason = Reason.GuardianSignedOut;
            else if (sensitive && !Admission(route, shieldOn).IsAllowed) reason = Reason.Sensitive;
            else if (unattended && route.InteractiveOnly) reason = Reason.Unattended;
            else if (machines.TryGetValue(where, out var first)) reason = Reason.SameMachine(first);
            else reason = null;
            if (reason is not null)
            {
                dropped.Add(new DroppedRoute(handle, route.RouteId, route.Label, reason));
            }
            else
            {
                machines[where] = handle;
                kept.Add(EntryFor(route, handle, primary: false));
            }
        }
        if (!(unattended && chat.InteractiveOnly)) kept.Add(EntryFor(chat, PrimaryId, primary: true));
        return new SubagentPool(kept, dropped, overflow);
    }

    /// <summary>One pool entry for a route.</summary>
    public static Entry EntryFor(SubagentRouteFacts route, string id, bool primary) => new()
    {
        Id = id,
        RouteId = route.RouteId,
        Label = route.Label,
        Provider = Provider(route.Kind, route.PlanProvider),
        Model = route.Model,
        Context = route.Context is { } tokens ? ContextLabel(tokens) : null,
        IsPrivate = Admission(route, shieldOn: false) is RouteAdmission.Private,
        Tier = Tier(route.Kind, route.PlanSubscription, route.PrivateAddress),
        Speed = Speed(route.Model),
        Images = route.Kind != SubagentKind.ClaudeCode && route.Images,
        OwnAgent = route.Kind == SubagentKind.ClaudeCode,
        Parallel = Math.Clamp(route.Parallel ?? (primary ? 4 : 2), 1, 16),
        Machine = Machine(route),
        Kind = route.Kind,
        InteractiveOnly = route.InteractiveOnly,
        IsPrimary = primary,
    };

    /// <summary>How a Sensitive chat may use the route (the sensitivity policy's own rule).</summary>
    public static RouteAdmission Admission(SubagentRouteFacts route, bool shieldOn) =>
        SensitivityPolicy.Admission(route.Label, readsFilesDirectly: route.Kind == SubagentKind.ClaudeCode,
            guardian: route.Kind == SubagentKind.Guardian, bedrock: route.Kind == SubagentKind.Bedrock, isPrivate: route.Private, shieldOn: shieldOn);

    // MARK: Labels

    /// <summary>"Ollama", "Amazon Bedrock", "Kimi" (a recognised plan), "OpenAI-compatible server".</summary>
    public static string Provider(SubagentKind kind, string? planProvider) => kind switch
    {
        SubagentKind.Guardian => "Guardian",
        SubagentKind.ClaudeCode => "Claude Code",
        SubagentKind.Bedrock => "Amazon Bedrock",
        SubagentKind.Anthropic => "Anthropic API",
        SubagentKind.Ollama => "Ollama",
        SubagentKind.LmStudio => "LM Studio",
        SubagentKind.OpenRouter => "OpenRouter",
        _ => planProvider is { Length: > 0 } ? planProvider : kind == SubagentKind.OpenAI ? "OpenAI" : "OpenAI-compatible server",
    };

    /// <summary>local / subscription / api / org.</summary>
    public static SubagentTier Tier(SubagentKind kind, bool? planSubscription, bool privateAddress)
    {
        if (kind == SubagentKind.ClaudeCode) return SubagentTier.Subscription;
        if (kind == SubagentKind.Guardian) return SubagentTier.Org;
        if (planSubscription == true) return SubagentTier.Subscription;
        if (kind != SubagentKind.Bedrock && privateAddress) return SubagentTier.Local;
        return SubagentTier.Api;
    }

    private static readonly string[] FastTokens = ["haiku", "flash", "mini", "nano", "lite", "air", "turbo", "instant", "fast", "small", "1.5b", "3b", "4b", "7b", "8b"];
    private static readonly string[] ThoroughTokens = ["opus", "fable", "-pro", "pro-", "-max", "reasoner", "thinking"];

    /// <summary>fast / standard / thorough, from the model id's name.</summary>
    public static SubagentSpeed Speed(string model)
    {
        var id = model.ToLowerInvariant();
        if (FastTokens.Any(t => id.Contains(t, StringComparison.Ordinal))) return SubagentSpeed.Fast;
        if (ThoroughTokens.Any(t => id.Contains(t, StringComparison.Ordinal))) return SubagentSpeed.Thorough;
        return SubagentSpeed.Standard;
    }

    /// <summary>262144 → "256K", 200000 → "200K", 1048576 → "1M"; null for nothing.</summary>
    public static string? ContextLabel(int tokens)
    {
        if (tokens <= 0) return null;
        if (tokens >= 1_000_000) return "1M";
        if (tokens % 1024 == 0) return (tokens / 1024).ToString(CultureInfo.InvariantCulture) + "K";
        return (tokens / 1000).ToString(CultureInfo.InvariantCulture) + "K";
    }

    /// <summary>What counts as one machine: a self-hosted OpenAI-style server is its base URL (a Spark serves one model at a
    /// time); everything else — Bedrock, Guardian, the Anthropic API, Claude Code, any hosted API — is its own route.</summary>
    public static string Machine(SubagentRouteFacts route) => Machine(route.Kind, route.BaseUrl, route.RouteId, route.SelfHosted);

    public static string Machine(SubagentKind kind, string baseUrl, string routeId, bool selfHosted) =>
        kind is SubagentKind.OpenAICompatible or SubagentKind.Ollama or SubagentKind.LmStudio or SubagentKind.OpenAI or SubagentKind.OpenRouter && selfHosted
            ? baseUrl.Trim().ToLowerInvariant().Trim('/')
            : "route:" + routeId;

    // MARK: Handles

    /// <summary>Lower case a–z and 0–9, every other run one "-", trimmed, at most <paramref name="limit"/> characters.</summary>
    public static string Slug(string text, int limit = 32)
    {
        var output = new StringBuilder();
        var gap = false;
        foreach (var c in text.ToLowerInvariant())
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                if (gap && output.Length > 0) output.Append('-');
                gap = false;
                output.Append(c);
            }
            else
            {
                gap = true;
            }
        }
        var capped = output.Length > limit ? output.ToString(0, limit) : output.ToString();
        return capped.TrimEnd('-');
    }

    /// <summary>The handle of every route, in order: its name as a slug ("dgx-spark"); routes whose slugs clash all take the
    /// long form, name and model ("guardian-claude-sonnet-5-5"); a clash left after that gets "-2", "-3" in order. "primary"
    /// is the chat's own model, so a route that would be called that is "primary-route".</summary>
    public static IReadOnlyList<string> Handles(IReadOnlyList<SubagentRouteFacts> routes)
    {
        static string Tidy(string slug) => slug.Length == 0 ? "route" : slug == PrimaryId ? "primary-route" : slug;
        var bases = routes.Select(r => Tidy(Slug(r.Label))).ToList();
        var counts = bases.GroupBy(b => b, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var candidates = routes.Select((route, index) => counts[bases[index]] > 1 ? Tidy(Slug(route.Label + " " + route.ConfiguredModel, 48)) : bases[index]).ToList();
        var used = new HashSet<string>(StringComparer.Ordinal);
        var handles = new List<string>();
        foreach (var candidate in candidates)
        {
            var handle = candidate;
            for (var n = 2; used.Contains(handle); n++) handle = $"{candidate}-{n}";
            used.Add(handle);
            handles.Add(handle);
        }
        return handles;
    }

    // MARK: What the model is told

    /// <summary>The heading of the catalogue in the agent tool's description.</summary>
    public const string CatalogueHeader =
        "Subagent servers — set `server` to one of these ids to choose where a task runs, or leave it out and Vision picks the least busy:";

    /// <summary>Its last sentence.</summary>
    public const string LoadNote = "How busy each server is comes back with every agent and delegate result, and from agent_status.";

    /// <summary>The catalogue: one line per server, then the privacy guidance. Stable — no load, health or counts — so the tool
    /// description (and the prompt cache) only changes when the pool really does. Null when there is no server besides the
    /// chat's own model.</summary>
    public static string? Describe(SubagentPool? pool)
    {
        if (pool is not { HasWorkers: true }) return null;
        var lines = new List<string> { CatalogueHeader };
        lines.AddRange(pool.Entries.Select(e => Line(e, pool.Overflow)));
        lines.Add(pool.Entries.Any(e => !e.IsPrivate)
            ? SubagentServers.Guidance + " A task that needs sensitive data gets sensitive: true, so it runs only on a private server. " + LoadNote
            : "All of them are private. " + LoadNote);
        return string.Join("\n", lines);
    }

    /// <summary>"- dgx-spark: OpenAI-compatible server · qwen3-coder-30b · 256K context · private · local · fast · 2 at a time"</summary>
    public static string Line(Entry entry, bool overflow)
    {
        if (entry.IsPrimary)
            return $"- {PrimaryId}: this chat's own model ({entry.Model} on {entry.Label}) · " +
                   (overflow ? "used when the others are busy" : "used only when you choose it");
        var text = new StringBuilder($"- {entry.Id}: {entry.Provider} · {entry.Model}");
        if (entry.Context is { } context) text.Append($" · {context} context");
        text.Append(" · ").Append(entry.IsPrivate ? "private" : "not private");
        text.Append($" · {entry.Tier.Word()} · {entry.Speed.Word()}");
        if (entry.Images) text.Append(" · images");
        if (entry.OwnAgent) text.Append(" · runs its own agent in this chat's folder");
        text.Append($" · {entry.Parallel.ToString(CultureInfo.InvariantCulture)} at a time");
        return text.ToString();
    }

    /// <summary>"Subagent servers now: dgx-spark 1/2 busy · claude-code-sonnet 0/2 busy, not answering (back in 45s) · primary
    /// 0/4 busy." — for tool results, never descriptions. <paramref name="loads"/> is keyed by entry id; null without servers
    /// besides the chat's own model.</summary>
    public static string? LoadLine(SubagentPool? pool, IReadOnlyDictionary<string, Load> loads)
    {
        if (pool is not { HasWorkers: true }) return null;
        var parts = pool.Entries.Select(entry =>
        {
            var load = loads.GetValueOrDefault(entry.Id) ?? new Load(0);
            var part = $"{entry.Id} {load.InFlight.ToString(CultureInfo.InvariantCulture)}/{entry.Parallel.ToString(CultureInfo.InvariantCulture)} busy";
            if (!load.Healthy)
            {
                part += ", not answering";
                if (load.RetryInSeconds is > 0 and var retry) part += $" (back in {RetryWords(retry)})";
            }
            return part;
        });
        return "Subagent servers now: " + string.Join(" · ", parts) + ".";
    }

    /// <summary>45 → "45s", 900 → "15m", 7200 → "2h" (rounded up).</summary>
    public static string RetryWords(double seconds)
    {
        var s = (int)Math.Ceiling(seconds);
        if (s < 120) return s.ToString(CultureInfo.InvariantCulture) + "s";
        var m = (s + 59) / 60;
        if (m < 120) return m.ToString(CultureInfo.InvariantCulture) + "m";
        return ((m + 59) / 60).ToString(CultureInfo.InvariantCulture) + "h";
    }

    // MARK: The server argument

    /// <summary>What a <c>server</c> argument names: one of the pool's entries, or the error the model gets back.</summary>
    public sealed record Choice(Entry? Entry, string? Error);

    /// <summary>Match a <c>server</c> argument: a handle (any case), then an exact route id, then "primary", then a part of one
    /// handle or name that fits only one server. Anything else is an error that says what there is — before anything runs.</summary>
    public static Choice Match(string raw, SubagentPool pool)
    {
        var value = raw.Trim();
        var workers = pool.Workers;
        if (workers.FirstOrDefault(e => string.Equals(e.Id, value, StringComparison.OrdinalIgnoreCase)) is { } byHandle) return new Choice(byHandle, null);
        if (pool.Entries.FirstOrDefault(e => e.RouteId == value) is { } byRoute) return new Choice(byRoute, null);
        if (string.Equals(value, PrimaryId, StringComparison.OrdinalIgnoreCase) && pool.Primary is { } primary) return new Choice(primary, null);
        var wanted = value.ToLowerInvariant();
        if (wanted.Length > 0)
        {
            var hits = workers.Where(e => e.Id.Contains(wanted, StringComparison.Ordinal) || e.Label.ToLowerInvariant().Contains(wanted, StringComparison.Ordinal)).ToList();
            if (hits.Count == 1) return new Choice(hits[0], null);
        }
        return new Choice(null, UnknownServer(value, pool));
    }

    /// <summary>"Error: “kimi” isn't one of this chat's subagent servers. Use one of: dgx-spark, bedrock, primary — or leave
    /// server out." — and, when it names an enlisted route this chat can't use, why.</summary>
    public static string UnknownServer(string value, SubagentPool pool)
    {
        var ids = pool.Workers.Select(e => e.Id).ToList();
        if (pool.Primary is not null) ids.Add(PrimaryId);
        var text = $"Error: “{value}” isn't one of this chat's subagent servers. " +
                   (ids.Count == 0 ? "This chat has none to choose from — leave server out." : $"Use one of: {string.Join(", ", ids)} — or leave server out.");
        if (pool.Dropped.FirstOrDefault(d => string.Equals(d.Id, value, StringComparison.OrdinalIgnoreCase) || d.RouteId == value) is { } gone)
            text += $" ({value} is {gone.Reason}.)";
        return text;
    }

    /// <summary>A pool made from a fleet's configured servers, for a context the app didn't give one (tests, a fleet with no
    /// chat): each server by its handle (else its name as a slug), the configured primary as "primary".</summary>
    public static SubagentPool From(IReadOnlyList<FleetServer> servers)
    {
        var entries = servers.Select(server => new Entry
        {
            Id = server.IsPrimary ? PrimaryId : server.Handle ?? Slug(server.Label),
            RouteId = server.Id,
            Label = server.Label,
            Provider = "",
            Model = "",
            IsPrivate = server.IsPrivate,
            Tier = SubagentTier.Api,
            Speed = SubagentSpeed.Standard,
            Images = true,
            Parallel = server.MaxParallel,
            Machine = server.Machine ?? "route:" + server.Id,
            Kind = SubagentKind.OpenAICompatible,
            InteractiveOnly = server.InteractiveOnly,
            IsPrimary = server.IsPrimary,
        }).ToList();
        return new SubagentPool([.. entries.Where(e => !e.IsPrimary), .. entries.Where(e => e.IsPrimary)]);
    }
}
