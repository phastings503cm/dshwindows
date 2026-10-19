using System.Text.Json.Serialization;

namespace Vision.Core;

// MARK: - Checkpoints: what a turn changed
//
// Before the agent changes a file in a turn, the file as it was goes into a content-addressed store (see
// CheckpointStore), and the turn's manifest records each file's state before and after the turn. "Rewind this turn"
// puts those files back the way they were before it — after checking that nothing changed them since. The same
// manifest, word for word, is what Vision for macOS writes (checkpoints/turns/<chat>/<turn>.json).

/// <summary>How a turn changed one file.</summary>
public enum CheckpointChange { Created, Modified, Deleted }

/// <summary>One file a turn changed: its content before the turn and after it, by SHA-256 (the blobs are in the store).</summary>
public sealed class CheckpointFile
{
    /// <summary>The file's full path.</summary>
    public string Path { get; set; } = "";
    /// <summary>SHA-256 (lowercase hex) of the file before the turn; null = it didn't exist.</summary>
    public string? Before { get; set; }
    /// <summary>SHA-256 of the file when the turn ended; null = it didn't exist then (deleted).</summary>
    public string? After { get; set; }
    /// <summary>Whether <see cref="After"/> was taken when the turn ended. False for a change a background agent made after
    /// the turn was over: what it left can't be told apart from a later edit, so rewinding it asks first.</summary>
    public bool AfterKnown { get; set; }
    public long BeforeBytes { get; set; }
    public long AfterBytes { get; set; }
    /// <summary>The file was larger than the store keeps (<see cref="CheckpointStore.MaxFileBytes"/>): it can't be rewound.</summary>
    public bool TooLarge { get; set; }
    /// <summary>What changed it first: the tool's name ("edit", "write_file", "run_shell_command" — a shell command's change
    /// found through git).</summary>
    public string Source { get; set; } = "";
    /// <summary>Lines added and removed (text files; 0 for a binary one).</summary>
    public int Added { get; set; }
    public int Removed { get; set; }
    /// <summary>Not text: no line diff.</summary>
    public bool Binary { get; set; }

    [JsonIgnore]
    public CheckpointChange Change => Before is null ? CheckpointChange.Created
        : AfterKnown && After is null ? CheckpointChange.Deleted
        : CheckpointChange.Modified;

    /// <summary>The file ended the turn exactly as it began it (changed and changed back): nothing to show or rewind.</summary>
    [JsonIgnore] public bool Unchanged => AfterKnown && !TooLarge && Before == After;

    public CheckpointFile Clone() => (CheckpointFile)MemberwiseClone();
}

/// <summary>One turn's checkpoint: the files it changed, and whether it was rewound.</summary>
public sealed class TurnCheckpoint
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    /// <summary>The turn: its prompt's id in the Prompts timeline (a turn the harness started by itself has one of its own).</summary>
    public string Id { get; set; } = "";
    public string ChatId { get; set; } = "";
    /// <summary>The folder the agent worked in.</summary>
    public string? Workspace { get; set; }
    /// <summary>The prompt's first line (up to 200 characters), for /rewind's list.</summary>
    public string Prompt { get; set; } = "";
    public DateTimeOffset At { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? FinishedAt { get; set; }
    public List<CheckpointFile> Files { get; set; } = [];
    /// <summary>When the person rewound this turn (null = not rewound), and how many files went back.</summary>
    public DateTimeOffset? RewoundAt { get; set; }
    public int RewoundCount { get; set; }
    /// <summary>Each rewound file's content just before the rewind (path → SHA-256, null = absent), so a rewind can be undone.</summary>
    public Dictionary<string, string?>? RewoundFrom { get; set; }

    /// <summary>The files that really changed (a file changed and changed back is left out).</summary>
    [JsonIgnore] public IReadOnlyList<CheckpointFile> Changed => Files.Where(f => !f.Unchanged).ToList();

    [JsonIgnore] public bool IsRewound => RewoundAt is not null;

    /// <summary>"Files changed (3)" — the same words on every harness.</summary>
    [JsonIgnore] public string Title => $"Files changed ({Changed.Count})";

    /// <summary>"+12 −3" over every text file.</summary>
    [JsonIgnore] public string LineSummary => $"+{Changed.Sum(f => f.Added)} −{Changed.Sum(f => f.Removed)}";

    public TurnCheckpoint Clone()
    {
        var copy = (TurnCheckpoint)MemberwiseClone();
        copy.Files = Files.Select(f => f.Clone()).ToList();
        copy.RewoundFrom = RewoundFrom is null ? null : new Dictionary<string, string?>(RewoundFrom, StringComparer.Ordinal);
        return copy;
    }
}
