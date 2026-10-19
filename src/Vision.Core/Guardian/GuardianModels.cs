using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Vision.Core;

// MARK: - Guardian's shapes, as the harness sees them
//
// Guardian (guardian.503cm.com) is the multiuser side of the family: one sign-in fronts the models a person's org
// allows behind an OpenAI-compatible proxy, and keeps their prompt notebook, skills, chats, code projects and the
// secrets they opted in for Vision. The wire is the Guardian ⇄ Vision harness contract v1
// (vision-program/harness-api-contract.md), the same one Vision for macOS speaks (GuardianClient.swift).

/// <summary>A signed-in Guardian account: who it is and the bearer token every harness call carries.</summary>
public sealed record GuardianSession(string UserId, string Email, string? Name, string Token, DateTimeOffset IssuedAt)
{
    /// <summary>The org the token was bound to at a browser sign-in (null for a password sign-in: the account's default).</summary>
    public string? OrgId { get; init; }

    /// <summary>"Ada Lovelace (ada@503cm.com)", or the email alone.</summary>
    public string Label => string.IsNullOrWhiteSpace(Name) ? Email : $"{Name} ({Email})";

    public override string ToString() => $"GuardianSession {{ Email = {Email}, Token = [{Token.Length} characters] }}";
}

/// <summary>One org the person belongs to (GET /api/harness/me).</summary>
public sealed record GuardianOrg(string Id, string Name, bool Active);

/// <summary>A limit the account runs under (GET /api/harness/limits, and the body of a 402/429).</summary>
/// <param name="Kind">"spend", "tokens" or "requests".</param>
/// <param name="Window">"day", "week", "month" or "run".</param>
/// <param name="Unit">"usd", "tokens", "requests"…</param>
public sealed record GuardianLimit(string Kind, string Window, double? Used, double? Max, string? Unit, DateTimeOffset? ResetsAt)
{
    /// <summary>What is left as Guardian reported it (the x-guardian-limit-remaining header), when the body had no numbers.</summary>
    public double? ReportedRemaining { get; init; }

    /// <summary>What is left, when it is known.</summary>
    public double? Remaining => Used is { } used && Max is { } max ? Math.Max(0, max - used) : ReportedRemaining;

    /// <summary>0…1 of the allowance used, when both sides are known.</summary>
    public double? Fraction => Used is { } used && Max is { } max && max > 0 ? Math.Clamp(used / max, 0, 1) : null;

    /// <summary>"$3.20 of $50.00 this week" · "1.2M of 2M tokens today" · "2 streams used this run".</summary>
    public string Describe()
    {
        var unit = UnitSuffix;
        var amount = Used is { } used && Max is { } max ? $"{Amount(used)} of {Amount(max)}{unit}"
            : Used is { } onlyUsed ? $"{Amount(onlyUsed)}{unit} used"
            : Max is { } onlyMax ? $"up to {Amount(onlyMax)}{unit}"
            : ReportedRemaining is { } left ? $"{Amount(left)}{unit} left" : "a limit";
        return $"{amount} {WindowPhrase(Window)}".Trim();
    }

    /// <summary>The dialog's name for the limit, as the Mac says it: "Spend today", "Tokens this week", "Requests for this
    /// run", "Streams at once"; a limit with no window (a storage cap) is just its kind.</summary>
    public string Heading
    {
        get
        {
            if (string.Equals(Unit, "streams", StringComparison.OrdinalIgnoreCase)) return "Streams at once";
            var window = (Window ?? "").ToLowerInvariant() switch
            {
                "" or "total" => "",
                "day" => "today",
                "week" => "this week",
                "month" => "this month",
                "run" => "for this run",
                "minute" => "this minute",
                "hour" => "this hour",
                var other => "per " + other,
            };
            return window.Length == 0 ? Title : $"{Title} {window}";
        }
    }

    /// <summary>"$4.80 of $5.00", "1.2M of 2M tokens", "4 GB of 4 GB", "3 requests used", "0 requests left" — or null when
    /// Guardian gave no numbers.</summary>
    public string? Usage
    {
        get
        {
            var unit = UnitSuffix;
            if (Used is { } used && Max is { } max) return $"{Amount(used)} of {Amount(max)}{unit}";
            if (Used is { } onlyUsed) return $"{Amount(onlyUsed)}{unit} used";
            if (ReportedRemaining is { } left) return $"{Amount(left)}{unit} left";
            return null;
        }
    }

