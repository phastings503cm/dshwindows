using System.Text.Json.Nodes;

namespace Dsh.Core.Tests;

/// <summary>Ported from the XMLToolCallTests class in HarnessTests.swift.</summary>
public sealed class XmlToolCallTests
{
    [Fact]
    public void FunctionStyleBlock()
    {
        const string text = """
            Sure, reading it now.
            <function=read_file>
            <parameter=file_path>src/main.swift</parameter>
            </function>
            """;
        var call = Assert.Single(XmlToolCalls.Parse(text));
        Assert.Equal("read_file", call.Name);
        Assert.Equal("src/main.swift", call.Arguments["file_path"]);
        // The JSON handed to the engine must read back as the same path.
        Assert.Equal("src/main.swift", JsonArgs.String(call.ArgumentsJson, "file_path"));
    }

    [Fact]
    public void ToolNameStyleBlock()
    {
        const string text = """
            <tool_name>run_shell_command
            <parameter_name>command</parameter_name>
            swift build
            </tool_name>
            """;
        var call = XmlToolCalls.Parse(text)[0];
        Assert.Equal("run_shell_command", call.Name);
        Assert.Equal("swift build", call.Arguments["command"]);
    }

    [Fact]
    public void PlainProseIsNotAToolCall()
    {
        Assert.False(XmlToolCalls.ContainsBlock("I would call read_file here, but I won't."));
        Assert.Empty(XmlToolCalls.Parse("nothing to see"));
    }
}

/// <summary>New: several blocks, argument JSON, and malformed blocks.</summary>
public sealed class XmlToolCallParsingTests
{
    [Fact]
    public void SeveralBlocksAreReturnedInOrder()
    {
        const string text = """
            <function=glob><parameter=pattern>**/*.cs</parameter></function>
            then
            <function=grep><parameter=pattern>TODO</parameter><parameter=include>*.cs</parameter></function>
            """;
        var calls = XmlToolCalls.Parse(text);
        Assert.Equal(["glob", "grep"], calls.Select(c => c.Name));
        Assert.Equal("*.cs", calls[1].Arguments["include"]);
        Assert.True(XmlToolCalls.ContainsBlock(text));
    }

    [Fact]
    public void ArgumentsJsonIsSortedAndEscapesValues()
    {
        var call = new ParsedXmlToolCall("write_file", new Dictionary<string, string>
        {
            ["file_path"] = @"C:\src\a.cs",
            ["content"] = "line \"one\"\nline two",
        });
        var json = JsonNode.Parse(call.ArgumentsJson)!.AsObject();
        Assert.Equal(["content", "file_path"], json.Select(kv => kv.Key));
        Assert.Equal(@"C:\src\a.cs", json["file_path"]!.GetValue<string>());
        Assert.Equal("line \"one\"\nline two", json["content"]!.GetValue<string>());
        Assert.Equal("{}", new ParsedXmlToolCall("exit_plan_mode", new Dictionary<string, string>()).ArgumentsJson);
    }

    [Fact]
    public void UnterminatedOrNamelessBlocksAreIgnored()
    {
        Assert.Empty(XmlToolCalls.Parse("<function=read_file><parameter=file_path>a</parameter>"));
        Assert.Empty(XmlToolCalls.Parse("<function=>x</function>"));
        Assert.Empty(XmlToolCalls.Parse("<tool_name>   </tool_name>"));
        Assert.False(XmlToolCalls.ContainsBlock("<function=read_file> never closed"));
    }

    [Fact]
    public void ParameterValuesAreTrimmedAndMayBeMultiline()
    {
        const string text = "<function=write_file>\n<parameter=content>\nfirst\nsecond\n</parameter>\n</function>";
        Assert.Equal("first\nsecond", XmlToolCalls.Parse(text)[0].Arguments["content"]);
    }
}
