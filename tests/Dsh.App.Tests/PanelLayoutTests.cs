using Dsh.App.Model;

namespace Dsh.App.Tests;

/// <summary>The plan and queue panels share what the window has left beside the chat: neither pushes the chat under its
/// minimum or runs off the right edge, they grow back when there is room again, and when the two won't fit at all the
/// plan gives way.</summary>
public sealed class PanelLayoutTests
{
    [Fact]
    public void PanelsGetTheWidthTheyWereLastGivenWhenThereIsRoom()
    {
        var fit = PanelLayout.Fit(room: 1_000, wantedPlan: 340, wantedQueue: 360, planOpen: true, queueOpen: true);
        Assert.Equal(new PanelLayout.Widths(340, 360, false), fit);
    }

    [Fact]
    public void WantedWidthsAreKeptWithinEachPanelsLimits()
    {
        var fit = PanelLayout.Fit(room: 5_000, wantedPlan: 50, wantedQueue: 9_000, planOpen: true, queueOpen: true);
        Assert.Equal(PanelLayout.PlanMin, fit.Plan);
        Assert.Equal(PanelLayout.QueueMax, fit.Queue);
    }

    [Fact]
    public void ThePlanGivesUpWidthBeforeTheQueueDoes()
    {
        // A 1200-pixel window with the sidebar open leaves 565 for the panels; the queue is open at 360 and the plan is
        // asked for at 340: 700 wanted, so the plan shrinks to its minimum, then the queue gives up the rest.
        var fit = PanelLayout.Fit(room: 565, wantedPlan: 340, wantedQueue: 360, planOpen: true, queueOpen: true);
        Assert.False(fit.ClosePlan);
        Assert.Equal(PanelLayout.PlanMin, fit.Plan);
        Assert.Equal(305, fit.Queue);
        Assert.Equal(565, fit.Plan + fit.Queue);
    }

    [Theory]
    [InlineData(1_000)]
    [InlineData(700)]
    [InlineData(600)]
    [InlineData(565)]
    [InlineData(561)]
    public void TheTwoPanelsNeverAddUpToMoreThanTheRoomOnceTheirMinimumsFit(double room)
    {
        var fit = PanelLayout.Fit(room, wantedPlan: 520, wantedQueue: 560, planOpen: true, queueOpen: true);
        Assert.False(fit.ClosePlan);
        Assert.True(fit.Plan + fit.Queue <= room + 1e-9, $"{fit.Plan} + {fit.Queue} in {room}");
        Assert.True(fit.Plan >= PanelLayout.PlanMin && fit.Queue >= PanelLayout.QueueMin);
    }

    [Fact]
    public void WhenEvenTheMinimumsDoNotFitTheQueueStaysAndThePlanCloses()
    {
        var fit = PanelLayout.Fit(room: 365, wantedPlan: 340, wantedQueue: 360, planOpen: true, queueOpen: true);
        Assert.True(fit.ClosePlan);
        Assert.Equal(0, fit.Plan);
        Assert.Equal(360, fit.Queue);
    }

    [Fact]
    public void ALonePanelGetsAsMuchAsItWantsAndNoMoreThanThereIsRoomFor()
    {
        Assert.Equal(new PanelLayout.Widths(340, 0, false), PanelLayout.Fit(room: 800, wantedPlan: 340, wantedQueue: 360, planOpen: true, queueOpen: false));
        Assert.Equal(new PanelLayout.Widths(0, 400, false), PanelLayout.Fit(room: 400, wantedPlan: 340, wantedQueue: 500, planOpen: false, queueOpen: true));
        // Less room than its minimum: it still gets its minimum (the chat is squeezed, nothing can be done about that).
        Assert.Equal(PanelLayout.QueueMin, PanelLayout.Fit(room: 120, wantedPlan: 340, wantedQueue: 360, planOpen: false, queueOpen: true).Queue);
        Assert.False(PanelLayout.Fit(room: 120, wantedPlan: 340, wantedQueue: 360, planOpen: true, queueOpen: false).ClosePlan);
    }

    [Fact]
    public void ClosedPanelsGetNothing()
    {
        Assert.Equal(new PanelLayout.Widths(0, 0, false), PanelLayout.Fit(room: 900, wantedPlan: 340, wantedQueue: 360, planOpen: false, queueOpen: false));
    }

    [Fact]
    public void PanelsGrowBackWhenTheWindowDoes()
    {
        // The wanted widths are what was last given, not what the last squeeze left, so the same call with more room restores them.
        var squeezed = PanelLayout.Fit(room: 580, wantedPlan: 340, wantedQueue: 360, planOpen: true, queueOpen: true);
        var roomy = PanelLayout.Fit(room: 900, wantedPlan: 340, wantedQueue: 360, planOpen: true, queueOpen: true);
        Assert.True(squeezed.Plan < 340);
        Assert.Equal(new PanelLayout.Widths(340, 360, false), roomy);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void AJunkStoredWidthIsTheMinimumNotACrash(double stored)
    {
        var fit = PanelLayout.Fit(room: 900, wantedPlan: stored, wantedQueue: stored, planOpen: true, queueOpen: true);
        Assert.Equal(PanelLayout.PlanMin, fit.Plan);
        Assert.Equal(PanelLayout.QueueMin, fit.Queue);
    }
}

/// <summary>Every poll of the Spark's status says whether it is switching; only the step from yes to no is an ending.</summary>
public sealed class SwitchEdgeTests
{
    [Fact]
    public void OnlyTheEndOfASwitchCounts()
    {
        var edge = new SwitchEdge();
        var finished = new[] { false, false, true, true, false, false, false, true, false }.Select(edge.Finished).ToList();
        Assert.Equal([false, false, false, false, true, false, false, false, true], finished);
    }

    [Fact]
    public void PollsThatNeverSwitchNeverFinishAnything()
    {
        var edge = new SwitchEdge();
        Assert.All(Enumerable.Range(0, 20), _ => Assert.False(edge.Finished(false)));
    }
}
