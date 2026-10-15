namespace Vision.Core;

/// <summary>The variables that tell a command the agent started it (a script can behave differently — no prompts,
/// no pager — when it sees them).</summary>
public static class AgentEnvironment
{
    /// <summary>Set to "1" for every command and background process the agent runs.</summary>
    public const string Variable = "VISION_AGENT";

    /// <summary>Legacy: the variable DSH (Vision's former name) set. Nothing in the app reads it, but users' own scripts
    /// written for DSH may check it, so it is still set alongside <see cref="Variable"/>.</summary>
    public const string LegacyVariable = "DSH_AGENT";

    /// <summary>Both variables, for an environment being built.</summary>
    public static IEnumerable<KeyValuePair<string, string>> Markers =>
        [new(Variable, "1"), new(LegacyVariable, "1")];
}
