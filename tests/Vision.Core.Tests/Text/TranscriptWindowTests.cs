using System.Collections.ObjectModel;

namespace Vision.Core.Tests;

/// <summary>The windowed transcript (10-19 #14): a chat renders a window of its rows, pages in earlier and newer ones, jumps
/// to an old row, and stays in step with the chat as rows are added, removed and replaced.</summary>
public sealed class TranscriptWindowTests
{
    private sealed record Row(int N)
    {
        public override string ToString() => N.ToString();
    }

    private static ObservableCollection<Row> Rows(int count) => new(Enumerable.Range(0, count).Select(i => new Row(i)));

    /// <summary>The window is always exactly the source's rows [Start, End).</summary>
    private static void AssertSlice(ObservableCollection<Row> source, TranscriptWindow<Row> window)
    {
        Assert.InRange(window.Start, 0, source.Count);
        Assert.InRange(window.End, window.Start, source.Count);
        Assert.Equal(source.Skip(window.Start).Take(window.End - window.Start), window.Items);
        Assert.Equal(window.Start, window.Earlier);
        Assert.Equal(source.Count - window.End, window.Later);
        Assert.Equal(window.End == source.Count, window.IsLive);
    }

    [Fact]
    public void OpensOnTheNewestPage()
    {
        var source = Rows(2_000);
        using var window = new TranscriptWindow<Row>(source);
        Assert.Equal(TranscriptPaging.PageSize, window.Items.Count);
        Assert.Equal(1_950, window.Earlier);
        Assert.Equal(0, window.Later);
        Assert.True(window.IsLive);
        Assert.Equal(new Row(1_999), window.Items[^1]);
        AssertSlice(source, window);
    }

    [Fact]
    public void AShortChatShowsEverything()
    {
        var source = Rows(12);
        using var window = new TranscriptWindow<Row>(source);
        Assert.Equal(12, window.Items.Count);
        Assert.Equal(0, window.Earlier);
        AssertSlice(source, window);

        using var empty = new TranscriptWindow<Row>([]);
        Assert.Empty(empty.Items);
        Assert.True(empty.IsLive);
    }

    [Fact]
    public void LoadEarlierBringsAPageAtTheTopAndStopsAtTheFirstRow()
    {
        var source = Rows(120);
        using var window = new TranscriptWindow<Row>(source);
        Assert.Equal(50, window.LoadEarlier());
        Assert.Equal(20, window.Earlier);
        Assert.Equal(new Row(20), window.Items[0]);
        Assert.Equal(20, window.LoadEarlier());
        Assert.Equal(0, window.LoadEarlier());
        Assert.Equal(120, window.Items.Count);
        Assert.True(window.IsLive);
        AssertSlice(source, window);
    }

    [Fact]
    public void LoadingEarlierPastTheCapLetsGoOfTheNewestRows()
    {
        var source = Rows(2_000);
        using var window = new TranscriptWindow<Row>(source, pageSize: 50, maxRows: 250);
        for (var i = 0; i < 4; i++) window.LoadEarlier();
        Assert.Equal(250, window.Items.Count);
        Assert.True(window.IsLive);
        window.LoadEarlier();
        Assert.Equal(250, window.Items.Count);
        Assert.False(window.IsLive);
        Assert.Equal(50, window.Later);
        Assert.Equal(1_700, window.Earlier);
        AssertSlice(source, window);

        // Back down: newer pages come in at the bottom and the oldest go, until the window reaches the end again.
        Assert.Equal(50, window.LoadLater());
        Assert.True(window.IsLive);
        Assert.Equal(250, window.Items.Count);
        Assert.Equal(0, window.LoadLater());
        AssertSlice(source, window);
    }

    [Fact]
    public void ADetachedWindowDoesNotShowNewRowsButCountsThem()
    {
        var source = Rows(1_000);
        using var window = new TranscriptWindow<Row>(source);
        window.ShowAround(100);
        Assert.False(window.IsLive);
        var later = window.Later;
        source.Add(new Row(5_000));
        Assert.Equal(later + 1, window.Later);
        Assert.DoesNotContain(new Row(5_000), window.Items);
        AssertSlice(source, window);
    }

    [Fact]
    public void ALiveWindowShowsRowsAddedAtTheEnd()
    {
        var source = Rows(80);
        using var window = new TranscriptWindow<Row>(source);
        source.Add(new Row(80));
        Assert.Equal(new Row(80), window.Items[^1]);
        Assert.Equal(51, window.Items.Count);
        AssertSlice(source, window);
    }

