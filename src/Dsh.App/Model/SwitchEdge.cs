namespace Dsh.App.Model;

/// <summary>Tells the step from "the Spark is switching models" to "it is not" apart from a status poll that merely says "not
/// switching" once more (every poll raises the change, whether or not anything changed).</summary>
public sealed class SwitchEdge
{
    private bool _was;

    /// <summary>Feed it every reading; true exactly when this one is the end of a switch.</summary>
    public bool Finished(bool switching)
    {
        var finished = _was && !switching;
        _was = switching;
        return finished;
    }
}
