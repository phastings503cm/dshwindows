namespace Vision.Core;

/// <summary>What Vision keeps inside a project: a <c>.vision</c> folder (skills, agents, plugins) and a
/// <c>VISION.md</c> instruction file.</summary>
/// <remarks>Vision was called DSH before 0.9, and projects set up then have <c>.dsh</c> and <c>DSH.md</c> —
/// often committed to the repository. Both names are read, always (the new one first, so it wins a clash); only
/// the new names are ever written, and nothing in a project is ever moved or renamed.</remarks>
public static class ProjectFolders
{
    /// <summary>The app's own folder in a project.</summary>
    public const string Name = ".vision";

    /// <summary>Legacy: DSH's folder in a project. Read, never written.</summary>
    public const string LegacyName = ".dsh";

    /// <summary>The project instruction file of this app's own.</summary>
    public const string InstructionFile = "VISION.md";

    /// <summary>Legacy: DSH's instruction file. Read, never written.</summary>
    public const string LegacyInstructionFile = "DSH.md";

    /// <summary><c>&lt;project&gt;\.vision\&lt;part&gt;</c>: where a new skill, agent or plugin is written.</summary>
    public static string Folder(string project, string part) => Path.Combine(project, Name, part);

    /// <summary>Legacy: <c>&lt;project&gt;\.dsh\&lt;part&gt;</c>.</summary>
    public static string LegacyFolder(string project, string part) => Path.Combine(project, LegacyName, part);

    /// <summary>The folders to read <paramref name="part"/> from, in precedence order: <c>.vision</c>, then <c>.dsh</c>.</summary>
    public static IReadOnlyList<string> ReadFolders(string project, string part) => [Folder(project, part), LegacyFolder(project, part)];
}
