namespace Vision.App.Model;

/// <summary>Tells the step from "the Spark is switching models" to "it is not" apart from a status poll that merely says "not
/// switching" once more (every poll raises the change, whether or not anything changed).</summary>
public sealed class SwitchEdge
{
    private bool _was;
    private HashSet<string> _switching = new(StringComparer.Ordinal);

    /// <summary>Feed it every reading; true exactly when this one is the end of a switch.</summary>
    public bool Finished(bool switching)
    {
        var finished = _was && !switching;
        _was = switching;
        return finished;
    }

    /// <summary>Feed it the boxes switching now (by id); true when any box that was switching no longer is — whichever box
    /// it is, so a chat on a box that isn't the primary's picks up as soon as its own box is done.</summary>
    public bool Finished(IEnumerable<string> switchingNow)
    {
        var now = new HashSet<string>(switchingNow, StringComparer.Ordinal);
        var finished = _switching.Any(id => !now.Contains(id));
        _switching = now;
        return finished;
    }
}
