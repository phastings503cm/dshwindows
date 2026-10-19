using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vision.Core;

// MARK: - The life of one turn: Planning → Execution → Validation → Results
//
// Ported from Vision for macOS (TurnPhases.swift, 0.15.1) with the same words, thresholds and file format, so a turn
// reads the same on both harnesses and the ETA cache one writes is the cache the other reads.
//
//   PLANNING   the model drafts its approach (streaming, live) and an ETA is stated. The estimate is learned per task
//              *kind* (a small catalog), refined by the scope signals in the ask (words, planned steps, project size)
//              and by what similar turns took before. Guardian keeps the same durations across users and orgs; this
//              harness keeps its own small cache (eta-cache.json) and blends Guardian's in when signed in.
//   EXECUTION  the work itself: tool calls, streamed text. A green, semi-animated working badge shows the runtime
//              against the estimate; drilling in shows timestamped steps.
//   VALIDATION the turn is checked against the plan it set out with: an error reported in the reply, or no reply at all
//              after working, fails it. A /goal or a task loops back to execution (unlimited by default, cappable, and
//              stopped by the no-progress check); a plain turn ends "Finished with issues" for the person to decide.
//   RESULTS    a green "Completed with results" with the evidence (screenshots, files changed, checks, the closing
//              summary), every item stamped.
//
// Plain values and pure functions: the host drives them, tests check them headlessly. Nothing here talks to a model,
// runs a tool or draws anything.

public enum TurnPhase
{
    Planning,
    Execution,
    Validation,
    Results,
}

public static class TurnPhases
{
    public static string Label(this TurnPhase phase) => phase switch
    {
        TurnPhase.Planning => "Planning",
        TurnPhase.Execution => "Execution",
        TurnPhase.Validation => "Validation",
        _ => "Results",
    };

    public static IReadOnlyList<TurnPhase> All { get; } = [TurnPhase.Planning, TurnPhase.Execution, TurnPhase.Validation, TurnPhase.Results];
}

/// <summary>What the ask is <i>like</i> — the axis an ETA is learned along. Stored (eta-cache.json, Guardian's /harness/eta)
/// as the raw values the Mac writes: quickQuestion, research, codeEdit, buildRun, writing, screenWork, openEnded.</summary>
public enum TaskKind
{
    QuickQuestion,
    Research,
    CodeEdit,
    BuildRun,
    Writing,
    ScreenWork,
    OpenEnded,
}

public static class TaskKinds
{
    public static IReadOnlyList<TaskKind> All { get; } =
    [
        TaskKind.QuickQuestion, TaskKind.Research, TaskKind.CodeEdit, TaskKind.BuildRun, TaskKind.Writing,
        TaskKind.ScreenWork, TaskKind.OpenEnded,
    ];

    public static string RawValue(this TaskKind kind) => kind switch
    {
        TaskKind.QuickQuestion => "quickQuestion",
        TaskKind.Research => "research",
        TaskKind.CodeEdit => "codeEdit",
        TaskKind.BuildRun => "buildRun",
        TaskKind.Writing => "writing",
        TaskKind.ScreenWork => "screenWork",
        _ => "openEnded",
    };

    public static TaskKind? FromRaw(string? raw) => All.Cast<TaskKind?>().FirstOrDefault(k => k!.Value.RawValue() == raw);

    public static string Label(this TaskKind kind) => kind switch
    {
        TaskKind.QuickQuestion => "Quick question",
        TaskKind.Research => "Research",
        TaskKind.CodeEdit => "Code change",
        TaskKind.BuildRun => "Build or run",
        TaskKind.Writing => "Writing",
        TaskKind.ScreenWork => "Screen control",
        _ => "Open-ended task",
    };

