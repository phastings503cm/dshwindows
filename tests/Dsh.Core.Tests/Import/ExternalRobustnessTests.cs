namespace Dsh.Core.Tests;

/// <summary>Real profiles are messy: half-written files, a file where a folder should be, junk from an
/// editor crash. Nothing here may throw from the scan or the import, and the good items beside the junk
/// must still come through.</summary>
public sealed class ExternalRobustnessTests : IDisposable
{
    private readonly ProfileFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    private string Claude(string relative) => Path.Combine([_fx.Home, ".claude", .. relative.Split('/')]);

    [Fact]
    public void JunkBesideGoodItemsNeverBreaksTheScanOrTheImport()
    {
        _fx.Skill(".claude/skills/good", "good", "Use when everything is fine.");

        // A folder named SKILL.md, and one with no manifest at all.
        Directory.CreateDirectory(Claude("skills/manifest-is-a-folder/SKILL.md"));
        Directory.CreateDirectory(Claude("skills/empty-folder"));
        // Invalid UTF-8, an empty manifest, unterminated frontmatter.
        Directory.CreateDirectory(Claude("skills/binary"));
        File.WriteAllBytes(Claude("skills/binary/SKILL.md"), [0xFF, 0xFE, 0x00, 0xC3, 0x28, 0xA0, 0xA1, 0x2D, 0x2D, 0x2D]);
        _fx.Write(".claude/skills/empty-manifest/SKILL.md", "");
        _fx.Write(".claude/skills/open-frontmatter/SKILL.md", "---\nname: open\ndescription: never closed\n");
        // A command that is a folder called x.md, a rule that is empty.
        Directory.CreateDirectory(Claude("commands/folder.md"));
        _fx.Write(".claude/rules/empty.md", "");
        // A subagent with no instructions, one that is only frontmatter.
        _fx.Write(".claude/agents/blank.md", "");
        _fx.Write(".claude/agents/header-only.md", "---\nname: header-only\ndescription: nothing else\n---\n");
        // Plugin list pointing at a file, at nothing, and at a non-string.
        var stray = _fx.Write("stray-file.txt", "not a folder");
        _fx.Write(".claude/plugins/installed_plugins.json",
            "{\"version\":2,\"plugins\":{\"a@m\":[{\"installPath\":" + ProfileFixture.Json(stray) + "}],\"b@m\":[{\"installPath\":\"/nowhere/at/all\"}],\"c@m\":[{\"installPath\":42}],\"d@m\":\"nope\"}}");
        // A project folder that is a file, and a notes folder with a note that is too big.
        _fx.Write(".claude/projects/-Users-file-not-folder", "just a file");
        var key = ClaudeProjectNames.Encode(_fx.Project("big-notes"));
        var huge = Claude($"projects/{key}/memory/huge.md");
        Directory.CreateDirectory(Path.GetDirectoryName(huge)!);
        File.WriteAllBytes(huge, new byte[ExternalConverters.MaxNoteBytes + 1]);
        _fx.Write($".claude/projects/{key}/memory/ok.md", "A fine note.");
        // Config files that are the wrong JSON shape.
        _fx.Write(".claude/settings.json", "[1,2,3]");
        _fx.Write(".claude.json", "{\"mcpServers\":[\"not\",\"an\",\"object\"]}");
        _fx.Write(".cursor/mcp.json", "null");
        Directory.CreateDirectory(Path.Combine(_fx.Home, ".cursor", "skills"));
        File.WriteAllText(Path.Combine(_fx.Home, ".cursor", "commands"), "a file where the commands folder goes");

        var inventory = _fx.Scan();
        Assert.Contains(inventory.Items, i => i.Name == "good");
        Assert.Contains(inventory.Items, i => i.Kind == ExternalKind.ProjectNotes && i.Name == "big-notes");
        Assert.Equal(1, inventory.Items.Single(i => i.Kind == ExternalKind.ProjectNotes && i.Name == "big-notes").FileCount); // the oversized note is not a note

        var result = ExternalImporter.Import(inventory, inventory.Items.Select(i => i.Id), _fx.Dsh);
        Assert.Contains(result.Imported, i => i.Item.Name == "good");
        Assert.True(File.Exists(Path.Combine(_fx.Dsh.UserSkills, "good", "SKILL.md")));
        Assert.True(File.Exists(Path.Combine(ProjectContext.ProjectNotesFolder(_fx.Project("big-notes"), _fx.Dsh), "ok.md")));
        Assert.False(File.Exists(Path.Combine(ProjectContext.ProjectNotesFolder(_fx.Project("big-notes"), _fx.Dsh), "huge.md")));
    }

    [Fact]
    public void ScanningTwiceGivesTheSameAnswer()
    {
        _fx.Populate();
        var first = _fx.Scan();
        var second = _fx.Scan();
        Assert.Equal(first.Items.Select(i => (i.Id, i.Status, i.SelectedByDefault)), second.Items.Select(i => (i.Id, i.Status, i.SelectedByDefault)));
        Assert.Equal(first.Left.Select(n => n.Text), second.Left.Select(n => n.Text));
    }

    [Fact]
    public void ACancelledScanStopsInsteadOfFinishing()
    {
        _fx.Populate();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => ExternalScanner.Scan(_fx.External, _fx.Dsh, cancelled.Token));
    }

    [Fact]
    public void AnUnreadableInventoryIdImportsNothing()
    {
        _fx.Populate();
        var inventory = _fx.Scan();
        var result = ExternalImporter.Import(inventory, ["Cursor|Skill|/no/such/item", ""], _fx.Dsh);
        Assert.Empty(result.Imported);
        Assert.Empty(result.Skipped);
    }

    [Fact]
    public void AnItemWhoseSourceVanishedAfterTheScanIsSkippedWithAReason()
    {
        _fx.Populate();
        var inventory = _fx.Scan();
        Directory.Delete(Claude("skills/review"), recursive: true);
        File.Delete(Claude("CLAUDE.md"));
        Directory.Delete(Claude("agents"), recursive: true);
        var ids = inventory.Items.Where(i => i.Name is "review" or "code-reviewer" || i.Kind == ExternalKind.Instructions || i.Name == "pdf-tools").Select(i => i.Id);

        var result = ExternalImporter.Import(inventory, ids, _fx.Dsh);
        Assert.Equal(["pdf-tools"], result.Imported.Select(i => i.Item.Name).ToArray());
        Assert.Equal(3, result.Skipped.Count);
        Assert.All(result.Skipped, s => Assert.False(string.IsNullOrWhiteSpace(s.Reason)));
        Assert.False(Directory.Exists(Path.Combine(_fx.Dsh.UserSkills, "review")), "nothing half-copied");
    }
}
