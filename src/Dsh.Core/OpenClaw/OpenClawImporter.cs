using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Dsh.Core;

// MARK: - Bringing an OpenClaw install into DSH
//
// The user picks what to bring; this applies exactly that.
//   skills       → copied (files fetched from the machine they live on) into DSH's skills, through the same
//                  importer the Settings › Skills sheet uses — its validation, naming and conflict rules
//   memory       → notes in the memory store (re-importing updates them, never duplicates)
//   credentials  → the Credentials Vault, "ask first" unless the user chose otherwise; values are moved,
//                  never logged, never shown, never written anywhere else
//   model servers→ handed back to the app, which adds them as routes (Core doesn't own settings)

/// <summary>What the user ticked.</summary>
public sealed class OpenClawSelection
{
    /// <summary>Skills to bring, by <see cref="OpenClawSkill.Directory"/>.</summary>
    public HashSet<string> Skills { get; init; } = new(StringComparer.Ordinal);
    /// <summary>Memory files to bring, by <see cref="OpenClawNoteFile.Path"/>.</summary>
    public HashSet<string> NoteFiles { get; init; } = new(StringComparer.Ordinal);
    /// <summary>Credentials to bring, by name, with what the agent may do with each.</summary>
    public Dictionary<string, VaultAccess> Credentials { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Model servers to bring, by id.</summary>
    public HashSet<string> ModelServers { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Hold the skills for approval in Settings › Skills instead of switching them on.</summary>
    public bool SkillsAsDrafts { get; init; }

    /// <summary>Everything the bundle offers that is ticked by default: memory, recent daily logs and
    /// USER.md; skills without scripts; credentials as "ask first"; compatible model servers.</summary>
    public static OpenClawSelection Suggested(OpenClawBundle bundle) => new()
    {
        Skills = bundle.Skills.Where(s => !s.Shadowed).Select(s => s.Directory).ToHashSet(StringComparer.Ordinal),
        NoteFiles = bundle.NoteFiles.Where(f => f.SelectedByDefault).Select(f => f.Path).ToHashSet(StringComparer.Ordinal),
        Credentials = bundle.Credentials.ToDictionary(c => c.Name, _ => VaultAccess.Ask, StringComparer.OrdinalIgnoreCase),
        ModelServers = bundle.ModelServers.Where(s => s.Compatible).Select(s => s.Id).ToHashSet(StringComparer.OrdinalIgnoreCase),
    };
}

/// <summary>Where the pieces go. A null target skips that kind.</summary>
public sealed record OpenClawTargets(MemoryStore? Memory, CredentialVault? Vault, SkillLocations? Skills);

public sealed record OpenClawImportResult
{
    public IReadOnlyList<string> Skills { get; init; } = [];
    public IReadOnlyList<string> SkillDrafts { get; init; } = [];
    /// <summary>Skill name → why it wasn't brought.</summary>
    public IReadOnlyDictionary<string, string> SkillsSkipped { get; init; } = new Dictionary<string, string>();
    public int Notes { get; init; }
    public int NotesUpdated { get; init; }
    /// <summary>The vault names created (never the values).</summary>
    public IReadOnlyList<string> Credentials { get; init; } = [];
    public IReadOnlyDictionary<string, string> CredentialsSkipped { get; init; } = new Dictionary<string, string>();
    /// <summary>The model servers to add as routes — the app does that.</summary>
    public IReadOnlyList<OpenClawModelServer> ModelServers { get; init; } = [];
    public IReadOnlyList<string> Problems { get; init; } = [];

    public bool BroughtAnything => Skills.Count + SkillDrafts.Count + Notes + NotesUpdated + Credentials.Count + ModelServers.Count > 0;

    /// <summary>"3 skills, 41 notes, 6 keys and 1 model server" — for the summary line.</summary>
    public string Summary
    {
        get
        {
            static string N(int n, string one, string many) => n == 1 ? $"1 {one}" : $"{n} {many}";
            var parts = new List<string>();
            if (Skills.Count + SkillDrafts.Count > 0) parts.Add(N(Skills.Count + SkillDrafts.Count, "skill", "skills"));
            if (Notes + NotesUpdated > 0) parts.Add(N(Notes + NotesUpdated, "memory note", "memory notes"));
            if (Credentials.Count > 0) parts.Add(N(Credentials.Count, "key or password", "keys and passwords"));
            if (ModelServers.Count > 0) parts.Add(N(ModelServers.Count, "model server", "model servers"));
            return parts.Count switch
            {
                0 => "nothing",
                1 => parts[0],
                _ => string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1],
            };
        }
    }
}

public static class OpenClawImporter
{
    private const int MaxFilesPerSkill = 300;
    private const long MaxBytesPerSkill = 5_000_000;
    private const long MaxBytesPerFile = 1_500_000;

