using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Vision.Core;

// MARK: - Provider errors in plain words
//
// Z.ai answers with HTTP status + a business code (docs.z.ai/api-reference/api-code):
//   {"error":{"code":"1113","message":"Insufficient balance or no resource package. Please recharge."}}
// Several of its 429s are not "slow down" at all: 1113 (no balance — what a GLM Coding Plan key gets on the General API),
// 1309 (the Coding Plan expired), 1311 (the plan doesn't include the model). Retrying those for ever is what "it would try
// to work and just straight up did not work" looked like. They now fail at once, in plain words, naming the fix. Rate
// limits (1302) and overload (1305) are still waited out. Vision for macOS: ProviderErrors.swift (same wording).

/// <summary>A provider's error explained: what to tell the person, whether retrying can help, and for Z.ai the endpoint
/// that may suit the key better.</summary>
public sealed record ProviderErrorExplanation(string Message, bool Permanent, ZaiEndpoint? SuggestedEndpoint = null);

/// <summary>The Z.ai endpoint kinds. Raw ids (coding, general, chinaCoding, chinaGeneral) are what the plan guide saves as
/// the region.</summary>
public enum ZaiEndpoint { Coding, General, ChinaCoding, ChinaGeneral }

public static class ZaiEndpoints
{
    public static IReadOnlyList<ZaiEndpoint> All { get; } = [ZaiEndpoint.Coding, ZaiEndpoint.General, ZaiEndpoint.ChinaCoding, ZaiEndpoint.ChinaGeneral];

    public static string BaseUrl(this ZaiEndpoint endpoint) => endpoint switch
    {
        ZaiEndpoint.Coding => "https://api.z.ai/api/coding/paas/v4",
        ZaiEndpoint.General => "https://api.z.ai/api/paas/v4",
        ZaiEndpoint.ChinaCoding => "https://open.bigmodel.cn/api/coding/paas/v4",
        _ => "https://open.bigmodel.cn/api/paas/v4",
    };

    /// <summary>"Coding Plan", "General API", "China — Coding Plan", "China — General API".</summary>
    public static string Name(this ZaiEndpoint endpoint) => endpoint switch
    {
        ZaiEndpoint.Coding => "Coding Plan",
        ZaiEndpoint.General => "General API",
        ZaiEndpoint.ChinaCoding => "China — Coding Plan",
        _ => "China — General API",
    };

    /// <summary>One line under the picker.</summary>
    public static string Detail(this ZaiEndpoint endpoint) => endpoint switch
    {
        ZaiEndpoint.Coding => "For a GLM Coding Plan subscription — Coding Plan keys work only here.",
        ZaiEndpoint.General => "Pay-as-you-go: billed per token from the account's balance.",
        ZaiEndpoint.ChinaCoding => "A Coding Plan bought at open.bigmodel.cn.",
        _ => "Pay-as-you-go at open.bigmodel.cn.",
    };

    /// <summary>The saved route's name for this endpoint.</summary>
    public static string RouteName(this ZaiEndpoint endpoint) => endpoint switch
    {
        ZaiEndpoint.Coding => "GLM Coding Plan",
        ZaiEndpoint.General => "Z.ai API",
        ZaiEndpoint.ChinaCoding => "GLM Coding Plan (China)",
        _ => "Z.ai API (China)",
    };

    /// <summary>coding, general, chinaCoding, chinaGeneral (as Vision for macOS spells them).</summary>
    public static string RawValue(this ZaiEndpoint endpoint) => endpoint switch
    {
        ZaiEndpoint.Coding => "coding",
        ZaiEndpoint.General => "general",
        ZaiEndpoint.ChinaCoding => "chinaCoding",
        _ => "chinaGeneral",
    };

    public static ZaiEndpoint? FromRaw(string? raw) => All.Cast<ZaiEndpoint?>().FirstOrDefault(e => e!.Value.RawValue() == raw);

    public static bool IsCoding(this ZaiEndpoint endpoint) => endpoint is ZaiEndpoint.Coding or ZaiEndpoint.ChinaCoding;
    public static bool IsChina(this ZaiEndpoint endpoint) => endpoint is ZaiEndpoint.ChinaCoding or ZaiEndpoint.ChinaGeneral;