    /// <summary>The median seconds a turn of this kind takes before anything is learned. Deliberately conservative.</summary>
    public static double DefaultSeconds(this TaskKind kind) => kind switch
    {
        TaskKind.QuickQuestion => 20,
        TaskKind.Research => 75,
        TaskKind.CodeEdit => 210,
        TaskKind.BuildRun => 180,
        TaskKind.Writing => 120,
        TaskKind.ScreenWork => 150,
        _ => 240,
    };

    /// <summary>How widely turns of this kind vary, as a fraction of the estimate.</summary>
    public static double Spread(this TaskKind kind) => kind switch
    {
        TaskKind.QuickQuestion => 0.6,
        TaskKind.Research => 0.8,
        TaskKind.CodeEdit => 1.0,
        TaskKind.BuildRun => 0.9,
        TaskKind.Writing => 0.7,
        TaskKind.ScreenWork => 0.9,
        _ => 1.4,
    };
}

/// <summary>One entry of the ETA cache: the running mean of finished turns of one kind. <see cref="MeanScope"/> is the mean
/// scope behind the mean, so a new ask's scope is corrected relative to what was averaged.</summary>
public sealed record DurationSample(int Count = 0, double MeanSeconds = 0, double MeanScope = 1);

/// <summary>One stamped line of a turn — what drilling into the working badge shows.</summary>
public sealed record PhaseStep(DateTimeOffset At, string Text);

/// <summary>What a piece of evidence is. Stored as screenshot, file, runOutput, check, note (as on the Mac).</summary>
public enum EvidenceKind
{
    Screenshot,
    File,
    RunOutput,
    Check,
    Note,
}

public static class EvidenceKinds
{
    public static string RawValue(this EvidenceKind kind) => kind switch
    {
        EvidenceKind.Screenshot => "screenshot",
        EvidenceKind.File => "file",
        EvidenceKind.RunOutput => "runOutput",
        EvidenceKind.Check => "check",
        _ => "note",
    };

    public static EvidenceKind FromRaw(string? raw) => raw switch
    {
        "screenshot" => EvidenceKind.Screenshot,
        "file" => EvidenceKind.File,
        "runOutput" => EvidenceKind.RunOutput,
        "check" => EvidenceKind.Check,
        _ => EvidenceKind.Note,
    };

    public static string Label(this EvidenceKind kind) => kind switch
    {
        EvidenceKind.Screenshot => "Screenshot",
        EvidenceKind.File => "File",
        EvidenceKind.RunOutput => "Run output",
        EvidenceKind.Check => "Check",
        _ => "Note",
    };
}

/// <summary>Proof the work is done: a picture, a file, a run's output, a check — shown on the results card and kept on the
/// prompt's timeline record.</summary>
public sealed record Evidence(EvidenceKind Kind, string Title, string Detail, DateTimeOffset At)
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
}

/// <summary>The phases of one turn. Mutable, owned by the host on the UI thread (the session raises a change after each
/// update); <see cref="Clone"/> takes a snapshot for anything that keeps it.</summary>
public sealed class TurnPhaseState
{
    /// <summary>A new prompt starts a new turn id; a /goal's rounds work inside one turn until the goal ends.</summary>
    public string TurnId { get; init; } = Guid.NewGuid().ToString();
    public TurnPhase Phase { get; set; } = TurnPhase.Planning;

    // Planning
    public TaskKind? TaskKind { get; set; }
    /// <summary>The scale the duration should scale with (1.0 = an ordinary ask).</summary>
    public double ScopeScale { get; set; } = 1;
    /// <summary>The plan as it is drafted: streamed text accumulates here live.</summary>
    public string Draft { get; set; } = "";
    /// <summary>The settled plan (its first todo list, its proposed plan, or the first paragraph of its answer).</summary>
    public string? Plan { get; set; }
    public double? EtaSeconds { get; set; }
    /// <summary>How the estimate was made ("learned from 6 similar tasks").</summary>
    public string? EtaBasis { get; set; }
    /// <summary>When planning began — the runtime clock starts here.</summary>
    public DateTimeOffset? StartedAt { get; set; }

