using System.Runtime.CompilerServices;

namespace Vision.Core;

// MARK: - What a Claude Code route answers to code that expects a model server

/// <summary>The stand-in every HTTP-shaped path gets for a Claude Code route: the context probe (the window the CLI last
/// reported, else 200K), Settings' Test (is the CLI there and signed in?), and a plain refusal for anything that wants a
/// raw model call (a Vision subagent, a skill draft, a summary) — the claude CLI answers chats, not single requests.
/// Chats on the route never come here: the host runs them through <see cref="ClaudeCodeRunner"/>.</summary>
public sealed class ClaudeCodeClient(ProviderProfile profile) : IProviderClient
{
    /// <summary>The path picked in Settings › Claude Code (the app sets it; empty = search).</summary>
    public static string? ConfiguredPath { get; set; }

    /// <summary>How the status check runs its commands (tests replace it).</summary>
    public static ClaudeCodeCommandRunner? RunnerForTesting { get; set; }

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

    public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        throw LlmException.Rejected(0,
            "Claude Code answers chats only — it can't run Vision's own side jobs (subagents, summaries, drafting skills). " +
            "Make another model the primary in Settings › AI Models for those, and keep Claude Code for the chats you put on it.", "", permanent: true);
#pragma warning disable CS0162 // an iterator needs a yield
        yield break;
#pragma warning restore CS0162
    }
}