    public static async Task<OpenClawImportResult> ApplyAsync(IFileSource source, OpenClawBundle bundle, OpenClawSelection selection,
                                                               OpenClawTargets targets, IProgress<string>? progress = null,
                                                               CancellationToken cancellationToken = default)
    {
        var problems = new List<string>();

        // Memory.
        var added = 0;
        var updated = 0;
        if (targets.Memory is { } memory)
        {
            var drafts = bundle.NoteFiles.Where(f => selection.NoteFiles.Contains(f.Path)).SelectMany(f => f.Notes).ToList();
            if (drafts.Count > 0)
            {
                progress?.Report($"Saving {drafts.Count} memory notes…");
                try
                {
                    var before = memory.Count;
                    added = memory.Import(drafts, out var withSecrets);
                    updated = Math.Max(0, drafts.Count - added - withSecrets);
                    if (withSecrets > 0)
                        problems.Add($"{withSecrets} memory note{(withSecrets == 1 ? " was" : "s were")} left out because {(withSecrets == 1 ? "it looks" : "they look")} like a key or a password. Those belong in the vault.");
                    if (memory.Count < before + added) problems.Add("Some memory notes were dropped because the memory store is full.");
                    if (memory.StorageProblem is { } storage) problems.Add(storage);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    // The other kinds are still brought in, and the report says what was not.
                    problems.Add($"The memory notes couldn't be saved: {error.Message}");
                }
            }
        }

        // Credentials.
        var created = new List<string>();
        var skippedCredentials = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (targets.Vault is { } vault)
        {
            foreach (var credential in bundle.Credentials.Where(c => selection.Credentials.ContainsKey(c.Name)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report($"Adding {credential.Name} to the vault…");
                try
                {
                    var name = AddToVault(vault, credential, selection.Credentials[credential.Name], out var reason);
                    if (name is null) skippedCredentials[credential.Name] = reason!;
                    else created.Add(name);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    // (A vault that can't encrypt, a name it refuses...: that credential's problem, not the import's.)
                    skippedCredentials[credential.Name] = error.Message;
                }
            }
        }

        // Skills.
        var imported = new List<string>();
        var drafted = new List<string>();
        var skippedSkills = new Dictionary<string, string>(StringComparer.Ordinal);
        if (targets.Skills is { } locations)
        {
            var chosen = bundle.Skills.Where(s => selection.Skills.Contains(s.Directory)).ToList();
            if (chosen.Count > 0)
            {
                try
                {
                    var (ok, skipped) = await ImportSkillsAsync(source, chosen, locations, selection.SkillsAsDrafts, progress, problems, cancellationToken)
                        .ConfigureAwait(false);
                    if (selection.SkillsAsDrafts) drafted.AddRange(ok);
                    else imported.AddRange(ok);
                    foreach (var (name, why) in skipped) skippedSkills[name] = why;
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    // What was already brought in stays brought in; the report says what was not.
                    problems.Add($"Skills stopped part-way: {error.Message}");
                }
            }
        }

        return new OpenClawImportResult
        {
            Skills = imported,
            SkillDrafts = drafted,
            SkillsSkipped = skippedSkills,
            Notes = added,
            NotesUpdated = updated,
            Credentials = created,
            CredentialsSkipped = skippedCredentials,
            ModelServers = bundle.ModelServers.Where(s => selection.ModelServers.Contains(s.Id)).ToList(),
            Problems = problems,
        };
    }

    // MARK: - Vault

    /// <summary>Add one credential; the name it landed under, or null with the reason it didn't.</summary>
    private static string? AddToVault(CredentialVault vault, OpenClawCredential credential, VaultAccess access, out string? reason)
    {
        reason = null;
        var value = credential.Secret.Reveal();
        var fingerprint = CredentialVault.Fingerprint(value);
        var tags = new List<string> { "openclaw" };
        if (credential.Provider is { Length: > 0 } provider) tags.Add(provider);
        var description = credential.Description + (credential.AlsoNamed.Count > 0 ? $" Also known as {string.Join(", ", credential.AlsoNamed)}." : "");
        var name = credential.Name;
        for (var attempt = 1; attempt <= 6; attempt++)
        {
            var existing = vault.Entry(name);
            if (existing is not null)
            {
                // Same value already stored under that name: nothing to do. Different value: a different name.
                if (existing.Fingerprint == fingerprint)
                {
                    reason = $"{name} is already in the vault with this value.";
                    return null;
                }
                name = CredentialName(credential.Name, attempt + 1);
                continue;
            }
            vault.Add(name, value, credential.Kind, description, tags, url: credential.Url, access: access);
            return name;
        }
        reason = "Its name was taken six times over.";
        return null;
    }

    private static string CredentialName(string baseName, int n)
    {
        var suffix = n == 2 ? "_OPENCLAW" : $"_OPENCLAW_{n}";
        var head = baseName.Length + suffix.Length > CredentialVault.MaxNameLength ? baseName[..(CredentialVault.MaxNameLength - suffix.Length)] : baseName;
        return head + suffix;
    }

    // MARK: - Skills

    /// <summary>Failures of reaching another machine or writing a file: the kind that end one step, not the whole import.</summary>
    private static bool Recoverable(Exception error) =>
        error is IOException or UnauthorizedAccessException or TimeoutException or SocketException or ObjectDisposedException or InvalidOperationException;

    private static async Task<(List<string> Imported, Dictionary<string, string> Skipped)> ImportSkillsAsync(
        IFileSource source, List<OpenClawSkill> chosen, SkillLocations locations, bool asDrafts, IProgress<string>? progress,
        List<string> problems, CancellationToken cancellationToken)
    {
        var skipped = new Dictionary<string, string>(StringComparer.Ordinal);
        var ledger = ImportLedger.Load(locations);
        var staging = Path.Combine(Path.GetTempPath(), "dsh-openclaw-" + Guid.NewGuid().ToString("N")[..12]);
        try
        {
            Directory.CreateDirectory(staging);
            // staging folder name → the skill it holds, what it was fetched as (for the ledger), and its fingerprint
            var staged = new Dictionary<string, (OpenClawSkill Skill, string Hash)>(StringComparer.OrdinalIgnoreCase);
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var skill in chosen)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report($"Fetching skill {skill.Name}…");
                var folder = SkillNaming.Slug(skill.Name);
                if (folder.Length == 0) folder = "skill";
                var unique = folder;
                for (var n = 2; !used.Add(unique); n++) unique = $"{folder}-{n}";
                var target = Path.Combine(staging, unique);
                try
                {
                    var (ok, why) = await FetchSkillAsync(source, skill, target, problems, cancellationToken).ConfigureAwait(false);
                    if (!ok)
                    {
                        skipped[skill.Name] = why!;
                        continue;
                    }
                    var hash = FolderHash(target);
                    if (!asDrafts && ledger.IsCurrent(LedgerKey(source, skill), hash))
                    {
                        skipped[skill.Name] = "Already in DSH, unchanged since it was brought in.";
                        continue;
                    }
                    staged[unique] = (skill, hash);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    // One skill that can't be fetched (a file name Windows won't hold, a dropped link) is that skill's problem.
                    skipped[skill.Name] = $"Couldn't fetch it: {error.Message}";
                }
            }
            if (staged.Count == 0) return ([], skipped);

            var plan = SkillImporter.Scan(staging);
            try
            {
                // Which skill is which is decided by the folder it was staged in, not by the name in its file: a skill with no
                // name of its own takes the folder's, and the two need not spell it alike.
                string FolderOf(ImportCandidate c) => Path.GetFileName(Path.GetDirectoryName(c.Id) ?? "");
                var mine = plan.Candidates.Where(c => staged.ContainsKey(FolderOf(c))).ToList();
                var ids = mine.Select(c => c.Id).ToList();
                var result = SkillImporter.Perform(plan, ids, SkillScope.User, null, asDrafts, ConflictPolicy.Rename, locations);
                foreach (var (name, reason) in result.Skipped) skipped[name] = reason;
                var landed = new List<string>();
                foreach (var candidate in mine)
                {
                    var (skill, hash) = staged[FolderOf(candidate)];
                    if (result.Skipped.ContainsKey(candidate.Skill.Name)) continue;
                    landed.Add(skill.Name);
                    if (!asDrafts && LandedFolder(result, candidate.Skill.Name) is { } slug) ledger.Record(LedgerKey(source, skill), hash, slug);
                }
                foreach (var note in plan.Notes) problems.Add(note);
                foreach (var (folder, entry) in staged.Where(e => landed.All(l => !l.Equals(e.Value.Skill.Name, StringComparison.Ordinal)) && !skipped.ContainsKey(e.Value.Skill.Name)))
                    skipped[entry.Skill.Name] = "DSH couldn't read it as a skill.";
                ledger.Save();
                return (landed, skipped);
            }
            finally
            {
                SkillImporter.Dispose(plan);
            }
        }
        finally
        {
            try
            {
                if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A temp folder left behind is harmless.
            }
        }
    }

