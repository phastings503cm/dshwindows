using System.Diagnostics;
using System.Text;

namespace Vision.Core;

// MARK: - What a shell command changed, through git
//
// A shell command names no files, so its changes are found through git when the project is a repository: before it runs,
// every file `git status` lists (changed, and new files git doesn't ignore) is kept as it is; after it, every file listed
// before or after whose content moved is one it changed, and its content before the command is what was kept, else — for a
// file that was clean — git's own copy, else nothing (the command created it). Ignored files (build output) are never
// looked at. The same rules and git flags as Vision for macOS (GitChanges.swift).

/// <summary>The files of a repository git listed before a shell command, as they were then.</summary>
public sealed class ShellSnapshot
{
    public required string RepoRoot { get; init; }
    /// <summary>Full path → SHA-256 of the file before the command (null = it was missing), for files git listed as changed.</summary>
    public required IReadOnlyDictionary<string, string?> Dirty { get; init; }
}

/// <summary>The git commands checkpoints need, with a short timeout each. Every method answers null when git isn't installed,
/// the folder isn't a repository, or git takes too long.</summary>
public static class GitFiles
{
    public static TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(15);
    /// <summary>A repository listing more files than this isn't tracked for the command.</summary>
    public const int MaxDirtyFiles = 3_000;

    /// <summary>The repository root that contains <paramref name="folder"/>, or null — spelled from <paramref name="folder"/> (git's
    /// own --show-toplevel resolves links: /var → /private/var on a Mac, a junction on Windows), so the paths found through git
    /// match the paths the file tools use.</summary>
    public static string? RepoRoot(string folder)
    {
        if (!Directory.Exists(folder) || !HasGitAbove(folder)) return null;
        if (Run(folder, ["rev-parse", "--show-cdup"]) is not { } output) return null;
        var up = Encoding.UTF8.GetString(output).Trim();
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(up.Length == 0 ? folder : Path.Combine(folder, up)));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static bool HasGitAbove(string folder)
    {
        for (var dir = new DirectoryInfo(folder); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")) || File.Exists(Path.Combine(dir.FullName, ".git"))) return true;
        return false;
    }

    /// <summary>The files git lists — changed ones and new ones it doesn't ignore (relative to <paramref name="root"/>, forward
    /// slashes) — or null.</summary>
    public static IReadOnlyList<string>? ChangedTracked(string root) => Status(root)?.Select(e => e.Path).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>`git status` as (two-letter status, path) pairs ("??" = new, not ignored), or null.</summary>
    public static IReadOnlyList<(string Status, string Path)>? Status(string root)
    {
        if (Run(root, ["-c", "core.quotepath=off", "status", "--porcelain=v1", "-z", "--untracked-files=all", "--no-renames", "--ignore-submodules=all"]) is not { } output)
            return null;
        return ParseEntries(Encoding.UTF8.GetString(output));
    }

    /// <summary>The entries of `git status --porcelain=v1 -z` output: "XY path\0", a rename or copy "XY new\0old\0" (both
    /// paths count — the old one was removed).</summary>
    public static IReadOnlyList<(string Status, string Path)> ParseEntries(string output)
    {
        var entries = new List<(string, string)>();
        var parts = output.Split('\0');
        for (var i = 0; i < parts.Length; i++)
        {
            var entry = parts[i];
            if (entry.Length < 4) continue;
            var status = entry[..2];
            entries.Add((status, entry[3..]));
            if ((status[0] is 'R' or 'C' || status[1] is 'R' or 'C') && i + 1 < parts.Length && parts[i + 1].Length > 0)
                entries.Add((" D", parts[++i]));
        }
        return entries;
    }

    /// <summary>The paths in `git status --porcelain=v1 -z` output (both sides of a rename or copy).</summary>
    public static IReadOnlyList<string> ParseStatus(string output) =>
        ParseEntries(output).Select(e => e.Path).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>What git's index has for <paramref name="relative"/> (a clean file's content), or null when it has nothing.</summary>
    public static byte[]? IndexContent(string root, string relative) => Run(root, ["cat-file", "blob", ":" + relative]);

    /// <summary>Run git in <paramref name="folder"/> and return its standard output; null on any failure.</summary>
    internal static byte[]? Run(string folder, IReadOnlyList<string> arguments)
    {
        try
        {
            var start = new ProcessStartInfo("git")
            {
                WorkingDirectory = folder,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            // Reading status must not take the index lock another git (or the person's editor) may be using.
            start.Environment["GIT_OPTIONAL_LOCKS"] = "0";
            start.Environment["GIT_TERMINAL_PROMPT"] = "0";
            using var process = Process.Start(start);
            if (process is null) return null;
            process.StandardInput.Close();
            using var buffer = new MemoryStream();
            var copy = process.StandardOutput.BaseStream.CopyToAsync(buffer);
            var drain = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit((int)Timeout.TotalMilliseconds))
            {
                try { process.Kill(entireProcessTree: true); } catch (Exception) { /* gone already */ }
                return null;
            }
            copy.Wait(Timeout);
            drain.Wait(Timeout);
            return process.ExitCode == 0 ? buffer.ToArray() : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException
                                       or AggregateException or PlatformNotSupportedException)
        {
            return null;
        }
    }
}