    /// <summary>The other kind of endpoint in the same region.</summary>
    public static ZaiEndpoint OtherKind(this ZaiEndpoint endpoint) => endpoint switch
    {
        ZaiEndpoint.Coding => ZaiEndpoint.General,
        ZaiEndpoint.General => ZaiEndpoint.Coding,
        ZaiEndpoint.ChinaCoding => ZaiEndpoint.ChinaGeneral,
        _ => ZaiEndpoint.ChinaCoding,
    };

    /// <summary>The same kind of endpoint in the other region.</summary>
    public static ZaiEndpoint OtherRegion(this ZaiEndpoint endpoint) => endpoint switch
    {
        ZaiEndpoint.Coding => ZaiEndpoint.ChinaCoding,
        ZaiEndpoint.General => ZaiEndpoint.ChinaGeneral,
        ZaiEndpoint.ChinaCoding => ZaiEndpoint.Coding,
        _ => ZaiEndpoint.General,
    };

    /// <summary>The endpoint a base URL is, or null when it isn't Z.ai's.</summary>
    public static ZaiEndpoint? Of(string? baseUrl)
    {
        if (!Uri.TryCreate(baseUrl?.Trim(), UriKind.Absolute, out var url) || string.IsNullOrEmpty(url.Host)) return null;
        var host = url.Host.ToLowerInvariant();
        var china = host == "bigmodel.cn" || host.EndsWith(".bigmodel.cn", StringComparison.Ordinal);
        if (!china && host != "z.ai" && !host.EndsWith(".z.ai", StringComparison.Ordinal)) return null;
        var coding = url.AbsolutePath.Contains("/coding", StringComparison.OrdinalIgnoreCase);
        return (china, coding) switch
        {
            (false, true) => ZaiEndpoint.Coding,
            (false, false) => ZaiEndpoint.General,
            (true, true) => ZaiEndpoint.ChinaCoding,
            _ => ZaiEndpoint.ChinaGeneral,
        };
    }
}

public static class ZaiErrors
{
    /// <summary>The business code and message of a Z.ai error body.</summary>
    public static (string Code, string Message)? Parse(string body)
    {
        if (ParameterRejection.BusinessCode(body) is not { } code) return null;
        string message;
        try
        {
            message = JsonNode.Parse(body) is JsonObject { } obj && obj["error"] is JsonObject err ? JsonArgs.String(err, "message") ?? "" : "";
        }
        catch (JsonException)
        {
            message = "";
        }
        return (code, message);
    }

    /// <summary>A Z.ai error in plain words; null when it is one to leave to the generic handling (rate limits, overload,
    /// server errors, and parameter refusals — those the client adapts to).</summary>
    public static ProviderErrorExplanation? Explain(int status, string body, ZaiEndpoint? endpoint, string model)
    {
        var parsed = Parse(body);
        var code = parsed?.Code ?? "";
        var said = parsed?.Message ?? "";
        var place = endpoint?.Name() ?? "this endpoint";
        var reset = ResetTime(said) is { } at ? $" It resets at {at}." : "";
        switch (code)
        {
            case "1000" or "1001" or "1002" or "1003" or "1004" or "401":
            {
                var text = $"Z.ai didn't accept the key ({code}: {(said.Length == 0 ? "authentication failed" : said)}).";
                if (endpoint is { } where)
                    text += where.IsChina()
                        ? " Keys made at open.bigmodel.cn work on the China endpoints; a key from z.ai needs the international ones."
                        : " Keys made at z.ai work on the international endpoints; a key from open.bigmodel.cn needs the China ones.";
                return new(text, true, endpoint?.OtherRegion());
            }
            case "1113":
                if (endpoint is { } general && !general.IsCoding())
                    return new("Z.ai says this account has no balance on the General API (1113). If this is a GLM Coding Plan key, switch the endpoint to Coding Plan — Coding Plan keys work only there. A General API key needs credit at z.ai.",
                        true, general.OtherKind());
                return new($"Z.ai says there's no balance or plan for this key on {place} (1113). Check that the GLM Coding Plan is active on this account, or use a General API key with credit.",
                    true, endpoint?.OtherKind());
            case "1211":
                return new($"Z.ai doesn't know the model `{model}` (1211). Pick one of its models — glm-5.3 is the current one.", true);
            case "1212" or "1220" or "1221" or "1222":
                return new($"Z.ai refused the request ({code}: {said}).", true);
            case "1301":
                return new("Z.ai's content filter turned this request down (1301). Rephrase the request, or use another model for it.", true);
            case "1308" or "1310" or "1316" or "1317" or "1318" or "1319" or "1320" or "1321":
                return new($"The Z.ai plan's usage limit is used up ({code}).{reset} Wait for it, or switch this chat to another model.", true);
            case "1309":
                return new("The GLM Coding Plan on this key has expired (1309). Renew it at z.ai/subscribe, or use a General API key.", true);
            case "1311":
                return new($"The plan on this key doesn't include `{model}` (1311). Pick a model the plan has — glm-5.3 is on every GLM Coding Plan.", true);
            case "1313":
                return new("Z.ai limited this key under its Fair Usage Policy (1313). Ask Z.ai to restore access.", true);
            case "1314" or "1315":
                return new($"Z.ai says this key is for a different product ({code}: {said}). Use a key made for the endpoint this route calls.", true);
            default:
                if (status == 401)
                    return new($"Z.ai didn't accept the key (HTTP 401{(said.Length == 0 ? "" : $": {said}")}).", true, endpoint?.OtherRegion());
                if (status == 404)
                    return new($"{endpoint?.BaseUrl() ?? "Z.ai"} doesn't know that request (404). Check the endpoint — Vision calls <endpoint>/chat/completions.", true);
                return null;
        }
    }

