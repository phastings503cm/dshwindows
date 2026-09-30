namespace Dsh.Core;

/// <summary>Where Claude Code and Cursor keep their files. <see cref="Standard"/> is the real profile;
/// tests and the UI self-test point <see cref="Home"/> at a scratch folder so nothing reads the
/// developer's own.</summary>
/// <param name="Home">The user's profile folder (%USERPROFILE%).</param>
/// <param name="ClaudeConfigDir">Claude Code's CLAUDE_CONFIG_DIR, when it has been moved off ~\.claude.</param>
public sealed record ExternalLocations(string Home, string? ClaudeConfigDir = null)
{
    public static ExternalLocations Standard =>
        new(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR"));

    /// <summary>Claude Code's folder: CLAUDE.md, skills, commands, agents, rules, plugins, projects.</summary>
    public string ClaudeHome => string.IsNullOrWhiteSpace(ClaudeConfigDir) ? Path.Combine(Home, ".claude") : ClaudeConfigDir;

    /// <summary>Claude Code's global state file, where its user-wide MCP servers are listed.</summary>
    public string ClaudeGlobalConfig =>
        string.IsNullOrWhiteSpace(ClaudeConfigDir) ? Path.Combine(Home, ".claude.json") : Path.Combine(ClaudeConfigDir, ".claude.json");

    /// <summary>Cursor's folder: skills, commands, agents, rules, plugins, mcp.json.</summary>
    public string CursorHome => Path.Combine(Home, ".cursor");
}