    // Execution
    public DateTimeOffset? ExecutionStartedAt { get; set; }
    public List<PhaseStep> Steps { get; set; } = [];
    public int FilesChanged { get; set; }

    // Validation
    /// <summary>How many times validation sent the turn back to execution.</summary>
    public int Loopbacks { get; set; }
    /// <summary>The cap on loopbacks; null = unlimited (the default).</summary>
    public int? LoopbackCap { get; set; }
    public List<Evidence> Evidence { get; set; } = [];
    public string? LastValidationFailure { get; set; }
    /// <summary>When results began — or when the turn ended early (see <see cref="EndedReason"/>).</summary>
    public DateTimeOffset? FinishedAt { get; set; }
    /// <summary>The turn ended before results: stopped, failed, or the goal needs the person. The card stops animating and
    /// says why instead of showing a phase that is no longer running.</summary>
    public string? EndedReason { get; set; }

    public const int MaxSteps = 400;
    public const int MaxEvidence = 24;

    public TurnPhaseState Clone() => new()
    {
        TurnId = TurnId, Phase = Phase, TaskKind = TaskKind, ScopeScale = ScopeScale, Draft = Draft, Plan = Plan,
        EtaSeconds = EtaSeconds, EtaBasis = EtaBasis, StartedAt = StartedAt, ExecutionStartedAt = ExecutionStartedAt,
        Steps = [.. Steps], FilesChanged = FilesChanged, Loopbacks = Loopbacks, LoopbackCap = LoopbackCap,
        Evidence = [.. Evidence], LastValidationFailure = LastValidationFailure, FinishedAt = FinishedAt, EndedReason = EndedReason,
    };

    /// <summary>The turn is over, one way or the other.</summary>
    public bool IsOver => Phase == TurnPhase.Results || EndedReason is not null;

    /// <summary>The whole turn's wall clock: planning to now (or to its end).</summary>
    public TimeSpan Runtime(DateTimeOffset now) => StartedAt is { } start ? (FinishedAt ?? now) - start : TimeSpan.Zero;

    /// <summary>Seconds spent executing (validation loopbacks included).</summary>
    public TimeSpan ExecutionRuntime(DateTimeOffset now) => ExecutionStartedAt is { } start ? (FinishedAt ?? now) - start : TimeSpan.Zero;

    // MARK: Transitions

    public void BeginPlanning(TaskKind kind, double scope, DateTimeOffset at)
    {
        TaskKind = kind;
        ScopeScale = scope;
        Draft = "";
        Plan = null;
        Phase = TurnPhase.Planning;
        StartedAt = at;
        Steps.Add(new PhaseStep(at, "Planning started — assessing the ask"));
    }

    /// <summary>The plan settled and the estimate is in: execution begins.</summary>
    public void BeginExecution(string plan, double etaSeconds, string basis, DateTimeOffset at)
    {
        Plan = plan;
        EtaSeconds = etaSeconds;
        EtaBasis = basis;
        Phase = TurnPhase.Execution;
        ExecutionStartedAt = at;
        Steps.Add(new PhaseStep(at, $"Execution started — estimated {TurnEstimator.Phrase(etaSeconds)} ({basis})"));
    }

    public void AddStep(string text, DateTimeOffset at)
    {
        Steps.Add(new PhaseStep(at, text));
        if (Steps.Count > MaxSteps) Steps.RemoveRange(0, Steps.Count - MaxSteps);
    }

    /// <summary>Validation failed: back to execution. False when the cap is reached (the caller finishes as it is).</summary>
    public bool LoopBack(string reason, DateTimeOffset at)
    {
        if (LoopbackCap is { } cap && Loopbacks >= cap) return false;
        Loopbacks++;
        LastValidationFailure = reason;
        Phase = TurnPhase.Execution;
        Steps.Add(new PhaseStep(at, $"Validation failed ({reason}) — back to execution, attempt {Loopbacks + 1}"));
        return true;
    }

