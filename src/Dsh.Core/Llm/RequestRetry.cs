using System.Net.Sockets;
using System.Security.Authentication;

namespace Dsh.Core;

// MARK: - Retrying model calls
//
// A model server on the desk (the Spark) goes away for ordinary reasons: it is swapping models
// (minutes), restarting, loading a model cold (~11 min for a big one), prefilling a huge context
// slower than the idle timeout, or simply overloaded. None of those should kill a chat turn, a
// /goal, or a week-long task queue. So a model call that fails for a reason that can fix itself is
// retried — with backoff, indefinitely — until the server answers or the user presses Stop
// (cancellation ends the wait at once). Failures that can't fix themselves (a malformed request,
// bad credentials, a bad URL) surface immediately instead of spinning.

/// <summary>How a failed model call should be handled.</summary>
public abstract record RetryDisposition
{
    /// <summary>Surface the error now; trying again cannot help.</summary>
    public sealed record Fail : RetryDisposition;
    /// <summary>The server is unreachable, slow, restarting or busy: retry until it answers.</summary>
    public sealed record UntilAvailable : RetryDisposition;
    /// <summary>The server answered but choked on this request (HTTP 500): often transient (an OOM, a
    /// worker restart), sometimes deterministic — retry a bounded number of times, then surface it.</summary>
    public sealed record Limited(int Times) : RetryDisposition;
}

/// <summary>Retry settings for an engine's model calls.</summary>
public sealed record RetryPolicy
{
    /// <summary>False disables retrying entirely (every failure surfaces at once).</summary>
    public bool Enabled { get; init; } = true;
    /// <summary>How long to wait before retry attempt n (1-based).</summary>
    public Func<int, TimeSpan> Delay { get; init; } = RequestRetry.Backoff;
    /// <summary>Give up (surface the error) after this many failures in a row, whatever their kind. Null =
    /// keep retrying transient failures for as long as it takes. A subagent on a multi-server fleet sets
    /// this so a dead server hands its task to another instead of being waited on for ever.</summary>
    public int? MaxFailures { get; init; }

    /// <summary>Retry transient failures indefinitely with the standard backoff.</summary>
    public static RetryPolicy Standard { get; } = new();
    /// <summary>Never retry.</summary>
    public static RetryPolicy Off { get; } = new() { Enabled = false };

    /// <summary>Whether to try again after the <paramref name="attempt"/>-th consecutive failure (1-based).</summary>
    public bool ShouldRetry(Exception error, int attempt)
    {
        if (!Enabled) return false;
        return RequestRetry.Disposition(error) switch
        {
            RetryDisposition.UntilAvailable => true,
            RetryDisposition.Limited limited => attempt <= limited.Times,
            _ => false,
        };
    }
}

public static class RequestRetry
{
    /// <summary>Backoff: 2s, 4s, 8s, 16s, then every 30s for as long as it takes.</summary>
    public static TimeSpan Backoff(int attempt)
    {
        var n = Math.Max(1, attempt);
        return n >= 5 ? TimeSpan.FromSeconds(30) : TimeSpan.FromSeconds(1 << n);
    }

    /// <summary>How many times an HTTP 500 is retried before it is reported.</summary>
    public const int ServerErrorRetries = 8;

    /// <summary>Classify a model-call failure.</summary>
    public static RetryDisposition Disposition(Exception error)
    {
        switch (error)
        {
            case OperationCanceledException:
            case LlmException { Permanent: true }:
                return new RetryDisposition.Fail();
            case LlmException llm:
                return llm.Kind switch
                {
                    LlmErrorKind.NoModel or LlmErrorKind.Unsupported or LlmErrorKind.Overflow => new RetryDisposition.Fail(),
                    // Unreachable, or the stream was cut off mid-reply — unless the connection failed
                    // for a reason time won't fix (an untrusted certificate, rejected credentials).
                    LlmErrorKind.Connection => llm.InnerException is { } inner && Permanent(inner)
                        ? new RetryDisposition.Fail()
                        : new RetryDisposition.UntilAvailable(),
                    LlmErrorKind.Sse => new RetryDisposition.UntilAvailable(),
                    _ => HttpStatus(llm.StatusCode, llm.Body),
                };
            case HttpRequestException http:
                return Permanent(http) ? new RetryDisposition.Fail() : new RetryDisposition.UntilAvailable();
            case IOException or SocketException or TimeoutException:
                // Socket-level failures (connection reset/refused) that escape the client's mapping.
                return new RetryDisposition.UntilAvailable();
            default:
                return new RetryDisposition.Fail();
        }
    }

