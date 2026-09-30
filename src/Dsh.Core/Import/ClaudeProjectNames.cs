namespace Dsh.Core;

/// <summary>How Claude Code names the per-project folder under ~/.claude/projects: the project's full
/// path with every character that isn't an ASCII letter or digit replaced by "-"
/// (<c>C:\Users\Pat\my_app</c> → <c>C--Users-Pat-my-app</c>, <c>/Users/pat/app</c> → <c>-Users-pat-app</c>).
/// DSH keeps imported project notes under the same name, so the notes for a project are found from its
/// path alone — and the same path always finds the same notes.</summary>
public static class ClaudeProjectNames
{
    /// <summary>The folder name for the project at <paramref name="path"/>.</summary>
    public static string Encode(string path)
    {
        var full = Path.GetFullPath(path);
        // "C:\" and "/" keep their separator; anything else loses a trailing one.
        return EncodeName(Path.TrimEndingDirectorySeparator(full));
    }

    /// <summary>The naming rule on its own, applied to text exactly as given.</summary>
    public static string EncodeName(string text)
    {
        var chars = new char[text.Length];
        for (var i = 0; i < text.Length; i++) chars[i] = char.IsAsciiLetterOrDigit(text[i]) ? text[i] : '-';
        return new string(chars);
    }

    /// <summary>The folder a name stands for, if it exists on this PC. The name alone is ambiguous — a
    /// "-" can be a separator, a dot, an underscore or a real hyphen — so this walks the disk, following
    /// only folders whose own encoded name fits, and gives up after <paramref name="budget"/> folder
    /// listings. Null when no such folder exists (the project was moved or deleted, or came from another
    /// PC).</summary>
    public static string? FindFolder(string encoded, int budget = 4_000)
    {
        string start, rest;
        if (encoded.Length >= 3 && char.IsAsciiLetter(encoded[0]) && encoded[1] == '-' && encoded[2] == '-')
        {
            if (!OperatingSystem.IsWindows()) return null; // "C:\…" can't exist here
            start = $"{encoded[0]}:{Path.DirectorySeparatorChar}";
            rest = encoded[3..];
        }
        else if (encoded.Length >= 2 && encoded[0] == '-')
        {
            if (OperatingSystem.IsWindows()) return null; // "/…" can't exist here
            start = "/";
            rest = encoded[1..];
        }
        else
        {
            return null;
        }
        return Resolve(start, rest, ref budget);
    }

    /// <summary>A short, readable label for a project whose folder can't be found: the name without
    /// the "Users-&lt;name&gt;-" prefix every home-folder project shares.</summary>
    public static string Readable(string encoded)
    {
        var text = encoded.TrimStart('-');
        foreach (var prefix in new[] { "Users-", "home-" })
        {
            if (!text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var user = text.IndexOf('-', prefix.Length);
            if (user > 0 && user + 1 < text.Length) return text[(user + 1)..];
        }
        var drive = text.IndexOf("-Users-", StringComparison.OrdinalIgnoreCase);
        if (drive >= 0)
        {
            var user = text.IndexOf('-', drive + 7);
            if (user > 0 && user + 1 < text.Length) return text[(user + 1)..];
        }
        return text.Length == 0 ? encoded : text;
    }

    private static StringComparison Comparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static string? Resolve(string directory, string rest, ref int budget)
    {
        if (rest.Length == 0) return directory;
        if (--budget < 0) return null;
        string[] children;
        try
        {
            children = Directory.GetDirectories(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
        Array.Sort(children, StringComparer.Ordinal);
        foreach (var child in children)
        {
            var name = EncodeName(Path.GetFileName(child));
            if (name.Length == 0) continue;
            if (rest.Equals(name, Comparison)) return child; // the rest of the path is exactly this folder
            if (rest.Length > name.Length && rest[name.Length] == '-' && rest.StartsWith(name, Comparison)
                && Resolve(child, rest[(name.Length + 1)..], ref budget) is { } found)
                return found;
        }
        return null;
    }
}
