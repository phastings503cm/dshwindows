using System.Globalization;

namespace Vision.Core;

// MARK: - Where one subagent task runs
//
// One pure decision, called by the fleet under its lock (and driven by the shared fixture placement-cases.json, so Vision
// for macOS decides the same way):
//
//   1. the servers the task may use at all — a sensitive task only private ones (the chat's own model only when it is
//      private and may take subagents, or was picked), an unattended one none that is for interactive use only;
//   2. the server the primary picked (`server`): it runs there when it has room; when it is busy it is waited for until the
//      pin's deadline (2 minutes); when it can't take the task — or the deadline passed — another server runs it, and the
//      note says why;
//   3. the server an agent file asks for, as a preference;
//   4. the least busy subagent server, then the chat's own model as overflow — else wait.

/// <summary>Why a task runs where it does (the codes both apps and their logs share).</summary>
public enum PlacementReason { Picked, AgentFile, LeastBusy, Fallback }

public static class SubagentPlacement
{
    /// <summary>How long a busy server the primary picked is waited for before another one runs the task.</summary>
    public static readonly TimeSpan PinWait = TimeSpan.FromSeconds(120);

    /// <summary>"picked", "agent-file", "least-busy", "fallback".</summary>
    public static string Code(this PlacementReason reason) => reason switch
    {
        PlacementReason.Picked => "picked",
        PlacementReason.AgentFile => "agent-file",
        PlacementReason.LeastBusy => "least-busy",
        _ => "fallback",
    };

    /// <summary>What the activity menu and the plan say.</summary>
    public static string Label(this PlacementReason reason) => reason switch
    {
        PlacementReason.Picked => "picked by the primary",
        PlacementReason.AgentFile => "set by its agent type",
        PlacementReason.LeastBusy => "least busy",
        _ => "fallback",
    };

    /// <summary>The reason a stored code names (null for none, or one this build doesn't know).</summary>
    public static PlacementReason? ReasonOf(string? code) =>
        Enum.GetValues<PlacementReason>().Cast<PlacementReason?>().FirstOrDefault(r => r!.Value.Code() == code);

    /// <summary>The label of a stored code (null for none, or one this build doesn't know).</summary>
    public static string? LabelOf(string? code) => ReasonOf(code)?.Label();

    /// <summary>"Running · picked by the primary", "Running · fallback — spark-2 stayed busy for 2 minutes", "Running · its
    /// agent file asks …" (an older run without a reason) — the activity menu's status line for a working subagent.</summary>
    public static string StatusLine(string? reasonCode, string? note) =>
        LabelOf(reasonCode) is { } label ? "Running · " + label + (note is { Length: > 0 } ? " — " + note : "")
        : note is { Length: > 0 } ? "Running · " + note
        : "Running";

    /// <summary>One server as the decision sees it.</summary>
    public sealed record Candidate
    {
        public required string Id { get; init; }
        /// <summary>What the model calls it ("dgx-spark", "primary").</summary>
        public string Handle { get; init; } = "";
        public string Label { get; init; } = "";
        /// <summary>The asking chat's own model.</summary>
        public bool IsPrimary { get; init; }
        public bool IsPrivate { get; init; } = true;
        public bool InteractiveOnly { get; init; }
        public int InFlight { get; init; }
        public int Parallel { get; init; } = 2;
        /// <summary>When a server that just failed is trusted again (null: it is healthy).</summary>
        public DateTimeOffset? HealthyAt { get; init; }
        /// <summary>Its last failure, for the note.</summary>
        public string? Error { get; init; }
    }

