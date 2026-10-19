namespace Vision.Core;

/// <summary>Where Vision keeps its own files: %APPDATA%\Vision on Windows (settings, conversations,
/// skills, plugins, drafts). The VISION_HOME environment variable points it elsewhere (tests and the
/// self-test do); a file named <c>portable</c> next to Vision.exe keeps everything in a <c>data</c>
/// folder beside it, for running from a USB stick.</summary>
/// <remarks>Vision was called DSH before 0.9. Its data folder (%APPDATA%\DSH), its environment variable
/// (DSH_HOME) and a portable copy's folder are still recognised: <see cref="DataFolderMigration"/> brings
/// the old folder over once, and DSH_HOME keeps working for scripts that set it.</remarks>
public static class AppPaths
{
    /// <summary>The environment variable that moves the data folder.</summary>
    public const string HomeVariable = "VISION_HOME";

    /// <summary>The folder's name under %APPDATA%.</summary>
    public const string FolderName = "Vision";

    /// <summary>Legacy: the variable DSH (Vision's former name) read. Still honoured when VISION_HOME is not
    /// set, so a script written for DSH keeps pointing the app at the same folder.</summary>
    public const string LegacyHomeVariable = "DSH_HOME";

    /// <summary>Legacy: the folder DSH (Vision's former name) kept under %APPDATA%, and the folder a portable
    /// copy of DSH unpacked to. Read only, to bring the user's data over (<see cref="DataFolderMigration"/>).</summary>
    public const string LegacyFolderName = "DSH";

    /// <summary>For this session only: run on another folder — the DSH folder itself, when bringing it over
    /// failed (it is tried again on the next launch). Null normally.</summary>
    public static string? SessionRoot { get; set; }

    public static string Root
    {
        get
        {
            if (SessionRoot is { Length: > 0 } session) return session;
            if (Overridden is { } overridden) return overridden;
            if (PortableRoot is { } portable) return portable;
            return DefaultRoot;
        }
    }

    /// <summary>The folder VISION_HOME (or, failing that, the legacy DSH_HOME) names, or null.</summary>
    public static string? Overridden
    {
        get
        {
            var home = Environment.GetEnvironmentVariable(HomeVariable);
            if (!string.IsNullOrWhiteSpace(home)) return home;
            var legacy = Environment.GetEnvironmentVariable(LegacyHomeVariable);
            return string.IsNullOrWhiteSpace(legacy) ? null : legacy;
        }
    }

    /// <summary>%APPDATA%\Vision.</summary>
    public static string DefaultRoot => Path.Combine(ApplicationData, FolderName);

    /// <summary>Legacy: %APPDATA%\DSH, where DSH kept everything.</summary>
    public static string LegacyDefaultRoot => Path.Combine(ApplicationData, LegacyFolderName);

    private static string ApplicationData => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    /// <summary>The data folder of a portable copy, or null.</summary>
    public static string? PortableRoot => PortableRootFor(AppContext.BaseDirectory);

    /// <summary>The data folder of a portable copy whose program is in <paramref name="programFolder"/>, or null.</summary>
    public static string? PortableRootFor(string programFolder) =>
        File.Exists(Path.Combine(programFolder, "portable")) ? Path.Combine(programFolder, "data") : null;

    /// <summary>Legacy: the data folder of a portable DSH unpacked beside this portable copy — the zip used to
    /// unpack to a folder named DSH, and now unpacks to one named Vision, so a user who unpacks the new one next
    /// to the old one finds DSH\data beside Vision\. Null when there is none (or this copy is in that very
    /// folder, which already has its data beside it).</summary>
    public static string? LegacyPortableRootFor(string programFolder)
    {
        var program = Path.TrimEndingDirectorySeparator(Path.GetFullPath(programFolder));
        if (Path.GetDirectoryName(program) is not { } parent) return null;
        var legacyProgram = Path.Combine(parent, LegacyFolderName);
        if (string.Equals(legacyProgram, program, StringComparison.OrdinalIgnoreCase)) return null;
        return PortableRootFor(legacyProgram) is { } data && Directory.Exists(data) ? data : null;
    }

    /// <summary>Legacy: the program file of DSH (Vision's former name). A portable DSH kept its data folder beside it.</summary>
    public const string LegacyExeName = "DSH.exe";

    /// <summary>Legacy: whether the folder this copy runs on can also be the data folder of DSH (Vision's former
    /// name), which runs under its own single-instance name, so the two could run at once and overwrite each other's
    /// settings, chats and task list. See <see cref="MayShareWithLegacyApp(string, string, string?)"/>.</summary>
    public static bool MayShareWithLegacyApp() =>
        MayShareWithLegacyApp(Root, AppContext.BaseDirectory, Environment.GetEnvironmentVariable(LegacyHomeVariable));

    /// <summary>Legacy: whether <paramref name="root"/> can be a folder DSH (Vision's former name) works in: its own
    /// %APPDATA%\DSH (Vision runs there for a session when bringing it over failed, or VISION_HOME names it), the folder
    /// <paramref name="legacyHome"/> (DSH_HOME, which DSH read too) names, the data folder of a portable DSH beside
    /// this portable copy, or this portable copy's own data folder when it was unpacked into a portable DSH's folder
    /// (DSH.exe beside Vision.exe, one data folder for both).</summary>
    public static bool MayShareWithLegacyApp(string root, string programFolder, string? legacyHome)
    {
        var candidates = new List<string?> { LegacyDefaultRoot, legacyHome, LegacyPortableRootFor(programFolder) };
        if (File.Exists(Path.Combine(programFolder, LegacyExeName))) candidates.Add(PortableRootFor(programFolder));
        return candidates.Any(candidate => !string.IsNullOrWhiteSpace(candidate) && SameFolder(candidate, root));
    }

    private static bool SameFolder(string a, string b)
    {
        try
        {
            return string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    public static string Settings => Path.Combine(Root, "settings.json");
    public static string Conversations => Path.Combine(Root, "conversations");
    public static string Plugins => Path.Combine(Root, "plugins");
    public static string Logs => Path.Combine(Root, "logs");
    /// <summary>What model servers refused, per server and model (see <see cref="RequestAdaptationStore"/>).</summary>
    public static string RequestAdaptations => Path.Combine(Root, "request-adaptations.json");

    /// <summary>Create <paramref name="path"/> if needed and return it.</summary>
    public static string Ensure(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }
}
