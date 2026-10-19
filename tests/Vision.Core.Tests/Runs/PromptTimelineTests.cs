using System.Text.Json.Nodes;

namespace Vision.Core.Tests;

/// <summary>The Prompts timeline's rules and file: the same words and JSON as the Mac's PromptTimeline.swift.</summary>
public sealed class PromptTimelineTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 15, 0, 0, TimeSpan.Zero);

    private static PromptRecord Record(string text, DateTimeOffset at, PromptOutcome outcome = PromptOutcome.Completed, double? seconds = null) => new()
    {
        ChatId = "chat-1", Text = text, At = at, Outcome = outcome, FinishedAt = seconds is { } s ? at.AddSeconds(s) : null,
    };

    // MARK: Words

    [Theory]
    [InlineData(PromptOutcome.Queued, false, "Queued")]
    [InlineData(PromptOutcome.Running, false, "Running")]
    [InlineData(PromptOutcome.Completed, false, "Completed")]
    [InlineData(PromptOutcome.Completed, true, "Completed with results")]
    [InlineData(PromptOutcome.Issues, false, "Finished with issues")]
    [InlineData(PromptOutcome.NeedsYou, false, "Needs you")]
    [InlineData(PromptOutcome.Failed, false, "Failed")]
    [InlineData(PromptOutcome.Stopped, false, "Stopped")]
    [InlineData(PromptOutcome.Unknown, false, "Outcome not recorded")]
    public void HeadlinesAreTheMacs(PromptOutcome outcome, bool evidence, string headline)
    {
        var record = Record("x", Now, outcome);
        if (evidence) record.Evidence.Add(new Evidence(EvidenceKind.File, "2 files changed", "a.cs, b.cs", Now));
        Assert.Equal(headline, record.Headline);
    }

    [Fact]
    public void TimingSaysDurationEstimateAttemptsRoundsAndEvidence()
    {
        var record = Record("x", Now, seconds: 312);
        record.EtaSeconds = 240;
        record.Attempts = 2;
        record.Rounds = 3;
        record.Evidence.Add(new Evidence(EvidenceKind.Check, "c", "", Now));
        Assert.Equal("5m 12s · est. about 4 min · 2 attempts · 3 rounds · 1 evidence item", PromptTimeline.Timing(record, Now));
        Assert.Equal("1m 12s over the estimate", PromptTimeline.VersusEstimate(record));

        var running = Record("y", Now.AddSeconds(-100), PromptOutcome.Running);
        Assert.Equal("running 1m 40s", PromptTimeline.Timing(running, Now));
    }

    // MARK: Grouping and search

    [Fact]
    public void DaysAreTodayYesterdayAWeekdayOrADate()
    {
        Assert.Equal("Today", PromptTimeline.DayTitle(Now.AddHours(-2), Now, Utc));
        Assert.Equal("Yesterday", PromptTimeline.DayTitle(Now.AddDays(-1), Now, Utc));
        Assert.Equal("Wednesday", PromptTimeline.DayTitle(new DateTimeOffset(2026, 10, 14, 9, 0, 0, TimeSpan.Zero), Now, Utc));
        Assert.Equal("Oct 2", PromptTimeline.DayTitle(new DateTimeOffset(2026, 10, 2, 9, 0, 0, TimeSpan.Zero), Now, Utc));
        Assert.Equal("Dec 30, 2025", PromptTimeline.DayTitle(new DateTimeOffset(2025, 12, 30, 9, 0, 0, TimeSpan.Zero), Now, Utc));
    }

    [Fact]
    public void GroupsAreNewestFirstUnderDayHeadings()
    {
        var records = new[]
        {
            Record("old", Now.AddDays(-1)), Record("newer", Now.AddHours(-1)), Record("newest", Now.AddMinutes(-1)),
        };
        var groups = PromptTimeline.Grouped(records, Now, Utc);
        Assert.Equal(["Today", "Yesterday"], groups.Select(g => g.Title));
        Assert.Equal(["newest", "newer"], groups[0].Records.Select(r => r.Text));
    }

    [Fact]
    public void SearchMatchesEveryWordAnywhere()
    {
        var a = Record("Fix the parser", Now);
        a.CompletionNote = "All 14 tests pass";
        var b = Record("Write the README", Now, PromptOutcome.Failed);
        b.ChatId = "chat-2";
        var titles = new Dictionary<string, string> { ["chat-2"] = "Docs week" };
        Assert.Equal([a], PromptTimeline.Filter([a, b], "parser 14"));
        Assert.Equal([b], PromptTimeline.Filter([a, b], "docs failed", titles));
        Assert.Equal(2, PromptTimeline.Filter([a, b], "  ").Count);
        Assert.Empty(PromptTimeline.Filter([a, b], "parser docs", titles));
    }

    // MARK: The quote

    [Fact]
    public void AddToComposerQuotesThePromptWithHowItEnded()
    {
        var record = Record("Fix the parser\nand add tests", new DateTimeOffset(2026, 10, 18, 14, 5, 0, TimeSpan.Zero), seconds: 312);
        record.Evidence.Add(new Evidence(EvidenceKind.RunOutput, "14 tests pass", "", Now));
        record.CompletionNote = "Validated against the plan: 3 of 3 steps done.\n\nMore text";
        Assert.Equal(
            "About my earlier prompt (sent Oct 18 at 14:05 — Completed with results, 5m 12s):\n> Fix the parser\n> and add tests\n" +
            "Outcome: 14 tests pass · Validated against the plan: 3 of 3 steps done.\n\n",
            PromptTimeline.Quote(record, Utc));

        var goal = Record(string.Join("\n", Enumerable.Range(1, 20).Select(i => $"line {i}")), Now, PromptOutcome.Stopped);
        goal.Source = PromptSource.Goal;
        goal.Reason = "Stopped before it finished.";
        var quote = PromptTimeline.Quote(goal, Utc);
        Assert.StartsWith("About my earlier goal (sent Oct 18 at 15:00 — Stopped):\n> line 1\n", quote);
        Assert.Contains("> line 12\n> …\nOutcome: Stopped before it finished.\n\n", quote);
        Assert.DoesNotContain("line 13", quote);
    }

    // MARK: Jumping back to it

    [Fact]
    public void JumpFindsTheRowWithTheSameTextClosestInTime()
    {
        var record = Record("hi", Now);
        record.EntryText = "hi";
        record.EntryAt = Now.AddSeconds(1);
        var rows = new[]
        {
            new PromptTimeline.EntryRef("e1", "hi", Now.AddHours(-1)),
            new PromptTimeline.EntryRef("e2", "hi", Now.AddSeconds(2)),
            new PromptTimeline.EntryRef("e3", "other", Now),
        };
        Assert.Equal("e2", PromptTimeline.MatchEntry(record, rows));

        var goal = Record("Ship it", Now);
        goal.EntryText = "🎯 /goal Ship it";
        Assert.Equal("g", PromptTimeline.MatchEntry(goal, [new PromptTimeline.EntryRef("g", "🎯 /goal Ship it", Now)]));
        // Its row text was never noted (an older record): the row containing its first line.
        var loose = Record("Ship it\nnow", Now);
        Assert.Equal("g", PromptTimeline.MatchEntry(loose, [new PromptTimeline.EntryRef("g", "🎯 /goal Ship it\nnow", Now)]));
        Assert.Null(PromptTimeline.MatchEntry(Record("gone", Now), rows));
    }

    // MARK: Backfill

    [Theory]
    [InlineData("Fix it", PromptSource.Typed, "Fix it")]
    [InlineData("🎯 /goal Ship 1.0", PromptSource.Goal, "Ship 1.0")]
    [InlineData("📋 Task: Write tests", PromptSource.Task, "Write tests")]
    [InlineData("/release-notes v2", PromptSource.Skill, "/release-notes v2")]
    public void PromptRowsAreClassified(string row, PromptSource source, string text) =>
        Assert.Equal((source, text), PromptTimeline.Classify(row));

    [Theory]
    [InlineData("↻ round 3: keep going")]
    [InlineData("↻ round 2: recover and keep going")]
    [InlineData("↻ validation: fix and check again (attempt 2)")]
    [InlineData("🤖 Background agents finished — continuing")]
    [InlineData("🎯 /goal (resuming) Ship 1.0")]
    [InlineData("📋 Task (resuming): Write tests")]
    public void TheHarnesssOwnRowsAreNotPrompts(string row) => Assert.Null(PromptTimeline.Classify(row));

    [Fact]
    public void BackfillReadsEachPromptsEndingFromWhatFollowedIt()
    {
        var at = Now;
        PromptTimeline.Row R(string kind, string text) => new(kind, text, at = at.AddSeconds(10));
        var rows = new[]
        {
            R("user", "What does it do?"), R("assistant", "It parses CSV."),
            R("user", "🎯 /goal Ship 1.0"), R("assistant", "Working"), R("user", "↻ round 2: keep going"), R("notice", "✅ Goal complete after 2 rounds."),
            R("user", "Deploy"), R("tool", "ran"), R("notice", "Stopped."),
            R("user", "📋 Task: Migrate"), R("error", "Task failed: “Migrate”. Fix what's needed in this chat, then press Resume on the task (Task List panel) to retry it."),
            R("user", "🎯 /goal Rotate keys"), R("notice", "⏸ Goal paused — the agent needs you: which vault entry?"),
            R("user", "Hello?"),
        };
        var records = PromptTimeline.Backfill("c", rows);
        Assert.Equal(
            [(PromptSource.Typed, PromptOutcome.Completed), (PromptSource.Goal, PromptOutcome.Completed), (PromptSource.Typed, PromptOutcome.Stopped),
             (PromptSource.Task, PromptOutcome.Failed), (PromptSource.Goal, PromptOutcome.NeedsYou), (PromptSource.Typed, PromptOutcome.Unknown)],
            records.Select(r => (r.Source, r.Outcome)));
        Assert.All(records, r => Assert.True(r.Backfilled));
        Assert.Equal("It parses CSV.", records[0].CompletionNote);
        Assert.Equal(2, records[1].Rounds);
        Assert.Null(records[0].Rounds);
        Assert.StartsWith("⏸ Goal paused", records[4].Reason);
        Assert.Equal("🎯 /goal Ship 1.0", records[1].EntryText);
        Assert.Equal(rows[3].At, records[1].At.AddSeconds(10)); // (sanity of the clock in this test)
        Assert.Equal(rows[5].At, records[1].FinishedAt);
    }

    [Fact]
    public void AChatKeepsItsNewestFiveHundred()
    {
        var many = Enumerable.Range(0, 520).Select(i => Record($"p{i}", Now.AddMinutes(i))).ToList();
        var capped = PromptTimeline.Capped(many);
        Assert.Equal(500, capped.Count);
        Assert.Equal("p20", capped[0].Text);
        Assert.Equal("p519", capped[^1].Text);
    }

    // MARK: The file

    [Fact]
    public void TheFileIsTheMacsShapeAndReadsBack()
    {
        using var dir = new TempDirectory("vision-prompts");
        var store = new PromptTimelineStore(dir["prompt-timeline"]);
        var record = Record("Fix the parser", Now, seconds: 90);
        record.Source = PromptSource.Goal;
        record.EtaSeconds = 120;
        record.TurnId = "turn-1";
        record.Model = "qwen3-coder";
        record.Route = "DGX Spark";
        record.Rounds = 2;
        record.Evidence.Add(new Evidence(EvidenceKind.RunOutput, "14 tests pass", "dotnet test", Now.AddSeconds(80)) { Id = "ev-1" });
        store.Save("chat/1", [record]);

        var path = store.FilePath("chat/1");
        Assert.EndsWith("chat_1.json", path);
        var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.Equal("vision.prompts/1", (string)json["schema"]!);
        Assert.Equal("chat/1", (string)json["chatID"]!);
        var stored = json["prompts"]![0]!.AsObject();
        Assert.Equal("goal", (string)stored["source"]!);
        Assert.Equal("completed", (string)stored["outcome"]!);
        Assert.Equal("turn-1", (string)stored["turnID"]!);
        Assert.Equal("2026-10-18T15:00:00.000Z", (string)stored["at"]!);
        Assert.Equal("runOutput", (string)stored["evidence"]![0]!["kind"]!);
        Assert.False(stored.ContainsKey("reason")); // nils are left out, as the Mac's encoder does
        Assert.Equal(stored.Select(p => p.Key).OrderBy(k => k, StringComparer.Ordinal), stored.Select(p => p.Key));

        var back = Assert.Single(store.Load("chat/1")!);
        Assert.Equal((record.Id, "Fix the parser", PromptSource.Goal, PromptOutcome.Completed, 120.0, 2, "DGX Spark"),
            (back.Id, back.Text, back.Source, back.Outcome, back.EtaSeconds!.Value, back.Rounds!.Value, back.Route));
        Assert.Equal(Now.AddSeconds(90), back.FinishedAt);
        Assert.Equal("ev-1", Assert.Single(back.Evidence).Id);
        Assert.Equal(["chat_1"], store.ChatIds());
    }

    [Fact]
    public void AFileTheMacWroteIsRead()
    {
        const string mac = """
            {"chatID":"ABC","prompts":[{"at":"2026-10-17T10:00:00.250Z","attempts":2,"backfilled":false,"chatID":"ABC",
            "completionTokens":12,"evidence":[{"at":"2026-10-17T10:02:00Z","detail":"d","id":"e","kind":"screenshot","title":"Screenshot of the result"}],
            "id":"R1","outcome":"issues","promptTokens":34,"source":"queue","text":"Do it","turnID":"T","futureField":true}],"schema":"vision.prompts/1"}
            """;
        var record = Assert.Single(PromptTimelineStore.Decode(mac)!);
        Assert.Equal((PromptSource.Queue, PromptOutcome.Issues, 2, 34L, 12L, "T"),
            (record.Source, record.Outcome, record.Attempts, record.PromptTokens, record.CompletionTokens, record.TurnId));
        Assert.Equal(EvidenceKind.Screenshot, record.Evidence.Single().Kind);
        Assert.Equal(new DateTimeOffset(2026, 10, 17, 10, 0, 0, 250, TimeSpan.Zero), record.At);
        Assert.Equal("Queue task", record.Source.Label());
    }

    [Fact]
    public void AValueOfTheWrongShapeTakesItsDefault()
    {
        var record = Assert.Single(PromptTimelineStore.Decode("""{"prompts":[{"id":"x","text":5,"outcome":"exploded","attempts":"two","at":"never"}]}""")!);
        Assert.Equal(("x", "", PromptOutcome.Unknown, 1), (record.Id, record.Text, record.Outcome, record.Attempts));
    }

    [Fact]
    public void AnUnreadableFileIsSetAsideNotOverwritten()
    {
        using var dir = new TempDirectory("vision-prompts");
        var store = new PromptTimelineStore(dir.Path);
        File.WriteAllText(store.FilePath("c"), "{ not json");
        Assert.Null(store.Load("c"));
        Assert.False(File.Exists(store.FilePath("c")));
        Assert.Single(Directory.GetFiles(dir.Path, "c.unreadable-*.json"));
        Assert.Empty(store.ChatIds());
    }
}
