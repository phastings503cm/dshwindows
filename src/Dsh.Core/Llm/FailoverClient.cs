using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

namespace Dsh.Core;

// MARK: - Backups for the primary
//
// The primary model is wrapped in a failover client: when a request to it fails before anything came back — it
// can't be reached, timed out, refused the key, lost the model, is overloaded or erroring — the same request goes
// to each enabled backup route in turn. A turn that ran on a backup says so in the chat. When every one fails the
// primary's own error surfaces, so the usual retry-with-back-off carries on exactly as before. A reply that has
// started is never moved: half an answer from one model and half from another is worse than a retry. Subagents
// don't use backups (the fleet already moves their tasks between servers), and each backup keeps its own privacy
// shield.

/// <summary>A turn that ran on a backup: who didn't answer, why, and who did.</summary>
public sealed record FailoverNotice(string Primary, string Reason, string Backup)
{
    /// <summary>"Spark didn't answer (server unreachable), so this turn ran on Kimi Code."</summary>
    public string Text => $"{Primary} didn't answer ({Reason}), so this turn ran on {Backup}.";
}

public sealed class FailoverClient(IProviderClient primary, IReadOnlyList<IProviderClient> backups, Action<FailoverNotice>? notice = null)
    : IProviderClient
{
    public IProviderClient Primary => primary;
    public IReadOnlyList<IProviderClient> Backups => backups;
    public ProviderProfile Profile => primary.Profile;

    public Task<ModelInfo> ModelInfoAsync(string? model = null, CancellationToken cancellationToken = default) =>
        primary.ModelInfoAsync(model, cancellationToken);

    public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default) =>
        primary.ListModelsAsync(cancellationToken);

    /// <summary>A failure another route may not have: the server unreachable or timing out, the key refused (401/403),
    /// the model gone (404), a timeout (408), the limit reached (429), or a server error (5xx). Not a request the
    /// server called wrong (400), a conversation too long for the window, or a Stop.</summary>
    public static bool ShouldFailOver(Exception error)
    {
        switch (error)
        {
            case OperationCanceledException:
                return false;
            case LlmException llm:
                return llm.Kind switch
                {
                    LlmErrorKind.Connection or LlmErrorKind.Sse => true,
                    LlmErrorKind.Http => llm.StatusCode is 401 or 403 or 404 or 408 or 429 or >= 500,
                    _ => false,
                };
            case HttpRequestException or IOException or SocketException or TimeoutException:
                return true;
            default:
                return false;
        }
    }

    public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(LlmRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Exception? primaryError = null;
        var candidates = new List<IProviderClient>(1 + backups.Count) { primary };
        candidates.AddRange(backups);
        for (var index = 0; index < candidates.Count; index++)
        {
            var client = candidates[index];
            var attempt = request;
            if (index > 0)
            {
                // A backup answers with its own model and settings (the request carries the primary's).
                if (await ServedModelAsync(client, cancellationToken).ConfigureAwait(false) is not { } model) continue;
                attempt = request with { Model = model, Temperature = null, MaxTokens = null };
            }
            var stream = client.StreamAsync(attempt, cancellationToken).GetAsyncEnumerator(cancellationToken);
            var started = false;
            try
            {
                while (true)
                {
                    bool moved;
                    try
                    {
                        moved = await stream.MoveNextAsync().ConfigureAwait(false);
                    }
                    catch (Exception error) when (!started && !cancellationToken.IsCancellationRequested && (index > 0 || ShouldFailOver(error)))
                    {
                        // Nothing came back yet: the next candidate may answer. (A backup that fails in any way is just skipped.)
                        primaryError ??= error;
                        break;
                    }
                    if (!moved)
                    {
                        if (!started && index > 0) Notify(primaryError, client);
                        yield break;
                    }
                    if (!started)
                    {
                        started = true;
                        if (index > 0) Notify(primaryError, client);
                    }
                    yield return stream.Current;
                }
            }
            finally
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
            if (index == 0 && backups.Count == 0) break;
        }
        ExceptionDispatchInfo.Capture(primaryError ?? LlmException.NoModel()).Throw();
    }

    private void Notify(Exception? primaryError, IProviderClient backup)
    {
        try
        {
            notice?.Invoke(new FailoverNotice(Label(primary.Profile), primaryError is null ? "no reply" : RequestRetry.Reason(primaryError),
                Label(backup.Profile)));
        }
        catch (Exception)
        {
            // The notice is a courtesy; the reply matters more.
        }
    }

    private static string Label(ProviderProfile profile) => profile.Name.Length > 0 ? profile.Name : profile.Model;

    /// <summary>The model a backup serves right now: a self-hosted server is asked (a Spark may have switched models; one
    /// that doesn't answer is skipped quickly); anything else serves its configured model.</summary>
    private static async Task<string?> ServedModelAsync(IProviderClient client, CancellationToken cancellationToken)
    {
        var profile = client.Profile;
        if (profile.Kind == ProviderKind.Bedrock || !profile.IsSelfHosted) return profile.Model;
        try
        {
            var info = await client.ModelInfoAsync(null, cancellationToken).ConfigureAwait(false);
            if (info.Served.Count == 0) return null;
            return info.Id.Length > 0 ? info.Id : profile.Model;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }
}
