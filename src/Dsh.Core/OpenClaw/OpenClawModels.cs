namespace Dsh.Core;

/// <summary>A secret held in memory. Printing it, logging it or putting it in a record's auto-generated
/// <c>ToString</c> shows only a fingerprint; the value comes out through <see cref="Reveal"/> and nowhere
/// else.</summary>
public sealed class SecretText
{
    private readonly string _value;

    public SecretText(string value) => _value = value;

    public int Length => _value.Length;

    /// <summary>The secret itself. Call it only where it is being stored.</summary>
    public string Reveal() => _value;

    /// <summary>"sha256:1a2b3c4d5e6f" — enough to tell two values apart, not enough to recover one.</summary>
    public string Fingerprint => CredentialVault.Fingerprint(_value);

    /// <summary>A hint for the import sheet: the first characters of a long key, never the rest.</summary>
    public string Hint
    {
        get
        {
            if (_value.Length < 16) return new string('•', Math.Min(_value.Length, 12));
            return _value[..3] + new string('•', 8) + $" ({_value.Length} characters)";
        }
    }

    public override string ToString() => $"[secret {Fingerprint}]";
}

/// <summary>A skill found in an OpenClaw workspace or managed-skills folder.</summary>
public sealed record OpenClawSkill
{
    public required string Name { get; init; }
    public string Description { get; init; } = "";
    /// <summary>Its folder on the machine it was found on.</summary>
    public required string Directory { get; init; }
    /// <summary>"workspace", "managed" (~/.openclaw/skills) or "extra" (configured extra folder).</summary>
    public string Location { get; init; } = "workspace";
    /// <summary>Paths inside the folder, relative to it, "/"-separated.</summary>
    public IReadOnlyList<string> Files { get; init; } = [];
    public bool HasScripts { get; init; }
    /// <summary>Another skill of the same name in a higher-priority place hides this one.</summary>
    public bool Shadowed { get; init; }
}

public enum OpenClawNoteKind
{
    /// <summary>MEMORY.md — the curated long-term memory.</summary>
    Memory,
    /// <summary>memory/YYYY-MM-DD.md — the daily log.</summary>
    Daily,
    /// <summary>USER.md — what the agent knows about its user.</summary>
    About,
    /// <summary>SOUL.md / IDENTITY.md — the agent's personality.</summary>
    Persona,
    /// <summary>AGENTS.md / TOOLS.md — operating instructions written for OpenClaw's own tools.</summary>
    Instructions,
}

/// <summary>One markdown file of memory, already cut into notes.</summary>
public sealed record OpenClawNoteFile
{
    public required string Path { get; init; }
    public required string Name { get; init; }
    public OpenClawNoteKind Kind { get; init; }
    public IReadOnlyList<MemoryDraft> Notes { get; init; } = [];
    /// <summary>Ticked when the sheet opens: memory, recent daily logs and USER.md are; personality and
    /// operating instructions are opt-in.</summary>
    public bool SelectedByDefault { get; init; }
}

/// <summary>A key, token or password from an OpenClaw install.</summary>
public sealed record OpenClawCredential
{
    /// <summary>A vault-legal name (letters, digits, _ - .).</summary>
    public required string Name { get; init; }
    public required SecretText Secret { get; init; }
    public VaultKind Kind { get; init; } = VaultKind.ApiKey;
    public string Description { get; init; } = "";
    /// <summary>Where it was found: "auth-profiles.json (anthropic:default)", "openclaw.json channels.telegram.botToken".</summary>
    public string Origin { get; init; } = "";
    public string? Provider { get; init; }
    public string? Url { get; init; }
    /// <summary>Other names the same value had (it is stored once).</summary>
    public IReadOnlyList<string> AlsoNamed { get; init; } = [];
}

/// <summary>A model server OpenClaw is configured to use.</summary>
public sealed record OpenClawModelServer
{
    public required string Id { get; init; }
    public required string BaseUrl { get; init; }
    public string? Api { get; init; }
    public IReadOnlyList<string> Models { get; init; } = [];
    public int? ContextWindow { get; init; }
    public SecretText? ApiKey { get; init; }
    /// <summary>DSH can talk to it (an OpenAI-style chat completions endpoint).</summary>
    public bool Compatible { get; init; } = true;
    public string? Note { get; init; }
    /// <summary>The model OpenClaw's agents use by default, when it belongs to this server.</summary>
    public string? DefaultModel { get; init; }
}

/// <summary>Everything read from one OpenClaw install, ready to be shown and then imported.</summary>
public sealed record OpenClawBundle
{
    public required OpenClawInstall Install { get; init; }
    /// <summary>"This PC" or "sam@spark-3".</summary>
    public string SourceLabel { get; init; } = "";
    public IReadOnlyList<string> Workspaces { get; init; } = [];
    public IReadOnlyList<OpenClawSkill> Skills { get; init; } = [];
    public IReadOnlyList<OpenClawNoteFile> NoteFiles { get; init; } = [];
    public IReadOnlyList<OpenClawCredential> Credentials { get; init; } = [];
    public IReadOnlyList<OpenClawModelServer> ModelServers { get; init; } = [];
    /// <summary>Found but not something DSH imports (channels, cron jobs, sessions…), said plainly.</summary>
    public IReadOnlyList<string> Left { get; init; } = [];
    /// <summary>Things that couldn't be read.</summary>
    public IReadOnlyList<string> Problems { get; init; } = [];

    public int NoteCount => NoteFiles.Sum(f => f.Notes.Count);
    public bool IsEmpty => Skills.Count == 0 && NoteFiles.Count == 0 && Credentials.Count == 0 && ModelServers.Count == 0;
}
