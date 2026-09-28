namespace Dsh.Core;

/// <summary>Where DSH keeps its own files: %APPDATA%\DSH on Windows (settings, conversations,
/// skills, plugins, drafts). The DSH_HOME environment variable points it elsewhere (tests do); a file
/// named <c>portable</c> next to DSH.exe keeps everything in a <c>data</c> folder beside it, for
/// running from a USB stick.</summary>
public static class AppPaths
{
    public static string Root
    {
        get
        {
            var overridden = Environment.GetEnvironmentVariable("DSH_HOME");
            if (!string.IsNullOrWhiteSpace(overridden)) return overridden;
            if (PortableRoot is { } portable) return portable;
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DSH");
        }
    }

    /// <summary>The data folder of a portable copy, or null.</summary>
    public static string? PortableRoot
    {
        get
        {
            var baseDirectory = AppContext.BaseDirectory;
            return File.Exists(Path.Combine(baseDirectory, "portable")) ? Path.Combine(baseDirectory, "data") : null;
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