    public void AddEvidence(Evidence item)
    {
        Evidence.Add(item);
        if (Evidence.Count > MaxEvidence) Evidence.RemoveRange(0, Evidence.Count - MaxEvidence);
    }

    /// <summary>Validation passed (or the turn finished with what it has): the results phase.</summary>
    public void BeginResults(string summary, DateTimeOffset at)
    {
        Phase = TurnPhase.Results;
        FinishedAt = at;
        Steps.Add(new PhaseStep(at, summary));
    }

    /// <summary>The turn ended without reaching results (stopped, an error, a goal that needs the person).</summary>
    public void End(string reason, DateTimeOffset at)
    {
        EndedReason = reason;
        FinishedAt = at;
        Steps.Add(new PhaseStep(at, reason));
    }

    /// <summary>"Completed with results" when there is evidence, "Completed" without, "Finished with issues" when validation
    /// still found something, or why it ended early — the card's headline, the same words on every harness.</summary>
    public string Headline => EndedReason is { } ended ? ended
        : Phase != TurnPhase.Results ? Phase.Label()
        : LastValidationFailure is not null ? "Finished with issues"
        : Evidence.Count > 0 ? "Completed with results" : "Completed";
}

// MARK: - The estimator

public static class TurnEstimator
{
    private static readonly string[] Widening = [" and ", " then ", " also ", " plus ", " after that"];

    /// <summary>How big an ask is, from what is known before answering: its words, the steps its plan named, the size of the
    /// workspace. 1.0 is an ordinary ask; clamped to 0.35…4.</summary>
    public static double Scope(string prompt, int plannedSteps = 0, int projectFiles = 0)
    {
        var words = prompt.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        var scale = 0.55 + 0.45 * Math.Log2(Math.Max(words, 1) / 12.0);
        scale = Math.Clamp(scale, 0.4, 2.2);
        var lower = prompt.ToLowerInvariant();
        var widenHits = Widening.Count(w => lower.Contains(w, StringComparison.Ordinal));
        scale *= 1 + 0.12 * Math.Min(widenHits, 4);
        if (plannedSteps > 0) scale *= 1 + 0.18 * Math.Min(plannedSteps - 1, 8);
        if (projectFiles > 40) scale *= 1 + Math.Min(Math.Log2(projectFiles / 40.0), 1.5) * 0.25;
        return Math.Clamp(scale, 0.35, 4.0);
    }

    private static readonly string[] ScreenWords = ["screenshot", "click", "the screen", "desktop", "keyboard", "type into", "open the app", "focus ", "mouse", "drag the"];
    private static readonly string[] BuildWords = ["build ", "run ", "launch", "compile", "start the server", "restart", "deploy", "install "];
    private static readonly string[] ExplainWords = ["explain", "what does"];
    private static readonly string[] CodeWords = ["fix", "refactor", "implement", "add ", "write a test", "tests", "bug", "compile error", "typescript", "swift", "function", "the build is broken"];
    private static readonly string[] WritingWords = ["write", "draft", "rewrite", "summarize", "summarise", "email", "readme", "document", "blog", "post", "changelog", "notes for"];
    private static readonly string[] ResearchWords = ["find", "research", "look up", "look into", "compare", "explain", "how does", "why does", "what is", "tour", "review", "search"];

    /// <summary>The catalog kind of an ask, from the prompt alone (the same word lists as the Mac).</summary>
    public static TaskKind Kind(string prompt)
    {
        var p = prompt.ToLowerInvariant();
        bool Has(string[] words) => words.Any(w => p.Contains(w, StringComparison.Ordinal));
        if (Has(ScreenWords)) return TaskKind.ScreenWork;
        if (Has(BuildWords) && !Has(ExplainWords)) return TaskKind.BuildRun;
        if (Has(CodeWords)) return TaskKind.CodeEdit;
        if (Has(WritingWords)) return TaskKind.Writing;
        if (Has(ResearchWords)) return TaskKind.Research;
        return TaskKind.OpenEnded;
    }

