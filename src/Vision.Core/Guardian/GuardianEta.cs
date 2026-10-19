using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Vision.Core;

// MARK: - Guardian's shared ETA cache
//
// Guardian is multitenant: every harness on an org teaches every other how long a kind of task takes. A finished turn's
// duration (kind + scope + seconds) goes up to POST /api/harness/eta; the org's learned means per kind come down from
// GET /api/harness/eta and blend into the local TurnPhaseRunner — a kind Guardian knows and this PC doesn't is adopted
// whole; otherwise the means merge weighted by count, when Guardian has more evidence. The wire is the Guardian ⇄ Vision
// harness contract v1 (vision-program/harness-api-contract.md), the same the Mac's GuardianClient speaks.

/// <summary>Where Guardian is and how to authenticate to it, as a route that goes through Guardian's model proxy
/// (<c>https://guardian.503cm.com/api/harness/v1</c>, its key the harness token) reveals it.</summary>
public sealed record GuardianEndpoint(string BaseUrl, string Token, string? OrgId = null)
{
    /// <summary>The path every harness route of the contract sits under.</summary>
    public const string HarnessPath = "/api/harness/";

    /// <summary>The Guardian a route's base URL points at (the part before <c>/api/harness/</c>), or null when the route isn't
    /// Guardian's proxy or has no key.</summary>
    public static GuardianEndpoint? FromRoute(ProviderProfile route, string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        var url = route.BaseUrl.Trim();
        var at = url.IndexOf(HarnessPath, StringComparison.OrdinalIgnoreCase);
        if (at <= 0 || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")) return null;
        string? org = null;
        if (route.CustomHeaders is { } headers)
            foreach (var (name, value) in headers)
                if (string.Equals(name, "x-guardian-org", StringComparison.OrdinalIgnoreCase)) org = value;
        return new GuardianEndpoint(url[..at], key.Trim(), org);
    }
}

/// <summary>Guardian refused (contract §Errors): 401 <c>unauthorized</c>, 403 <c>forbidden</c> with what is missing, 402/429
/// with the limit reached. <see cref="Message"/> is Guardian's plain sentence.</summary>
public sealed class GuardianException(int status, string error, string message, string? reason = null, string? needed = null)
    : Exception(message)
{
    public int Status { get; } = status;
    public string Error { get; } = error;
    public string? Reason { get; } = reason;
    public string? Needed { get; } = needed;
}

public sealed class GuardianEtaClient
{
    /// <summary>What this harness calls itself in the samples it sends.</summary>
    public const string Harness = "vision-windows";

    private readonly HttpClient _http;
    private readonly GuardianEndpoint _endpoint;

    public GuardianEtaClient(GuardianEndpoint endpoint, HttpMessageHandler? handler = null)
    {
        _endpoint = endpoint;
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = TimeSpan.FromSeconds(20);
    }

