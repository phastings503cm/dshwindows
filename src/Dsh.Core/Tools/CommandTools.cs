using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Dsh.Core;

// MARK: - run_shell_command

public sealed class RunShellCommandTool : IToolExecutor
{
    public const string ToolName = "run_shell_command";
    public string Name => ToolName;

    /// <summary>The spec names the shell the command will run in, so the model writes the right syntax.</summary>
    public ToolSpec Spec { get; }

    public RunShellCommandTool(AgentShell? shell = null) => Spec = SpecFor(shell ?? AgentShell.Default);

    public static ToolSpec SpecFor(AgentShell shell) => new(ToolName,
        $"Run a command in the project folder with {shell.DisplayName}. {shell.SyntaxHint} " +
        "stdout and stderr are captured and returned (truncated to ~16 KB). Nothing can be typed into the command: " +
        "stdin is closed, so pass flags that answer prompts (e.g. -y, --yes, -Force). A command that prints a prompt " +
        "and then goes silent is stopped after ~45 s, and its output includes the prompt it was stuck on. " +
        "Long-running commands get a timeout (default 120 s, max 600 s); quiet long-running builds run to the full timeout.",
        """{"type":"object","properties":{"command":{"type":"string","description":"The command to run"},"timeout":{"type":"integer","description":"Timeout in seconds (default 120)"},"description":{"type":"string","description":"Short summary of what the command does"}},"required":["command"]}""");

