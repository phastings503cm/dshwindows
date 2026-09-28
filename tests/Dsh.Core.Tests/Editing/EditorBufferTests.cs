using System.ComponentModel;

namespace Dsh.Core.Tests;

/// <summary>Ported from EditorBufferTests.swift. "See files edit in real time" is this class's job: a
/// buffer must follow the agent's writes silently when it is clean, and never throw away unsaved work
/// when it is not.</summary>
public sealed class EditorBufferTests : IDisposable
{
    private readonly TempDirectory _root = new("dsh-buf");

    public void Dispose() => _root.Dispose();

    private string Write(string text, string name = "main.swift") => _root.Write(name, text);

    private static EditorBuffer Open(string path)
    {
        var buffer = EditorBuffer.Open(path);
        Assert.NotNull(buffer);
        return buffer;
    }

    [Fact]
    public void LoadsFileAndDetectsLanguage()
    {
        var buffer = Open(Write("let x = 1\n"));
        Assert.Equal("let x = 1\n", buffer.Text);
        Assert.False(buffer.IsDirty);
        Assert.Equal("Swift", buffer.Language.Name);
        Assert.Equal("main.swift", buffer.Name);
    }

    [Fact]
    public void EditingMarksDirtyAndSavingClearsIt()
    {
        var path = Write("one\n");
        var buffer = Open(path);
        buffer.Text = "two\n";
        Assert.True(buffer.IsDirty);

        buffer.Save();
        Assert.False(buffer.IsDirty);
        Assert.Equal("two\n", File.ReadAllText(path));
    }

    /// <summary>The agent rewrites a file the user has open but has not touched: the editor should just
    /// show the new contents.</summary>
    [Fact]
    public void CleanBufferFollowsExternalWriteSilently()
    {
        var path = Write("before\n");
        var buffer = Open(path);
        var token = buffer.ReloadToken;

        File.WriteAllText(path, "after\n");
        buffer.ExternalChangeDetected();

        Assert.Equal("after\n", buffer.Text);
        Assert.False(buffer.DiskConflict);
        Assert.False(buffer.IsDirty);
        Assert.True(buffer.ReloadToken > token, "the text view must be told to reload");
    }

    /// <summary>Same write, but the user has unsaved edits: their work must survive and a conflict must
    /// be raised instead.</summary>
    [Fact]
    public void DirtyBufferRaisesConflictInsteadOfLosingWork()
    {
        var path = Write("before\n");
        var buffer = Open(path);
        buffer.Text = "my unsaved edit\n";

        File.WriteAllText(path, "the agent's version\n");
        buffer.ExternalChangeDetected();

        Assert.True(buffer.DiskConflict);
        Assert.Equal("my unsaved edit\n", buffer.Text); // unsaved work must not be overwritten
    }

    [Fact]
    public void ReloadFromDiskDiscardsLocalEdits()
    {
        var path = Write("before\n");
        var buffer = Open(path);
        buffer.Text = "mine\n";
        File.WriteAllText(path, "theirs\n");
        buffer.ExternalChangeDetected();

        buffer.ReloadFromDisk();
        Assert.Equal("theirs\n", buffer.Text);
        Assert.False(buffer.DiskConflict);
        Assert.False(buffer.IsDirty);
    }

    /// <summary>"Keep Mine" clears the warning but leaves the buffer dirty against the new file, so the
    /// next save is still an explicit overwrite.</summary>
    [Fact]
    public void KeepMineLeavesTheBufferDirty()
    {
        var path = Write("before\n");
        var buffer = Open(path);
        buffer.Text = "mine\n";
        File.WriteAllText(path, "theirs\n");
        buffer.ExternalChangeDetected();

        buffer.KeepMine();
        Assert.False(buffer.DiskConflict);
        Assert.True(buffer.IsDirty);
        Assert.Equal("mine\n", buffer.Text);

        buffer.Save();
        Assert.Equal("mine\n", File.ReadAllText(path));
    }

