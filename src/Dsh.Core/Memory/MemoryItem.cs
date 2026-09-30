using System.Text.Json.Serialization;

namespace Dsh.Core;

// MARK: - Long-term memory
//
// Durable notes the agent (or the user) keeps between chats: preferences, decisions, conventions,
// how-tos. They are not loaded into every prompt — that is what makes a memory a hog. Each turn the
// message is matched against the store (BM25 over title, tags and body) and only the few notes that are
// actually relevant ride along with it, never the same one twice in a short while. Pinned notes are the
// exception: a handful of standing facts that belong in the system prompt.

/// <summary>What a note is about; it only colours how the note is listed and whether it is global by default.</summary>
public static class MemoryKinds
{
    public const string Preference = "preference";
    public const string Fact = "fact";
    public const string Decision = "decision";
    public const string Procedure = "procedure";
    public const string Reference = "reference";
    public const string Note = "note";

    public static IReadOnlyList<string> All { get; } = [Preference, Fact, Decision, Procedure, Reference, Note];

    /// <summary>The known kind spelled like <paramref name="raw"/>, or "note".</summary>
    public static string Normalize(string? raw)
    {
        var lower = (raw ?? "").Trim().ToLowerInvariant();
        return All.Contains(lower) ? lower : Note;
    }

    /// <summary>Preferences describe the user and follow them everywhere; the rest belong to a project by default.</summary>
    public static bool GlobalByDefault(string kind) => kind == Preference;
}

/// <summary>One stored note.</summary>
public sealed record MemoryItem
{
    /// <summary>Eight hex characters: short enough for the model to quote back in memory_forget.</summary>
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Body { get; init; } = "";
    public IReadOnlyList<string> Tags { get; init; } = [];
    public string Kind { get; init; } = MemoryKinds.Note;
    /// <summary>The project folder (full path) this note is about; null = it applies everywhere.</summary>
    public string? Project { get; init; }
    /// <summary>Always part of the system prompt (a few standing facts), instead of searched for.</summary>
    public bool Pinned { get; init; }
    /// <summary>"agent", "user", or "import:…" (where an imported note came from).</summary>
    public string Source { get; init; } = "agent";
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset? LastUsedAt { get; init; }
    public int UseCount { get; init; }

    [JsonIgnore] public bool IsGlobal => Project is null;

    /// <summary>The text a search matches against.</summary>
    [JsonIgnore] public string SearchText => Title + " " + string.Join(" ", Tags) + " " + Body;

    public bool Equals(MemoryItem? other) =>
        other is not null && Id == other.Id && Title == other.Title && Body == other.Body && Kind == other.Kind
        && Project == other.Project && Pinned == other.Pinned && Source == other.Source
        && Tags.SequenceEqual(other.Tags) && CreatedAt == other.CreatedAt && UpdatedAt == other.UpdatedAt
        && LastUsedAt == other.LastUsedAt && UseCount == other.UseCount;

    public override int GetHashCode() => HashCode.Combine(Id, Title, Body, Kind, Project, Pinned);
}

/// <summary>A note that matched a query, with how well.</summary>
public sealed record MemoryHit(MemoryItem Item, double Score, double Relevance);