    public async Task<ToolResult> ExecuteAsync(string arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var args = JsonArgs.Object(arguments);
        var command = JsonArgs.String(args, "command");
        if (string.IsNullOrWhiteSpace(command)) return "Error: command is required.";
        var timeoutSec = Math.Clamp(JsonArgs.Int(args, "timeout", 120), 5, 600);
        var timeout = TimeSpan.FromSeconds(timeoutSec);

        var result = await ShellRunner.RunAsync(command, context.Workspace, context.Shell, timeout,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var text = result.Output.Trim();

        if (result.TimedOut)
        {
            var idle = (int)ShellRunner.IdleLimit(timeout).TotalSeconds;
            var note = text.Length == 0
                ? $"Command timed out after {timeoutSec}s (it produced no output)."
                : $"Stopped after {timeoutSec}s limit — the command appears to be waiting for interactive input (silent for ~{idle}s). Rerun with flags that answer the prompt, or tell the user what input it needs.";
            return $"{note}\n{TerminalText(text)}";
        }
        if (result.ExitCode == -1 && text.StartsWith("Could not start", StringComparison.Ordinal))
            return $"Error: {text}";
        if (result.ExitCode == 0) return text.Length == 0 ? "(no output)" : TerminalText(text);
        return $"Command exited with code {result.ExitCode}.\n{TerminalText(text)}";
    }

    /// <summary>Keep the tail of long output (~16 KB).</summary>
    private static string TerminalText(string s, int limit = 16_000) =>
        s.Length <= limit ? s : $"… [truncated {s.Length - limit} chars] …\n" + TextUtil.Suffix(s, limit);
}

// MARK: - web_fetch

public sealed partial class WebFetchTool : IToolExecutor
{
    public const string ToolName = "web_fetch";
    public string Name => ToolName;
    public ToolSpec Spec { get; } = new(ToolName,
        "Fetch a URL and return its content as text. HTML is converted to readable text (scripts/styles stripped, tags removed). Useful for reading docs or API responses.",
        """{"type":"object","properties":{"url":{"type":"string","description":"Absolute http(s) URL"},"description":{"type":"string"}},"required":["url"]}""");

    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        ConnectTimeout = TimeSpan.FromSeconds(15),
    })
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    public async Task<ToolResult> ExecuteAsync(string arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var raw = JsonArgs.String(arguments, "url");
        if (raw is null || !Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https"))
            return "Error: a valid http(s) url is required.";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", $"Mozilla/5.0 (Windows NT 10.0; Win64; x64) DSH/{AppInfo.Version}");
        try
        {
            using var response = await Http.SendAsync(req, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return $"Error: HTTP {(int)response.StatusCode} for {url}.";
            var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var text = contentType.Contains("html") || contentType.Contains("xml") ? HtmlToText(body) : body;
            var cut = text.Length > 24_000 ? TextUtil.Prefix(text, 24_000) + "\n\n… [truncated]" : text;
            return $"URL: {url}\n\n{cut}";
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return $"Error: fetch failed: no answer from {url.Host} within 30 s.";
        }
        catch (HttpRequestException ex)
        {
            return $"Error: fetch failed: {ex.Message}";
        }
    }

    /// <summary>A quick-and-dirty HTML → text converter: strip script/style/head/nav/footer, block-level
    /// tags become newlines, all other tags are removed, entities decoded.</summary>
    public static string HtmlToText(string html)
    {
        var s = JunkBlocks().Replace(html, " ");
        s = BlockBreaks().Replace(s, "\n");
        s = Tags().Replace(s, " ");
        s = WebUtility.HtmlDecode(s).Replace((char)0x00A0, ' ');
        s = Spaces().Replace(s, " ");
        s = BlankLines().Replace(s, "\n\n");
        return s.Trim();
    }

    [GeneratedRegex(@"<script.*?</script>|<style.*?</style>|<head.*?</head>|<nav.*?</nav>|<footer.*?</footer>|<!--.*?-->",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex JunkBlocks();

    [GeneratedRegex(@"<(br|/p|/div|/h[1-6]|/li|/tr|/pre)[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockBreaks();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"[ \t]{2,}")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"\n\s*\n(\s*\n)+")]
    private static partial Regex BlankLines();
}

// MARK: - todo_write

public sealed class TodoWriteTool : IToolExecutor
{
    public const string ToolName = "todo_write";
    public string Name => ToolName;
    public ToolSpec Spec { get; } = new(ToolName,
        "Create or replace the structured task list for the current session. Items: {content, status: pending|in_progress|completed}. Use for multi-step work so progress is visible to the user.",
        """{"type":"object","properties":{"todos":{"type":"array","items":{"type":"object","properties":{"content":{"type":"string"},"status":{"type":"string","enum":["pending","in_progress","completed"]}},"required":["content","status"]},"description":"The complete updated task list"}},"required":["todos"]}""");

    public Task<ToolResult> ExecuteAsync(string arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var args = JsonArgs.Object(arguments);
        JsonArray? items = args["todos"] as JsonArray;
        // Some models send the list as a JSON string.
        if (items is null && JsonArgs.String(args, "todos") is { } encoded)
        {
            try { items = JsonNode.Parse(encoded) as JsonArray; } catch (System.Text.Json.JsonException) { }
        }
        if (items is null) return Task.FromResult<ToolResult>("Error: 'todos' array required.");

        var parsed = new List<TodoItem>();
        var index = 0;
        foreach (var node in items)
        {
            index++;
            if (node is not JsonObject item) continue;
            var content = JsonArgs.String(item, "content");
            if (string.IsNullOrEmpty(content)) continue;
            var status = JsonArgs.String(item, "status")?.ToLowerInvariant() switch
            {
                "in_progress" or "inprogress" => TodoStatus.InProgress,
                "completed" or "done" => TodoStatus.Completed,
                _ => TodoStatus.Pending,
            };
            parsed.Add(new TodoItem(index.ToString(), content, status));
        }
        var summary = string.Join("\n", parsed.Select(t => t.Status switch
        {
            TodoStatus.InProgress => "◐ ",
            TodoStatus.Completed => "✓ ",
            _ => "☐ ",
        } + t.Content));
        return Task.FromResult(new ToolResult(summary.Length == 0 ? "Task list cleared." : $"Task list updated:\n{summary}")
        {
            Todos = parsed,
        });
    }
}