    /// <summary>The estimate for an ask of <paramref name="kind"/> at <paramref name="scope"/>: the catalog default scaled,
    /// until there are samples; then the learned mean (scope-corrected) shrunk toward that default while evidence is thin.</summary>
    public static (double Seconds, string Basis) Estimate(TaskKind kind, double scope, DurationSample samples)
    {
        var prior = kind.DefaultSeconds() * scope;
        if (samples.Count <= 0 || samples.MeanSeconds <= 0)
            return (Round(prior), $"first estimate for a {kind.Label().ToLowerInvariant()} — it refines as these complete");
        var scopeCorrected = samples.MeanSeconds * (scope / Math.Max(samples.MeanScope, 0.2));
        var weight = samples.Count / (samples.Count + 2.0);
        var blended = weight * scopeCorrected + (1 - weight) * prior;
        var seconds = Round(Math.Clamp(blended, 3, 45 * 60));
        var basis = $"learned from {samples.Count} similar task{(samples.Count == 1 ? "" : "s")}";
        if (Math.Abs(scope - samples.MeanScope) > 0.25)
            basis += scope > samples.MeanScope ? " · this one is bigger than usual" : " · this one is smaller than usual";
        return (seconds, basis);
    }

    /// <summary>Fold a finished turn's wall clock into its kind's entry. Junk (under half a second, over four hours) is
    /// ignored.</summary>
    public static DurationSample Record(double seconds, double scope, DurationSample samples)
    {
        if (seconds <= 0.5 || seconds >= 4 * 3600) return samples;
        var count = samples.Count + 1;
        var mean = (samples.MeanSeconds * samples.Count + seconds) / count;
        var meanScope = (samples.MeanScope * (count - 1) + scope) / count;
        return new DurationSample(count, mean, meanScope);
    }

    /// <summary>"about 20s", "about a minute", "about 4 min", "about 1h 10m".</summary>
    public static string Phrase(double seconds)
    {
        var s = (long)Round(seconds);
        if (s < 45) return $"about {Math.Max(s, 5)}s";
        if (s < 90) return "about a minute";
        if (s < 3600) return $"about {(long)Round(s / 60.0)} min";
        var hours = s / 3600;
        var minutes = (long)Round(s % 3600 / 60.0);
        return minutes >= 5 ? $"about {hours}h {minutes}m" : $"about {hours}h";
    }

    /// <summary>Swift's rounded(): halves away from zero.</summary>
    private static double Round(double value) => Math.Round(value, MidpointRounding.AwayFromZero);
}

// MARK: - The runner: the ETA cache and the moves between phases

/// <summary>Drives the phases around a turn and keeps the learned-ETA cache (eta-cache.json:
/// <c>{"entries":{"codeEdit":{"count":3,"meanSeconds":184.2,"meanScope":1.1}}}</c>, the Mac's format). Thread-safe.</summary>
public sealed class TurnPhaseRunner
{
    public const string FileName = "eta-cache.json";

    private readonly Lock _lock = new();
    private readonly Dictionary<TaskKind, DurationSample> _samples;
    private readonly string? _path;

    /// <summary>The default cap on validation loopbacks; null = unlimited (the default — Patrick's answer to Q1).</summary>
    public int? LoopbackCapDefault { get; set; }

    public TurnPhaseRunner(string? path = null, int? loopbackCapDefault = null)
    {
        _path = path;
        LoopbackCapDefault = loopbackCapDefault;
        _samples = Load(path);
    }

    public IReadOnlyDictionary<TaskKind, DurationSample> Samples
    {
        get
        {
            lock (_lock) return new Dictionary<TaskKind, DurationSample>(_samples);
        }
    }

    public DurationSample Sample(TaskKind kind)
    {
        lock (_lock) return _samples.GetValueOrDefault(kind) ?? new DurationSample();
    }

