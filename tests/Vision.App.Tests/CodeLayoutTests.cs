using System.IO;
using System.Windows.Threading;
using Vision.App.Model;
using Vision.App.Model.Code;
using Vision.Core;

namespace Vision.App.Tests;

/// <summary>Code mode with the chat as the main view: the rules for which panes show, the editor coming and going with
/// files, "Chat only" and back, the layout kept per project, and what the project status bar says.</summary>
public sealed class CodeLayoutTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"vision-layout-{Guid.NewGuid():N}");

    public CodeLayoutTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string Project(string name)
    {
        var path = Path.Combine(_dir, name);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "README.md"), "# " + name + "\n");
        File.WriteAllText(Path.Combine(path, "Program.cs"), "class P { }\n");
        return path;
    }

    [Theory]
    // tree, editor, terminal, chat, has terminals → tree, editor, terminal, chat, chat is main, chat only
    [InlineData(true, false, false, true, false, true, false, false, true, true, false)]
    [InlineData(false, false, false, false, false, false, false, false, true, true, true)]
    [InlineData(false, true, false, false, false, false, true, false, false, false, false)]
    [InlineData(true, true, true, true, true, true, true, true, true, false, false)]
    [InlineData(false, false, true, true, false, false, false, false, true, true, true)]
    [InlineData(false, false, true, true, true, false, false, true, true, true, false)]
    public void TheChatFillsWhatTheEditorLeavesAndCantBeHiddenWithoutIt(bool tree, bool editor, bool terminal, bool chat, bool terminals,
        bool showsTree, bool showsEditor, bool showsTerminal, bool showsChat, bool chatIsMain, bool chatOnly)
    {
        var layout = CodeLayout.For(tree, editor, terminal, chat, terminals);
        Assert.Equal(new CodeLayout(showsTree, showsEditor, showsTerminal, showsChat), layout);
        Assert.Equal(chatIsMain, layout.ChatIsMain);
        Assert.Equal(chatOnly, layout.ChatOnly);
    }

    [Fact]
    public void AProjectOpensOnTheChatAndAFileOpensInTheCentre() => UiThread.Run(() =>
    {
        var config = new AppConfig(Path.Combine(_dir, "settings.json"));
        var code = new CodeWorkspace(config, Dispatcher.CurrentDispatcher);
        var project = Project("inventory");
        code.Open(project);
        Assert.False(code.ShowEditor);
        Assert.True(code.Layout.ChatIsMain);

        code.OpenFile(Path.Combine(project, "README.md"));
        Assert.True(code.ShowEditor);
        Assert.False(code.Layout.ChatIsMain);
        code.OpenFile(Path.Combine(project, "Program.cs"));
        code.CloseActiveBuffer();
        Assert.True(code.ShowEditor);
        code.CloseActiveBuffer();
        Assert.False(code.ShowEditor); // the last file closed: the chat takes the centre again
        code.Close();
        return Task.CompletedTask;
    });

    [Fact]
    public void ChatOnlyHidesThePanesAndBringsThemBack() => UiThread.Run(() =>
    {
        var config = new AppConfig(Path.Combine(_dir, "settings.json"));
        var code = new CodeWorkspace(config, Dispatcher.CurrentDispatcher);
        var project = Project("shop");
        code.Open(project);
        code.OpenFile(Path.Combine(project, "README.md"));
        code.ShowTree = true;
        code.ShowChat = false;

        code.ToggleChatOnly();
        Assert.True(code.Layout.ChatOnly);
        Assert.True(code.Layout.Chat);
        Assert.False(code.ShowTree);
        Assert.False(code.ShowEditor);

        code.ToggleChatOnly();
        Assert.True(code.ShowTree);
        Assert.True(code.ShowEditor);
        Assert.False(code.Layout.ChatOnly);

        // Chat only with nothing to bring back: the file tree comes back.
        code.ShowTree = false;
        code.CloseActiveBuffer();
        Assert.True(code.Layout.ChatOnly);
        code.ToggleChatOnly();
        Assert.True(code.ShowTree);
        code.Close();
        return Task.CompletedTask;
    });

    [Fact]
    public void EachProjectKeepsItsOwnLayout() => UiThread.Run(() =>
    {
        var settings = Path.Combine(_dir, "settings.json");
        var config = new AppConfig(settings);
        var code = new CodeWorkspace(config, Dispatcher.CurrentDispatcher);
        var a = Project("a");
        var b = Project("b");
        code.Open(a);
        code.OpenFile(Path.Combine(a, "README.md"));
        code.ShowTree = false;
        code.Open(b);
        Assert.False(code.ShowEditor); // b has no layout of its own yet
        code.ShowTree = true;
        code.ToggleChatOnly();

        Assert.True(config.CodeStates[a].Editor);
        Assert.False(config.CodeStates[a].Tree);
        Assert.False(config.CodeStates[b].Editor);
        Assert.False(config.CodeStates[b].Tree);

        code.Open(a);
        Assert.True(code.ShowEditor);
        Assert.False(code.ShowTree);
        Assert.Single(code.Buffers);
        code.Close();

        // Saved and read back with the settings, and a layout saved before the switch existed shows the editor with files.
        config.SaveNow();
        var reloaded = new AppConfig(settings);
        Assert.True(reloaded.CodeStates[a].Editor);
        Assert.Null(new CodeState([Path.Combine(a, "README.md")], null, false, true).Editor);
        return Task.CompletedTask;
    });

    [Fact]
    public void TheStatusBarSaysWhatTheChatIsDoing()
    {
        Assert.Equal(("No chat open", ProjectStatus.Tone.Idle), ProjectStatus.Agent(null));
        var vm = new SessionVM("s1", "Fix it", _dir, PermissionPreset.FullAccess);
        Assert.Equal(("Idle", ProjectStatus.Tone.Idle), ProjectStatus.Agent(vm));
        vm.SetTodos([new TodoItem("1", "a", TodoStatus.Completed), new TodoItem("2", "b", TodoStatus.InProgress), new TodoItem("3", "c")]);
        Assert.Equal(("Idle · plan 1 of 3 done", ProjectStatus.Tone.Idle), ProjectStatus.Agent(vm));
        vm.Running = true;
        vm.StartTool("t1", "edit", "src/Stock.cs");
        var (text, tone) = ProjectStatus.Agent(vm);
        Assert.Equal(ProjectStatus.Tone.Busy, tone);
        Assert.StartsWith("Working · ", text);
        Assert.EndsWith("src/Stock.cs · plan 1 of 3 done", text);
        vm.PendingGates.Add(new GateVM("g1", "run_shell_command", "dotnet test"));
        Assert.Equal(("Waiting for your permission", ProjectStatus.Tone.Waiting), ProjectStatus.Agent(vm));
        Assert.Equal("", ProjectStatus.Changes(vm));
        vm.RecordFileChanges([new FileChange(Path.Combine(_dir, "a.cs"), FileChangeKind.Modified), new FileChange(Path.Combine(_dir, "b.cs"), FileChangeKind.Created)]);
        Assert.Equal("2 files changed", ProjectStatus.Changes(vm));
    }
}