    /// <summary>"resets in 3 hours (Mon 4:00 PM)", in local time (the Mac's words), or null.</summary>
    public string? ResetPhrase(DateTimeOffset? now = null, TimeZoneInfo? zone = null)
    {
        if (ResetsAt is not { } at) return null;
        var clock = TimeZoneInfo.ConvertTime(at, zone ?? TimeZoneInfo.Local).ToString("ddd h:mm tt", CultureInfo.InvariantCulture);
        return $"resets {Relative(at - (now ?? DateTimeOffset.Now))} ({clock})";
    }

    /// <summary>"now", "in 5 minutes", "in 3 hours", "in 4 days" (the Mac's rounding).</summary>
    internal static string Relative(TimeSpan span)
    {
        var seconds = span.TotalSeconds;
        if (seconds <= 30) return "now";
        var minutes = (int)Math.Round(seconds / 60, MidpointRounding.AwayFromZero);
        if (minutes < 60) return $"in {Math.Max(1, minutes)} minute{(minutes == 1 ? "" : "s")}";
        var hours = (int)Math.Round(seconds / 3600, MidpointRounding.AwayFromZero);
        if (hours < 48) return $"in {hours} hour{(hours == 1 ? "" : "s")}";
        return $"in {(int)Math.Round(seconds / 86400, MidpointRounding.AwayFromZero)} days";
    }

    private bool IsMoney => Unit is { } u && (u.Equals("usd", StringComparison.OrdinalIgnoreCase) || u == "$") || (Unit is null && Kind == "spend");

    private bool IsBytes => Unit is "bytes" or "B" || (Unit is null && Kind == "storage");

    private string UnitSuffix => IsMoney || IsBytes ? "" : Unit is { Length: > 0 } unit ? " " + unit : Kind switch
    {
        "tokens" => " tokens",
        "requests" => " requests",
        _ => "",
    };

    private string Amount(double value)
    {
        if (IsMoney) return "$" + value.ToString("0.00", CultureInfo.InvariantCulture);
        if (IsBytes) return value switch
        {
            >= 1L << 30 => (value / (1L << 30)).ToString("0.#", CultureInfo.InvariantCulture) + " GB",
            >= 1L << 20 => (value / (1L << 20)).ToString("0.#", CultureInfo.InvariantCulture) + " MB",
            _ => (value / 1024).ToString("0", CultureInfo.InvariantCulture) + " KB",
        };
        if (Kind == "tokens" || Unit == "tokens") return Fmt(value);
        return value % 1 == 0 ? ((long)value).ToString(CultureInfo.InvariantCulture) : value.ToString("0.##", CultureInfo.InvariantCulture);
    }

    private static string Fmt(double tokens) => tokens switch
    {
        >= 1_000_000 => (tokens / 1_000_000).ToString("0.#", CultureInfo.InvariantCulture) + "M",
        >= 10_000 => (tokens / 1_000).ToString("0", CultureInfo.InvariantCulture) + "K",
        _ => ((long)tokens).ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>"today", "this week", "this month", "this run".</summary>
    public static string WindowPhrase(string? window) => window switch
    {
        "minute" => "this minute",
        "day" => "today",
        "week" => "this week",
        "month" => "this month",
        "run" => "this run",
        "total" => "in all",
        null or "" => "",
        _ => $"per {window}",
    };

    /// <summary>"Spend", "Tokens", "Requests".</summary>
    public string Title => Kind switch
    {
        "spend" => "Spend",
        "tokens" => "Tokens",
        "requests" => "Requests",
        "storage" => "Storage",
        _ => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(Kind),
    };
}

/// <summary>Who the token belongs to and what their role allows (GET /api/harness/me).</summary>
public sealed record GuardianIdentity(string UserId, string Email, string? Name, IReadOnlyList<GuardianOrg> Orgs, string? OrgId,
                                      IReadOnlyList<string> Modules, IReadOnlyList<string> Permissions, IReadOnlyList<GuardianLimit> Limits)
{
    /// <summary>The org calls go to: the one Guardian named, else the one marked active, else the first.</summary>
    public GuardianOrg? ActiveOrg => Orgs.FirstOrDefault(o => o.Id == OrgId) ?? Orgs.FirstOrDefault(o => o.Active) ?? Orgs.FirstOrDefault();
}

/// <summary>One model the account may run through the proxy.</summary>
public sealed record GuardianModel(string Id, string? Label = null, int? ContextWindow = null, int? MaxOutputTokens = null, bool? InteractiveOnly = null)
{
    /// <summary>guardian/default and guardian/fast: Guardian picks the model behind them.</summary>
    public bool IsAlias => Id.StartsWith("guardian/", StringComparison.OrdinalIgnoreCase);