    /// <summary>Push finished-turn durations up to Guardian's shared cache.</summary>
    public async Task PushAsync(IEnumerable<(TaskKind Kind, double Scope, double Seconds)> samples, CancellationToken cancellationToken = default)
    {
        var list = new JsonArray();
        foreach (var (kind, scope, seconds) in samples)
            list.Add(new JsonObject { ["kind"] = kind.RawValue(), ["scope"] = scope, ["seconds"] = seconds, ["harness"] = Harness });
        if (list.Count == 0) return;
        using var request = Request(HttpMethod.Post, "/api/harness/eta");
        request.Content = new StringContent(new JsonObject { ["samples"] = list }.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureOkAsync(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The org's learned durations per kind — the multitenant cache — in the shape the local runner keeps. Kinds this
    /// build doesn't know are skipped.</summary>
    public async Task<IReadOnlyDictionary<TaskKind, DurationSample>> SummaryAsync(CancellationToken cancellationToken = default)
    {
        using var request = Request(HttpMethod.Get, "/api/harness/eta");
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var text = await EnsureOkAsync(response, cancellationToken).ConfigureAwait(false);
        var output = new Dictionary<TaskKind, DurationSample>();
        JsonObject? root;
        try
        {
            root = JsonNode.Parse(text) as JsonObject;
        }
        catch (JsonException error)
        {
            throw new GuardianException((int)response.StatusCode, "bad_response", $"Guardian's ETA summary isn't JSON: {error.Message}");
        }
        if (root?["byKind"] is not JsonObject byKind) return output;
        foreach (var (key, node) in byKind)
        {
            if (TaskKinds.FromRaw(key) is not { } kind || node is not JsonObject entry) continue;
            var count = Number(entry["count"]);
            var mean = Number(entry["meanSeconds"]);
            var scope = Number(entry["meanScope"]) ?? 1;
            if (count is not { } c || mean is not { } m || c < 0 || !double.IsFinite(m) || m < 0) continue;
            output[kind] = new DurationSample((int)c, m, double.IsFinite(scope) ? scope : 1);
        }
        return output;
    }

    /// <summary>Blend the org's learned durations into <paramref name="runner"/>: a kind this PC hasn't learned is adopted
    /// whole; one Guardian has more evidence for merges, weighted by count. (A second sync with the same numbers changes
    /// nothing: after a merge this PC's count is the larger.) The kinds that changed.</summary>
    public async Task<IReadOnlyList<TaskKind>> SyncIntoAsync(TurnPhaseRunner runner, CancellationToken cancellationToken = default) =>
        Blend(runner, await SummaryAsync(cancellationToken).ConfigureAwait(false));

    /// <summary>The blend rule on its own (what <see cref="SyncIntoAsync"/> applies).</summary>
    public static IReadOnlyList<TaskKind> Blend(TurnPhaseRunner runner, IReadOnlyDictionary<TaskKind, DurationSample> shared)
    {
        var changed = new List<TaskKind>();
        foreach (var (kind, entry) in shared)
        {
            if (entry.Count <= 0) continue;
            var local = runner.Sample(kind);
            if (local.Count == 0)
            {
                runner.MergeSample(kind, entry);
                changed.Add(kind);
            }
            else if (entry.Count > local.Count)
            {
                var total = (double)(local.Count + entry.Count);
                runner.MergeSample(kind, new DurationSample(
                    Math.Max(local.Count, 1) + entry.Count,
                    (local.MeanSeconds * local.Count + entry.MeanSeconds * entry.Count) / total,
                    (local.MeanScope * local.Count + entry.MeanScope * entry.Count) / total));
                changed.Add(kind);
            }
        }
        return changed;
    }

    // MARK: The Prompt Notebook (contract §Sync)

    /// <summary>Push prompts to the account's prompt history (Guardian's Prompt Notebook), which deduplicates on (user, text,
    /// at) — so a prompt pushed when it starts and again when it ends is one row, the second carrying its outcome.</summary>
    public async Task PushPromptsAsync(IEnumerable<(PromptRecord Record, string? ChatTitle, string? Project)> prompts, CancellationToken cancellationToken = default)
    {
        var list = new JsonArray();
        foreach (var (record, chatTitle, project) in prompts)
        {
            var item = new JsonObject
            {
                ["text"] = record.Text,
                ["at"] = record.At.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture),
                // The contract's source is the harness ("vision-mac", "vision-windows"); how it was sent (typed, /goal, a
                // task) stays on this PC's timeline.
                ["source"] = Harness,
                ["chatId"] = record.ChatId,
            };
            if (NotebookOutcome(record.Outcome) is { } outcome) item["outcome"] = outcome;
            if (chatTitle is { Length: > 0 }) item["chatTitle"] = chatTitle;
            if (project is { Length: > 0 }) item["project"] = project;
            if (record.Model is { Length: > 0 } model) item["model"] = model;
            if (record.DurationSeconds is { } seconds) item["durationSeconds"] = Math.Round(seconds, 1);
            if (record.TurnId is { Length: > 0 } turn) item["runId"] = turn;
            list.Add(item);
        }
        if (list.Count == 0) return;
        using var request = Request(HttpMethod.Post, "/api/harness/prompts");
        request.Content = new StringContent(new JsonObject { ["prompts"] = list }.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureOkAsync(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>How the Prompt Notebook spells an ending (the Mac's words); null while it is still going (the start push).</summary>
    public static string? NotebookOutcome(PromptOutcome outcome) => outcome switch
    {
        PromptOutcome.Completed => "completed",
        PromptOutcome.Issues => "completed with issues",
        PromptOutcome.NeedsYou => "needs you",
        PromptOutcome.Failed => "failed",
        PromptOutcome.Stopped => "stopped",
        _ => null,
    };

    // MARK: Wire helpers

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, _endpoint.BaseUrl.TrimEnd('/') + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _endpoint.Token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (_endpoint.OrgId is { Length: > 0 } org) request.Headers.TryAddWithoutValidation("x-guardian-org", org);
        request.Headers.UserAgent.ParseAdd($"Vision/{AppInfo.Version.Split('+')[0]} (Windows)");
        return request;
    }

    /// <summary>The body of a 2xx answer; a structured <see cref="GuardianException"/> otherwise.</summary>
    private static async Task<string> EnsureOkAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode) return text;
        var status = (int)response.StatusCode;
        string error = status switch { 401 => "unauthorized", 403 => "forbidden", 402 => "budget_exceeded", 429 => "rate_limited", _ => "http_" + status };
        string? reason = null, needed = null, message = null;
        try
        {
            if (JsonNode.Parse(text) is JsonObject body)
            {
                error = Text(body["error"]) ?? error;
                reason = Text(body["reason"]);
                needed = Text(body["needed"]);
                message = Text(body["message"]);
            }
        }
        catch (JsonException)
        {
            // Not Guardian's JSON (a proxy's page): the status says it.
        }
        message ??= response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "Guardian didn't accept the sign-in — sign in again.",
            HttpStatusCode.Forbidden => "Guardian says this account isn't allowed to do that.",
            _ => $"Guardian answered HTTP {status}.",
        };
        throw new GuardianException(status, error, message, reason, needed);
    }

    private static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static double? Number(JsonNode? node)
    {
        if (node is not JsonValue v) return null;
        if (v.TryGetValue<double>(out var d)) return d;
        if (v.TryGetValue<long>(out var l)) return l;
        return null;
    }
}