    /// <summary>The folder name a skill landed under in DSH's skills, from the paths <see cref="SkillImporter.Perform"/> reports.</summary>
    private static string? LandedFolder(ImportResult result, string skillName)
    {
        var slug = SkillNaming.Slug(skillName);
        foreach (var path in result.Imported)
        {
            var folder = Path.GetFileName(Path.GetDirectoryName(path) ?? "");
            if (folder.Equals(slug, StringComparison.OrdinalIgnoreCase) || folder.StartsWith(slug + "-", StringComparison.OrdinalIgnoreCase)) return folder;
        }
        return null;
    }

    private static string LedgerKey(IFileSource source, OpenClawSkill skill) => source.Label + "|" + skill.Directory;

    /// <summary>Files that are credentials whatever they are called inside a skill: never copied.</summary>
    internal static bool IsCredentialFile(string relativePath)
    {
        var name = Path.GetFileName(relativePath.Replace('\\', '/'));
        var lower = name.ToLowerInvariant();
        if (lower is ".env" or ".netrc" or ".npmrc" or ".pypirc" or ".git-credentials" or "credentials" or "id_rsa" or "id_dsa" or "id_ecdsa"
            or "id_ed25519" or "token" or "secrets" or "auth.json" or "service-account.json") return true;
        if (lower.StartsWith(".env.", StringComparison.Ordinal) && lower is not (".env.example" or ".env.sample" or ".env.template")) return true;
        var extension = Path.GetExtension(lower);
        return extension is ".pem" or ".key" or ".p12" or ".pfx" or ".jks" or ".keystore" or ".kdbx" or ".ppk"
               || lower.StartsWith("credentials.", StringComparison.Ordinal) || lower.StartsWith("secrets.", StringComparison.Ordinal);
    }

