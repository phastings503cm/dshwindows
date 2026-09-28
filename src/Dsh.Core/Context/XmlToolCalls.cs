using System.Text.Json;

namespace Dsh.Core;

// MARK: - XML tool-call parser (Qwen / DeepSeek / Cline compatibility)
//
// Not every OpenAI-compatible backend (Ollama, LM Studio, some vLLM + Qwen setups) returns native
// tool_calls. Many Qwen-class models instead emit tool calls inline in the text using an XML
// convention such as:
//
//     <function=read_file>
//       <parameter=file_path>src/main.cs</parameter>
//     </function>
//
// or the Cline style:
//
//     <tool_name>read_file</tool_name> … <parameter_name>file_path</parameter_name> src/main.cs
//
// OpenAiClient runs the completed assistant text through this parser whenever the stream finished
// with zero native tool_calls but the text contains a recognizable block.

/// <summary>One tool call recovered from text.</summary>
public sealed record ParsedXmlToolCall(string Name, IReadOnlyDictionary<string, string> Arguments)
{
    /// <summary>JSON object of the arguments (keys sorted), ready to hand to a <see cref="ToolCall"/>.</summary>
    public string ArgumentsJson
    {
        get
        {
            if (Arguments.Count == 0) return "{}";
            var sorted = new SortedDictionary<string, string>(Arguments.ToDictionary(kv => kv.Key, kv => kv.Value), StringComparer.Ordinal);
            return JsonSerializer.Serialize(sorted);
        }
    }
}

public static class XmlToolCalls
{
    /// <summary>Every tool block in <paramref name="text"/>; empty when nothing matches. Both the
    /// &lt;function=name&gt; and the &lt;tool_name&gt; conventions are understood.</summary>
    public static IReadOnlyList<ParsedXmlToolCall> Parse(string text)
    {
        var result = new List<ParsedXmlToolCall>();
        result.AddRange(ParseFunctionStyle(text));
        result.AddRange(ParseToolNameStyle(text));
        return result;
    }

    /// <summary>Quick check: does <paramref name="text"/> carry at least one recognizable tool block?</summary>
    public static bool ContainsBlock(string text) =>
        (text.Contains("<function=", StringComparison.Ordinal) || text.Contains("<tool_name>", StringComparison.Ordinal))
        && Parse(text).Count > 0;

    // MARK: <function=name> / <parameter=key>

    private static IEnumerable<ParsedXmlToolCall> ParseFunctionStyle(string text)
    {
        const string openPrefix = "<function=";
        const string paramOpen = "<parameter=";
        const string paramClose = "</parameter>";
        const string blockClose = "</function>";
        var searchFrom = 0;
        while (true)
        {
            var open = text.IndexOf(openPrefix, searchFrom, StringComparison.Ordinal);
            if (open < 0) yield break;
            var nameStart = open + openPrefix.Length;
            var gt = text.IndexOf('>', nameStart);
            if (gt < 0) { searchFrom = nameStart; continue; }
            var name = text[nameStart..gt].Trim();
            var close = name.Length == 0 ? -1 : text.IndexOf(blockClose, gt, StringComparison.Ordinal);
            if (close < 0) { searchFrom = nameStart; continue; }
            var body = text[(gt + 1)..close];
            yield return new ParsedXmlToolCall(name, ExtractParams(body, paramOpen, paramClose));
            searchFrom = close + blockClose.Length;
        }
    }

    private static Dictionary<string, string> ExtractParams(string body, string paramOpen, string paramClose)
    {
        var output = new Dictionary<string, string>();
        var searchFrom = 0;
        while (true)
        {
            var open = body.IndexOf(paramOpen, searchFrom, StringComparison.Ordinal);
            if (open < 0) break;
            var keyStart = open + paramOpen.Length;
            var gt = body.IndexOf('>', keyStart);
            if (gt < 0) break;
            var key = body[keyStart..gt].Trim();
            var close = key.Length == 0 ? -1 : body.IndexOf(paramClose, gt, StringComparison.Ordinal);
            if (close < 0) { searchFrom = keyStart; continue; }
            output[key] = body[(gt + 1)..close].Trim();
            searchFrom = close + paramClose.Length;
        }
        return output;
    }

    // MARK: <tool_name>NAME</tool_name> / <parameter_name>KEY</parameter_name>VALUE

    private static IEnumerable<ParsedXmlToolCall> ParseToolNameStyle(string text)
    {
        const string blockOpen = "<tool_name>";
        const string blockClose = "</tool_name>";
        const string paramOpen = "<parameter_name>";
        const string paramClose = "</parameter_name>";
        var searchFrom = 0;
        while (true)
        {
            var open = text.IndexOf(blockOpen, searchFrom, StringComparison.Ordinal);
            if (open < 0) yield break;
            var bodyStart = open + blockOpen.Length;
            var close = text.IndexOf(blockClose, bodyStart, StringComparison.Ordinal);
            if (close < 0) yield break;
            var body = text[bodyStart..close];
            // Tool name = the first line before the first <parameter_name>.
            var nameEnd = body.IndexOf(paramOpen, StringComparison.Ordinal);
            if (nameEnd < 0) nameEnd = body.Length;
            var name = body[..nameEnd].Trim();
            var newline = name.IndexOf('\n');
            if (newline >= 0) name = name[..newline].Trim();
            if (name.Length == 0) { searchFrom = close + blockClose.Length; continue; }

            var args = new Dictionary<string, string>();
            // Keys come from <parameter_name>K</parameter_name>; the value is the text after that, up
            // to the next <parameter_name> (or the block end).
            var argsFrom = nameEnd;
            while (true)
            {
                var pOpen = body.IndexOf(paramOpen, argsFrom, StringComparison.Ordinal);
                if (pOpen < 0) break;
                var keyStart = pOpen + paramOpen.Length;
                var pClose = body.IndexOf(paramClose, keyStart, StringComparison.Ordinal);
                if (pClose < 0) break;
                var key = body[keyStart..pClose].Trim();
                var valueStart = pClose + paramClose.Length;
                var valueEnd = body.IndexOf(paramOpen, valueStart, StringComparison.Ordinal);
                if (valueEnd < 0) valueEnd = body.Length;
                if (key.Length > 0) args[key] = body[valueStart..valueEnd].Trim();
                argsFrom = valueEnd;
            }
            yield return new ParsedXmlToolCall(name, args);
            searchFrom = close + blockClose.Length;
        }
    }
}
