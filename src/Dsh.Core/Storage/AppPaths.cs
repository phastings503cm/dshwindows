namespace Dsh.Core;

/// <summary>Where DSH keeps its own files: %APPDATA%\DSH on Windows (settings, conversations,
/// skills, plugins, drafts). Set the DSH_HOME environment variable to use another folder (tests do,
/// and so can a portable install).</summary>
public static class AppPaths
{
    public static string Root
    {
        get
        {
            var overridden = Environment.GetEnvironmentVariable("DSH_HOME");
            if (!string.IsNullOrWhiteSpace(overridden)) return overridden;
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DSH");
        }
    }

    public static string Settings => Path.Combine(Root, "settings.json");
    public static string Conversations => Path.Combine(Root, "conversations");
    public static string Plugins => Path.Combine(Root, "plugins");
    public static string Logs => Path.Combine(Root, "logs");

    /// <summary>Create <paramref name="path"/> if needed and return it.</summary>
    public static string Ensure(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }
}