    /// <summary>A write that lands identical to the buffer (the agent reformatted to what was already
    /// there) must not flag a conflict.</summary>
    [Fact]
    public void IdenticalExternalWriteIsNotAConflict()
    {
        var path = Write("same\n");
        var buffer = Open(path);
        buffer.Text = "same\n";
        File.WriteAllText(path, "same\n");
        buffer.ExternalChangeDetected();

        Assert.False(buffer.DiskConflict);
        Assert.False(buffer.IsDirty);
    }

    [Fact]
    public void MissingFileYieldsNoBuffer()
    {
        Assert.Null(EditorBuffer.Open(_root["nope.txt"]));
    }

    [Fact]
    public void BinaryFileYieldsNoBuffer()
    {
        var path = _root["blob.bin"];
        File.WriteAllBytes(path, [0xFF, 0xFE, 0x00, 0x01, 0xC0]);
        Assert.Null(EditorBuffer.Open(path)); // invalid UTF-8 must not open as text
    }

    [Fact]
    public void DeletedFileLeavesTheBufferIntact()
    {
        var path = Write("content\n");
        var buffer = Open(path);
        File.Delete(path);
        buffer.ExternalChangeDetected();
        Assert.Equal("content\n", buffer.Text); // a delete must not blank the editor
    }
}

/// <summary>New: Windows line endings and BOMs survive an edit, and the size/encoding guards.</summary>
public sealed class EditorBufferFormatTests : IDisposable
{
    private readonly TempDirectory _root = new("dsh-buf2");

    public void Dispose() => _root.Dispose();

    [Fact]
    public void CrlfFileIsEditedAsLfAndSavedAsCrlf()
    {
        var path = _root["win.cs"];
        File.WriteAllText(path, "a\r\nb\r\n");
        var buffer = EditorBuffer.Open(path)!;

        Assert.Equal("a\nb\n", buffer.Text);
        Assert.True(buffer.Format.UsesCrlf);
        Assert.False(buffer.IsDirty);

        buffer.Text = "a\nB\nc\n";
        buffer.Save();
        Assert.Equal("a\r\nB\r\nc\r\n", File.ReadAllText(path));
    }

    [Fact]
    public void ByteOrderMarkIsKeptOnSave()
    {
        var path = _root["bom.txt"];
        File.WriteAllBytes(path, [0xEF, 0xBB, 0xBF, .. "hello\n"u8]);
        var buffer = EditorBuffer.Open(path)!;
        Assert.Equal("hello\n", buffer.Text); // the BOM is not part of the text
        Assert.True(buffer.Format.HasBom);

        buffer.Text = "bye\n";
        buffer.Save();
        byte[] expected = [0xEF, 0xBB, 0xBF, .. "bye\n"u8];
        Assert.Equal(expected, File.ReadAllBytes(path));
    }

    [Fact]
    public void InvalidUtf8WithoutNulBytesYieldsNoBuffer()
    {
        var path = _root["latin1.txt"];
        File.WriteAllBytes(path, [0x63, 0x61, 0x66, 0xE9]); // "café" in Latin-1
        Assert.Null(EditorBuffer.Open(path));
    }

    [Fact]
    public void TooLargeFileYieldsNoBuffer()
    {
        var path = _root["huge.log"];
        File.WriteAllText(path, new string('a', EditorBuffer.MaxEditableBytes + 1));
        Assert.Null(EditorBuffer.Open(path));
    }

    [Fact]
    public void ReloadPicksUpAChangedLineEnding()
    {
        var path = _root["x.txt"];
        File.WriteAllText(path, "a\nb\n");
        var buffer = EditorBuffer.Open(path)!;
        Assert.False(buffer.Format.UsesCrlf);

        File.WriteAllText(path, "a\r\nb\r\nc\r\n");
        buffer.ExternalChangeDetected();
        Assert.Equal("a\nb\nc\n", buffer.Text);
        Assert.True(buffer.Format.UsesCrlf);
    }

    [Fact]
    public void EditingRaisesPropertyChangedForTextAndIsDirty()
    {
        var path = _root["n.md"];
        File.WriteAllText(path, "x");
        var buffer = EditorBuffer.Open(path)!;
        var changed = new List<string?>();
        buffer.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        buffer.Text = "y";
        Assert.Equal([nameof(EditorBuffer.Text), nameof(EditorBuffer.IsDirty)], changed);

        changed.Clear();
        buffer.Text = "y"; // no change, no event
        Assert.Empty(changed);
        Assert.Equal(path, buffer.Id);
    }

