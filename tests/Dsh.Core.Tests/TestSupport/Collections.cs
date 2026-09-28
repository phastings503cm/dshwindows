namespace Dsh.Core.Tests;

/// <summary>Tests that mutate process-wide state (ReasoningEffortCache.Shared, SecretStore.UseMemoryOnly,
/// environment variables such as DSH_HOME). xUnit runs non-parallel collections on their own, after
/// every parallel collection has finished, so nothing else observes the temporary values.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SerialStaticState
{
    public const string Name = "Serial static state";
}

/// <summary>Tests whose assertions depend on wall-clock timing (a shell command's idle limit). They
/// run on their own so CPU contention from the rest of the suite can't skew the clock.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TimingSensitive
{
    public const string Name = "Timing-sensitive";
}
