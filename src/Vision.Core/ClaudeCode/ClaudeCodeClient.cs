using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Vision.Core;

// MARK: - What a Claude Code route answers to code that expects a model server

/// <summary>The client every HTTP-shaped path gets for a Claude Code route. Chats on the route never come here — the host
/// runs their turns through <see cref="ClaudeCodeRunner"/> with Claude Code's own agent, tools and session. What does
/// come here: the context probe (the window the CLI last reported, else 200K), Settings' Test (is the CLI there and
/// signed in?), and the few one-off requests Vision makes of a chat's model — a summary, a skill draft, a subagent with
/// Claude Code as the primary — each answered by one `claude -p` with Claude Code's system prompt replaced by Vision's,
/// no tools and no saved session, streamed back as text (the same as Vision for macOS).</summary>
public sealed class ClaudeCodeClient(ProviderProfile profile) : IProviderClient
{
    /// <summary>The path picked in Settings › Claude Code (the app sets it; empty = search).</summary>
    public static string? ConfiguredPath { get; set; }

    /// <summary>How the status check runs its commands (tests replace it).</summary>
    public static ClaudeCodeCommandRunner? RunnerForTesting { get; set; }

    /// <summary>How one-off requests start the CLI, and which CLI (tests replace them).</summary>
    public static ClaudeCodeProcessStarter? StarterForTesting { get; set; }
    public static ClaudeCodeLaunch? LaunchForTesting { get; set; }

    private static readonly ConcurrentDictionary<string, ClaudeCodeLaunch> Launches = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Find the CLI again next time (Settings changed its path, or it was installed).</summary>
    public static void ResetCache() => Launches.Clear();

    public ProviderProfile Profile { get; } = profile;

    public Task<ModelInfo> ModelInfoAsync(string? model = null, CancellationToken cancellationToken = default)
    {
        var alias = model ?? Profile.Model;
        return Task.FromResult(new ModelInfo(alias, ClaudeCodeRoutes.LearnedWindow(alias) ?? ClaudeCodeRoutes.DefaultContextWindow,
            ServedModels: ClaudeCodeRoutes.Models.Select(m => m.Alias).ToList()));
    }

    /// <summary>Settings' Test: the CLI is found, runs, and is signed in.</summary>
    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        var status = await CheckAsync(Profile, cancellationToken).ConfigureAwait(false);
        return status is ClaudeCodeStatus.Ready
            ? ClaudeCodeRoutes.Models.Select(m => m.Alias).ToList()
            : throw LlmException.Rejected(0, status.Summary, "", permanent: true);
    }

    /// <summary>Check the CLI a route uses (on this computer, or in WSL).</summary>
    public static Task<ClaudeCodeStatus> CheckAsync(ProviderProfile route, CancellationToken cancellationToken = default) =>
        ClaudeCodeCli.CheckAsync(ClaudeCodeSearch.ForThisMachine(ConfiguredPath is { Length: > 0 } p ? p : null), RunnerForTesting,
            ClaudeCodeRoutes.UsesWsl(route) ? ClaudeCodeLaunch.InWsl(ClaudeCodeRoutes.WslDistro(route)) : null, cancellationToken);

    private async Task<ClaudeCodeLaunch> LaunchAsync(CancellationToken cancellationToken)
    {
        if (LaunchForTesting is { } testing) return testing;
        if (Launches.TryGetValue(Profile.BaseUrl, out var known)) return known;
        var status = await CheckAsync(Profile, cancellationToken).ConfigureAwait(false);
        var launch = status switch
        {
            ClaudeCodeStatus.Ready ready => ready.Launch,
            ClaudeCodeStatus.SignedOut signedOut => signedOut.Launch,
            _ => throw LlmException.Rejected(0, status.Summary, "", permanent: true),
        };
        Launches[Profile.BaseUrl] = launch;
        return launch;
    }

    /// <summary>One-off request: the conversation as one prompt, no tools — a reply of text only.</summary>
    public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var launch = await LaunchAsync(cancellationToken).ConfigureAwait(false);
        var events = Channel.CreateUnbounded<LlmStreamEvent>();
        var streamed = false;
        var stream = new ClaudeCodeStream(e =>
        {
            switch (e)
            {
                case EngineEvent.TextDelta d:
                    events.Writer.TryWrite(new LlmStreamEvent.Text(d.Text));
                    break;
                case EngineEvent.ReasoningDelta r:
                    events.Writer.TryWrite(new LlmStreamEvent.Reasoning(r.Text));
                    break;
                // Whole text blocks (an older CLI without partial messages): the text, once.
                case EngineEvent.AssistantMessage m when !streamed:
                    events.Writer.TryWrite(new LlmStreamEvent.Text(m.Text));
                    break;
            }
            if (e is EngineEvent.TextDelta) streamed = true;
        });
        var options = new ClaudeCodeTurnOptions
        {
            Model = request.Model.Length > 0 ? request.Model : Profile.Model,
            Thinking = request.Thinking ?? Profile.Thinking,
            OneShotSystemPrompt = request.SystemPrompt.Length > 0 ? request.SystemPrompt : "You are a helpful assistant.",
        };
        var run = Task.Run(async () =>
        {
            try
            {
                return await ClaudeCodeRunner.RunAsync(launch, options, Path.GetTempPath(), ClaudeCodeCommand.UserMessage(Prompt(request.Messages)), stream,
                    (_, _) => Task.FromResult<ClaudeCodePermissionDecision>(new ClaudeCodePermissionDecision.Deny(ClaudeCodePermissions.Declined)),
                    cancellationToken, StarterForTesting).ConfigureAwait(false);
            }
            finally
            {
                events.Writer.TryComplete();
            }
        }, CancellationToken.None);
        await foreach (var e in events.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false)) yield return e;
        var outcome = await run.ConfigureAwait(false);
        if (outcome.Failure is { } failure)
            throw LlmException.Rejected(failure.Kind == ClaudeCodeFailureKind.NotSignedIn ? 401 : 500, failure.Message, "",
                permanent: failure.Kind is ClaudeCodeFailureKind.NotSignedIn or ClaudeCodeFailureKind.RateLimited);
        yield return new LlmStreamEvent.Done([], "stop", outcome.Usage ?? outcome.Result?.Usage);
    }

    /// <summary>The request's messages as one prompt: a single message as it is, a conversation as labelled turns.</summary>
    public static string Prompt(IReadOnlyList<LlmMessage> messages)
    {
        var spoken = messages.Where(m => m.Role != MessageRole.System && !string.IsNullOrEmpty(m.Content)).ToList();
        if (spoken.Count == 1 && spoken[0].Role == MessageRole.User) return spoken[0].Content!;
        var lines = spoken.Select(m => (m.Role switch
        {
            MessageRole.User => "Person",
            MessageRole.Assistant => "Assistant",
            MessageRole.Tool => "Tool result" + (m.Name is { Length: > 0 } name ? $" ({name})" : ""),
            _ => "System",
        }) + ": " + m.Content);
        return string.Join("\n\n", lines) + "\n\n(Reply to the last Person message.)";
    }
}