    [Fact]
    public void PropertyChangedArgsAreStandard()
    {
        var path = _root["p.txt"];
        File.WriteAllText(path, "x");
        INotifyPropertyChanged buffer = EditorBuffer.Open(path)!;
        PropertyChangedEventArgs? last = null;
        buffer.PropertyChanged += (_, e) => last = e;
        ((EditorBuffer)buffer).DiskConflict = true;
        Assert.Equal(nameof(EditorBuffer.DiskConflict), last?.PropertyName);
    }
}

/// <summary>Ported from the LanguageTests class in EditorBufferTests.swift.</summary>
public sealed class LanguageTests
{
    private static Language Detect(string name) => Language.Detect(Path.Combine(Path.GetTempPath(), name));

    [Fact]
    public void DetectsByExtension()
    {
        Assert.Equal("Swift", Detect("a.swift").Name);
        Assert.Equal("JSON", Detect("a.json").Name);
        Assert.Equal("YAML", Detect("a.yml").Name);
        Assert.Equal("SQL", Detect("a.sql").Name);
        Assert.Equal("Shell", Detect("a.sh").Name);
    }

    [Fact]
    public void DetectsExtensionlessConventions()
    {
        Assert.Equal("Shell", Detect("Makefile").Name);
        Assert.Equal("Shell", Detect("Dockerfile").Name);
    }

    [Fact]
    public void UnknownFallsBackToPlain()
    {
        Assert.Equal("Text", Detect("notes.xyz").Name);
        Assert.Empty(Detect("notes.xyz").Keywords);
    }

    [Fact]
    public void SwiftVocabularyIsPresent()
    {
        var swift = Language.Swift;
        Assert.Contains("guard", swift.Keywords);
        Assert.Contains("actor", swift.Keywords);
        Assert.Equal("//", swift.LineComment);
        Assert.Equal("/*", swift.BlockComment?.Open);
    }

    [Fact]
    public void JavaScriptGetsItsOwnKeywords()
    {
        var ts = Detect("a.ts");
        Assert.Contains("interface", ts.Keywords);
        Assert.Contains("await", ts.Keywords);
    }
}

/// <summary>New: the Windows-side languages.</summary>
public sealed class WindowsLanguageTests
{
    private static Language Detect(string name) => Language.Detect(Path.Combine(Path.GetTempPath(), name));

    [Fact]
    public void DetectsWindowsLanguages()
    {
        Assert.Equal("C#", Detect("Program.cs").Name);
        Assert.Equal("C#", Detect("PROGRAM.CS").Name);
        Assert.Equal("PowerShell", Detect("build.ps1").Name);
        Assert.Equal("PowerShell", Detect("Module.psm1").Name);
        Assert.Equal("Batch", Detect("setup.cmd").Name);
        Assert.Equal("Batch", Detect("run.bat").Name);
        Assert.Equal("Markup", Detect("App.xaml").Name);
        Assert.Equal("Markup", Detect("Dsh.Core.csproj").Name);
        Assert.Equal("Shell", Detect(".editorconfig").Name);
    }

    [Fact]
    public void CaseInsensitiveLanguagesMatchKeywordsInAnyCase()
    {
        Assert.True(Language.PowerShell.IgnoreCase);
        Assert.Contains("FOREACH", Language.PowerShell.Keywords);
        Assert.Contains("-EQ", Language.PowerShell.Keywords);
        Assert.Contains("SELECT", Language.Sql.Keywords);
        Assert.Equal("::", Language.Batch.LineComment);
        Assert.Equal(("<#", "#>"), Language.PowerShell.BlockComment);
        Assert.DoesNotContain("FOREACH", Language.CSharp.Keywords);
        Assert.Contains("foreach", Language.CSharp.Keywords);
    }

    [Fact]
    public void PlainHasNothingToColour()
    {
        Assert.True(Language.Plain.IsPlain);
        Assert.False(Language.Json.IsPlain);
    }
}