    [Fact]
    public void JumpingShowsTheRowWithContextAboveIt()
    {
        var source = Rows(2_000);
        using var window = new TranscriptWindow<Row>(source);
        Assert.True(window.ShowAround(500));
        Assert.Equal(500 - TranscriptPaging.JumpContext, window.Start);
        Assert.Contains(new Row(500), window.Items);
        Assert.Equal(TranscriptPaging.PageSize, window.Items.Count);
        Assert.False(window.IsLive);
        AssertSlice(source, window);

        // A row already in the window: the window stays.
        var start = window.Start;
        Assert.True(window.ShowAround(505));
        Assert.Equal(start, window.Start);

        // Near the end: a window that reaches the end, so it is live again.
        Assert.True(window.ShowAround(1_970));
        Assert.True(window.IsLive);
        Assert.Contains(new Row(1_970), window.Items);

        Assert.False(window.ShowAround(-1));
        Assert.False(window.ShowAround(2_000));
        AssertSlice(source, window);
    }

    [Fact]
    public void TrimTopKeepsTheNewestRows()
    {
        var source = Rows(30);
        using var window = new TranscriptWindow<Row>(source);
        for (var i = 30; i < 400; i++) source.Add(new Row(i));
        Assert.Equal(400, window.Items.Count);
        Assert.Equal(150, window.TrimTop(250));
        Assert.Equal(250, window.Items.Count);
        Assert.True(window.IsLive);
        // Mostly going: done as one reset, same result.
        for (var i = 400; i < 2_000; i++) source.Add(new Row(i));
        Assert.Equal(1_600, window.TrimTop(250));
        Assert.Equal(new Row(1_750), window.Items[0]);
        Assert.Equal(0, window.TrimTop(250));
        AssertSlice(source, window);
    }

    [Fact]
    public void RowsAddedOrRemovedAboveTheWindowShiftIt()
    {
        var source = Rows(200);
        using var window = new TranscriptWindow<Row>(source);
        var first = window.Items[0];
        source.Insert(10, new Row(-1)); // a compaction marker far above
        Assert.Equal(first, window.Items[0]);
        source.RemoveAt(0);
        Assert.Equal(first, window.Items[0]);
        AssertSlice(source, window);
    }

    [Fact]
    public void RowsReplacedOrRemovedInsideTheWindowFollow()
    {
        var source = Rows(200);
        using var window = new TranscriptWindow<Row>(source);
        source[190] = new Row(-190); // "Not delivered to the model: …"
        Assert.Contains(new Row(-190), window.Items);
        source.RemoveAt(199); // a dropped streaming reply
        Assert.DoesNotContain(new Row(199), window.Items);
        AssertSlice(source, window);
    }

    [Fact]
    public void AWindowEmptiedFromUnderItShowsTheNewestPage()
    {
        var source = Rows(300);
        using var window = new TranscriptWindow<Row>(source, pageSize: 5);
        window.ShowAround(100);
        for (var i = 0; i < 6; i++) source.RemoveAt(window.Start);
        Assert.NotEmpty(window.Items);
        AssertSlice(source, window);
        source.Clear();
        Assert.Empty(window.Items);
        source.Add(new Row(1));
        Assert.Single(window.Items);
        AssertSlice(source, window);
    }

    [Fact]
    public void DisposedWindowsStopListening()
    {
        var source = Rows(10);
        var window = new TranscriptWindow<Row>(source);
        window.Dispose();
        source.Add(new Row(10));
        Assert.Equal(10, window.Items.Count);
    }

    /// <summary>Thousands of random edits and page moves: the window is always the exact slice it claims to be.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void StaysAnExactSliceThroughRandomEdits(int seed)
    {
        var random = new Random(seed);
        var source = Rows(random.Next(0, 400));
        var next = 10_000;
        using var window = new TranscriptWindow<Row>(source, pageSize: random.Next(3, 60), maxRows: random.Next(60, 200));
        var changed = 0;
        window.Changed += () => changed++;
        for (var step = 0; step < 3_000; step++)
        {
            switch (random.Next(12))
            {
                case 0 or 1 or 2:
                    source.Add(new Row(next++));
                    break;
                case 3:
                    source.Insert(random.Next(source.Count + 1), new Row(next++));
                    break;
                case 4 when source.Count > 0:
                    source.RemoveAt(random.Next(source.Count));
                    break;
                case 5 when source.Count > 0:
                    source[random.Next(source.Count)] = new Row(next++);
                    break;
                case 6 when source.Count > 1:
                    source.Move(random.Next(source.Count), random.Next(source.Count));
                    break;
                case 7:
                    window.LoadEarlier();
                    break;
                case 8:
                    window.LoadLater(random.Next(1, 80));
                    break;
                case 9 when source.Count > 0:
                    window.ShowAround(random.Next(source.Count));
                    break;
                case 10:
                    window.TrimTop(random.Next(1, 120));
                    break;
                case 11 when random.Next(40) == 0:
                    source.Clear();
                    break;
            }
            AssertSlice(source, window);
        }
        Assert.True(changed > 0);
    }
}