    /// <summary>Merge one kind's entry in (Guardian's shared cache) and save.</summary>
    public void MergeSample(TaskKind kind, DurationSample sample)
    {
        lock (_lock) _samples[kind] = sample;
        Save();
    }

    /// <summary>A new turn: classify, scope, and open planning.</summary>
    public TurnPhaseState Begin(string prompt, DateTimeOffset at, int projectFiles = 0)
    {
        var state = new TurnPhaseState { LoopbackCap = LoopbackCapDefault };
        state.BeginPlanning(TurnEstimator.Kind(prompt), TurnEstimator.Scope(prompt, projectFiles: projectFiles), at);
        return state;
    }

    /// <summary>The plan settled: estimate from the scope so far (now with the planned steps) and what similar turns took,
    /// and move to execution.</summary>
    public void PlanSettled(TurnPhaseState state, string plan, int stepsPlanned, DateTimeOffset at)
    {
        var scope = TurnEstimator.Scope(state.Draft.Length == 0 ? state.Plan ?? plan : state.Draft, plannedSteps: stepsPlanned);
        state.ScopeScale = Math.Max(state.ScopeScale, scope);
        if (state.TaskKind is not { } kind) return;
        var (seconds, basis) = TurnEstimator.Estimate(kind, state.ScopeScale, Sample(kind));
        state.BeginExecution(plan, seconds, basis, at);
    }

    /// <summary>The plan grew mid-execution: move the estimate up, at most a third of the way, so it never whipsaws.</summary>
    public void Refine(TurnPhaseState state, int stepsPlanned)
    {
        if (state.Phase != TurnPhase.Execution || state.TaskKind is not { } kind || state.EtaSeconds is not { } eta) return;
        var scope = TurnEstimator.Scope(state.Plan ?? "", plannedSteps: stepsPlanned);
        var fresh = TurnEstimator.Estimate(kind, scope, Sample(kind)).Seconds;
        state.EtaSeconds = Math.Max(eta, eta + (fresh - eta) * 0.34);
    }

    /// <summary>A turn finished (any outcome): fold its wall clock into its kind's entry. The updated entry, or null when the
    /// turn never got far enough to classify.</summary>
    public DurationSample? RecordCompletion(TurnPhaseState state, DateTimeOffset now)
    {
        if (state.TaskKind is not { } kind || state.StartedAt is not { } start) return null;
        var seconds = ((state.FinishedAt ?? now) - start).TotalSeconds;
        DurationSample after;
        lock (_lock)
        {
            after = TurnEstimator.Record(seconds, state.ScopeScale, _samples.GetValueOrDefault(kind) ?? new DurationSample());
            _samples[kind] = after;
        }
        Save();
        return after;
    }

    // MARK: Persistence

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    private sealed class CacheFile
    {
        [JsonPropertyName("entries")] public Dictionary<string, DurationSample>? Entries { get; set; }
    }

    private static Dictionary<TaskKind, DurationSample> Load(string? path)
    {
        var output = new Dictionary<TaskKind, DurationSample>();
        if (path is null || !File.Exists(path)) return output;
        try
        {
            var file = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(path), Json);
            foreach (var (key, value) in file?.Entries ?? [])
            {
                if (TaskKinds.FromRaw(key) is { } kind && value is { Count: >= 0 } && double.IsFinite(value.MeanSeconds) && double.IsFinite(value.MeanScope))
                    output[kind] = value;
            }
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // A cache that can't be read starts over: estimates fall back to the catalog until turns finish.
        }
        return output;
    }

    private void Save()
    {
        if (_path is null) return;
        try
        {
            Dictionary<string, DurationSample> entries;
            lock (_lock) entries = _samples.ToDictionary(p => p.Key.RawValue(), p => p.Value);
            var text = JsonSerializer.Serialize(new CacheFile { Entries = entries }, Json);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, text);
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // The cache is a help; a write that fails is tried again with the next finished turn.
        }
    }
}

