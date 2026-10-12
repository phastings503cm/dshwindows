namespace Dsh.Core.Tests;

/// <summary>Chats without a project: the prompt they get and their scratch folders (under a DSH_HOME of their own).</summary>
[Collection(SerialStaticState.Name)]
public sealed class PlainChatTests : IDisposable
{
    private const string Coding = "You are a capable coding agent…";
    private readonly TempDirectory _home = new("dsh-plain-home");
    private readonly string? _previous = Environment.GetEnvironmentVariable("DSH_HOME");

    public PlainChatTests() => Environment.SetEnvironmentVariable("DSH_HOME", _home.Path);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("DSH_HOME", _previous);
        _home.Dispose();
    }

    [Fact]
    public void AChatWithoutAProjectGetsTheRegularConversationPrompt()
    {
        var scratch = Path.Combine("C:", "Users", "pat", "AppData", "Roaming", "DSH", "Chats", "abc");
        var prompt = PlainChat.BasePrompt(null, scratch, Coding);
        Assert.StartsWith("You are DSH, a helpful assistant in a native Windows app. This is a regular conversation: no project folder is open and none is needed — never ask the user to open one.", prompt);
        Assert.Contains($"work in this chat's own scratch folder, {scratch}; it belongs to this chat and starts empty.", prompt);
        Assert.Contains("use one of the user's skills when it fits the request", prompt);
        Assert.EndsWith("Tell the user where a file you made is when it matters.", prompt);
        Assert.DoesNotContain("coding agent", prompt);
        Assert.Equal(PlainChat.BasePrompt("", scratch, Coding), prompt);
        Assert.Contains("native macOS app", PlainChat.SystemPrompt("macOS", scratch));
    }

    [Fact]
    public void AChatWithAProjectKeepsTheCodingPrompt() =>
        Assert.Equal(Coding, PlainChat.BasePrompt(@"C:\code\inventory", "scratch", Coding));

    [Fact]
    public void ScratchFoldersLiveUnderTheDataFolder()
    {
        var folder = PlainChat.Folder("1234");
        Assert.Equal(Path.Combine(_home.Path, "Chats", "1234"), folder);
        Assert.True(PlainChat.IsScratch(folder));
        Assert.True(PlainChat.IsScratch(Path.Combine(folder, "notes")));
        Assert.False(PlainChat.IsScratch(PlainChat.Root)); // the root itself is no chat's
        Assert.False(PlainChat.IsScratch(Path.Combine(Path.GetTempPath(), "Chats", "1234")));
        Assert.False(PlainChat.IsScratch(null));
        Assert.False(PlainChat.IsScratch(@"C:\code\inventory"));
    }

    [Fact]
    public void AScratchFolderIsMadeOnFirstUseAndDeletedWithTheChat()
    {
        var folder = PlainChat.Folder("chat-1");
        Assert.False(Directory.Exists(folder));
        Assert.Equal(folder, PlainChat.Ensure(folder));
        File.WriteAllText(Path.Combine(folder, "draft.md"), "hello");
        PlainChat.Delete("chat-1");
        Assert.False(Directory.Exists(folder));
        PlainChat.Delete("never-made"); // nothing to do, no harm
    }

    [Fact]
    public void TheEnvironmentNamesTheScratchFolderForWhatItIs()
    {
        var block = ProjectContext.EnvironmentBlock("/tmp/chats/x", "m", PermissionPreset.WorkspaceWrite, scratch: true);
        Assert.Contains("This chat's scratch folder: /tmp/chats/x", block);
        Assert.DoesNotContain("Project folder", block);
        Assert.Contains("Project folder: /p", ProjectContext.EnvironmentBlock("/p", "m", PermissionPreset.WorkspaceWrite));
    }

    [Fact]
    public void ASkillCallShowsTheSkillsName() =>
        Assert.Equal("pdf", Engine.Preview(new ToolCall("1", "use_skill", """{"arguments":"report.pdf","name":"pdf"}""")));
}