    /// <summary>"Your limit will reset at 2026-10-19 03:00:00" → "2026-10-19 03:00:00".</summary>
    internal static string? ResetTime(string message)
    {
        foreach (var marker in new[] { "reset at ", "resets at ", "Resets at " })
        {
            var at = message.IndexOf(marker, StringComparison.Ordinal);
            if (at < 0) continue;
            var tail = message[(at + marker.Length)..].Trim(' ', '.', '`');
            var end = tail.IndexOfAny(['\n']);
            var value = (end >= 0 ? tail[..end] : tail).Trim();
            // A trailing sentence stop ("…03:00:00.") ends the time, but the dots inside a date don't.
            if (value.EndsWith('.')) value = value[..^1];
            return value.Length == 0 ? null : value;
        }
        return null;
    }
}

public static class KimiErrors
{
    /// <summary>A Kimi / Moonshot error in plain words (null = leave it to the generic handling). Kimi Code's error reference:
    /// kimi.com/code/docs/en/kimi-code/error-reference.html.</summary>
    public static ProviderErrorExplanation? Explain(int status, string body, KnownProvider provider, string model)
    {
        var lower = ParameterRejection.Text(body).ToLowerInvariant();
        switch (provider, status)
        {
            case (KnownProvider.KimiCode, 401) when lower.Contains("invalid authentication", StringComparison.Ordinal):
                return new("Kimi Code didn't accept the key. A Moonshot platform key (or address) doesn't work with Kimi Code — use a key from the Kimi Code console, or set this route up as “Moonshot (Kimi) — pay-as-you-go API”.", true);
            case (KnownProvider.KimiCode, 401) when lower.Contains("does not have access", StringComparison.Ordinal):
                return new($"Your Kimi Code membership doesn't include `{model}`. Use kimi-for-coding, or upgrade the membership.", true);
            case (KnownProvider.KimiCode, 401) when lower.Contains("model id does not exist", StringComparison.Ordinal):
                return new($"Kimi Code doesn't know the model `{model}`. Use k3, kimi-for-coding or kimi-for-coding-highspeed.", true);
            case (KnownProvider.KimiCode, 403) when lower.Contains("usage limit", StringComparison.Ordinal):
                return new("The Kimi Code usage limit for this window is used up. Wait for it to reset, or switch this chat to another model.", true);
            case (KnownProvider.Moonshot, 401):
                return new("Moonshot didn't accept the key. A key works only in the region it was made in (platform.moonshot.ai or platform.moonshot.cn), and a Kimi Code key doesn't work here.", true);
            case (KnownProvider.Moonshot, 404) when lower.Contains("model", StringComparison.Ordinal):
                return new($"Moonshot doesn't serve `{model}` — older Kimi models were retired. kimi-k3 or kimi-k2.7-code are the current ones.", true);
            default:
                return null;
        }
    }
}

public static class DeepSeekErrors
{
    /// <summary>Said in the chat when a turn went without thinking because DeepSeek wanted reasoning this chat no longer has.</summary>
    public const string ReasoningRetryNotice =
        "DeepSeek wanted the reasoning from an earlier turn that this chat no longer has, so this turn ran without thinking.";

