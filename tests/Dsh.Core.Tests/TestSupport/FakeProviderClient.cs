using System.Runtime.CompilerServices;

namespace Dsh.Core.Tests;

/// <summary>A provider client whose stream is a script: each request is recorded and answered by
/// <paramref name="reply"/> — a list of events, or an exception thrown before (or after) some of them.</summary>
public sealed class FakeProviderClient(ProviderProfile profile, Func<LlmRequest, int, FakeStream> reply) : IProviderClient
{
    private readonly Lock _lock = new();
    private readonly List<LlmRequest> _requests = [];

    public ProviderProfile Profile { get; } = profile;

    public IReadOnlyList<LlmRequest> Requests
    {
        get
        {
            lock (_lock) return [.. _requests];
        }
    }

    /// <summary>What <c>/models</c> says this server serves (empty: it doesn't answer).</summary>
    public IReadOnlyList<string> Served { get; init; } = ["served-model"];

    public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        int index;
        lock (_lock)
        {
            _requests.Add(request);
            index = _requests.Count;
        }
        var script = reply(request, index);
        await Task.Yield();
        foreach (var ev in script.Events)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return ev;
        }
        if (script.Error is { } error) throw error;
    }

    public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default) => Task.FromResult(Served);

    public Task<ModelInfo> ModelInfoAsync(string? model = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ModelInfo(Served.FirstOrDefault() ?? "", null, null, Served));

    /// <summary>A plain text reply, streamed in the given pieces.</summary>
    public static FakeStream Text(params string[] pieces) =>
        new([.. pieces.Select(p => (LlmStreamEvent)new LlmStreamEvent.Text(p)), new LlmStreamEvent.Done([], "stop", null)]);

    public static FakeStream Fails(Exception error, params LlmStreamEvent[] before) => new(before, error);
}

/// <summary>One scripted reply: these events, then (optionally) an exception.</summary>
public sealed record FakeStream(IReadOnlyList<LlmStreamEvent> Events, Exception? Error = null);
