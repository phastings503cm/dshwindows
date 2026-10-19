using System.Text;

namespace Vision.Core.Tests;

/// <summary>The Mac review's rewind rules (10-19 #20): each file is looked at again just before it is rewound, and a very long
/// file isn't compared line by line.</summary>
public sealed class RewindRecheckTests : IDisposable
{
    private readonly TempDirectory _data = new("vision-rewind");
    private readonly ToolTestContext _tools = new();
    private CheckpointStore Store => new(_data["checkpoints"]);

    public void Dispose()
    {
        _data.Dispose();
        _tools.Dispose();
    }

    [Fact]
    public async Task EachFileIsLookedAtAgainJustBeforeItIsRewound()
    {
        var a = _tools.Root.Write("a.txt", "a0");
        var b = _tools.Root.Write("b.txt", "b0");
        var c = _tools.Root.Write("c.txt", "c0");
        var recorder = new TurnRecorder(Store, "chat-1", "turn-1", _tools.Root.Path, "Edit three");
        var context = _tools.Context with { Checkpoint = recorder };
        foreach (var name in new[] { "a.txt", "b.txt", "c.txt" })
            await new WriteFileTool().ExecuteAsync(Args.Json(new { file_path = name, content = name[0] + "1" }), context, default);
        var turn = recorder.Finish()!;
        var plan = CheckpointRewind.Plan(Store, turn);
        Assert.Equal(3, plan.Safe.Count);

        // While the person read the confirmation: a changed again, b was put back by hand.
        File.WriteAllText(a, "a2");
        File.WriteAllText(b, "b0");
        var result = CheckpointRewind.Apply(Store, plan, includeConflicts: false);

        Assert.Equal("a2", File.ReadAllText(a));
        Assert.Equal(CheckpointRewind.ChangedWhileDeciding, result.Failed[a]);
        Assert.Equal([c], result.Restored);
        Assert.Equal("c0", File.ReadAllText(c));
        Assert.EndsWith("Couldn't rewind a.txt (it changed after you confirmed — left as it is).",
            CheckpointRewind.Summary(result, turn.Title, _tools.Root.Path));
    }

    [Fact]
    public void AVeryLongFileIsNotComparedLineByLine()
    {
        var file = _tools.Root.Write("long.txt", string.Join("\n", Enumerable.Range(0, 20_001).Select(n => $"line {n}")));
        var recorder = new TurnRecorder(Store, "chat-1", "turn-2", _tools.Root.Path, "Grow it");
        recorder.BeforeChange(file, "edit");
        File.AppendAllText(file, "\nmore", Encoding.UTF8);
        var turn = recorder.Finish()!;
        Assert.Equal("Too large to compare line by line (20,001 → 20,002 lines).", CheckpointRewind.Diff(Store, turn.Files.Single(), _tools.Root.Path));
    }
}