    public string Title => string.IsNullOrWhiteSpace(Label) || Label == Id ? Id : $"{Label} ({Id})";
}

/// <summary>One prompt-notebook entry. Pushed on submit, and again with the outcome when the turn ends (Guardian updates
/// the row it already has: the dedupe key is user + text + <see cref="At"/>).</summary>
public sealed record GuardianPrompt(string Text, DateTimeOffset At)
{
    public string? Id { get; init; }
    /// <summary>How the turn ended: "completed", "needs you", "failed", "stopped" — the Mac's words.</summary>
    public string? Outcome { get; init; }
    /// <summary>"vision-windows" for prompts sent from here; "guardian", "vision-mac"… when read back.</summary>
    public string? Source { get; init; }
    public string? ChatId { get; init; }
    public string? ChatTitle { get; init; }
    public string? Project { get; init; }
    public string? Model { get; init; }
    public double? DurationSeconds { get; init; }
}

/// <summary>A skill as Guardian stores it: its files as text, by "/"-separated relative path.</summary>
public sealed record GuardianSkill(string Name, string Description, IReadOnlyDictionary<string, string> Files);

/// <summary>A chat Guardian holds for the account.</summary>
public sealed record GuardianChatSummary(string Id, string Title, DateTimeOffset UpdatedAt);

/// <summary>A code project uploaded to the account's private store.</summary>
public sealed record GuardianProjectSummary(string Id, string Name, DateTimeOffset? UpdatedAt, int? FileCount);

/// <summary>A vault secret the person owns and marked "Usable in Vision" — its name only; the value comes from a reveal.</summary>
public sealed record GuardianVaultSecret(string Id, string Name, string? Kind, string? Description);

// MARK: - Refusals and failures

public enum GuardianErrorKind
{
    /// <summary>The address isn't a Guardian address.</summary>
    BadAddress,
    /// <summary>The email, password or code was refused (sign-in only).</summary>
    Credentials,
    /// <summary>The account has TOTP on: finish with the code (<see cref="GuardianError.Challenge"/>).</summary>
    TwoFactorRequired,
    /// <summary>401: the token is missing, expired or revoked.</summary>
    Unauthorized,
    /// <summary>403: the person's role or modules don't allow it (<see cref="GuardianError.Reason"/>, <see cref="GuardianError.Needed"/>).</summary>
    Forbidden,
    /// <summary>402/429: a budget, limit or rate (<see cref="GuardianError.Limit"/>).</summary>
    Limit,
    /// <summary>404: Guardian has no such thing (or no harness API yet).</summary>
    NotFound,
    /// <summary>409: a name taken, an upload chunk out of order.</summary>
    Conflict,
    /// <summary>The browser sign-in was denied, timed out or came back wrong.</summary>
    BrowserSignIn,
    /// <summary>Guardian answered something else (a 5xx, a page that isn't JSON).</summary>
    Server,
    /// <summary>Guardian couldn't be reached.</summary>
    Network,
}

/// <summary>Guardian said no, or couldn't be asked. <see cref="Exception.Message"/> is always a plain sentence for the
/// person — never a raw HTTP code (contract §Errors).</summary>
public sealed class GuardianError : Exception
{
    public GuardianErrorKind Kind { get; }
    /// <summary>The HTTP status, when there was one.</summary>
    public int Status { get; init; }
    /// <summary>Guardian's error code: "unauthorized", "forbidden", "budget_exceeded", "limit_reached", "rate_limited", "name_taken"…</summary>
    public string? Code { get; init; }
    /// <summary>403: "module_disabled", "permission_missing", "org_not_member", "scope_missing"…</summary>
    public string? Reason { get; init; }
    /// <summary>403: the module or permission that is missing.</summary>
    public string? Needed { get; init; }
    /// <summary>402/429: the limit that was reached.</summary>
    public GuardianLimit? Limit { get; init; }
    /// <summary>429: how long Guardian asks to wait (Retry-After).</summary>
    public TimeSpan? RetryAfter { get; init; }
    /// <summary>The 2FA challenge to finish the sign-in with.</summary>
    public string? Challenge { get; init; }
    /// <summary>The sentence Guardian's reply carried, if it carried one.</summary>
    public string? ServerMessage { get; init; }

