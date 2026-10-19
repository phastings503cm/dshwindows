using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vision.Core;

// MARK: - Checkpoints: what a turn changed
//
// Before the agent changes a file in a turn, the file as it was goes into a content-addressed store (see
// CheckpointStore), and the turn's record lists each file it changed with its content before and after, by hash. The
// turn's "Files changed (N)" row shows them with their diffs, and "Rewind this turn…" puts them back — after checking
// that nothing changed them since. The record is the one Vision for macOS writes (checkpoints/turns/<chat>/<turn>.json:
// version, id, chatID, title, startedAt, updatedAt, files[path, before, after, by, skipped], rewoundAt), with a few
// fields of Windows' own that the Mac ignores (added, removed, binary, late, workspace).

/// How a turn changed one file.
public enum CheckpointChange { Created, Modified, Deleted }

/// One file a turn changed.
public sealed class CheckpointFile
{
    /// The file's full path.
    public string Path { get; set; } = "";
    /// SHA-256 (lowercase hex) of its content before the turn first changed it; null = it didn't exist.
    public string? Before { get; set; }
    /// SHA-256 of what the turn left; null = the turn deleted it.
    public string? After { get; set; }
    /// What changed it: the tool ("edit", "write_file", "run_shell_command" — a shell command's change found through git — an
    /// MCP tool, "rewind").
    public string By { get; set; } = "";
    /// Why it can't be rewound (its content wasn't kept: too large, unreadable); null = it can.
    public string? Skipped { get; set; }
    /// Lines added and removed (text files; 0 for a binary one). Windows only.
    public int Added { get; set; }
    public int Removed { get; set; }
    /// Not text: no line diff. Windows only.
    public bool Binary { get; set; }
    /// Changed after its turn had ended (a background agent of that turn): what it left can't be told apart from a later
    /// edit, so rewinding it asks first. Windows only.
    public bool? Late { get; set; }

    [JsonIgnore] public bool IsLate => Late == true;

    [JsonIgnore]
    public CheckpointChange Change => Before is null ? CheckpointChange.Created
        : After is null && !IsLate ? CheckpointChange.Deleted
        : CheckpointChange.Modified;

    /// The file ended the turn holding what it began it with (changed and put back): no change.
    [JsonIgnore] public bool Unchanged => !IsLate && Before == After;

    public CheckpointFile Clone() => (CheckpointFile)MemberwiseClone();
}

/// Everything one turn changed.
public sealed class TurnCheckpoint
{
    public int Version { get; set; } = 1;
    /// The turn: its prompt's id in the Prompts timeline (every round of a /goal is one turn); "auto-…" for a turn the harness
    /// started by itself, "rewind-…" for a rewind (which is recorded as a turn of its own, so it can be rewound in turn).
    public string Id { get; set; } = "";
    [JsonPropertyName("chatID")] public string ChatId { get; set; } = "";
    /// What was asked, short: the prompt's first line, up to 80 characters ("Rewind of “…”" for a rewind).
    public string Title { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
    public List<CheckpointFile> Files { get; set; } = [];
    /// When it was rewound (null = not).
    public DateTimeOffset? RewoundAt { get; set; }
    /// The folder the agent worked in (paths are shown relative to it). Windows only.
    public string? Workspace { get; set; }

    /// The files that changed (a file edited and then put back is no change).
    [JsonIgnore] public IReadOnlyList<CheckpointFile> Changed => Files.Where(f => !f.Unchanged).ToList();

    [JsonIgnore] public bool IsRewound => RewoundAt is not null;

    /// "Files changed (3)" — the same words on every harness.
    [JsonIgnore] public string Heading => $"Files changed ({Changed.Count})";

    /// "+12 −3" over every text file.
    [JsonIgnore] public string LineSummary => $"+{Changed.Sum(f => f.Added)} −{Changed.Sum(f => f.Removed)}";

    /// The first line of <paramref name="prompt"/>, up to 80 characters — a turn's title.
    public static string TitleOf(string? prompt)
    {
        var line = (prompt ?? "").Replace("\r", "").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
        return line.Length == 0 ? "Carrying on" : line.Length > 80 ? line[..80] : line;
    }

    public TurnCheckpoint Clone()
    {
        var copy = (TurnCheckpoint)MemberwiseClone();
        copy.Files = Files.Select(f => f.Clone()).ToList();
        return copy;
    }
}

/// Dates as Vision for macOS writes and reads them (Swift's ISO 8601: UTC, whole seconds, "Z").
public sealed class Iso8601SecondsConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        DateTimeOffset.Parse(reader.GetString() ?? "", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
}