// MARK: - Validation

/// <summary>What the harness checks at the end of a turn on its own — what it can see, never the model's word alone — and the
/// no-progress rule that stops an unlimited loop-back.</summary>
public static class TurnValidation
{
    /// <summary>A line that begins with one of these reports a failure.</summary>
    private static readonly string[] Markers = ["Error:", "error: ", "Traceback (most recent call last)", "Build FAILED"];

    /// <summary>Why validation fails this turn (empty: it passes). <paramref name="reply"/> is the turn's closing words.
    /// A marker counts only at the start of a line (after indentation or a list marker) and outside code fences, so a reply
    /// that quotes an error while explaining it doesn't fail.</summary>
    public static IReadOnlyList<string> Failures(TurnPhaseState state, string reply)
    {
        var failures = new List<string>();
        if (ReportedError(reply) is { } marker) failures.Add($"the reply reports an error ({marker.Trim()})");
        if (reply.Trim().Length == 0 && (state.Plan is not null || state.FilesChanged > 0)) failures.Add("the turn ended without a summary");
        return failures;
    }

    /// <summary>The first failure marker the reply reports, or null.</summary>
    public static string? ReportedError(string reply)
    {
        var inFence = false;
        foreach (var raw in reply.Split('\n'))
        {
            var line = raw.TrimEnd('\r').TrimStart();
            if (line.StartsWith("```", StringComparison.Ordinal) || line.StartsWith("~~~", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }
            if (inFence) continue;
            while (line.Length > 1 && (line[0] is '-' or '*' or '>' or '•') && char.IsWhiteSpace(line[1])) line = line[2..].TrimStart();
            foreach (var marker in Markers)
                if (line.StartsWith(marker, StringComparison.Ordinal)) return marker;
        }
        return null;
    }

    /// <summary>The no-progress check (always on): after a failed validation, this attempt made no progress when it fails the
    /// same way as the attempt before it and finished no new plan step.</summary>
    public static bool MadeNoProgress(string? previousFailure, string failure, int stepsDoneBefore, int stepsDoneNow) =>
        previousFailure is not null && previousFailure == failure && stepsDoneNow <= stepsDoneBefore;

    /// <summary>How many attempts in a row may make no progress before the loop-back stops: two.</summary>
    public const int NoProgressLimit = 2;

    /// <summary>What a goal round is told after validation sends it back.</summary>
    public static string LoopBackMessage(string failure, int attempt) =>
        $"(Automatic — not from the user.) Validation of your work found a problem: {failure}. This is attempt {attempt}. " +
        "Fix it — run what proves it works — and only then declare the goal complete, with a closing summary of what you did and how you checked it.";

    /// <summary>The transcript row of a loop-back (one of the harness's own rows, never a prompt of the person's).</summary>
    public static string LoopBackDisplay(int attempt) => $"↻ validation: fix and check again (attempt {attempt})";
}

/// <summary>Formats shared by the cards: "runtime 2m 5s · est. about 4 min".</summary>
public static class PhaseText
{
    public static string RuntimeLine(TurnPhaseState state, DateTimeOffset now)
    {
        var text = "runtime " + state.Runtime(now).FormattedDuration();
        if (state.EtaSeconds is { } eta) text += " · est. " + TurnEstimator.Phrase(eta);
        return text;
    }

    /// <summary>"2m over the estimate", "on time", "40s under the estimate" — null without both numbers.</summary>
    public static string? VersusEstimate(double? durationSeconds, double? etaSeconds)
    {
        if (durationSeconds is not { } duration || etaSeconds is not { } eta || eta <= 0) return null;
        var delta = duration - eta;
        if (Math.Abs(delta) <= Math.Max(10, eta * 0.15)) return "on time";
        return delta > 0 ? $"{delta.FormattedDuration()} over the estimate" : $"{(-delta).FormattedDuration()} under the estimate";
    }

    public static string Stamp(DateTimeOffset at) => at.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
}
