using System.Text.Json.Nodes;

namespace Dsh.Core;

// MARK: - Tool set

/// <summary>The background-process tools. The engine gates process_start (on `command`) and
/// process_write (on `input`) through the shell permission policy; reading, stopping and listing are
/// free.</summary>
public static class ProcessTools
{
    public static IReadOnlyList<string> Names { get; } =
    [
        ProcessStartTool.ToolName, ProcessReadTool.ToolName, ProcessWriteTool.ToolName,
        ProcessStopTool.ToolName, ProcessListTool.ToolName,
    ];

    /// <summary>All five, sharing <paramref name="manager"/> (default <see cref="ProcessManager.Shared"/>),
    /// so a process started by the main agent is readable by a subagent.</summary>
    public static IReadOnlyList<IToolExecutor> All(ProcessManager? manager = null)
    {
        var shared = manager ?? ProcessManager.Shared;
        return
        [
            new ProcessStartTool(shared), new ProcessReadTool(shared), new ProcessWriteTool(shared),
            new ProcessStopTool(shared), new ProcessListTool(shared),
        ];
    }

    /// <summary>One shared "tool" cursor for the agent loop: whatever a read (or process_start's
    /// first-output peek) reported is consumed, so no bytes are ever sent to the model twice and the
    /// token budget for a chatty process stays proportional to what it newly prints. Plan mode reads
    /// on its own cursor.</summary>
    internal static string Reader(ToolContext context) =>
        context.Policy.Preset == PermissionPreset.Plan ? "plan" : "tool";

    /// <summary>Keep the tail of long output (~16 KB).</summary>
    internal static string Cap(string s, int limit = 16_000) =>
        s.Length > limit ? $"… [truncated {s.Length - limit} chars] …\n" + TextUtil.Suffix(s, limit) : s;

    internal static string ListLines(ProcessManager manager) =>
        string.Join("\n", manager.All().Select(p => $"- {p.SummaryLine()}"));
}

// MARK: - process_start

public sealed class ProcessStartTool(ProcessManager? manager = null) : IToolExecutor
{
    public const string ToolName = "process_start";
    private readonly ProcessManager _manager = manager ?? ProcessManager.Shared;

    public string Name => ToolName;

    public ToolSpec Spec { get; } = new(ToolName,
        "Start a long-running process on its own pseudo console and keep it alive between tool calls — game engines, " +
        "dev servers, REPLs, watchers, installers that ask questions. The command runs in the same shell as " +
        "run_shell_command, so write it in the same syntax. Returns immediately with a process id; read its output " +
        "with process_read (which can wait for a line matching `until`), type into it with process_write, end it with " +
        "process_stop. Use this instead of run_shell_command whenever the program must keep running while you work, " +
        "or must respond to input.",
        """{"type":"object","properties":{"command":{"type":"string","description":"The command to start"},"cwd":{"type":"string","description":"Working directory (default: project folder)"},"env":{"type":"object","additionalProperties":{"type":"string"},"description":"Extra environment variables"},"cols":{"type":"integer","description":"Terminal width (default 160)"},"rows":{"type":"integer","description":"Terminal height (default 50)"},"description":{"type":"string","description":"Short summary of what this process is for"}},"required":["command"]}""");