    /// <summary>DeepSeek's 400 when an earlier assistant turn came back without its reasoning (thinking mode, with tools):
    /// {"error":{"message":"The `reasoning_content` in the thinking mode must be passed back to the API.",
    /// "type":"invalid_request_error","param":null,"code":"invalid_request_error"}}.</summary>
    public static bool WantsReasoningBack(string body)
    {
        var lower = ParameterRejection.Text(body).ToLowerInvariant();
        return lower.Contains("reasoning_content", StringComparison.Ordinal) && lower.Contains("passed back", StringComparison.Ordinal);
    }

    /// <summary>A DeepSeek error in plain words (null = leave it to the generic handling: 422 is learned from, 429/500/503 are
    /// waited out). Codes from api-docs.deepseek.com/quick_start/error_codes; bodies as DeepSeek sends them (its 402 says
    /// "type":"unknown_error" with "code":"invalid_request_error").</summary>
    public static ProviderErrorExplanation? Explain(int status, string body, string model)
    {
        var lower = ParameterRejection.Text(body).ToLowerInvariant();
        switch (status)
        {
            case 401:
                return new("DeepSeek didn't accept the key. Check you copied the whole key from platform.deepseek.com/api_keys (it starts with sk-), or make a new one there.", true);
            case 402:
                return new("Your DeepSeek balance has run out. Top up at platform.deepseek.com/top_up, then try again.", true);
            case 400 when WantsReasoningBack(body):
                return new("DeepSeek wants the reasoning from earlier turns sent back, and this chat no longer has it. Switch thinking off for this chat, or carry on in a new chat.", true);
            case 400 or 404 when lower.Contains("model not exist", StringComparison.Ordinal) || lower.Contains("model_not_found", StringComparison.Ordinal):
                return new($"DeepSeek doesn't serve `{model}`. Use deepseek-v4-pro or deepseek-flash.", true);
            default:
                return null;
        }
    }
}

public static partial class AnthropicErrors
{
    /// <summary>The error's type and message from Anthropic's body: {"type":"error","error":{"type":"authentication_error",
    /// "message":"invalid x-api-key"},"request_id":"req_…"}.</summary>
    public static (string? Type, string Message) Parse(string body)
    {
        try
        {
            if (JsonNode.Parse(body) is JsonObject obj && obj["error"] is JsonObject error)
                return (JsonArgs.String(error, "type"), JsonArgs.String(error, "message") ?? "");
        }
        catch (JsonException) { }
        return (null, body.Trim());
    }

    /// <summary>"prompt is too long: 1012345 tokens > 1000000 maximum" → 1000000.</summary>
    public static int? OverflowLimit(string body)
    {
        var m = PromptTooLong().Match(body);
        return m.Success && int.TryParse(m.Groups[1].Value, out var limit) && limit > 0 ? limit : null;
    }

