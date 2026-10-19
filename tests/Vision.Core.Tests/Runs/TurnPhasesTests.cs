namespace Vision.Core.Tests;

/// <summary>Ported from the Mac's TurnPhasesTests.swift with the same inputs and expected values (the estimator is shared
/// behaviour), plus the Windows validation rule and the no-progress check.</summary>
public sealed class TurnPhasesTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 18, 9, 0, 0, TimeSpan.Zero);

    // MARK: Classification

    [Theory]
    [InlineData("Take a screenshot of the settings window and click Save", TaskKind.ScreenWork)]
    [InlineData("Fix the flaky CSV import test", TaskKind.CodeEdit)]
    [InlineData("Run the test suite and tell me what fails", TaskKind.BuildRun)]
    [InlineData("Draft release notes for 0.3.0", TaskKind.Writing)]
    [InlineData("Find every place we hardcode the API URL", TaskKind.Research)]
    [InlineData("Explain how compaction works", TaskKind.Research)]
    [InlineData("what is a context window?", TaskKind.Research)]
    [InlineData("Do the thing", TaskKind.OpenEnded)]
    public void AsksAreClassifiedAsOnTheMac(string prompt, TaskKind kind) => Assert.Equal(kind, TurnEstimator.Kind(prompt));

    [Fact]
    public void KindsAreStoredAsTheMacsRawValues()
    {
        Assert.Equal(["quickQuestion", "research", "codeEdit", "buildRun", "writing", "screenWork", "openEnded"],
            TaskKinds.All.Select(k => k.RawValue()));
        foreach (var kind in TaskKinds.All) Assert.Equal(kind, TaskKinds.FromRaw(kind.RawValue()));
        Assert.Null(TaskKinds.FromRaw("somethingNew"));
    }

    // MARK: Scope

    [Fact]
    public void ScopeGrowsWithWordsAndSteps()
    {
        var shortAsk = TurnEstimator.Scope("fix the bug");
        var wordy = TurnEstimator.Scope(string.Concat(Enumerable.Repeat("please also fix the parser and then run everything and also update the readme then deploy it after that ", 3)));
        Assert.True(wordy > shortAsk * 1.5);
        Assert.True(TurnEstimator.Scope("fix the bug", plannedSteps: 8) > shortAsk * 1.5);
    }

    [Fact]
    public void ScopeIsBounded()
    {
        Assert.True(TurnEstimator.Scope(string.Concat(Enumerable.Repeat("word ", 5000)), plannedSteps: 40, projectFiles: 5000) <= 4.0);
        Assert.True(TurnEstimator.Scope("hi") >= 0.35);
    }

    // MARK: Estimates

    [Fact]
    public void TheFirstEstimateIsTheCatalogPrior()
    {
        var (seconds, basis) = TurnEstimator.Estimate(TaskKind.CodeEdit, 1.0, new DurationSample());
        Assert.Equal(210, seconds);
        Assert.Equal("first estimate for a code change — it refines as these complete", basis);
    }

    [Fact]
    public void TheEstimateLearnsFromSamples()
    {
        var samples = new DurationSample(3, 100, 1);
        for (var i = 0; i < 3; i++) samples = TurnEstimator.Record(100, 1.0, samples);
        var (seconds, basis) = TurnEstimator.Estimate(TaskKind.CodeEdit, 1.0, samples);
        // Six samples of 100s: weight 0.75 of 100 toward the 210 prior.
        Assert.Equal(128, seconds, 0);
        Assert.Equal("learned from 6 similar tasks", basis);
    }

    [Fact]
    public void TheEstimateScalesWithScopeAgainstTheLearnedMean()
    {
        var samples = new DurationSample();
        for (var i = 0; i < 6; i++) samples = TurnEstimator.Record(60, 1.0, samples);
        var (seconds, basis) = TurnEstimator.Estimate(TaskKind.Writing, 2.0, samples);
        Assert.InRange(seconds, 101, 199);
        Assert.EndsWith(" · this one is bigger than usual", basis);
    }

    [Fact]
    public void JunkDurationsAreIgnored()
    {
        var samples = new DurationSample(2, 120, 1);
        samples = TurnEstimator.Record(0.1, 1, samples);
        samples = TurnEstimator.Record(100_000, 1, samples);
        Assert.Equal(2, samples.Count);
        Assert.Equal(120, samples.MeanSeconds, 3);
    }

    [Theory]
    [InlineData(2, "about 5s")]
    [InlineData(20, "about 20s")]
    [InlineData(60, "about a minute")]
    [InlineData(240, "about 4 min")]
    [InlineData(4200, "about 1h 10m")]
    [InlineData(3600, "about 1h")]
    public void EstimatesArePhrasedAsOnTheMac(double seconds, string phrase) => Assert.Equal(phrase, TurnEstimator.Phrase(seconds));

    // MARK: Transitions

    [Fact]
    public void PlanningToExecutionToResults()
    {
        var state = new TurnPhaseState();
        state.BeginPlanning(TaskKind.CodeEdit, 1.2, T0);
        Assert.Equal(TurnPhase.Planning, state.Phase);
        Assert.Equal("Planning started — assessing the ask", state.Steps.Single().Text);

        state.BeginExecution("1. fix\n2. test", 120, "learned from 3 similar tasks", T0.AddSeconds(5));
        Assert.Equal(TurnPhase.Execution, state.Phase);
        Assert.Equal("Execution started — estimated about 2 min (learned from 3 similar tasks)", state.Steps[^1].Text);
        Assert.Equal(TimeSpan.FromSeconds(10), state.ExecutionRuntime(T0.AddSeconds(15)));

        state.AddStep("edited report.py", T0.AddSeconds(30));
        state.AddEvidence(new Evidence(EvidenceKind.Check, "All 14 tests pass", "python3 -m pytest", T0.AddSeconds(40)));
        state.BeginResults("Validated against the plan: 2 of 2 steps done.", T0.AddSeconds(45));
        Assert.Equal(TurnPhase.Results, state.Phase);
        Assert.True(state.IsOver);
        Assert.Equal("Completed with results", state.Headline);
        Assert.Equal(TimeSpan.FromSeconds(45), state.Runtime(T0.AddHours(1)));
        Assert.Equal("runtime 45s · est. about 2 min", PhaseText.RuntimeLine(state, T0.AddHours(1)));
    }

    [Fact]
    public void ATurnThatEndsEarlySaysWhyAndStops()
    {
        var state = new TurnPhaseState();
        state.BeginPlanning(TaskKind.Research, 1, T0);
        state.End("Stopped before finishing.", T0.AddSeconds(3));
        Assert.True(state.IsOver);
        Assert.Equal("Stopped before finishing.", state.Headline);
        Assert.Equal(TimeSpan.FromSeconds(3), state.Runtime(T0.AddMinutes(9)));
    }

    [Fact]
    public void TheLoopbackIsUnlimitedByDefault()
    {
        var state = new TurnPhaseState();
        state.BeginExecution("p", 10, "b", T0);
        for (var i = 0; i < 7; i++)
        {
            Assert.True(state.LoopBack("test still fails", T0));
            Assert.Equal(TurnPhase.Execution, state.Phase);
            Assert.Equal(i + 1, state.Loopbacks);
        }
        Assert.Equal("Validation failed (test still fails) — back to execution, attempt 8", state.Steps[^1].Text);
    }

    [Fact]
    public void TheLoopbackCanBeCapped()
    {
        var state = new TurnPhaseState { LoopbackCap = 3 };
        state.BeginExecution("p", 10, "b", T0);
        Assert.True(state.LoopBack("x", T0));
        Assert.True(state.LoopBack("x", T0));
        Assert.True(state.LoopBack("x", T0));
        Assert.False(state.LoopBack("x", T0));
        Assert.Equal(3, state.Loopbacks);
        Assert.Equal("x", state.LastValidationFailure);
    }

    [Fact]
    public void StepsAndEvidenceAreBounded()
    {
        var state = new TurnPhaseState();
        for (var i = 0; i < 450; i++) state.AddStep($"step {i}", T0);
        Assert.Equal(400, state.Steps.Count);
        Assert.Equal("step 449", state.Steps[^1].Text);
        for (var i = 0; i < 30; i++) state.AddEvidence(new Evidence(EvidenceKind.Note, $"e{i}", "", T0));
        Assert.Equal(24, state.Evidence.Count);
        Assert.Equal("e6", state.Evidence[0].Title);
    }

    [Fact]
    public void ACloneIsIndependent()
    {
        var state = new TurnPhaseState();
        state.AddStep("a", T0);
        var copy = state.Clone();
        state.AddStep("b", T0);
        Assert.Single(copy.Steps);
        Assert.Equal(state.TurnId, copy.TurnId);
    }

    // MARK: The runner and its cache

    [Fact]
    public void TheRunnerClassifiesScopesAndStartsPlanning()
    {
        var state = new TurnPhaseRunner().Begin("take a screenshot of the dashboard", T0);
        Assert.Equal(TaskKind.ScreenWork, state.TaskKind);
        Assert.Equal(TurnPhase.Planning, state.Phase);
        Assert.True(state.ScopeScale > 0);
        Assert.Null(state.LoopbackCap);
    }

    [Fact]
    public void TheRunnerRefinesUpwardOnlyAndAtMostAThird()
    {
        var runner = new TurnPhaseRunner();
        var state = runner.Begin("fix the bug and write tests", T0);
        runner.PlanSettled(state, "a plan", 3, T0);
        var first = state.EtaSeconds!.Value;
        runner.Refine(state, 9);
        Assert.True(state.EtaSeconds > first);
        var fresh = TurnEstimator.Estimate(state.TaskKind!.Value, TurnEstimator.Scope("a plan", plannedSteps: 9), runner.Sample(state.TaskKind!.Value)).Seconds;
        Assert.True(state.EtaSeconds <= Math.Min(first + (fresh - first) * 0.34 + 0.5, fresh + 0.5));
        var before = state.EtaSeconds;
        runner.Refine(state, 1); // a plan that shrinks never lowers it
        Assert.Equal(before, state.EtaSeconds);
    }

    [Fact]
    public void TheCacheIsSavedInTheMacsFormatAndReadBack()
    {
        using var dir = new TempDirectory("vision-eta");
        var path = dir[TurnPhaseRunner.FileName];
        var runner = new TurnPhaseRunner(path);
        var state = runner.Begin("fix the flaky test", T0);
        state.BeginExecution("p", 10, "b", T0.AddSeconds(5));
        state.BeginResults("done", T0.AddSeconds(90));
        Assert.Equal(1, runner.RecordCompletion(state, T0.AddHours(1))?.Count);

        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        Assert.Equal(1, (int)json["entries"]!["codeEdit"]!["count"]!);
        Assert.Equal(90, (double)json["entries"]!["codeEdit"]!["meanSeconds"]!, 3);
        Assert.NotNull(json["entries"]!["codeEdit"]!["meanScope"]);

        Assert.Equal(1, new TurnPhaseRunner(path).Sample(TaskKind.CodeEdit).Count);
    }

    [Fact]
    public void ACacheTheMacWroteIsRead()
    {
        using var dir = new TempDirectory("vision-eta");
        var path = dir.Write("eta-cache.json", """{"entries":{"writing":{"count":4,"meanSeconds":61.5,"meanScope":1.2},"tomorrowsKind":{"count":1,"meanSeconds":5,"meanScope":1}}}""");
        var runner = new TurnPhaseRunner(path);
        Assert.Equal(new DurationSample(4, 61.5, 1.2), runner.Sample(TaskKind.Writing));
        Assert.Single(runner.Samples);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"entries":{"writing":"many"}}""")]
    public void AnUnreadableCacheStartsOver(string text)
    {
        using var dir = new TempDirectory("vision-eta");
        var runner = new TurnPhaseRunner(dir.Write("eta-cache.json", text));
        Assert.Empty(runner.Samples);
    }

    // MARK: Validation

    [Theory]
    [InlineData("All done. Tests pass.", null)]
    [InlineData("Error: the build failed on line 3", "Error:")]
    [InlineData("I ran it.\n  - error: missing semicolon", "error: ")]
    [InlineData("Traceback (most recent call last):\n  File x", "Traceback (most recent call last)")]
    [InlineData("Build FAILED.\n0 Warning(s)", "Build FAILED")]
    // Quoting an error while explaining it is not reporting one.
    [InlineData("The message `Error: x` comes from the parser; I fixed it.", null)]
    [InlineData("Here is what it printed:\n```\nError: x\n```\nNow it passes.", null)]
    public void AReplyFailsValidationOnlyWhenItReportsAnError(string reply, string? marker) =>
        Assert.Equal(marker, TurnValidation.ReportedError(reply));

    [Fact]
    public void AnEmptyReplyAfterWorkFailsAndAQuestionDoesNot()
    {
        var worked = new TurnPhaseState { Plan = "1. do it" };
        Assert.Equal(["the turn ended without a summary"], TurnValidation.Failures(worked, " \n"));
        Assert.Empty(TurnValidation.Failures(new TurnPhaseState(), ""));
        Assert.Equal(["the reply reports an error (Error:)"], TurnValidation.Failures(new TurnPhaseState(), "Error: boom"));
    }

    [Fact]
    public void NoProgressMeansTheSameFailureAndNoNewStep()
    {
        Assert.False(TurnValidation.MadeNoProgress(null, "x", 0, 0)); // the first failure is not a lack of progress
        Assert.True(TurnValidation.MadeNoProgress("x", "x", 2, 2));
        Assert.False(TurnValidation.MadeNoProgress("x", "x", 2, 3)); // a step got done
        Assert.False(TurnValidation.MadeNoProgress("x", "y", 2, 2)); // a different problem
        Assert.Equal(2, TurnValidation.NoProgressLimit);
        Assert.Equal("↻ validation: fix and check again (attempt 2)", TurnValidation.LoopBackDisplay(2));
    }

    [Theory]
    [InlineData(300, 240, "1m over the estimate")]
    [InlineData(250, 240, "on time")]
    [InlineData(100, 240, "2m 20s under the estimate")]
    public void TheDurationIsComparedWithTheEstimate(double duration, double eta, string text) =>
        Assert.Equal(text, PhaseText.VersusEstimate(duration, eta));
}
