namespace Vision.Core;

/// <summary>Copies the items a user chose from an <see cref="ExternalInventory"/> into DSH's own data
/// folder. Everything written lands under %APPDATA%\DSH (skills, instructions, memory); the other tool's
/// files are only read, and nothing is run. The same limits as every other import apply — links are
/// never followed, sizes are capped — and one item failing never stops the rest.</summary>
public static class ExternalImporter
{
    public static ExternalImportResult Import(ExternalInventory inventory, IEnumerable<string> selectedIds, SkillLocations vision,
                                              bool skillsAsDrafts = false, CancellationToken cancellationToken = default)
    {
        var chosen = selectedIds.ToHashSet(StringComparer.Ordinal);
        var imported = new List<ExternalImported>();
        var drafts = new List<SkillDraft>();
        var skipped = new List<ExternalSkipped>();
        var queue = ExternalScanner.DraftIndex.Read(vision); // what already waits for approval, and what this run adds

        foreach (var item in inventory.Items.Where(i => chosen.Contains(i.Id)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                switch (item.Kind)
                {
                    case ExternalKind.Instructions:
                        Record(ImportInstructions(item, vision));
                        break;
                    case ExternalKind.ProjectNotes:
                        Record(ImportNotes(item, vision));
                        break;
                    default:
                        Record(ImportSkill(item, vision, skillsAsDrafts, drafts, queue));
                        break;
                }
            }
            catch (Exception ex) when (ex is SkillException or IOException or UnauthorizedAccessException or ArgumentException
                                           or NotSupportedException or InvalidOperationException)
            {
                skipped.Add(new ExternalSkipped(item, ex.Message));
            }

            void Record(Outcome outcome)
            {
                if (outcome.SkipReason is { } reason) skipped.Add(new ExternalSkipped(item, reason));
                else if (outcome.Destination is { } destination) imported.Add(new ExternalImported(item, destination, outcome.Renamed, outcome.IsDraft));
            }
        }
        return new ExternalImportResult { Imported = imported, Drafts = drafts, Skipped = skipped };
    }

    /// <summary>What happened to one item: written (or drafted), or left alone with a reason.</summary>
    private readonly record struct Outcome(string? Destination = null, bool Renamed = false, string? SkipReason = null, bool IsDraft = false)
    {
        public static Outcome AlreadyThere { get; } = new(SkipReason: "Already in DSH.");
        public static Outcome AlreadyWaiting { get; } = new(SkipReason: "Already waiting for your approval.");
    }

    // MARK: Skills, commands, rules, subagents

    private static Outcome ImportSkill(ExternalItem item, SkillLocations vision, bool asDraft, List<SkillDraft> drafts,
                                       ExternalScanner.DraftIndex queue)
    {
        var slug = SkillFiles.NonEmpty(item.Target, "imported");
        var fileName = Path.GetFileName(item.SourcePath);
        Func<string, string?> render;
        if (item.Kind == ExternalKind.Subagent)
        {
            var source = SkillFiles.ReadText(item.SourcePath) ?? throw SkillException.Io($"Can't read {fileName}.");
            var converted = ExternalConverters.SubagentSkill(item.Tool, fileName, source)
                            ?? throw SkillException.Invalid($"{fileName} has no instructions.");
            slug = converted.Slug;
            render = name => ExternalConverters.SubagentSkill(item.Tool, fileName, source, name)?.Text;
        }
        else
        {
            var skill = item.Skill ?? throw SkillException.Invalid($"{item.Name} can't be read.");
            render = name => SkillConverter.SkillFileText(skill, name);
        }

        // Decided against the disk as it is now, not as the scan saw it: a skill copied into both tools is
        // one import, and a second run changes nothing.
        switch (ExternalScanner.SkillStatus(vision, slug, render, queue))
        {
            case ExternalStatus.Imported: return Outcome.AlreadyThere;
            case ExternalStatus.Waiting: return Outcome.AlreadyWaiting;
        }

        // A copy kept beside a different one of the same name is numbered (review-2) — in its folder AND in
        // its own name, or the original would shadow it and it would never switch on. (A draft keeps the
        // plain name: approving it asks about a clash then.)
        var finalName = asDraft ? slug : SkillNaming.Unique(slug, n => Path.Exists(Path.Combine(vision.UserSkills, n)));
        var staged = SkillFiles.StagingDirectory();
        try
        {
            if (item.Kind == ExternalKind.Subagent)
            {
                Directory.CreateDirectory(staged);
                SkillFiles.WriteText(Path.Combine(staged, "SKILL.md"), render(finalName) ?? throw SkillException.Invalid($"{fileName} has no instructions."));
            }
            else
            {
                SkillConverter.WriteSkillFolder(item.Skill!, staged, finalName);
            }
            if (asDraft)
            {
                var draft = SkillDrafts.Stage(staged, SkillScope.User, null, "import",
                    note: $"Imported from {item.Tool.Label()} ({item.Kind.Label().ToLowerInvariant()} “{item.Name}”)", locations: vision);
                SkillFiles.DeleteQuietly(staged);
                drafts.Add(draft);
                if (render(slug) is { } queued) queue.Add(slug, queued);
                return new Outcome(Destination: draft.Directory, IsDraft: true);
            }
            var landed = SkillFiles.Place(staged, Path.Combine(vision.UserSkills, finalName), ConflictPolicy.Rename);
            return new Outcome(Destination: landed, Renamed: !SkillFiles.SamePath(landed, Path.Combine(vision.UserSkills, slug)));
        }
        catch
        {
            SkillFiles.DeleteQuietly(staged);
            throw;
        }
    }

    // MARK: Instructions

    private static Outcome ImportInstructions(ExternalItem item, SkillLocations vision)
    {
        var text = SkillFiles.ReadText(item.SourcePath) ?? throw SkillException.Io($"Can't read {Path.GetFileName(item.SourcePath)}.");
        if (text.Trim().Length == 0) throw SkillException.Invalid($"{Path.GetFileName(item.SourcePath)} is empty.");
        if (ExternalScanner.InstructionsStatus(vision, item.Target, text) == ExternalStatus.Imported) return Outcome.AlreadyThere;

        var destination = Path.Combine(vision.UserInstructions, item.Target + ".md");
        Directory.CreateDirectory(vision.UserInstructions);
        // A different copy is being replaced: keep it beside the new one, where nothing loads it.
        if (File.Exists(destination)) ExternalConverters.Backup(destination);
        SkillFiles.WriteText(destination, text);
        return new Outcome(Destination: destination);
    }

    // MARK: Saved project notes

    private static Outcome ImportNotes(ExternalItem item, SkillLocations vision)
    {
        if (item.Target.Length == 0 || item.Target.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || item.Target is "." or "..")
            throw SkillException.Invalid("That project's name can't be used as a folder name.");
        var notes = ExternalConverters.NoteFiles(item.SourcePath);
        if (notes.Count == 0) throw SkillException.Invalid("There are no notes to copy.");
        if (ExternalScanner.NotesStatus(vision, item.Target, notes) == ExternalStatus.Imported) return Outcome.AlreadyThere;

        var destination = Path.Combine(vision.ProjectNotes, item.Target);
        Directory.CreateDirectory(destination);
        ExternalConverters.CopyNotes(notes, destination);
        var index = Path.Combine(destination, "MEMORY.md");
        if (!File.Exists(index)) SkillFiles.WriteText(index, ExternalConverters.IndexOf(destination, notes));
        return new Outcome(Destination: destination);
    }
}
