namespace Vision.Core.Tests;

/// <summary>The branch the code-mode status bar shows, read from git's files.</summary>
public sealed class GitHeadTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vision-githead-" + Guid.NewGuid().ToString("N"));

    public GitHeadTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string Repo(string name, string head)
    {
        var repo = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        File.WriteAllText(Path.Combine(repo, ".git", "HEAD"), head);
        return repo;
    }

    [Fact]
    public void ABranchIsReadFromHead() => Assert.Equal("feat/vision", GitHead.Branch(Repo("a", "ref: refs/heads/feat/vision\n")));

    [Fact]
    public void ADetachedHeadShowsItsShortCommit() =>
        Assert.Equal("d6383aa", GitHead.Branch(Repo("b", "d6383aa1c2b3e4f5a6b7c8d9e0f1a2b3c4d5e6f7\n")));

    [Fact]
    public void ASubfolderOfARepoFindsItsBranch()
    {
        var repo = Repo("c", "ref: refs/heads/main\n");
        var sub = Path.Combine(repo, "src", "App");
        Directory.CreateDirectory(sub);
        Assert.Equal("main", GitHead.Branch(sub));
    }

    [Fact]
    public void AWorktreeFollowsItsGitFile()
    {
        var main = Repo("main", "ref: refs/heads/main\n");
        var worktreeGit = Path.Combine(main, ".git", "worktrees", "files");
        Directory.CreateDirectory(worktreeGit);
        File.WriteAllText(Path.Combine(worktreeGit, "HEAD"), "ref: refs/heads/night/files\n");
        var worktree = Path.Combine(_root, "vision-win-files");
        Directory.CreateDirectory(worktree);
        File.WriteAllText(Path.Combine(worktree, ".git"), $"gitdir: {worktreeGit}\n");
        Assert.Equal("night/files", GitHead.Branch(worktree));
    }

    [Fact]
    public void AFolderOutsideGitHasNoBranch()
    {
        var plain = Path.Combine(_root, "plain");
        Directory.CreateDirectory(plain);
        Assert.Null(GitHead.Branch(null));
        Assert.Null(GitHead.Branch(Path.Combine(_root, "missing")));
    }
}
