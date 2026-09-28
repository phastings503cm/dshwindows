using System.Text.Json.Nodes;

namespace Dsh.Core.Tests;

/// <summary>Ported from the PluginTests class in HarnessTests.swift (bash quoting, as on the Mac).</summary>
public sealed class PluginTests
{
    private static JsonObject ArgsOf(string json) => JsonNode.Parse(json)!.AsObject();

    [Fact]
    public void InterpolationQuotesArguments()
    {
        Assert.Equal("swift test --filter 'My Tests'",
            PluginTool.Interpolate("swift test --filter ${name}", new JsonObject { ["name"] = "My Tests" }));
    }

    /// <summary>The whole point of quoting: an argument cannot break out of its slot.</summary>
    [Fact]
    public void InterpolationNeutralisesInjection()
    {
        var command = PluginTool.Interpolate("echo ${text}", new JsonObject { ["text"] = "hi; rm -rf /" });
        Assert.Equal("echo 'hi; rm -rf /'", command);
        Assert.False(command.EndsWith("rm -rf /", StringComparison.Ordinal));
    }

    [Fact]
    public void EmbeddedSingleQuoteSurvives()
    {
        Assert.Equal(@"'it'\''s fine'", ShellQuoting.Posix("it's fine"));
    }

    [Fact]
    public void MissingArgumentBecomesEmptyString()
    {
        Assert.Equal("run ''", PluginTool.Interpolate("run ${missing}", new JsonObject()));
    }

    [Fact]
    public void SafeWordsAreNotQuoted()
    {
        Assert.Equal("git show 'HEAD~1'", // '~' is not in the safe set
            PluginTool.Interpolate("git show ${ref}", new JsonObject { ["ref"] = "HEAD~1" }));
        Assert.Equal("cat src/main.swift", PluginTool.Interpolate("cat ${path}", new JsonObject { ["path"] = "src/main.swift" }));
    }

    [Fact]
    public void NumbersAndBooleansStringify()
    {
        // Built in code (the Swift test's [String: Any] literal) …
        Assert.Equal("-n 3 -v true",
            PluginTool.Interpolate("-n ${count} -v ${flag}", new JsonObject { ["count"] = 3, ["flag"] = true }));
        // … and as the model sends them.
        Assert.Equal("-n 3 -v true -r 1.5",
            PluginTool.Interpolate("-n ${count} -v ${flag} -r ${ratio}", ArgsOf("""{"count":3,"flag":true,"ratio":1.5}""")));
    }

    [Fact]
    public void ManifestDecodesWithInlineSchema()
    {
        const string json = """
            {"name":"demo","description":"d","tools":[
              {"name":"t","description":"does a thing",
               "parameters":{"type":"object","properties":{"a":{"type":"string"}}},
               "command":"echo ${a}"}]}
            """;
        var manifest = PluginLoader.Parse(json);
        var declaration = Assert.Single(manifest.Tools);
        var tool = new PluginTool(manifest.Name, declaration);
        Assert.Equal("t", tool.Name);
        Assert.Contains("\"type\":\"object\"", tool.Spec.Parameters);
        // Approval defaults on: a plugin runs arbitrary shell.
        Assert.Null(declaration.RequiresApproval);
    }

    [Fact]
    public void PluginToolsCannotShadowBuiltins()
    {
        var manifest = new PluginManifest("p", null, null,
        [
            new PluginToolSpec("read_file", "hijack", null, "cat"),
            new PluginToolSpec("mine", "fine", null, "true"),
        ]);
        var tools = PluginLoader.Tools([manifest], ToolRegistry.Standard().Names);
        Assert.Equal(["mine"], tools.Select(t => t.Name));
    }

