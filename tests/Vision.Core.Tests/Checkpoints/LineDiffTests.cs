namespace Vision.Core.Tests;

public sealed class LineDiffTests
{
    [Fact]
    public void SameTextsHaveNoDiff()
    {
        Assert.Equal("", LineDiff.Unified("a\nb\n", "a\nb\n", "a/x", "b/x"));
        Assert.Equal((0, 0), LineDiff.Count("a\r\nb", "a\nb\n"));
    }

    [Fact]
    public void ChangesFarApartGetHunksOfTheirOwnWithThreeLinesAround()
    {
        var before = string.Join("\n", Enumerable.Range(1, 30).Select(n => $"line {n}")) + "\n";
        var after = before.Replace("line 2\n", "line two\n").Replace("line 28\n", "line twenty-eight\n");

        var diff = LineDiff.Unified(before, after, "a/f", "b/f");

        Assert.Equal(2, diff.Split('\n').Count(l => l.StartsWith("@@", StringComparison.Ordinal)));
        Assert.Contains("@@ -1,5 +1,5 @@\n line 1\n-line 2\n+line two\n line 3\n line 4\n line 5\n", diff);
        Assert.Contains("@@ -25,6 +25,6 @@\n", diff);
        Assert.Equal((2, 2), LineDiff.Count(before, after));
    }

    [Fact]
    public void ANewFileIsAllAdditionsAndADeletedOneAllRemovals()
    {
        Assert.Equal("--- /dev/null\n+++ b/n\n@@ -0,0 +1,2 @@\n+x\n+y\n", LineDiff.Unified("", "x\ny\n", "/dev/null", "b/n"));
        Assert.Equal("--- a/n\n+++ /dev/null\n@@ -1,1 +0,0 @@\n-x\n", LineDiff.Unified("x\n", "", "a/n", "/dev/null"));
    }

    [Fact]
    public void AnInsertionInTheMiddleIsFoundExactly()
    {
        var script = LineDiff.Compute("a\nb\nc\nd\n", "a\nb\nNEW\nc\nd\n");
        var added = Assert.Single(script, l => l.Op == LineDiff.Op.Add);
        Assert.Equal(("NEW", 3), (added.Text, added.NewNumber));
        Assert.DoesNotContain(script, l => l.Op == LineDiff.Op.Remove);
    }

    [Fact]
    public void TwoUnrelatedLargeFilesFinishQuicklyAsOneReplacement()
    {
        var before = string.Join("\n", Enumerable.Range(0, 6000).Select(n => $"a{n}"));
        var after = string.Join("\n", Enumerable.Range(0, 6000).Select(n => $"b{n}"));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var (added, removed) = LineDiff.Count(before, after);
        Assert.Equal((6000, 6000), (added, removed));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), watch.Elapsed.ToString());
    }
}
