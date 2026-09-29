namespace Dsh.Core.Tests;

/// <summary>Ported from testBuiltinGodotSkillInstallsAndParses in MachineTests.swift, plus the
/// Windows rewrite of the skill's text.</summary>
public sealed class BuiltinSkillsTests : IDisposable
{
    private readonly TempDirectory _temp = new("dsh-builtin");

    public void Dispose() => _temp.Dispose();

    /// <summary>Home and app-support both at the temp root, so the builtin folder is
    /// &lt;temp&gt;/skills-builtin exactly as SkillLocations places it.</summary>
    private SkillLocations Locations => new(_temp.Path, _temp.Path);

    private string Builtin => Locations.BuiltinSkills;

    private string Read(string relative) => File.ReadAllText(Path.Combine([Builtin, .. relative.Split('/')]));

    [Fact]
    public void InstallWritesTheGodotSkillAndIsIdempotent()
    {
        Assert.Equal(["godot-debugging"], BuiltinSkills.Install(Builtin));
        // A second install finds identical copies and reports nothing changed.
        Assert.Empty(BuiltinSkills.Install(Builtin));
        Assert.Equal(BuiltinSkills.GodotDebugging.SkillMarkdown, Read("godot-debugging/SKILL.md"));
        Assert.Empty(Directory.GetFiles(Builtin, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void InstalledManifestParsesAsTheSkill()
    {
        BuiltinSkills.Install(Builtin);
        var doc = SkillDocument.Parse(Read("godot-debugging/SKILL.md"));
        Assert.Equal("godot-debugging", doc["name"]);
        Assert.Contains("Godot", doc["description"]);
        Assert.Contains("Windows", doc["description"]);
        Assert.DoesNotContain("\n", doc["description"]); // the folded block scalar joins into one line
        Assert.Contains("process_start", doc.Body);
        Assert.Contains("screen_watch", doc.Body);
        Assert.Contains("_console.exe", doc.Body);
        Assert.Contains("windows-notes.md", doc.Body);
    }

    [Fact]
    public void BundledNotesLandNextToTheManifest()
    {
        BuiltinSkills.Install(Builtin);
        var notes = Read("godot-debugging/windows-notes.md");
        Assert.Contains("--headless --import", notes);
        Assert.Contains(@"%APPDATA%\Godot\app_userdata\<project name>\logs\godot.log", notes);
        Assert.Contains("Get-Content", notes);
        Assert.Contains("keys: [\"F5\"]", notes);
        Assert.False(File.Exists(Path.Combine(Builtin, "godot-debugging", "macos-notes.md")));
    }

    [Fact]
    public void TheCatalogDiscoversItAsABuiltinSkill()
    {
        BuiltinSkills.Install(Builtin);
        var found = SkillCatalog.LoadAll(null, Locations, SkillSources.None);
        var skill = Assert.Single(found, s => s.Name == "godot-debugging");
        Assert.Equal(SkillOrigin.Builtin, skill.Origin);
        Assert.Equal(SkillScope.User, skill.Scope);
        Assert.True(skill.ModelInvocable);
        Assert.Contains("windows-notes.md", skill.Resources());
    }

    [Fact]
    public void AnEditedCopyIsRewrittenWithTheShippedText()
    {
        BuiltinSkills.Install(Builtin);
        var manifest = Path.Combine(Builtin, "godot-debugging", "SKILL.md");
        var notes = Path.Combine(Builtin, "godot-debugging", "windows-notes.md");
        // An editor that saved CRLF counts as a change too: the shipped text is authoritative.
        File.WriteAllText(manifest, BuiltinSkills.GodotDebugging.SkillMarkdown.Replace("\n", "\r\n"));
        Assert.Equal(["godot-debugging"], BuiltinSkills.Install(Builtin));
        Assert.Equal(BuiltinSkills.GodotDebugging.SkillMarkdown, File.ReadAllText(manifest));

        // A bundled file alone going stale is also reported and repaired.
        File.WriteAllText(notes, "stale");
        Assert.Equal(["godot-debugging"], BuiltinSkills.Install(Builtin));
        Assert.Equal(BuiltinSkills.GodotDebugging.Files["windows-notes.md"], File.ReadAllText(notes));

        File.Delete(notes);
        Assert.Equal(["godot-debugging"], BuiltinSkills.Install(Builtin));
        Assert.True(File.Exists(notes));
    }

    [Fact]
    public void ShippedTextIsWindowsOnlyAndLineFeedTerminated()
    {
        foreach (var skill in BuiltinSkills.All)
        {
            var texts = new[] { skill.SkillMarkdown }.Concat(skill.Files.Values).ToList();
            foreach (var text in texts)
            {
                // The sources are pinned to LF; a CRLF checkout would make every launch rewrite them.
                Assert.DoesNotContain("\r", text);
                Assert.DoesNotContain("/Applications", text);
                Assert.DoesNotContain("~/Library", text);
                Assert.DoesNotContain("macos-notes", text);
                Assert.DoesNotContain("\t", text);
            }
            Assert.StartsWith("---\nname: " + skill.Slug + "\n", skill.SkillMarkdown);
            Assert.Equal(skill.Slug, SkillNaming.Slug(skill.Slug));
        }
    }
}
