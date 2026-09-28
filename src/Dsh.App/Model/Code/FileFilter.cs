using System.IO;

namespace Dsh.App.Model.Code;

/// <summary>Names never shown in the file tree and never watched for reload, and which files the
/// editor opens as text.</summary>
public static class FileFilter
{
    public static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".build", "node_modules", ".swiftpm", "DerivedData", ".next", "dist", "build", "target",
        "__pycache__", ".venv", "venv", ".mypy_cache", ".pytest_cache", ".gradle", ".idea", "Pods",
        ".vs", "obj", ".svn", ".hg",
    };

    public static readonly HashSet<string> IgnoredFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        ".DS_Store", "Thumbs.db", "desktop.ini",
    };

    public static bool IsIgnored(string name, bool isDirectory) =>
        IgnoredFiles.Contains(name) || (isDirectory && IgnoredDirectories.Contains(name));

    /// <summary>Anything under an ignored directory is noise for reload purposes too.</summary>
    public static bool IsNoise(string path)
    {
        foreach (var part in path.Split('\\', '/'))
        {
            if (IgnoredDirectories.Contains(part) || IgnoredFiles.Contains(part)) return true;
        }
        return false;
    }

    public static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "swift", "m", "mm", "h", "hpp", "hh", "c", "cc", "cpp", "cxx", "rs", "go", "java", "kt", "kts",
        "js", "jsx", "ts", "tsx", "mjs", "cjs", "py", "pyi", "rb", "php", "pl", "lua", "sh", "bash", "zsh", "fish",
        "sql", "r", "jl", "scala", "clj", "ex", "exs", "erl", "hs", "ml", "cs", "csx", "vb", "fs", "fsx", "dart",
        "zig", "nim", "ps1", "psm1", "psd1", "bat", "cmd", "vue", "svelte", "astro", "graphql", "gql", "proto",
        "tf", "hcl", "razor", "cshtml",
        "json", "jsonc", "json5", "yaml", "yml", "toml", "ini", "cfg", "conf", "env", "properties", "editorconfig",
        "md", "markdown", "mdx", "txt", "rst", "adoc", "tex", "csv", "tsv", "log",
        "html", "htm", "xml", "svg", "css", "scss", "sass", "less", "xaml", "axaml",
        "csproj", "vbproj", "fsproj", "vcxproj", "sln", "slnx", "props", "targets", "resx", "config", "nuspec",
        "manifest", "wxs", "iss", "reg", "filters", "runsettings", "ruleset",
        "gitignore", "gitattributes", "gitmodules", "dockerignore", "dockerfile", "makefile", "cmake", "gradle",
        "plist", "entitlements", "xcconfig", "podspec", "lock", "patch", "diff",
    };

    private static readonly HashSet<string> TextNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "makefile", "dockerfile", "readme", "license", "changelog", "authors", "contributing", "codeowners",
        "gemfile", "rakefile", "procfile", "brewfile", "jenkinsfile", "vagrantfile", ".gitignore", ".env",
        ".editorconfig", ".npmrc", ".nvmrc", ".prettierrc", ".eslintrc",
    };

    public static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "png", "jpg", "jpeg", "gif", "bmp", "ico", "tif", "tiff", "webp",
    };

    /// <summary>Files that run something when opened. The tree never launches these on a
    /// double-click; it shows them in Explorer instead.</summary>
    public static readonly HashSet<string> LaunchableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "exe", "com", "scr", "pif", "msi", "msp", "msix", "appx", "appinstaller", "lnk", "url", "cpl", "dll",
        "sys", "jar", "vbs", "vbe", "js", "jse", "wsf", "wsh", "hta", "ps1", "bat", "cmd", "reg", "application",
        "gadget", "msc", "inf", "chm", "library-ms", "search-ms", "settingcontent-ms",
    };

    public static bool IsLaunchable(string path) => LaunchableExtensions.Contains(Path.GetExtension(path).TrimStart('.'));

    public static bool IsImage(string path) => ImageExtensions.Contains(Path.GetExtension(path).TrimStart('.'));

    /// <summary>Whether the editor should open this file. Known extensions decide quickly; anything
    /// else is sniffed — no NUL bytes in the first 8 KB reads as text.</summary>
    public static bool IsTextFile(string path)
    {
        var ext = Path.GetExtension(path).TrimStart('.');
        if (ext.Length > 0 && TextExtensions.Contains(ext)) return true;
        if (TextNames.Contains(Path.GetFileName(path))) return true;
        if (ImageExtensions.Contains(ext)) return false;
        return LooksLikeText(path);
    }

    private static bool LooksLikeText(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > Dsh.Core.EditorBuffer.MaxEditableBytes) return false;
            var buffer = new byte[8192];
            var read = stream.Read(buffer, 0, buffer.Length);
            return Array.IndexOf(buffer, (byte)0, 0, read) < 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
