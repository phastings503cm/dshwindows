namespace Dsh.Core.Tests;

/// <summary>New: read_file numbering and ranges, list_directory layout, read_many_files.</summary>
public sealed class FileToolsTests : IDisposable
{
    private readonly ToolTestContext _tools = new();

    public void Dispose() => _tools.Dispose();

    private TempDirectory Root => _tools.Root;

    private static string Numbered(int from, int to) =>
        string.Join("\n", Enumerable.Range(from, to - from + 1).Select(n => $"{n} | line {n}"));

    private void WriteLines(string name, int count) =>
        Root.Write(name, string.Join("\n", Enumerable.Range(1, count).Select(n => $"line {n}")));

    // MARK: read_file

    [Fact]
    public async Task ReadFileNumbersLinesFromOne()
    {
        WriteLines("a.txt", 3);
        Assert.Equal(Numbered(1, 3), await _tools.Output(new ReadFileTool(), new { file_path = "a.txt" }));
    }

    [Fact]
    public async Task ReadFileReturnsARangeAndSaysWhatItShowed()
    {
        WriteLines("a.txt", 10);
        Assert.Equal(Numbered(4, 6) + "\n\n(File has 10 lines total; showing 4-6.)",
            await _tools.Output(new ReadFileTool(), new { file_path = "a.txt", start_line = 4, end_line = 6 }));
        // Lenient: numbers as strings, and an end past the file is clamped.
        Assert.Equal(Numbered(9, 10),
            await _tools.Output(new ReadFileTool(), """{"file_path":"a.txt","start_line":"9","end_line":"99"}"""));
    }

    [Fact]
    public async Task ReadFileDefaultsToTheFirstThousandLines()
    {
        WriteLines("big.txt", 1_500);
        var output = await _tools.Output(new ReadFileTool(), new { file_path = "big.txt" });
        Assert.StartsWith("1 | line 1\n", output);
        Assert.Contains("\n1000 | line 1000\n", output);
        Assert.DoesNotContain("1001 | ", output);
        Assert.EndsWith("(File has 1500 lines total; showing 1-1000.)", output);
    }

    [Fact]
    public async Task ReadFileRejectsARangePastTheEnd()
    {
        WriteLines("a.txt", 3);
        Assert.Equal("Error: no such line range (file has 3 lines).",
            await _tools.Output(new ReadFileTool(), new { file_path = "a.txt", start_line = 10 }));
    }

    [Fact]
    public async Task ReadFileExplainsDirectoriesAndMissingFiles()
    {
        Root.Write("src/x.cs", "");
        Assert.Equal("Error: src is a directory. Use list_directory to see what is in it.",
            await _tools.Output(new ReadFileTool(), new { file_path = "src" }));
        Assert.StartsWith("Error: cannot read nope.txt",
            await _tools.Output(new ReadFileTool(), new { file_path = "nope.txt" }));
        Assert.Equal("Error: file_path is required.", await _tools.Output(new ReadFileTool(), "{}"));
    }

    [Fact]
    public async Task ReadFileAcceptsAbsolutePaths()
    {
        using var elsewhere = new TempDirectory("dsh-read-elsewhere");
        var path = elsewhere.Write("outside.txt", "hello");
        Assert.Equal("1 | hello", await _tools.Output(new ReadFileTool(), new { file_path = path }));
    }

    // MARK: list_directory

    private void MakeTree()
    {
        Root.Write("b.txt", "");
        Root.Write("A.md", "");
        Root.Write("src/main.cs", "");
        Root.Write("src/lib/x.cs", "");
        Root.Write("docs/guide.md", "");
        Root.Write(".git/HEAD", "ref: refs/heads/main");
        Root.Write(".env", "SECRET=1");
    }

    [Fact]
    public async Task ListDirectoryPutsFoldersFirstAndSkipsHiddenEntries()
    {
        MakeTree();
        Assert.Equal("📁 docs\n📁 src\n  A.md\n  b.txt", await _tools.Output(new ListDirectoryTool(), "{}"));
        Assert.Equal("📁 lib\n  main.cs", await _tools.Output(new ListDirectoryTool(), new { path = "src" }));
    }

