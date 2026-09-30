namespace Dsh.App.Model;

/// <summary>How the plan and queue panels share the right-hand side of the window. The chat keeps its own minimum;
/// what is left is theirs: each panel asks for the width it was last given, the plan gives some up first, then the queue,
/// and when even the two minimums don't fit side by side the plan panel has to close.</summary>
public static class PanelLayout
{
    public const double PlanMin = 260, PlanMax = 520;
    public const double QueueMin = 300, QueueMax = 560;

    /// <summary>The widths to give (0 for a panel that is closed) and whether the plan panel must close for the queue to fit.</summary>
    public readonly record struct Widths(double Plan, double Queue, bool ClosePlan);

    /// <param name="room">What the panels can share: the window body less the sidebar, the splitters and the chat's minimum.</param>
    /// <param name="wantedPlan">The width the plan panel was last given (a drag, or the default).</param>
    /// <param name="wantedQueue">The width the queue panel was last given.</param>
    public static Widths Fit(double room, double wantedPlan, double wantedQueue, bool planOpen, bool queueOpen)
    {
        var plan = planOpen ? Wanted(wantedPlan, PlanMin, PlanMax) : 0;
        var queue = queueOpen ? Wanted(wantedQueue, QueueMin, QueueMax) : 0;
        // The queue may be running unattended work someone is watching; the plan is the one to give way.
        var closePlan = planOpen && queueOpen && room < PlanMin + QueueMin;
        if (closePlan) plan = 0;

        var over = plan + queue - room;
        if (over > 0 && plan > 0)
        {
            var give = Math.Min(over, plan - PlanMin);
            plan -= give;
            over -= give;
        }
        if (over > 0 && queue > 0) queue -= Math.Min(over, queue - QueueMin);
        return new Widths(plan, queue, closePlan);
    }

    /// <summary>A stored width is a number the settings file could hold anything in: NaN or infinity mean "the smallest".</summary>
    public static double Clamp(double value, double min, double max) => double.IsFinite(value) ? Math.Clamp(value, min, max) : min;

    private static double Wanted(double value, double min, double max) => Clamp(value, min, max);
}
