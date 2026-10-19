namespace Vision.Core.Tests;

/// <summary>Each chat owns the processes it started and everything they start in turn; the run's async
/// flow says which chat that is.</summary>
public sealed class ChatProcessesTests
{
    private static readonly DateTimeOffset T0 = ManualClock.Epoch;

    private static Func<int, ProcessEntry?> Table(params ProcessEntry[] rows)
    {
        var map = rows.ToDictionary(r => r.ProcessId);
        return id => map.GetValueOrDefault(id);
    }

    [Fact]
    public void AGrandchildOfARootBelongsToTheChat()
    {
        var roots = new[] { new SpawnedProcess(100, T0) };
        var table = Table(new ProcessEntry(100, 4, T0), new ProcessEntry(200, 100, T0.AddSeconds(1)),
                          new ProcessEntry(300, 200, T0.AddSeconds(2)), new ProcessEntry(400, 4, T0));
        Assert.True(ProcessTree.Descends(100, roots, table));
        Assert.True(ProcessTree.Descends(300, roots, table));
        Assert.False(ProcessTree.Descends(400, roots, table));
        Assert.False(ProcessTree.Descends(999, roots, table));
        Assert.False(ProcessTree.Descends(300, [], table));
    }

    [Fact]
    public void AnAppWhoseShellAlreadyExitedStillBelongsToTheChat()
    {
        // `Start-Process notepad` in a run_shell_command: the shell (100) is gone, notepad remains.
        var roots = new[] { new SpawnedProcess(100, T0) };
        Assert.True(ProcessTree.Descends(200, roots, Table(new ProcessEntry(200, 100, T0.AddSeconds(1)))));
        // …unless that parent id is not a root, or the "root" started after the app (the id was reused).
        Assert.False(ProcessTree.Descends(200, [new SpawnedProcess(150, T0)], Table(new ProcessEntry(200, 100, T0.AddSeconds(1)))));
        Assert.False(ProcessTree.Descends(200, [new SpawnedProcess(100, T0.AddMinutes(5))], Table(new ProcessEntry(200, 100, T0))));
    }

    [Fact]
    public void AReusedIdIsNotTheRoot()
    {
        // The chat started 100 at T0; it exited and a stranger got id 100 an hour later.
        var roots = new[] { new SpawnedProcess(100, T0) };
        Assert.False(ProcessTree.Descends(100, roots, Table(new ProcessEntry(100, 4, T0.AddHours(1)))));
        // A "parent" younger than its child is a reused id too.
        Assert.False(ProcessTree.Descends(300, roots, Table(new ProcessEntry(300, 100, T0.AddSeconds(1)), new ProcessEntry(100, 4, T0.AddHours(1)))));
        // Without start times (access denied) the ids are trusted.
        Assert.True(ProcessTree.Descends(300, roots, Table(new ProcessEntry(300, 100, null), new ProcessEntry(100, 4, null))));
    }

    [Fact]
    public void ALoopInTheTableEnds()
    {
        var table = Table(new ProcessEntry(1, 2, null), new ProcessEntry(2, 1, null));
        Assert.False(ProcessTree.Descends(1, [new SpawnedProcess(50, T0)], table));
    }

    [Fact]
    public async Task TheRunsFlowSaysWhichChatStartedAProcess()
    {
        var chat = $"chat-{Guid.NewGuid():N}";
        ChatProcesses.Record(4242); // outside any run: nobody's
        await Task.Run(async () =>
        {
            ChatProcesses.Current = chat;
            await Task.Yield();
            // A background agent the run started, on another task, is still the chat.
            await Task.Run(() => ChatProcesses.Record(1001));
            ChatProcesses.Record(1002);
            ChatProcesses.Record(1001); // started again: one entry, the newest
        });
        Assert.Null(ChatProcesses.Current);
        Assert.Equal([1002, 1001], ChatProcesses.For(chat).Select(p => p.ProcessId));
        ChatProcesses.Forget(chat);
        Assert.Empty(ChatProcesses.For(chat));
    }

    [Fact]
    public void TheLedgerKeepsTheNewestRoots()
    {
        var chat = $"chat-{Guid.NewGuid():N}";
        for (var i = 1; i <= ChatProcesses.Limit + 10; i++) ChatProcesses.Record(chat, i, T0);
        var roots = ChatProcesses.For(chat);
        Assert.Equal(ChatProcesses.Limit, roots.Count);
        Assert.Equal(11, roots[0].ProcessId);
        ChatProcesses.Forget(chat);
    }

    [Fact]
    public async Task ARunShellCommandIsRecordedForItsChat()
    {
        var chat = $"chat-{Guid.NewGuid():N}";
        using var folder = new TempDirectory();
        await Task.Run(async () =>
        {
            ChatProcesses.Current = chat;
            await ShellRunner.RunAsync("echo hi", folder.Path, AgentShell.Default, TimeSpan.FromSeconds(20));
        });
        Assert.Single(ChatProcesses.For(chat));
        ChatProcesses.Forget(chat);
    }
}
