namespace Dsh.Core.Tests;

/// <summary>New: glob and grep over a project, including the dependency/build folders (node_modules,
/// bin, obj) that are skipped unless the pattern names them.</summary>
public sealed class SearchToolsTests : IDisposable
{
    private readonly ToolTestContext _tools = new();

    public void Dispose() => _tools.Dispose();

    private TempDirectory Root => _tools.Root;

    private void MakeProject()
    {
        Root.Write("src/App.cs", "class App\n{\n    // TODO: needle in the app\n}\n");
        Root.Write("src/Util/Strings.cs", "static class Strings { } // needle\n");
        Root.Write("lib/b.cs", "// nothing here\n");
        Root.Write("README.md", "The needle is documented here.\n");
        Root.Write("node_modules/pkg/index.js", "module.exports = 'needle';\n");
        Root.Write("bin/Debug/App.cs", "// needle in build output\n");
        Root.Write("obj/Generated.cs", "// needle generated\n");
        Root.Write(".git/config", "needle in a hidden folder\n");
    }

    // MARK: glob

    [Fact]
    public async Task GlobSkipsDependencyAndBuildFolders()
    {
        MakeProject();
        Assert.Equal("lib/b.cs\nsrc/App.cs\nsrc/Util/Strings.cs\n(3 matches)",
            await _tools.Output(new GlobTool(), new { pattern = "**/*.cs" }));
        Assert.Equal("No files matched pattern '**/*.js'.", await _tools.Output(new GlobTool(), new { pattern = "**/*.js" }));
    }

    [Fact]
    public async Task GlobSearchesAHeavyFolderWhenThePatternNamesIt()
    {
        MakeProject();
        Assert.Equal("node_modules/pkg/index.js\n(1 match)", await _tools.Output(new GlobTool(), new { pattern = "node_modules/**/*.js" }));
        Assert.Equal("obj/Generated.cs\n(1 match)", await _tools.Output(new GlobTool(), new { pattern = "obj/*.cs" }));
        Assert.Equal("bin/Debug/App.cs\n(1 match)", await _tools.Output(new GlobTool(), new { pattern = "bin/**/App.cs" }));
    }

    [Fact]
    public async Task GlobCanStartBelowTheProjectRoot()
    {
        MakeProject();
        Assert.Equal("App.cs\nUtil/Strings.cs\n(2 matches)", await _tools.Output(new GlobTool(), new { pattern = "**/*.cs", path = "src" }));
        Assert.StartsWith("Error: ", await _tools.Output(new GlobTool(), new { pattern = "*", path = "missing" }));
        Assert.Equal("Error: pattern is required.", await _tools.Output(new GlobTool(), "{}"));
    }

    // MARK: grep

    [Fact]
    public async Task GrepSkipsDependencyBuildAndHiddenFolders()
    {
        MakeProject();
        var output = await _tools.Output(new GrepTool(), new { pattern = "needle" });

        Assert.Equal(string.Join("\n",
            "README.md:1: The needle is documented here.",
            "src/App.cs:3: // TODO: needle in the app",
            "src/Util/Strings.cs:1: static class Strings { } // needle",
            "(3 files with matches, 3 lines)"), output);
    }

    [Fact]
    public async Task GrepSearchesAHeavyFolderWhenTheIncludeNamesIt()
    {
        MakeProject();
        Assert.Equal("node_modules/pkg/index.js:1: module.exports = 'needle';\n(1 files with matches, 1 line)",
            await _tools.Output(new GrepTool(), new { pattern = "needle", include = "node_modules/**/*.js" }));
    }

    [Fact]
    public async Task GrepIncludeFilterMatchesFileNamesOrPaths()
    {
        MakeProject();
        var byName = await _tools.Output(new GrepTool(), new { pattern = "needle", include = "*.cs" });
        Assert.Contains("src/App.cs:3:", byName);
        Assert.Contains("src/Util/Strings.cs:1:", byName);
        Assert.DoesNotContain("README.md", byName);

        var byPath = await _tools.Output(new GrepTool(), new { pattern = "needle", include = "src/*.cs" });
        Assert.StartsWith("src/App.cs:3:", byPath);
        Assert.DoesNotContain("Strings.cs", byPath);
    }

    [Fact]
    public async Task InvalidRegexFallsBackToALiteralSearch()
    {
        Root.Write("calls.cs", "var a = foo(bar);\nvar b = foo;\n");
        Assert.Equal("calls.cs:1: var a = foo(bar);\n(1 files with matches, 1 line)",
            await _tools.Output(new GrepTool(), new { pattern = "foo(" }));
        Assert.StartsWith("No matches found for 'baz\\[' (1 files scanned).",
            await _tools.Output(new GrepTool(), new { pattern = "baz[" }));
    }

    [Fact]
    public async Task GrepIsACaseInsensitiveRegexAndReportsEachLineOnce()
    {
        Root.Write("a.txt", "Alpha beta ALPHA\n\nnothing\nalphabet\n");
        Assert.Equal("a.txt:1: Alpha beta ALPHA\na.txt:4: alphabet\n(1 files with matches, 2 lines)",
            await _tools.Output(new GrepTool(), new { pattern = "alpha" }));
        Assert.Equal("a.txt:4: alphabet\n(1 files with matches, 1 line)",
            await _tools.Output(new GrepTool(), new { pattern = "^alpha\\w+$" }));
    }

    [Fact]
    public async Task GrepReadsCrlfFilesWithCorrectLineNumbers()
    {
        Root.Write("win.txt", "one\r\ntwo\r\nthree needle\r\n");
        Assert.Equal("win.txt:3: three needle\n(1 files with matches, 1 line)",
            await _tools.Output(new GrepTool(), new { pattern = "needle$" }));
    }

    [Fact]
    public async Task GrepSkipsBinaryFiles()
    {
        File.WriteAllBytes(Root["blob.bin"], [0x6E, 0x65, 0x65, 0x64, 0x6C, 0x65, 0x00]); // "needle\0"
        Assert.Equal("No matches found for 'needle' (0 files scanned).", await _tools.Output(new GrepTool(), new { pattern = "needle" }));
    }

    [Fact]
    public void HeavyFoldersAreMatchedCaseInsensitively()
    {
        Root.Write("Bin/x.cs", "");
        Root.Write("OBJ/y.cs", "");
        Root.Write("src/z.cs", "");
        Assert.Equal(["src/z.cs"], RelativeWalk.Files(Root.Path));
        Assert.Equal(["OBJ/y.cs", "src/z.cs"], RelativeWalk.Files(Root.Path, mention: "obj/**"));
    }
}
