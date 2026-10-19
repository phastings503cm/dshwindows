using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Vision.Core;

// MARK: - MCP tools as the model sees them
//
// Each tool of a connected server is offered as mcp__<server>__<tool> (the names Claude Code uses, so an agent definition
// written for it finds them), described with the server's name, and run through the normal permission prompt: the first use
// of each tool in a chat asks (unless the server is set to run its tools without asking); the person's answer holds for
// the rest of that chat.

/// <summary>The MCP tools a chat has allowed (its "Allow" answers), kept across engine rebuilds.</summary>
public sealed class McpGrants
{
    private readonly HashSet<string> _allowed = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    public bool IsAllowed(string tool)
    {
        lock (_lock) return _allowed.Contains(tool);
    }

    public void Allow(string tool)
    {
        lock (_lock) _allowed.Add(tool);
    }
}

/// <summary>One tool of one MCP server.</summary>
public sealed class McpTool : IToolExecutor
{
    public const string Prefix = "mcp__";
    /// <summary>OpenAI-style tool names are at most 64 characters of letters, digits, '_' and '-'.</summary>
    public const int MaxNameLength = 64;

    private readonly McpConnection _connection;
    private readonly McpGrants _grants;

    public McpTool(McpConnection connection, McpToolInfo info, McpGrants grants)
    {
        _connection = connection;
        Info = info;
        _grants = grants;
        Name = NameFor(connection.Config.Id, info.Name);
        var title = info.Title is { Length: > 0 } t && t != info.Name ? $"{t}. " : "";
        Spec = new ToolSpec(Name, $"{title}{info.Description}".Trim() + $" (MCP server “{connection.Config.Name}”)", info.InputSchema);
    }

    public McpToolInfo Info { get; }
    public string Server => _connection.Config.Id;
    public string Name { get; }
    public ToolSpec Spec { get; }

    /// <summary>mcp__server__tool, made safe for every model API: other characters become '_', and a name that would pass 64
    /// characters is cut and given a short hash of the whole so two long names stay apart.</summary>
    public static string NameFor(string server, string tool)
    {
        static string Clean(string s) => new(s.Select(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '_').ToArray());
        var name = Prefix + Clean(server) + "__" + Clean(tool);
        if (name.Length <= MaxNameLength) return name;
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(name)))[..8];
        return name[..(MaxNameLength - 9)] + "_" + hash;
    }

    public async Task<ToolResult> ExecuteAsync(string arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var args = JsonArgs.Object(arguments);
        var config = _connection.Config;
        // In a Sensitive chat a server on someone else's machine always asks first (once per tool per chat), whatever its setting:
        // what the tool is given leaves this PC.
        var remote = config.Transport != McpTransportKind.Stdio && !RoutePrivacy.IsPrivateAddress(config.Url);
        if ((config.AskBeforeUse || (context.Sensitive && remote)) && !_grants.IsAllowed(Name))
        {
            var shown = args.Count == 0 ? "(no arguments)" : TextUtil.Prefix(args.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }), 800);
            var warning = context.Sensitive && remote && Uri.TryCreate(config.Url, UriKind.Absolute, out var url)
                ? $"\nThis chat is Sensitive: what {Info.Name} is given goes to {url.Host}."
                : "";
            var approved = await context.RequestPermission($"mcp-{Guid.NewGuid():N}", Name,
                $"MCP server “{config.Name}” wants to run {Info.Name}:\n{shown}{warning}\nAllowing it lets {Info.Name} run for the rest of this chat.").ConfigureAwait(false);
            if (!approved) return "Error: the user declined to run this MCP tool.";
            _grants.Allow(Name);
        }
        JsonObject result;
        try
        {
            result = await _connection.CallAsync(Info.Name, args, cancellationToken).ConfigureAwait(false);
        }
        catch (McpException error)
        {
            return $"Error: MCP server “{_connection.Config.Name}”: {error.Message}";
        }
        return Present(result);
    }

    /// <summary>What the model is told: the text content (a resource's text, a link's address), pictures as images, the
    /// structured content when there is no text. "Error: …" when the server flagged the result as an error.</summary>
    public static ToolResult Present(JsonObject result)
    {
        var text = new StringBuilder();
        var images = new List<MessageAttachment>();
        var index = 0;
        foreach (var item in (result["content"] as JsonArray ?? []).OfType<JsonObject>())
        {
            index++;
            switch (McpSession.Text(item, "type"))
            {
                case "text":
                    Line(text, McpSession.Text(item, "text") ?? "");
                    break;
                case "image":
                    if (McpSession.Text(item, "data") is { } data && TryBase64(data) is { } bytes)
                    {
                        var extension = (McpSession.Text(item, "mimeType") ?? "image/png").Split('/').LastOrDefault() ?? "png";
                        images.Add(new MessageAttachment(AttachmentKind.Image, $"mcp-image-{index}.{extension}", bytes));
                        Line(text, $"[image {index}: {McpSession.Text(item, "mimeType") ?? "image"}]");
                    }
                    break;
                case "audio":
                    Line(text, $"[audio {index} ({McpSession.Text(item, "mimeType") ?? "audio"}) — not shown]");
                    break;
                case "resource":
                    if (item["resource"] is JsonObject resource)
                    {
                        var uri = McpSession.Text(resource, "uri") ?? "";
                        if (McpSession.Text(resource, "text") is { } body) Line(text, $"[resource {uri}]\n{body}");
                        else Line(text, $"[resource {uri} ({McpSession.Text(resource, "mimeType") ?? "binary"}) — not text]");
                    }
                    break;
                case "resource_link":
                    Line(text, $"[link: {McpSession.Text(item, "name") ?? ""} {McpSession.Text(item, "uri") ?? ""}]".Replace("  ", " "));
                    break;
            }
        }
        if (text.Length == 0 && result["structuredContent"] is JsonNode structured) text.Append(structured.ToJsonString());
        var output = text.Length == 0 ? "(no output)" : text.ToString().TrimEnd();
        var isError = result["isError"] is JsonValue flag && flag.TryGetValue<bool>(out var e) && e;
        if (isError) output = "Error: " + output;
        return new ToolResult(output) { Images = images };
    }

    private static void Line(StringBuilder text, string line)
    {
        if (text.Length > 0) text.Append('\n');
        text.Append(line);
    }

    private static byte[]? TryBase64(string data)
    {
        try
        {
            return Convert.FromBase64String(data);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