    [Fact]
    public async Task RecursiveListingIndentsByDepth()
    {
        MakeTree();
        var expected = string.Join("\n",
            "  A.md",
            "  b.txt",
            "📁 docs",
            "    guide.md",
            "📁 src",
            "  📁 lib",
            "      x.cs",
            "    main.cs");
        Assert.Equal(expected, await _tools.Output(new ListDirectoryTool(), new { recursive = true }));
    }

    [Fact]
    public async Task RecursiveListingStopsAt500Entries()
    {
        for (var i = 0; i < 520; i++) Root.Write($"many/f{i:D3}.txt", "");
        var lines = (await _tools.Output(new ListDirectoryTool(), """{"path":"many","recursive":"true"}""")).Split('\n');

        Assert.Equal(501, lines.Length);
        Assert.Equal("  f000.txt", lines[0]);
        Assert.Equal("  f499.txt", lines[499]);
        Assert.Equal("… (truncated at 500 entries)", lines[^1]);
    }

    [Fact]
    public async Task ListDirectoryReportsEmptyAndMissingFolders()
    {
        Directory.CreateDirectory(Root["empty"]);
        Assert.Equal("(empty directory)", await _tools.Output(new ListDirectoryTool(), new { path = "empty" }));
        Assert.Equal($"Error: {Root["missing"]} is not a directory.", await _tools.Output(new ListDirectoryTool(), new { path = "missing" }));
    }

    [WindowsFact]
    public async Task EntriesMarkedHiddenAreSkippedOnWindows()
    {
        var hidden = Root.Write("desktop.ini", "");
        File.SetAttributes(hidden, FileAttributes.Hidden | FileAttributes.System);
        Root.Write("visible.txt", "");
        Assert.Equal("  visible.txt", await _tools.Output(new ListDirectoryTool(), "{}"));
    }

    // MARK: read_many_files

    [Fact]
    public async Task ReadManyFilesConcatenatesAndReportsFailures()
    {
        Root.Write("a.txt", "alpha");
        Root.Write("sub/b.txt", "beta\r\ngamma");
        Root.Write("bin.dat", "\0\0");
        var output = await _tools.Output(new ReadManyFilesTool(), new { paths = "a.txt, sub/b.txt ,missing.txt,bin.dat" });

        Assert.Equal("=== a.txt ===\nalpha\n\n=== b.txt ===\nbeta\ngamma\n\nCould not read: missing.txt, bin.dat", output);
    }

    [Fact]
    public async Task ReadManyFilesCapsEachFile()
    {
        WriteLines("long.txt", 600);
        var output = await _tools.Output(new ReadManyFilesTool(), new { paths = "long.txt" });
        Assert.Contains("\nline 500\n… (truncated, 600 lines total)", output);
        Assert.DoesNotContain("line 501", output);
        Assert.Equal("Could not read: nope.txt", await _tools.Output(new ReadManyFilesTool(), new { paths = "nope.txt" }));
        Assert.Equal("Error: no readable files.", await _tools.Output(new ReadManyFilesTool(), new { paths = " , " }));
    }

    [Fact]
    public async Task TodoWriteParsesItemsAndAStringifiedList()
    {
        var result = await _tools.Run(new TodoWriteTool(),
            """{"todos":"[{\"content\":\"plan\",\"status\":\"done\"},{\"content\":\"\"},{\"content\":\"ship\",\"status\":\"in_progress\"}]"}""");

        Assert.Equal("Task list updated:\n✓ plan\n◐ ship", result.Output);
        Assert.NotNull(result.Todos);
        Assert.Equal([new TodoItem("1", "plan", TodoStatus.Completed), new TodoItem("3", "ship", TodoStatus.InProgress)], result.Todos);
        Assert.Equal("Task list cleared.", await _tools.Output(new TodoWriteTool(), """{"todos":[]}"""));
    }
}