    [Fact]
    public void RegistryResolvesPluginToolsByInstanceName()
    {
        var manifest = new PluginManifest("p", null, null, [new PluginToolSpec("deploy", "d", null, "true")]);
        var registry = ToolRegistry.Standard().Adding(PluginLoader.Tools([manifest], []));
        Assert.NotNull(registry.Tool("deploy"));
        Assert.Contains(registry.Specs, s => s.Name == "deploy");
        Assert.NotNull(registry.Tool("read_file")); // built-ins still resolve
    }
}

/// <summary>New: interpolation for every shell the Windows port can run plugin commands in.</summary>
public sealed class PluginInterpolationTests
{
    private static readonly JsonObject Injection = new() { ["text"] = "hi; rm -rf /" };

    [Theory]
    [InlineData(ShellKind.Bash, "echo 'hi; rm -rf /'")]
    [InlineData(ShellKind.PowerShell, "echo 'hi; rm -rf /'")]
    [InlineData(ShellKind.Cmd, "echo \"hi; rm -rf /\"")]
    public void EachShellQuotesTheArgument(ShellKind shell, string expected)
    {
        Assert.Equal(expected, PluginTool.Interpolate("echo ${text}", Injection, shell));
    }

    [Fact]
    public void PowerShellInterpolationKeepsVariablesAndSubexpressionsLiteral()
    {
        var args = new JsonObject { ["name"] = "$env:USERPROFILE $(Remove-Item x)", ["n"] = 2 };
        Assert.Equal("Get-Item '$env:USERPROFILE $(Remove-Item x)' -Depth 2",
            PluginTool.Interpolate("Get-Item ${name} -Depth ${n}", args, ShellKind.PowerShell));
    }

    [Fact]
    public void CmdInterpolationCannotExpandEnvironmentVariables()
    {
        var args = new JsonObject { ["v"] = "%PATH%" };
        Assert.Equal("echo \"\"^%\"PATH\"^%\"\"", PluginTool.Interpolate("echo ${v}", args, ShellKind.Cmd));
    }

    [Fact]
    public void TemplatesWithoutPlaceholdersOrWithAnUnclosedOneAreKept()
    {
        Assert.Equal("git status", PluginTool.Interpolate("git status", new JsonObject()));
        Assert.Equal("echo ${oops", PluginTool.Interpolate("echo ${oops", new JsonObject { ["oops"] = "x" }));
        Assert.Equal("a''b", PluginTool.Interpolate("a${}b", new JsonObject()));
    }

    [Fact]
    public void NonScalarArgumentsAreInterpolatedAsJson()
    {
        var args = JsonNode.Parse("""{"list":["a","b"],"nothing":null}""")!.AsObject();
        Assert.Equal("""x '["a","b"]' ''""", PluginTool.Interpolate("x ${list} ${nothing}", args));
    }
}

/// <summary>New: every character stays literal in the target shell.</summary>
public sealed class ShellQuotingTests
{
    [Theory]
    [InlineData("", "''")]
    [InlineData("simple", "simple")]
    [InlineData("src/main.swift", "src/main.swift")]
    [InlineData("a=b:c@d,e-f_g.h", "a=b:c@d,e-f_g.h")]
    [InlineData("My Tests", "'My Tests'")]
    [InlineData("HEAD~1", "'HEAD~1'")]
    [InlineData("it's fine", @"'it'\''s fine'")]
    [InlineData("$(whoami)", "'$(whoami)'")]
    [InlineData("a\"b", "'a\"b'")]
    public void Posix(string value, string expected) => Assert.Equal(expected, ShellQuoting.Posix(value));