    /// <summary>One task asking for a server.</summary>
    public sealed record Request
    {
        /// <summary>The candidate id the primary picked (already matched from its <c>server</c> argument).</summary>
        public string? Pinned { get; init; }
        /// <summary>An agent file's soft preference (matched against names, ids and handles).</summary>
        public string? AgentFile { get; init; }
        public bool Sensitive { get; init; }
        public bool Unattended { get; init; }
        /// <summary>Until when a busy pinned server is waited for.</summary>
        public DateTimeOffset? PinDeadline { get; init; }
        /// <summary>Servers that couldn't be reached for this task.</summary>
        public IReadOnlySet<string> Tried { get; init; } = new HashSet<string>();
        /// <summary>The chat's own model takes subagents when the others are busy.</summary>
        public bool Overflow { get; init; } = true;
        /// <summary>The last server's error, for "No model server answered: …".</summary>
        public string? LastError { get; init; }
        /// <summary>How long the pin is waited for — only for the note's words.</summary>
        public TimeSpan PinWait { get; init; } = SubagentPlacement.PinWait;
    }

    /// <summary>What to do.</summary>
    public abstract record Decision
    {
        /// <summary>Run it on this server.</summary>
        public sealed record Run(string Id, PlacementReason Reason, string? Note) : Decision;
        /// <summary>Wait for a slot (a release wakes the fleet), at the latest until <paramref name="Until"/>.</summary>
        public sealed record Wait(DateTimeOffset? Until) : Decision;
        /// <summary>Nothing can take it: tell the main agent.</summary>
        public sealed record Unavailable(string Message) : Decision;
    }

    /// <summary>When nothing that may take unattended work is left (the same words on the Mac).</summary>
    public const string UnattendedOnly = NoUnattendedServerException.Text;
    /// <summary>When a sensitive task finds no private server (unchanged, the same on the Mac).</summary>
    public const string NoPrivateServer = NoPrivateServerException.Text;
    /// <summary>When there are no servers at all.</summary>
    public const string NoServer = "No model server is configured.";