    public async Task<ToolResult> ExecuteAsync(string arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var args = JsonArgs.Object(arguments);
        var command = JsonArgs.String(args, "command");
        if (string.IsNullOrWhiteSpace(command)) return "Error: command is required.";

        var environment = new Dictionary<string, string>();
        if (args["env"] is JsonObject env)
        {
            foreach (var (key, value) in env)
            {
                if (value is null) continue;
                environment[key] = value is JsonValue v && v.TryGetValue<string>(out var s) ? s : value.ToJsonString();
            }
        }
        var cwd = context.Policy.Resolve(JsonArgs.String(args, "cwd") is { Length: > 0 } raw ? raw : ".").Path;
        if (!Directory.Exists(cwd)) return $"Error: working directory not found: {cwd}";
        var cols = Math.Clamp(JsonArgs.Int(args, "cols", 160), 20, 400);
        var rows = Math.Clamp(JsonArgs.Int(args, "rows", 50), 8, 200);

        BackgroundProcess process;
        try
        {
            process = _manager.Start(command, cwd, environment, cols, rows, context.Shell, JsonArgs.String(args, "description"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Error: could not start the process ({ex.Message}).";
        }

        // A short settle so the reply carries the first output (a banner, an immediate error); a
        // process that exits sooner returns sooner.
        await process.WaitForExitAsync(_manager.StartSettle, cancellationToken).ConfigureAwait(false);
        var (text, status, running) = process.Drain(ProcessTools.Reader(context));
        var cleaned = PtyText.Clean(text);
        var output = $"Started {process.Id} (pid {process.ProcessId}): {command}";
        if (running)
        {
            output += $"\nStill running. Read output with process_read(id:\"{process.Id}\") — use `until` to wait for a specific line — and send input with process_write.";
        }
        else if (status is { } code)
        {
            output += $"\nIt already exited with code {code}.";
        }
        if (cleaned.Length > 0) output += $"\nFirst output:\n{TextUtil.Prefix(cleaned, 2000)}";
        return output;
    }
}

// MARK: - process_read

public sealed class ProcessReadTool(ProcessManager? manager = null) : IToolExecutor
{
    public const string ToolName = "process_read";
    private readonly ProcessManager _manager = manager ?? ProcessManager.Shared;

    public string Name => ToolName;

    public ToolSpec Spec { get; } = new(ToolName,
        "Read everything a background process printed since your last read (per-call cursor — you never see the same " +
        "bytes twice). With `until`, poll for up to `timeout` seconds until the new output contains that substring — " +
        "the efficient way to wait for \"Godot Engine v\", \"ready to accept connections\", or a \"SCRIPT ERROR\" line " +
        "instead of blindly re-reading. Reports exit status when the process has ended.",
        """{"type":"object","properties":{"id":{"type":"string","description":"Process id from process_start"},"until":{"type":"string","description":"Wait until the new output contains this text"},"timeout":{"type":"integer","description":"Max seconds to wait for `until` (default 20, max 120)"},"all":{"type":"boolean","description":"Also include output other readers already consumed"}},"required":["id"]}""");

    public async Task<ToolResult> ExecuteAsync(string arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var args = JsonArgs.Object(arguments);
        var id = JsonArgs.String(args, "id");
        if (id is null || _manager.Get(id) is not { } process)
        {
            var listed = ProcessTools.ListLines(_manager);
            return "Error: no such process" + (listed.Length == 0 ? "" : $". Live processes:\n{listed}");
        }
        id = process.Id;
        var reader = ProcessTools.Reader(context);

        if (JsonArgs.String(args, "until") is { Length: > 0 } until)
        {
            var timeout = Math.Clamp(JsonArgs.Int(args, "timeout", 20), 1, 120);
            var deadline = DateTimeOffset.UtcNow.AddSeconds(timeout);
            // Start at the reader's cursor so output printed before this call is matched, not skipped.
            var baseline = process.Cursor(reader) ?? process.EndOffset;
            var collected = "";
            while (DateTimeOffset.UtcNow < deadline)
            {
                var (chunk, end, running) = process.TailSince(baseline);
                baseline = end;
                collected += chunk;
                if (PtyText.Clean(collected).Contains(until, StringComparison.Ordinal))
                {
                    process.Consume(reader, baseline);
                    return Finish(process, collected, until);
                }
                if (!running)
                {
                    process.Consume(reader, baseline);
                    return Finish(process, collected, null);
                }
                var remaining = deadline - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero) break;
                await Task.Delay(remaining < _manager.PollInterval ? remaining : _manager.PollInterval, cancellationToken)
                    .ConfigureAwait(false);
            }
            // One last look: the line may have landed during the final sleep.
            var (last, lastEnd, _) = process.TailSince(baseline);
            baseline = lastEnd;
            collected += last;
            var cleaned = PtyText.Clean(collected);
            process.Consume(reader, baseline);
            if (cleaned.Contains(until, StringComparison.Ordinal)) return Finish(process, collected, until);
            var state = process.IsRunning
                ? "It is still running — keep waiting with a longer timeout, or check whether it is stuck at a prompt (process_write may unblock it)."
                : "The process has exited.";
            return $"No line matching \"{until}\" within {timeout}s of {id}.\nOutput seen while waiting:\n{ProcessTools.Cap(cleaned)}\n{state}";
        }

        var raw = JsonArgs.Bool(args, "all", false) ? process.PeekAll() : process.Drain(reader).Text;
        return Finish(process, raw, null);
    }

    private static string Finish(BackgroundProcess process, string raw, string? matched)
    {
        var cleaned = PtyText.Clean(raw);
        var head = new List<string>();
        if (matched is not null) head.Add($"Matched \"{matched}\" from {process.Id}.");
        if (process.ExitCode is { } code) head.Add($"{process.Id} exited with code {code}.");
        var body = cleaned.Length == 0 ? "(no new output)" : ProcessTools.Cap(cleaned);
        return (head.Count == 0 ? "" : string.Join(" ", head) + "\n") + body;
    }
}

// MARK: - process_write

public sealed class ProcessWriteTool(ProcessManager? manager = null) : IToolExecutor
{
    public const string ToolName = "process_write";
    private readonly ProcessManager _manager = manager ?? ProcessManager.Shared;