    public GuardianError(GuardianErrorKind kind, string message, Exception? inner = null) : base(message, inner) => Kind = kind;

    public static GuardianError BadAddress() => new(GuardianErrorKind.BadAddress,
        "That doesn't look like a Guardian address — it should be https://guardian.503cm.com (or your own deployment).");

    public static GuardianError Network(string why, Exception? inner = null) => new(GuardianErrorKind.Network, $"Couldn't reach Guardian: {why}", inner);

    /// <summary>A refusal a person must see as a dialog (contract: 401/403/402/429 are never shown as raw codes).</summary>
    public bool IsRefusal => Kind is GuardianErrorKind.Unauthorized or GuardianErrorKind.Forbidden or GuardianErrorKind.Limit;

    /// <summary>The status-and-body of a non-2xx reply, in Guardian's structured shape when it sent one.</summary>
    public static GuardianError FromResponse(int status, string body, HttpResponseHeaders? headers = null)
    {
        var error = Build(status, body, headers, out var said);
        return said is null ? error : new GuardianError(error.Kind, error.Message, error.InnerException)
        {
            Status = error.Status, Code = error.Code, Reason = error.Reason, Needed = error.Needed, Limit = error.Limit,
            RetryAfter = error.RetryAfter, ServerMessage = said,
        };
    }

    private static GuardianError Build(int status, string body, HttpResponseHeaders? headers, out string? said)
    {
        JsonObject? json = null;
        try
        {
            json = JsonNode.Parse(body) as JsonObject;
        }
        catch (JsonException)
        {
            // Not Guardian's JSON (a proxy's page): the status says it.
        }
        // In-stream proxy errors come in OpenAI's shape: {"error":{"message","type","code"}}.
        var inner = json?["error"] as JsonObject;
        var code = GuardianJson.Text(json?["error"]) ?? GuardianJson.Text(inner?["code"]) ?? GuardianJson.Text(inner?["type"]);
        var message = GuardianJson.Text(json?["message"]) ?? GuardianJson.Text(inner?["message"]);
        said = message;
        var reason = GuardianJson.Text(json?["reason"]);
        var needed = GuardianJson.Text(json?["needed"]);
        var limit = GuardianJson.Limit(json?["limit"]) ?? LimitFromHeaders(headers);
        TimeSpan? retryAfter = headers?.RetryAfter?.Delta;
        if (retryAfter is null && headers?.RetryAfter?.Date is { } date) retryAfter = date - DateTimeOffset.UtcNow;

        // A storage cap answers 409 with the limit shape (Guardian's review fix 4): a limit like any other.
        var shape = status == 409 && code is "limit_reached" or "budget_exceeded" && limit is not null ? 402 : status;
        switch (shape)
        {
            case 401:
                return new GuardianError(GuardianErrorKind.Unauthorized,
                    message ?? "Your Guardian sign-in has expired or was signed out — sign in again to keep using it.")
                { Status = status, Code = code ?? "unauthorized" };
            case 403:
                return new GuardianError(GuardianErrorKind.Forbidden,
                    message ?? "Guardian says this account isn't allowed to do that. An owner or admin of your Guardian organization can change what your role allows.")
                { Status = status, Code = code ?? "forbidden", Reason = reason, Needed = needed };
            case 402 or 429:
            {
                var fallback = status == 429 && code is null or "rate_limited"
                    ? "Guardian asks Vision to slow down for a moment."
                    : "You've reached a Guardian limit.";
                return new GuardianError(GuardianErrorKind.Limit, message ?? fallback)
                {
                    Status = status,
                    Code = code ?? (status == 429 ? "rate_limited" : "budget_exceeded"),
                    Limit = limit,
                    RetryAfter = retryAfter is { } wait && wait > TimeSpan.Zero ? wait : null,
                };
            }
            case 404:
                return new GuardianError(GuardianErrorKind.NotFound, message ?? "Guardian doesn't have that (it may not be updated for Vision yet).")
                { Status = status, Code = code };
            case 409:
                return new GuardianError(GuardianErrorKind.Conflict, message ?? "Guardian already has something by that name.") { Status = status, Code = code };
            case 400 or 413 or 422:
                return new GuardianError(GuardianErrorKind.Server, message ?? (status == 413 ? "That is too big for Guardian to take in one go." : "Guardian refused the request."))
                { Status = status, Code = code };
            default:
                return new GuardianError(GuardianErrorKind.Server, message is null ? $"Guardian had a problem (HTTP {status})." : $"Guardian had a problem: {message}")
                { Status = status, Code = code };
        }
    }

