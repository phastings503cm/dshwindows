namespace Vision.Core;

/// <summary>The branch a project folder has checked out, read straight from git's own files (no git process): the
/// project status bar in code mode shows it.</summary>
public static class GitHead
{
    /// <summary>"main", "feat/x", or the first seven characters of the commit when HEAD is detached; null when the folder
    /// isn't in a git repository (or HEAD can't be read). Looks in <paramref name="root"/> and up to its parents, and
    /// follows a worktree's ".git" file to its real git folder.</summary>
    public static string? Branch(string? root)
    {
        if (string.IsNullOrEmpty(root)) return null;
        try
        {
            for (var dir = new DirectoryInfo(root); dir is not null; dir = dir.Parent)
            {
                var dotGit = Path.Combine(dir.FullName, ".git");
                string? gitDir = null;
                if (Directory.Exists(dotGit)) gitDir = dotGit;
                else if (File.Exists(dotGit))
                {
                    // A worktree or submodule: "gitdir: <path>".
                    var line = File.ReadLines(dotGit).FirstOrDefault()?.Trim() ?? "";
                    if (!line.StartsWith("gitdir:", StringComparison.Ordinal)) return null;
                    var target = line["gitdir:".Length..].Trim();
                    gitDir = Path.IsPathRooted(target) ? target : Path.GetFullPath(Path.Combine(dir.FullName, target));
                }
                if (gitDir is null) continue;
                var head = File.ReadLines(Path.Combine(gitDir, "HEAD")).FirstOrDefault()?.Trim() ?? "";
                const string prefix = "ref: refs/heads/";
                if (head.StartsWith(prefix, StringComparison.Ordinal)) return head[prefix.Length..];
                if (head.StartsWith("ref: ", StringComparison.Ordinal)) return head["ref: ".Length..];
                return head.Length >= 7 && head.All(Uri.IsHexDigit) ? head[..7] : null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
        return null;
    }
}