    public string Name => ToolName;

    // Examples in the parameter descriptions use single quotes: a double quote there has to survive
    // as \" inside the JSON, and upstream once shipped five specs whose JSON broke exactly that way.
    public ToolSpec Spec { get; } = new(ToolName,
        "Type into a background process: answer its prompts, drive a REPL, press keys in a console program. `input` is " +
        "typed verbatim (a newline in it is Enter; set `enter` to press Enter after it), or send named keys with `keys`: " +
        "enter, ctrl-c, ctrl-d, ctrl-z, tab, esc, up, down, left, right, home, end, pgup, pgdn, backspace, delete, " +
        "f1-f12, or ctrl-<letter>. On Windows, end of input for a console program is ctrl-z then enter. Reading what it " +
        "printed back is a separate process_read — pass `wait` to get a short settle before returning.",
        """{"type":"object","properties":{"id":{"type":"string","description":"Process id"},"input":{"type":"string","description":"Text to type"},"keys":{"type":"array","items":{"type":"string"},"description":"Named keys/controls, e.g. ['ctrl-c'] or ['down','enter']"},"enter":{"type":"boolean","description":"Press Enter after input"},"wait":{"type":"integer","description":"Seconds to let it react before returning (default 1)"}},"required":["id"]}""");

    /// <summary>Named keys → the bytes a terminal sends for them. A pseudo console turns these back into
    /// key events, so a Windows console program sees a real Ctrl+C, arrow or Enter.</summary>
    internal static IReadOnlyDictionary<string, string> KeyCodes { get; } = BuildKeyCodes();

    private static Dictionary<string, string> BuildKeyCodes()
    {
        static string Key(TerminalKey key) => System.Text.Encoding.ASCII.GetString(TerminalInput.Encode(key)!);
        var codes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["enter"] = "\r", ["return"] = "\r",
            ["ctrl-c"] = "\u0003", ["ctrl-d"] = "\u0004",
            // Upstream maps ctrl-z to a line feed; the control code is 0x1A (SUB), which is also what
            // a Windows console reads as end of input.
            ["ctrl-z"] = "\u001A",
            ["tab"] = "\t", ["esc"] = "\u001B", ["escape"] = "\u001B",
            ["up"] = Key(TerminalKey.Up), ["down"] = Key(TerminalKey.Down),
            ["right"] = Key(TerminalKey.Right), ["left"] = Key(TerminalKey.Left),
            ["pgup"] = Key(TerminalKey.PageUp), ["pgdn"] = Key(TerminalKey.PageDown),
            ["home"] = Key(TerminalKey.Home), ["end"] = Key(TerminalKey.End),
            ["backspace"] = Key(TerminalKey.Backspace), ["delete"] = Key(TerminalKey.Delete),
            ["insert"] = Key(TerminalKey.Insert),
        };
        TerminalKey[] functionKeys =
        [
            TerminalKey.F1, TerminalKey.F2, TerminalKey.F3, TerminalKey.F4, TerminalKey.F5, TerminalKey.F6,
            TerminalKey.F7, TerminalKey.F8, TerminalKey.F9, TerminalKey.F10, TerminalKey.F11, TerminalKey.F12,
        ];
        for (var i = 0; i < functionKeys.Length; i++) codes[$"f{i + 1}"] = Key(functionKeys[i]);
        return codes;
    }

    /// <summary>The bytes for a key name, including any "ctrl-&lt;letter&gt;" (or "ctrl+x"), or null.</summary>
    internal static string? KeyCode(string name)
    {
        var key = name.Trim();
        if (KeyCodes.TryGetValue(key, out var code)) return code;
        if (key.Length == 6 && (key.StartsWith("ctrl-", StringComparison.OrdinalIgnoreCase) || key.StartsWith("ctrl+", StringComparison.OrdinalIgnoreCase))
            && char.IsAsciiLetter(key[5]) && TerminalInput.ControlCode(key[5]) is { } control)
        {
            return ((char)control).ToString();
        }
        return null;
    }