    /// <summary>The x-guardian-limit-* headers, when the body didn't carry the limit.</summary>
    internal static GuardianLimit? LimitFromHeaders(HttpResponseHeaders? headers)
    {
        if (headers is null) return null;
        string? Header(string name) => headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;
        var kind = Header("x-guardian-limit-kind");
        if (kind is null) return null;
        double? remaining = double.TryParse(Header("x-guardian-limit-remaining"), NumberStyles.Float, CultureInfo.InvariantCulture, out var r) ? r : null;
        DateTimeOffset? reset = null;
        if (Header("x-guardian-limit-reset") is { } raw)
        {
            if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)) reset = at;
            else if (long.TryParse(raw, out var epoch)) reset = DateTimeOffset.FromUnixTimeSeconds(epoch);
        }
        return new GuardianLimit(kind, Header("x-guardian-limit-window") ?? "", null, null, null, reset) { ReportedRemaining = remaining };
    }
}

/// <summary>A Guardian refusal put into a dialog's words: the title, Guardian's own sentence, and what is missing and who can
/// grant it, or the limit, when it resets and who can raise it. The titles and lines are Vision for macOS's
/// (GuardianProblem.title / detail), so both apps say the same thing; Windows adds a storage limit's title, the words for
/// two more reasons and Guardian's Retry-After.</summary>
public sealed record GuardianRefusalText(string Title, string Message, IReadOnlyList<string> Details, bool OffersSignIn)
{
    public static GuardianRefusalText For(GuardianError error, DateTimeOffset? now = null)
    {
        var details = new List<string>();
        switch (error.Kind)
        {
            case GuardianErrorKind.Unauthorized:
                details.Add("Sign in again in Settings ▸ Guardian.");
                return new("Guardian sign-in needed", error.Message, details, OffersSignIn: true);
            case GuardianErrorKind.Forbidden:
            {
                var what = error.Needed is { Length: > 0 } needed ? $"“{needed}”" : "access";
                details.Add(error.Reason switch
                {
                    "module_disabled" => $"Missing: the {what} module is turned off for you. An admin of your Guardian organization can turn it on for you (Guardian ▸ Settings ▸ Members).",
                    "permission_missing" => $"Missing: the {what} permission. An admin of your Guardian organization can grant it (Guardian ▸ Settings ▸ Members).",
                    "scope_missing" => $"Missing: this sign-in doesn't carry the {what} scope. Sign out and sign in again in Settings ▸ Guardian; if it is still missing, an admin of your organization can grant it.",
                    "org_not_member" => "You aren't a member of that organization. Pick another one in Settings ▸ Guardian, or ask its admin to add you.",
                    _ => $"Missing: {what}" + (ReasonPhrase(error.Reason) is { } why ? $" — {why}" : error.Reason is { Length: > 0 } raw ? $" ({raw})" : "")
                         + ". An admin of your Guardian organization can grant it.",
                });
                return new(error.Reason == "org_not_member" ? "Not a member of that Guardian organization" : "Your Guardian account doesn't allow this",
                    error.Message, details, OffersSignIn: false);
            }
            case GuardianErrorKind.Limit:
            {
                var rateLimited = error.Code == "rate_limited";
                if (error.Limit is { } limit)
                {
                    var line = limit.Heading;
                    if (limit.Usage is { } usage) line += ": " + usage;
                    if (limit.ResetPhrase(now) is { } reset) line += " — " + reset;
                    details.Add(line + ".");
                    details.Add(rateLimited ? "Vision tries again after the reset."
                        : "Your Guardian organization's admin can raise the limit; otherwise it frees up when it resets.");
                }
                else if (!rateLimited)
                {
                    details.Add("Your Guardian organization's admin can raise the limit.");
                }
                if (error.RetryAfter is { } wait) details.Add($"Try again in {Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds))} s.");
                var title = rateLimited ? "Guardian is slowing this account down" : error.Limit?.Kind.ToLowerInvariant() switch
                {
                    "spend" => "Guardian spend limit reached",
                    "tokens" => "Guardian token limit reached",
                    "requests" => "Guardian request limit reached",
                    "storage" => "Guardian storage limit reached",
                    _ => "Guardian limit reached",
                };
                return new(title, error.Message, details, OffersSignIn: false);
            }
            default:
                return new("Guardian refused the request", error.Message, details, OffersSignIn: false);
        }
    }