    [GeneratedRegex(@"prompt is too long:\s*\d+\s*tokens\s*>\s*(\d+)\s*maximum", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PromptTooLong();

    /// <summary>An Anthropic error in plain words. Types from the API's error reference: invalid_request_error (400),
    /// authentication_error (401), billing_error (402), permission_error (403), not_found_error (404), request_too_large (413),
    /// rate_limit_error (429), api_error (500), overloaded_error (529). Rate limits and overload are waited out (not
    /// permanent); the rest can't be.</summary>
    public static ProviderErrorExplanation? Explain(int status, string body, string model)
    {
        var (type, message) = Parse(body);
        var said = message.Length == 0 ? "" : $" Anthropic said: {TextUtil.Prefix(message, 200)}";
        switch (type, status)
        {
            case ("authentication_error", _) or (_, 401):
                return new("Anthropic didn't accept the key. Use an API key from console.anthropic.com (it starts with sk-ant-api) — a Claude Pro or Max sign-in isn't an API key.", true);
            case ("billing_error", _) or (_, 402):
                return new($"Your Anthropic account needs credit or a payment method: console.anthropic.com → Billing.{said}", true);
            case ("permission_error", _) or (_, 403):
                return new($"Anthropic doesn't allow this request with this key.{said}", true);
            case ("not_found_error", _) or (_, 404):
                return new($"Anthropic doesn't offer `{model}` to this key. Pick another model, such as claude-opus-5-5, claude-sonnet-5-5 or claude-haiku-5-5.", true);
            case ("rate_limit_error", _) or (_, 429):
                return new($"Anthropic's rate limit for this key was reached; Vision waits and tries again.{said}", false);
            case ("overloaded_error", _) or (_, 529):
                return new("Anthropic is overloaded right now; Vision waits and tries again.", false);
            case ("invalid_request_error", 400) when message.Length > 0:
                return new($"Anthropic refused the request: {TextUtil.Prefix(message, 300)}", true);
            default:
                return null;
        }
    }
}

// MARK: - Testing a Z.ai key

/// <summary>The Test button for a Z.ai route: a one-token reply on the chosen endpoint; when that fails because of the key or
/// the account (wrong key type, wrong region, no balance), the same key is tried on the endpoints it may belong to, so the
/// answer can say "This looks like a GLM Coding Plan key — switch the endpoint to Coding Plan".</summary>
public static class ZaiDiagnosis
{
    /// <param name="Ok">The key answered on the endpoint it was tested on.</param>
    /// <param name="WorksOn">The endpoint the key worked on instead (offer to switch to it).</param>
    public sealed record Result(bool Ok, string Message, ZaiEndpoint? WorksOn = null);

    /// <summary>Codes (and HTTP statuses) that say the key or account doesn't fit the endpoint — worth trying elsewhere.</summary>
    internal static readonly IReadOnlySet<string> KeyProblems = new HashSet<string>(StringComparer.Ordinal)
    {
        "1000", "1001", "1002", "1003", "1004", "401", "1113", "1309", "1314", "1315",
    };

    /// <summary><paramref name="ping"/> sends a one-token request to an endpoint and throws what the server said.</summary>
    public static async Task<Result> RunAsync(ZaiEndpoint endpoint, string model, Func<ZaiEndpoint, CancellationToken, Task> ping,
                                              CancellationToken cancellationToken = default)
    {
        try
        {
            await ping(endpoint, cancellationToken).ConfigureAwait(false);
            return new(true, $"✔ The key works on {endpoint.Name()} with {model}.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            var (status, body) = StatusAndBody(error);
            var explained = status is { } s ? ZaiErrors.Explain(s, body, endpoint, model) : null;
            var code = ZaiErrors.Parse(body)?.Code ?? status?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";
            var first = explained?.Message ?? Plain(error);
            if (!KeyProblems.Contains(code) && status != 401) return new(false, first);
            var candidates = new List<ZaiEndpoint>();
            if (explained?.SuggestedEndpoint is { } suggested) candidates.Add(suggested);
            foreach (var c in new[] { endpoint.OtherKind(), endpoint.OtherRegion() })
                if (!candidates.Contains(c)) candidates.Add(c);
            foreach (var candidate in candidates)
            {
                try
                {
                    await ping(candidate, cancellationToken).ConfigureAwait(false);
                    return new(false, SwitchAdvice(endpoint, candidate), candidate);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception)
                {
                    // Not this one either.
                }
            }
            return new(false, first);
        }
    }

    /// <summary>What to say when the key works on another endpoint.</summary>
    public static string SwitchAdvice(ZaiEndpoint from, ZaiEndpoint to)
    {
        if (to.IsCoding() && !from.IsCoding())
            return $"This looks like a GLM Coding Plan key — switch the endpoint to {to.Name()}. (It answers there; on {from.Name()} Z.ai says the account has no balance.)";
        if (!to.IsCoding() && from.IsCoding())
            return $"This looks like a General API key, not a GLM Coding Plan one — switch the endpoint to {to.Name()}.";
        return to.IsChina()
            ? $"This key is from open.bigmodel.cn — switch the endpoint to {to.Name()}."
            : $"This key is from z.ai — switch the endpoint to {to.Name()}.";
    }

    private static (int? Status, string Body) StatusAndBody(Exception error) =>
        error is LlmException { Kind: LlmErrorKind.Http } http ? (http.StatusCode, http.Body) : (null, "");

    private static string Plain(Exception error) => error switch
    {
        LlmException { Kind: LlmErrorKind.Connection or LlmErrorKind.Sse } => "Couldn't reach Z.ai. Check the internet connection, then try again.",
        HttpRequestException => "Couldn't reach Z.ai. Check the internet connection, then try again.",
        _ => error.Message,
    };
}
