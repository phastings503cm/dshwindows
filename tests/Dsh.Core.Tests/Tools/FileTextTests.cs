using System.Text;

namespace Dsh.Core.Tests;

/// <summary>New: Windows projects are full of CRLF files and BOMs; an edit must keep both, or a one-line
/// change becomes a whole-file diff. Binary files are refused.</summary>
public sealed class FileTextTests : IDisposable
{
    private readonly ToolTestContext _tools = new();

    public void Dispose() => _tools.Dispose();

    private static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];

    private string PathOf(string name) => _tools.Root[name];

    private string WriteBytes(string name, byte[] bytes)
    {
        var path = PathOf(name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    private static byte[] WithBom(string text) => [.. Bom, .. Utf8(text)];

    // MARK: FileText

    [Fact]
    public void DecodeReportsBomAndLineEndingsAndStripsTheBom()
    {
        var decoded = FileText.Decode(WithBom("a\r\nb"));
        Assert.NotNull(decoded);
        Assert.Equal("a\r\nb", decoded.Value.Text);
        Assert.Equal(new TextFileFormat(HasBom: true, UsesCrlf: true), decoded.Value.Format);

        Assert.Equal(TextFileFormat.Default, FileText.Decode(Utf8("a\nb"))?.Format);
    }

    [Fact]
    public void DecodeRefusesBinaryAndInvalidUtf8()
    {
        Assert.Null(FileText.Decode([0x68, 0x69, 0x00, 0x21]));   // NUL byte
        Assert.Null(FileText.Decode([0x63, 0x61, 0x66, 0xE9]));   // Latin-1 "café"
        Assert.Null(FileText.Decode([.. Bom, 0x00]));              // a BOM doesn't make it text
        Assert.Equal("", FileText.Decode([])?.Text);
    }

    [Fact]
    public void WriteConvertsLineEndingsKeepsTheBomAndLeavesNoTempFile()
    {
        var path = PathOf("out.txt");
        FileText.Write(path, "a\nb\r\nc\n", new TextFileFormat(HasBom: true, UsesCrlf: true));

        Assert.Equal(WithBom("a\r\nb\r\nc\r\n"), File.ReadAllBytes(path));
        Assert.False(File.Exists(path + ".dsh-tmp"));
    }

    [Fact]
    public void WriteCreatesMissingFolders()
    {
        var path = PathOf("deep/er/new.txt");
        FileText.Write(path, "x", TextFileFormat.Default);
        Assert.Equal(Utf8("x"), File.ReadAllBytes(path));
    }

    [Fact]
    public void ReadOfAMissingFileIsNull()
    {
        Assert.Null(FileText.Read(PathOf("missing.txt")));
        Assert.Null(FileText.Read(_tools.Root.Path)); // a directory
    }

    // MARK: edit

    [Fact]
    public async Task CrlfFileEditedWithLfOldStringMatchesAndStaysCrlf()
    {
        var path = WriteBytes("win.cs", Utf8("line1\r\nline2\r\nline3\r\n"));
        var output = await _tools.Output(new EditTool(),
            new { file_path = "win.cs", old_string = "line2\nline3", new_string = "LINE2\nLINE3\nline4" });

        Assert.Equal("Edited win.cs: replaced 1 occurrence.", output);
        Assert.Equal(Utf8("line1\r\nLINE2\r\nLINE3\r\nline4\r\n"), File.ReadAllBytes(path));
    }

    [Fact]
    public async Task CrlfOldStringAlsoMatchesACrlfFile()
    {
        var path = WriteBytes("win.txt", Utf8("a\r\nb\r\n"));
        await _tools.Output(new EditTool(), new { file_path = "win.txt", old_string = "a\r\nb", new_string = "x\r\ny" });
        Assert.Equal(Utf8("x\r\ny\r\n"), File.ReadAllBytes(path));
    }

    [Fact]
    public async Task BomIsPreservedOnEdit()
    {
        var path = WriteBytes("bom.txt", WithBom("hello world\n"));
        await _tools.Output(new EditTool(), new { file_path = "bom.txt", old_string = "world", new_string = "there" });
        Assert.Equal(WithBom("hello there\n"), File.ReadAllBytes(path));
    }

    [Fact]
    public async Task LfFileStaysLf()
    {
        var path = WriteBytes("unix.sh", Utf8("echo a\necho b\n"));
        await _tools.Output(new EditTool(), new { file_path = "unix.sh", old_string = "echo b", new_string = "echo c" });
        Assert.Equal(Utf8("echo a\necho c\n"), File.ReadAllBytes(path));
    }

    [Fact]
    public async Task BinaryFilesAreRefusedByEditAndLeftUntouched()
    {
        byte[] blob = [0x4D, 0x5A, 0x00, 0x90, 0x41];
        var path = WriteBytes("app.exe", blob);
        var output = await _tools.Output(new EditTool(), new { file_path = "app.exe", old_string = "MZ", new_string = "XX" });

        Assert.Equal("Error: cannot edit app.exe: it is not a UTF-8 text file.", output);
        Assert.Equal(blob, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task EditReportsMissingAndAmbiguousMatches()
    {
        WriteBytes("dup.txt", Utf8("x = 1\nx = 1\n"));
        Assert.StartsWith("Error: old_string not found in dup.txt.",
            await _tools.Output(new EditTool(), new { file_path = "dup.txt", old_string = "y", new_string = "z" }));
        Assert.StartsWith("Error: old_string matches 2 times.",
            await _tools.Output(new EditTool(), new { file_path = "dup.txt", old_string = "x = 1", new_string = "x = 2" }));
        Assert.StartsWith("Error: file_path, old_string and new_string are required.",
            await _tools.Output(new EditTool(), new { file_path = "dup.txt" }));
    }

    [Fact]
    public async Task ReplaceAllReplacesEveryOccurrenceAndAcceptsAStringFlag()
    {
        var path = WriteBytes("dup.txt", Utf8("x = 1\nx = 1\n"));
        var result = await _tools.Run(new EditTool(),
            """{"file_path":"dup.txt","old_string":"x = 1","new_string":"x = 2","replace_all":"true"}""");

        Assert.Equal("Edited dup.txt: replaced 2 occurrences.", result.Output);
        Assert.Equal("x = 2\nx = 2\n", File.ReadAllText(path));
        Assert.Equal(new FileChange(path, FileChangeKind.Modified), Assert.Single(result.Files));
    }

    [Fact]
    public async Task EditOfAMissingFileCreatesItWithoutABom()
    {
        var result = await _tools.Run(new EditTool(), new { file_path = "src/new.cs", old_string = "", new_string = "class A {}\n" });
        var path = PathOf("src/new.cs");

        Assert.Equal("Created new.cs with the new block.", result.Output);
        Assert.Equal(Utf8("class A {}\n"), File.ReadAllBytes(path));
        Assert.Equal(FileChangeKind.Created, Assert.Single(result.Files).Kind);
    }

    // MARK: write_file

    [Fact]
    public async Task NewFilesAreWrittenWithoutBom()
    {
        var result = await _tools.Run(new WriteFileTool(), new { file_path = "notes/new.md", content = "# Notes\n" });
        var path = PathOf("notes/new.md");

        Assert.Equal(Utf8("# Notes\n"), File.ReadAllBytes(path));
        Assert.Equal("Wrote 8 characters to new.md.", result.Output);
        Assert.Equal(new FileChange(path, FileChangeKind.Created), Assert.Single(result.Files));
    }

    [Fact]
    public async Task BomAndCrlfArePreservedOnOverwrite()
    {
        var path = WriteBytes("App.config", WithBom("<old/>\r\n"));
        var result = await _tools.Run(new WriteFileTool(), new { file_path = "App.config", content = "<new>\n  <x/>\n</new>\n" });

        Assert.Equal(WithBom("<new>\r\n  <x/>\r\n</new>\r\n"), File.ReadAllBytes(path));
        Assert.Equal(FileChangeKind.Modified, Assert.Single(result.Files).Kind);
    }

    [Fact]
    public async Task ContentThatAlreadyHasCrlfIsWrittenAsIs()
    {
        var path = WriteBytes("mixed.txt", Utf8("a\r\n"));
        await _tools.Output(new WriteFileTool(), new { file_path = "mixed.txt", content = "x\r\ny\nz" });
        Assert.Equal(Utf8("x\r\ny\nz"), File.ReadAllBytes(path));
    }

    [Fact]
    public async Task OverwritingABinaryFileStartsFromDefaults()
    {
        var path = WriteBytes("data.bin", [0x00, 0x01]);
        await _tools.Output(new WriteFileTool(), new { file_path = "data.bin", content = "text\n" });
        Assert.Equal(Utf8("text\n"), File.ReadAllBytes(path));
    }

    [Fact]
    public async Task WriteFileNeedsBothArguments()
    {
        Assert.Equal("Error: file_path and content are required.", await _tools.Output(new WriteFileTool(), new { file_path = "x" }));
    }

    // MARK: read_file

    [Fact]
    public async Task BinaryFilesAreRefusedByReadFile()
    {
        WriteBytes("image.png", TestImages.Png(4, 4));
        Assert.Equal("Error: cannot read image.png: not a readable text file (binary or missing).",
            await _tools.Output(new ReadFileTool(), new { file_path = "image.png" }));
    }

    [Fact]
    public async Task ReadFileShowsCrlfAndBomFilesWithoutEitherMarker()
    {
        WriteBytes("win.txt", WithBom("first\r\nsecond"));
        Assert.Equal("1 | first\n2 | second", await _tools.Output(new ReadFileTool(), new { file_path = "win.txt" }));
    }
}