    /// <summary>One line for a chat's error row (the Mac's oneLine): the title, Guardian's sentence, then the details.</summary>
    public string OneLine() => string.Join(" ", new[] { Title + "." }.Concat(new[] { Message }.Where(m => !string.IsNullOrWhiteSpace(m))).Concat(Details));

    /// <summary>The reason code in words, for the reasons the Mac's lines don't name, or null for one this build doesn't know.</summary>
    public static string? ReasonPhrase(string? reason) => reason switch
    {
        "module_disabled" => "that module is switched off for you",
        "permission_missing" => "your role doesn't include that permission",
        "scope_missing" => "this sign-in doesn't carry that scope",
        "org_not_member" => "you aren't a member of that organization",
        "not_owner" => "only the owner of that item can do this",
        "model_not_allowed" => "that model isn't available to you in this organization",
        _ => null,
    };
}

/// <summary>Reading Guardian's JSON leniently: numbers or strings, ISO dates or epoch seconds.</summary>
public static class GuardianJson
{
    public static string? Text(JsonNode? node) => node is JsonValue value
        ? value.TryGetValue<string>(out var s) ? s
          : value.TryGetValue<long>(out var l) ? l.ToString(CultureInfo.InvariantCulture)
          : value.TryGetValue<double>(out var d) ? d.ToString(CultureInfo.InvariantCulture) : null
        : null;

    public static double? Number(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (value.TryGetValue<double>(out var d)) return double.IsFinite(d) ? d : null;
        if (value.TryGetValue<long>(out var l)) return l;
        if (value.TryGetValue<string>(out var s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)) return parsed;
        return null;
    }

    public static bool? Bool(JsonNode? node) => node is JsonValue value && value.TryGetValue<bool>(out var b) ? b : null;

    public static DateTimeOffset? Date(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (value.TryGetValue<string>(out var s))
        {
            if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at)) return at;
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var secondsText)) return FromEpoch(secondsText);
            return null;
        }
        return Number(node) is { } seconds ? FromEpoch(seconds) : null;
    }

    /// <summary>Epoch seconds, or milliseconds when the number is too big to be seconds.</summary>
    private static DateTimeOffset? FromEpoch(double value)
    {
        try
        {
            return value > 100_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds((long)value) : DateTimeOffset.FromUnixTimeMilliseconds((long)(value * 1000));
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    public static IReadOnlyList<string> Strings(JsonNode? node) => node is JsonArray array
        ? array.Select(n => Text(n) ?? (n as JsonObject)?["id"]?.ToString() ?? (n as JsonObject)?["name"]?.ToString()).OfType<string>().ToList()
        : [];

    public static GuardianLimit? Limit(JsonNode? node)
    {
        if (node is not JsonObject o) return null;
        var kind = Text(o["kind"]);
        if (kind is null) return null;
        return new GuardianLimit(kind, Text(o["window"]) ?? "", Number(o["used"]), Number(o["max"]), Text(o["unit"]), Date(o["resetsAt"]));
    }

    /// <summary>A limits list: <c>[…]</c>, <c>{limits:[…]}</c> (what /me carries), or nothing.</summary>
    public static IReadOnlyList<GuardianLimit> Limits(JsonNode? node)
    {
        var array = node as JsonArray ?? (node as JsonObject)?["limits"] as JsonArray;
        return array is null ? [] : array.Select(Limit).OfType<GuardianLimit>().ToList();
    }
}