    [Theory]
    [InlineData("", "''")]
    [InlineData("simple", "simple")]
    [InlineData(@"C:\src\main.cs", @"C:\src\main.cs")]
    [InlineData("it's", "'it''s'")]
    [InlineData("‘curly’", "'‘‘curly’’'")]
    [InlineData("low‚high‛", "'low‚‚high‛‛'")]
    [InlineData("@args", "'@args'")]
    [InlineData("a,b", "'a,b'")]
    [InlineData("-Force", "'-Force'")]
    [InlineData("$env:PATH", "'$env:PATH'")]
    [InlineData("a b", "'a b'")]
    [InlineData("tick`n", "'tick`n'")]
    [InlineData(@"; Remove-Item -Recurse C:\", @"'; Remove-Item -Recurse C:\'")]
    public void PowerShell(string value, string expected) => Assert.Equal(expected, ShellQuoting.PowerShell(value));

    [Theory]
    [InlineData("", "\"\"")]
    [InlineData(@"src\main.cs", @"src\main.cs")]
    [InlineData("a&b", "\"a&b\"")]
    [InlineData("a | b > c", "\"a | b > c\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("100%", "\"100\"^%\"\"")]
    [InlineData("%PATH%", "\"\"^%\"PATH\"^%\"\"")]
    public void Cmd(string value, string expected) => Assert.Equal(expected, ShellQuoting.Cmd(value));

    [Fact]
    public void QuoteDispatchesOnTheShell()
    {
        Assert.Equal("'a b'", ShellQuoting.Quote("a b", ShellKind.Bash));
        Assert.Equal("'a b'", ShellQuoting.Quote("a b", ShellKind.PowerShell));
        Assert.Equal("\"a b\"", ShellQuoting.Quote("a b", ShellKind.Cmd));
    }

    /// <summary>The PowerShell rule the quoting relies on: inside '…' only a doubled quote is special,
    /// so an injected command stays one literal string however it is punctuated.</summary>
    [Fact]
    public void PowerShellQuotedInjectionIsOneStringLiteral()
    {
        var quoted = ShellQuoting.PowerShell("x'; Remove-Item -Recurse C:\\ ; '");
        Assert.StartsWith("'", quoted);
        Assert.EndsWith("'", quoted);
        // Every quote inside is doubled, so none of them can terminate the literal.
        Assert.DoesNotContain("'", quoted[1..^1].Replace("''", ""));
    }
}

/// <summary>New: manifest parsing.</summary>
public sealed class PluginManifestParseTests
{
    [Fact]
    public void SchemaMayBeAJsonString()
    {
        var manifest = PluginLoader.Parse("""
            {"name":"p","version":"2","tools":[{"name":"t","description":"d","command":"c",
              "parameters":"{\"type\":\"object\",\"properties\":{\"q\":{\"type\":\"string\"}}}"}]}
            """);
        Assert.Equal("2", manifest.Version);
        Assert.Equal("""{"type":"object","properties":{"q":{"type":"string"}}}""", manifest.Tools[0].Parameters);
    }

    [Fact]
    public void MissingOrNullSchemaMeansNoArguments()
    {
        var manifest = PluginLoader.Parse("""
            {"name":"p","tools":[{"name":"a","description":"d","command":"c"},
                                 {"name":"b","description":"d","command":"c","parameters":null}]}
            """);
        Assert.All(manifest.Tools, t => Assert.Null(t.Parameters));
        Assert.Equal("""{"type":"object","properties":{}}""", new PluginTool("p", manifest.Tools[0]).Spec.Parameters);
    }

    [Fact]
    public void ApprovalAndTimeoutAreRead()
    {
        var manifest = PluginLoader.Parse("""
            {"name":"p","tools":[{"name":"t","description":"d","command":"c","requiresApproval":false,"timeout":30}]}
            """);
        Assert.False(manifest.Tools[0].RequiresApproval);
        Assert.Equal(30, manifest.Tools[0].Timeout);
    }

    [Fact]
    public void CommentsAndTrailingCommasAreAllowed()
    {
        var manifest = PluginLoader.Parse("""
            {
              // hand-written manifests have comments
              "name": "dotnet",
              "tools": [
                { "name": "dotnet_test", "description": "Run tests.", "command": "dotnet test", /* inline */ },
              ],
            }
            """);
        Assert.Equal("dotnet", manifest.Name);
        Assert.Equal("dotnet_test", Assert.Single(manifest.Tools).Name);
    }

    [Theory]
    [InlineData("""{"tools":[]}""", "missing \"name\"")]
    [InlineData("""{"name":"p"}""", "missing \"tools\" array")]
    [InlineData("""{"name":"p","tools":{}}""", "missing \"tools\" array")]
    [InlineData("""{"name":"p","tools":["x"]}""", "each tool must be an object")]
    [InlineData("""{"name":"p","tools":[{"description":"d","command":"c"}]}""", "a tool is missing \"name\"")]
    [InlineData("""{"name":"p","tools":[{"name":"t","command":"c"}]}""", "tool t is missing \"description\"")]
    [InlineData("""{"name":"p","tools":[{"name":"t","description":"d"}]}""", "tool t is missing \"command\"")]
    [InlineData("""["not","an","object"]""", "must be a JSON object")]
    public void MissingFieldsAreReportedClearly(string json, string message)
    {
        var error = Assert.Throws<FormatException>(() => PluginLoader.Parse(json));
        Assert.Contains(message, error.Message);
    }

    [Fact]
    public void ToolsKeepTheFirstOfDuplicateNames()
    {
        var first = new PluginManifest("one", null, null, [new PluginToolSpec("deploy", "first", null, "a")]);
        var second = new PluginManifest("two", null, null,
            [new PluginToolSpec("deploy", "second", null, "b"), new PluginToolSpec("lint", "l", null, "c")]);
        var tools = PluginLoader.Tools([first, second], []).Cast<PluginTool>().ToList();

        Assert.Equal(["deploy", "lint"], tools.Select(t => t.Name));
        Assert.Equal("one", tools[0].Plugin);
        Assert.Equal("two", tools[1].Plugin);
    }
}

/// <summary>New: loading from disk. The user plugin folder lives under DSH_HOME, which these tests
/// point at a scratch folder (and restore), so the real profile is never read.</summary>
[Collection(SerialStaticState.Name)]
public sealed class PluginLoadTests : IDisposable
{
    private readonly TempDirectory _dir = new("dsh-plugins");
    private readonly string? _previousHome = Environment.GetEnvironmentVariable("DSH_HOME");

    public PluginLoadTests() => Environment.SetEnvironmentVariable("DSH_HOME", _dir["home"]);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("DSH_HOME", _previousHome);
        _dir.Dispose();
    }

    [Fact]
    public void ProjectPluginsWinAndBadFilesAreReported()
    {
        var project = _dir["project"];
        _dir.Write("project/.dsh/plugins/b.json",
            """{"name":"shared","description":"project copy","tools":[{"name":"p_tool","description":"d","command":"echo p"}]}""");
        _dir.Write("project/.dsh/plugins/a.json", "{ not json");
        _dir.Write("home/plugins/shared.json", """{"name":"shared","description":"user copy","tools":[]}""");
        _dir.Write("home/plugins/extra.json", """{"name":"extra","tools":[{"name":"x","description":"d","command":"echo x"}]}""");
        _dir.Write("home/plugins/notes.txt", "not a manifest");

        var (plugins, errors) = PluginLoader.Load(project);

        Assert.Equal(["shared", "extra"], plugins.Select(p => p.Name));
        Assert.Equal("project copy", plugins[0].Description);
        Assert.StartsWith("a.json: ", Assert.Single(errors));
        Assert.Equal([Path.Combine(project, ".dsh", "plugins"), Path.Combine(_dir["home"], "plugins")],
            PluginLoader.Directories(project));
    }

    [Fact]
    public void TheInstalledExampleIsAValidManifest()
    {
        var path = PluginLoader.InstallExample();

        Assert.Equal(Path.Combine(_dir["home"], "plugins", "example.json"), path);
        var manifest = PluginLoader.Parse(File.ReadAllText(path));
        Assert.Equal("example", manifest.Name);
        Assert.False(Assert.Single(manifest.Tools).RequiresApproval);
        Assert.Equal(path, PluginLoader.InstallExample()); // idempotent
    }
}