    public async Task<ToolResult> ExecuteAsync(string arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var args = JsonArgs.Object(arguments);
        var id = JsonArgs.String(args, "id");
        if (id is null || _manager.Get(id) is not { } process)
            return $"Error: no such process '{id ?? "?"}' — check process_list.";
        id = process.Id;
        if (!process.IsRunning)
            return $"Error: {id} already exited (code {process.ExitCode ?? -1}). Start it again with process_start.";

        var text = JsonArgs.String(args, "input") ?? "";
        // Typing a newline presses Enter, as it does when pasting into a terminal; a bare LF means
        // something else to a Windows console.
        var payload = text.Replace("\r\n", "\r").Replace('\n', '\r');
        if (JsonArgs.Bool(args, "enter", false)) payload += "\r";
        var keyNames = new List<string>();
        if (args["keys"] is JsonArray keys)
        {
            foreach (var node in keys)
            {
                if (node is not JsonValue v || !v.TryGetValue<string>(out var key)) continue;
                if (KeyCode(key) is not { } code)
                {
                    var known = string.Join(", ", KeyCodes.Keys.Order(StringComparer.Ordinal));
                    return $"Error: unknown key '{key}'. Known: {known}, ctrl-a … ctrl-z";
                }
                payload += code;
                keyNames.Add(key);
            }
        }
        if (payload.Length == 0) return "Error: nothing to send — give input and/or keys.";
        process.Write(payload);

        // A short settle so the model's next process_read usually sees the reaction. Deliberately does
        // NOT touch the read cursor: the output this input caused belongs to the read, not to this call.
        var wait = Math.Clamp(JsonArgs.Int(args, "wait", 1), 0, 30);
        if (wait > 0) await process.WaitForExitAsync(TimeSpan.FromSeconds(wait), cancellationToken).ConfigureAwait(false);
        var state = process.ExitCode is { } code2 ? $"exited({code2})" : "running";
        var sent = text.Length == 0
            ? (keyNames.Count > 0 ? string.Join("+", keyNames) : "input")
            : $"\"{TextUtil.Prefix(text, 80)}\"";
        return $"Sent {sent} to {id}; it is {state}. Next: process_read(id:\"{id}\") — with `until` if you are waiting for a specific line.";
    }
}

// MARK: - process_stop

public sealed class ProcessStopTool(ProcessManager? manager = null) : IToolExecutor
{
    public const string ToolName = "process_stop";
    private readonly ProcessManager _manager = manager ?? ProcessManager.Shared;

    /// <summary>How long a stop waits to see the exit, so a process_list right after is accurate.</summary>
    internal static TimeSpan ExitWait { get; } = TimeSpan.FromSeconds(3);

    public string Name => ToolName;

    public ToolSpec Spec { get; } = new(ToolName,
        "Stop a background process: Ctrl+C first, then the whole process tree is ended if it ignores that (`force` " +
        "ends it at once). Everything it started goes with it, so a game engine takes its helper processes too.",
        """{"type":"object","properties":{"id":{"type":"string","description":"Process id"},"force":{"type":"boolean","description":"End the process tree immediately instead of trying Ctrl+C first"}},"required":["id"]}""");

    public async Task<ToolResult> ExecuteAsync(string arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var args = JsonArgs.Object(arguments);
        var id = JsonArgs.String(args, "id");
        if (string.IsNullOrWhiteSpace(id)) return "Error: id is required.";
        if (_manager.Get(id) is not { } process) return $"Error: no such process '{id}'.";
        process.Stop(kill: JsonArgs.Bool(args, "force", false));
        await process.WaitForExitAsync(ExitWait, cancellationToken).ConfigureAwait(false);
        return $"Stopped {process.Id}.";
    }
}

// MARK: - process_list

public sealed class ProcessListTool(ProcessManager? manager = null) : IToolExecutor
{
    public const string ToolName = "process_list";
    private readonly ProcessManager _manager = manager ?? ProcessManager.Shared;

    public string Name => ToolName;

    public ToolSpec Spec { get; } = new(ToolName,
        "List background processes: id, state, pid, age, command. Use it to recover ids after a break or in a subagent.",
        """{"type":"object","properties":{}}""");

    public Task<ToolResult> ExecuteAsync(string arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var lines = ProcessTools.ListLines(_manager);
        return Task.FromResult<ToolResult>(lines.Length == 0 ? "No background processes." : $"Background processes:\n{lines}");
    }
}