    /// <summary>Whether a server is the one a preference names: its name, route id or handle contains it (any case).</summary>
    public static bool Matches(Candidate candidate, string wanted)
    {
        var value = wanted.Trim();
        return value.Length > 0 && new[] { candidate.Label, candidate.Id, candidate.Handle }.Any(s => s.Contains(value, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The agent file's notes.</summary>
    public static string AgentFileBusy(string wanted) => $"its agent file asks for “{wanted}”, which was busy or not answering";

    public static string AgentFileMissing(string wanted) =>
        $"its agent file asks for “{wanted}”, but no model server by that name is set up (Settings ▸ AI Models: turn on “Use for subagents” for that route)";

    /// <summary>"2 minutes", "30 seconds".</summary>
    public static string WaitWords(TimeSpan wait)
    {
        var s = (int)Math.Round(wait.TotalSeconds);
        if (s >= 60 && s % 60 == 0) return s == 60 ? "1 minute" : (s / 60).ToString(CultureInfo.InvariantCulture) + " minutes";
        return s == 1 ? "1 second" : s.ToString(CultureInfo.InvariantCulture) + " seconds";
    }

    /// <summary>The note when a task moves after its server failed mid-way.</summary>
    public static string MovedNote(string handle, string error) => $"{handle} failed ({Clip(error)}) — moved here";

    private static string Clip(string text) => text.Length <= 140 ? text : text[..140];

    public static Decision Choose(Request request, IReadOnlyList<Candidate> candidates, DateTimeOffset now)
    {
        if (candidates.Count == 0) return new Decision.Unavailable(NoServer);
        var workersExist = candidates.Any(c => !c.IsPrimary);
        bool Eligible(Candidate c)
        {
            if (request.Unattended && c.InteractiveOnly) return false;
            if (!request.Sensitive) return true;
            if (!c.IsPrivate) return false;
            return !c.IsPrimary || request.Overflow || !workersExist || request.Pinned == c.Id;
        }
        var allowed = candidates.Where(Eligible).ToList();
        if (allowed.Count == 0) return new Decision.Unavailable(request.Sensitive ? NoPrivateServer : UnattendedOnly);
        var open = allowed.Where(c => !request.Tried.Contains(c.Id)).ToList();
        if (open.Count == 0)
        {
            if (request.Sensitive) return new Decision.Unavailable(NoPrivateServer);
            return new Decision.Unavailable("No model server answered" + (request.LastError is { } last ? ": " + last : "."));
        }
        bool Healthy(Candidate c) => c.HealthyAt is not { } at || at <= now;
        bool HasRoom(Candidate c) => c.InFlight < Math.Max(1, c.Parallel);
        var order = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < candidates.Count; i++) order.TryAdd(candidates[i].Id, i);
        Candidate? LeastBusy(IEnumerable<Candidate> list)
        {
            Candidate? best = null;
            var bestLoad = 0.0;
            var bestIndex = 0;
            foreach (var c in list.Where(HasRoom))
            {
                var load = c.InFlight / (double)Math.Max(1, c.Parallel);
                var index = order.GetValueOrDefault(c.Id, int.MaxValue);
                if (best is not null && (bestLoad < load || (bestLoad == load && bestIndex <= index))) continue;
                (best, bestLoad, bestIndex) = (c, load, index);
            }
            return best;
        }
        // A blacklisted server comes back by itself when its time is up, with nothing else to wake the waiter.
        DateTimeOffset? recovery = open.Select(c => c.HealthyAt).Where(at => at is { } t && t > now).Min();

        PlacementReason? forced = null;
        string? note = null;

        // 2. The server the primary picked.
        if (request.Pinned is { } pinned && candidates.FirstOrDefault(c => c.Id == pinned) is { } pin)
        {
            if (Eligible(pin) && Healthy(pin) && !request.Tried.Contains(pin.Id))
            {
                if (HasRoom(pin)) return new Decision.Run(pin.Id, PlacementReason.Picked, null);
                if (request.PinDeadline is { } deadline && now < deadline)
                    return new Decision.Wait(recovery is { } back && back < deadline ? back : deadline);
                note = $"{pin.Handle} stayed busy for {WaitWords(request.PinWait)}";
            }
            else if (request.Sensitive && !pin.IsPrivate)
            {
                note = $"{pin.Handle} isn't private and this task is sensitive";
            }
            else if (request.Unattended && pin.InteractiveOnly)
            {
                note = $"{pin.Handle} is for interactive use only and this run is unattended";
            }
            else
            {
                note = $"{pin.Handle} isn't answering" + (pin.Error is { } error ? $" ({Clip(error)})" : "");
            }
            forced = PlacementReason.Fallback;
        }

        // Skip servers that just failed — unless that would leave none.
        var fit = open.Where(Healthy).ToList();
        var usable = fit.Count == 0 ? open : fit;
        Decision Finish(Candidate chosen, PlacementReason reason)
        {
            if (chosen.Id == request.Pinned) return new Decision.Run(chosen.Id, PlacementReason.Picked, null);
            if (forced is { } f) return new Decision.Run(chosen.Id, f, note);
            return new Decision.Run(chosen.Id, reason, reason == PlacementReason.Fallback ? note : null);
        }

        // 3. The agent file's preference.
        var fallbackReason = PlacementReason.LeastBusy;
        if (request.AgentFile?.Trim() is { Length: > 0 } wanted)
        {
            if (LeastBusy(usable.Where(c => Matches(c, wanted))) is { } favourite) return Finish(favourite, PlacementReason.AgentFile);
            if (forced is null)
            {
                note = candidates.Any(c => Matches(c, wanted)) ? AgentFileBusy(wanted) : AgentFileMissing(wanted);
                fallbackReason = PlacementReason.Fallback;
            }
        }

        // 4. The least busy subagent server, then the chat's own model as overflow.
        var workers = usable.Where(c => !c.IsPrimary).ToList();
        if (LeastBusy(workers) is { } worker) return Finish(worker, fallbackReason);
        if ((request.Sensitive || workers.Count == 0 || request.Overflow) && LeastBusy(usable.Where(c => c.IsPrimary)) is { } primary)
            return Finish(primary, fallbackReason);
        return new Decision.Wait(recovery);
    }
}