    /// <summary>Download a skill's files into <paramref name="target"/>, refusing anything that could land outside it — and
    /// anything that is a credential: a skill is instructions and helpers, and a key kept beside them would end up where the
    /// agent can read it.</summary>
    private static async Task<(bool Ok, string? Reason)> FetchSkillAsync(IFileSource source, OpenClawSkill skill, string target,
                                                                         List<string> problems, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(target);
        var total = 0L;
        var count = 0;
        var manifest = false;
        // The manifest first, whatever the listing's order; then the rest, shallow files before deep ones.
        var files = skill.Files.Where(f => SafeRelativePath(f, out _)).Distinct(StringComparer.Ordinal)
            .OrderBy(f => f.Equals("SKILL.md", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(f => f.Count(c => c == '/')).ThenBy(f => f, StringComparer.Ordinal)
            .Take(MaxFilesPerSkill).ToList();
        // The listing names files of their own, not links: a SKILL.md that is a link to some other file on that machine (a
        // dotfile, a config with keys in it) would be read through the link and copied into a skill the model can read.
        if (!files.Any(f => f.Equals("SKILL.md", StringComparison.OrdinalIgnoreCase)))
            return (false, "Its SKILL.md is a link to another file, not a file of its own — DSH doesn't follow links out of a skill. Copy the real file into the skill and try again.");
        // (The scan lists at most this many, so a full list means there may be more.)
        if (skill.Files.Count >= MaxFilesPerSkill)
            problems.Add($"Skill “{skill.Name}”: it has {MaxFilesPerSkill} files or more, and only the first {MaxFilesPerSkill} were brought in.");
        // A few files at a time, so a skill that is too big is found out before all of it has been downloaded.
        const int Chunk = 20;
        for (var start = 0; start < files.Count; start += Chunk)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = files.Skip(start).Take(Chunk).ToList();
            var contents = await source.ReadManyAsync(batch.Select(f => source.Combine(skill.Directory, f)).ToList(), MaxBytesPerFile, cancellationToken)
                .ConfigureAwait(false);
            for (var i = 0; i < batch.Count; i++)
            {
                var isManifest = batch[i].Equals("SKILL.md", StringComparison.OrdinalIgnoreCase);
                if (contents[i] is not { } bytes)
                {
                    if (!isManifest) problems.Add($"Skill “{skill.Name}”: {batch[i]} couldn't be read, or is larger than {MaxBytesPerFile / 1_000_000.0:0.#} MB, so it was left out.");
                    continue;
                }
                if (IsCredentialFile(batch[i]))
                {
                    problems.Add($"Skill “{skill.Name}”: left out {batch[i]} — it looks like it holds credentials. Add them to the vault instead.");
                    continue;
                }
                if (LooksLikeText(bytes) && SecretGuard.LooksLikeSecret(Encoding.UTF8.GetString(bytes)))
                {
                    if (isManifest) return (false, "SKILL.md contains what looks like a key or a password. Take it out (keys belong in the vault) and try again.");
                    problems.Add($"Skill “{skill.Name}”: left out {batch[i]} — it contains what looks like a key or a password.");
                    continue;
                }
                total += bytes.Length;
                if (total > MaxBytesPerSkill) return (false, $"It is larger than {MaxBytesPerSkill / 1_000_000} MB.");
                SafeRelativePath(batch[i], out var segments);
                var destination = Path.Combine([target, .. segments]);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await File.WriteAllBytesAsync(destination, bytes, cancellationToken).ConfigureAwait(false);
                count++;
                if (isManifest) manifest = true;
            }
        }
        if (!manifest) return (false, "Its SKILL.md couldn't be read.");
        return (true, count == 0 ? "empty" : null);
    }

    private static bool LooksLikeText(byte[] bytes) => Array.IndexOf(bytes, (byte)0) < 0;

    /// <summary>A fingerprint of a folder's files (names and bytes), for telling whether a skill has changed at its source.</summary>
    private static string FolderHash(string folder)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(Path.GetRelativePath(folder, file).Replace('\\', '/') + "\0"));
            hash.AppendData(File.ReadAllBytes(file));
        }
        return Convert.ToHexString(hash.GetHashAndReset(), 0, 12);
    }

    /// <summary>What was brought in from where, so bringing the same skill in again — unchanged — does nothing instead of
    /// adding a "-2" copy. Kept beside the skills; a missing or unreadable file just means everything counts as new.</summary>
    private sealed class ImportLedger
    {
        private readonly string _path;
        private readonly string _skills;
        private readonly Dictionary<string, Entry> _entries;

        private sealed record Entry(string Hash, string Slug);

        private ImportLedger(string path, string skills, Dictionary<string, Entry> entries)
        {
            _path = path;
            _skills = skills;
            _entries = entries;
        }

        public static ImportLedger Load(SkillLocations locations)
        {
            var path = Path.Combine(locations.AppSupport, "openclaw-imports.json");
            var entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
            try
            {
                if (File.Exists(path))
                {
                    // (A hand-edited file can hold a null where an entry belongs.)
                    var loaded = JsonSerializer.Deserialize<Dictionary<string, Entry?>>(File.ReadAllText(path)) ?? [];
                    entries = loaded.Where(e => e.Value is { Hash: not null, Slug: not null }).ToDictionary(e => e.Key, e => e.Value!, StringComparer.Ordinal);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // Start over: at worst a skill is brought in twice.
            }
            return new ImportLedger(path, locations.UserSkills, entries);
        }

        /// <summary>Brought in before with exactly these files, and still there.</summary>
        public bool IsCurrent(string key, string hash) =>
            _entries.TryGetValue(key, out var entry) && entry.Hash == hash && File.Exists(Path.Combine(_skills, entry.Slug, "SKILL.md"));

        public void Record(string key, string hash, string slug)
        {
            _entries[key] = new Entry(hash, slug);
            Save(); // at once: what landed is written down even if a later step fails
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.WriteAllText(_path, JsonSerializer.Serialize(_entries), TextUtil.Utf8NoBom);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best effort.
            }
        }
    }

    private static readonly char[] WindowsInvalid = ['<', '>', ':', '"', '|', '?', '*'];

    private static readonly HashSet<string> WindowsReserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>A relative path made only of ordinary file-name segments: no "..", no drive letters, nothing
    /// Windows can't hold. Judged the same way on every OS, because the files come from a Linux server and
    /// land on Windows.</summary>
    internal static bool SafeRelativePath(string relative, out string[] segments)
    {
        segments = relative.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || relative.StartsWith('/') || relative.StartsWith('\\')) return false;
        foreach (var segment in segments)
        {
            if (segment is "." or "..") return false;
            if (segment.IndexOfAny(WindowsInvalid) >= 0 || segment.Any(c => c < ' ')) return false;
            if (segment.EndsWith('.') || segment.EndsWith(' ')) return false;
            var stem = segment.Contains('.') ? segment[..segment.IndexOf('.')] : segment;
            if (WindowsReserved.Contains(stem)) return false;
        }
        return true;
    }
}
