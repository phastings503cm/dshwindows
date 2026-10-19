using System.Text;
using System.Text.Json.Nodes;

namespace Vision.Core;

// MARK: - A server's tool, as one of the model's
//
// Each tool of a connected server is offered as mcp__<server>__<tool> (the names Claude Code uses, so an agent definition
// written for it finds them), described "[MCP server “name”] …", plus mcp__<server>__resources for a server with
// resources. A call asks first unless the chat has full access, the server is trusted with all its tools, or the tool is
// read-only on a server trusted with read-only tools — and, in a Sensitive chat, a server on someone else's machine always
// asks. A local server's tool that isn't read-only may change files: in a git repository the turn's checkpoint works out
// which, as for a shell command. The words are Vision for macOS's (MCPManager.swift).

/// <summary>One tool of one MCP server (or its resources).</summary>
public sealed class McpTool : IToolExecutor
{
    private readonly McpConnection _connection;

    /// <param name="info">The tool; null = the server's resources (list them, or read one by its uri).</param>
    public McpTool(McpConnection connection, McpToolInfo? info)
    {
        _connection = connection;
        Info = info;
        Name = McpNames.ToolName(connection.Config.Name, info?.Name ?? "resources");
        var prefix = $"[MCP server “{connection.Config.Name}”] ";
        Spec = info is null
            ? new ToolSpec(Name, prefix + "List the server's resources, or read one by its uri.",
                """{"type":"object","properties":{"uri":{"type":"string","description":"The resource to read; leave out to list them"}}}""")
            : new ToolSpec(Name, prefix + TextUtil.Prefix(info.Description.Length > 0 ? info.Description : info.Title ?? info.Name, 2_000), info.InputSchema);
    }

    public McpToolInfo? Info { get; }
    public McpServerConfig Server => _connection.Config;
    public string Name { get; }
    public ToolSpec Spec { get; }

    /// <summary>Whether a call asks first (see the top of the file).</summary>
    public bool NeedsApproval(ToolContext context)
    {
        var server = _connection.Config;
        var remote = server.Transport != McpTransportKind.Stdio && !RoutePrivacy.IsPrivateAddress(server.Url);
        if (context.Sensitive && remote) return true;
        if (context.Policy.Preset == PermissionPreset.FullAccess) return false;
        return server.Trust switch
        {
            McpTrust.Always => false,
            McpTrust.ReadOnly => !(Info?.ReadOnly ?? true),
            _ => true,
        };
    }

    public async Task<ToolResult> ExecuteAsync(string arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var server = _connection.Config;
        var args = JsonArgs.Object(arguments);
        if (NeedsApproval(context))
        {
            var shown = args.ToJsonString();
            if (shown.Length > 400) shown = shown[..400] + "…";
            var what = Info is null ? "its resources" : $"its tool “{Info.Name}”";
            var warning = context.Sensitive && server.Transport != McpTransportKind.Stdio && Uri.TryCreate(server.Url, UriKind.Absolute, out var url)
                          && !RoutePrivacy.IsPrivateAddress(server.Url)
                ? $"\nThis chat is Sensitive: what it is given goes to {url.Host}."
                : "";
            var approved = await context.RequestPermission($"mcp-{Guid.NewGuid():N}", Name,
                $"MCP server “{server.Name}” wants to run {what} with {shown}{warning}").ConfigureAwait(false);
            if (!approved) return "Error: the user declined this MCP call. Don't retry it; say what you wanted it for.";
        }
        // A local server's tool that isn't read-only may change files: the turn's checkpoint works out which (git).
        var checkpoint = server.Transport == McpTransportKind.Stdio && !(Info?.ReadOnly ?? true) ? context.Checkpoint : null;
        var baseline = checkpoint?.BeforeShell(context.Workspace);
        try
        {
            if (Info is null) return await _connection.ResourcesAsync(JsonArgs.String(args, "uri"), cancellationToken).ConfigureAwait(false);
            var result = Present(await _connection.CallAsync(Info.Name, args, cancellationToken).ConfigureAwait(false));
            if (baseline is not null) result = result with { Files = checkpoint!.AfterShell(baseline, Name) };
            return result;
        }
        catch (McpException error)
        {
            return $"Error: MCP server “{server.Name}”: {error.Message}";
        }
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
        var output = text.ToString().Trim();
        var isError = result["isError"] is JsonValue flag && flag.TryGetValue<bool>(out var e) && e;
        if (isError) return "Error: " + (output.Length == 0 ? "the tool reported an error." : output);
        return new ToolResult(output.Length == 0 ? "(no output)" : output) { Images = images };
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