    /// <summary>Connection failures that retrying can't fix.</summary>
    private static bool Permanent(Exception error)
    {
        for (var e = error; e is not null; e = e.InnerException)
        {
            if (e is AuthenticationException) return true; // the server's certificate isn't trusted
            if (e is HttpRequestException { HttpRequestError: HttpRequestError.UserAuthenticationError
                                                           or HttpRequestError.ConfigurationLimitExceeded })
                return true;
            if (e is UriFormatException or NotSupportedException) return true; // a bad URL / scheme
        }
        return false;
    }

    public static RetryDisposition HttpStatus(int code, string body)
    {
        switch (code)
        {
            case 408 or 409 or 425 or 429 or 502 or 503 or 504 or 507 or (>= 520 and <= 524) or 529 or 598 or 599:
                // Timeout, overloaded, gateway can't reach the model (nginx in front of a server that
                // is down or restarting).
                return new RetryDisposition.UntilAvailable();
            case 500:
                return new RetryDisposition.Limited(ServerErrorRetries);
            case 404:
            {
                // "The model `x` does not exist" while the Spark swaps models: the route re-resolves
                // between attempts, so this heals itself.
                var lower = body.ToLowerInvariant();
                if (lower.Contains("model")
                    && (lower.Contains("not found") || lower.Contains("does not exist") || lower.Contains("not exist")
                        || lower.Contains("unknown") || lower.Contains("not loaded") || lower.Contains("loading")))
                    return new RetryDisposition.UntilAvailable();
                return new RetryDisposition.Fail();
            }
            case 400:
            {
                // A few servers say "still loading" with a 400.
                var lower = body.ToLowerInvariant();
                if (lower.Contains("loading model") || lower.Contains("model is loading") || lower.Contains("model is not loaded")
                    || lower.Contains("server is starting") || lower.Contains("not ready"))
                    return new RetryDisposition.UntilAvailable();
                return new RetryDisposition.Fail();
            }
            default:
                return code >= 500 ? new RetryDisposition.Limited(ServerErrorRetries) : new RetryDisposition.Fail();
        }
    }

    /// <summary>A short, plain reason for the status line ("timed out", "server unreachable").</summary>
    public static string Reason(Exception error)
    {
        switch (error)
        {
            case LlmException { Kind: LlmErrorKind.Http } http:
            {
                var snippet = http.Body.Trim().Replace("\r", " ").Replace('\n', ' ');
                return snippet.Length == 0 ? $"HTTP {http.StatusCode}" : $"HTTP {http.StatusCode}: {TextUtil.Prefix(snippet, 80)}";
            }
            case LlmException { Kind: LlmErrorKind.Sse } sse:
                return $"stream cut off ({TextUtil.Prefix(Detail(sse.Message), 80)})";
            case LlmException { Kind: LlmErrorKind.Connection } connection:
                return $"server unreachable ({TextUtil.Prefix(Detail(connection.Message), 80)})";
            case LlmException llm:
                return llm.Message;
            case TimeoutException:
                return "the request timed out";
            case HttpRequestException http:
                return http.HttpRequestError switch
                {
                    HttpRequestError.NameResolutionError => "the server's address can't be found",
                    HttpRequestError.ConnectionError => "the server refused the connection",
                    HttpRequestError.SecureConnectionError => "the secure connection failed",
                    HttpRequestError.ResponseEnded => "the connection dropped",
                    HttpRequestError.InvalidResponse => "the server sent a bad response",
                    _ => http.Message,
                };
            case IOException or SocketException:
                return "the connection dropped";
            default:
                return error.Message;
        }
    }

    /// <summary>The "(why)" inside an LlmException's user-facing sentence, when it has one.</summary>
    private static string Detail(string message)
    {
        var open = message.IndexOf('(');
        var close = message.LastIndexOf(')');
        return open >= 0 && close > open ? message[(open + 1)..close] : message;
    }
}
