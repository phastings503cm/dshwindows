using System.IO;
using System.Text.Json.Nodes;
using Vision.Core;

namespace Vision.Core.Tests;

/// <summary>The real claude CLI's stream-json captures (Fixtures/ClaudeCode, see its README), line by line.</summary>
public static class ClaudeFixtures
{
    public static string Folder => System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", "ClaudeCode");

    public static IReadOnlyList<string> Lines(string name) =>
        File.ReadAllLines(System.IO.Path.Combine(Folder, name)).Where(l => l.Trim().Length > 0).ToList();

    public static string Text(string name) => File.ReadAllText(System.IO.Path.Combine(Folder, name));

    /// <summary>The session id a capture's result line carries.</summary>
    public static string SessionOf(string name) =>
        Lines(name).Select(l => JsonNode.Parse(l)!).Last(n => (string?)n["type"] == "result")["session_id"]!.GetValue<string>();
}

/// <summary>A stand-in for the claude CLI that replays a capture the way the real one behaves on its pipes: it reads the
/// prompt first, stops at each permission question until an answer with its request id is written, can hold a line
/// back until something is written (an interrupt), and closes stdout only once stdin is closed (or it is killed).</summary>
public sealed class ScriptedClaudeProcess : IClaudeCodeProcess
{
    public sealed record Step(string Line, Func<IReadOnlyList<string>, bool>? WaitFor = null);

    private readonly List<Step> _steps;
    private readonly List<string> _written = [];
    private readonly Lock _lock = new();
    private int _next;
    private bool _killed;
    private bool _inputClosed;
    private bool _started;

    public ScriptedClaudeProcess(IEnumerable<Step> steps, string stderr = "", int exitCode = 0, bool exitWithoutInputClose = false)
    {
        _steps = steps.ToList();
        StderrText = stderr;
        _exitCode = exitCode;
        _exitWithoutInputClose = exitWithoutInputClose;
    }

    /// <summary>Replay a capture: every line as it came, each permission question waiting for its answer.</summary>
    public static ScriptedClaudeProcess Replay(IEnumerable<string> lines, string stderr = "", int exitCode = 0) =>
        new(lines.Select(l => new Step(l)), stderr, exitCode);

    private readonly int _exitCode;
    private readonly bool _exitWithoutInputClose;

    public IReadOnlyList<string> Args { get; set; } = [];
    public string WorkingDirectory { get; set; } = "";
    public ClaudeCodeLaunch? Launch { get; set; }
    public string StderrText { get; }

    public IReadOnlyList<string> Written
    {
        get
        {
            lock (_lock) return _written.ToList();
        }
    }

    public async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        // Like the CLI: nothing happens until the prompt is in.
        await Until(() => Written.Count > 0 || _killed).ConfigureAwait(false);
        _started = true;
        if (_killed) return null;
        if (_next >= _steps.Count)
        {
            if (!_exitWithoutInputClose) await Until(() => _inputClosed || _killed).ConfigureAwait(false);
            return null;
        }
        var step = _steps[_next];
        if (step.WaitFor is { } wait) await Until(() => wait(Written) || _killed).ConfigureAwait(false);
        if (_killed) return null;
        // A permission question is followed by nothing until it is answered.
        if (_next > 0 && JsonNode.Parse(_steps[_next - 1].Line) is JsonObject previous && (string?)previous["type"] == "control_request"
            && (string?)previous["request"]?["subtype"] == "can_use_tool")
        {
            var id = (string)previous["request_id"]!;
            await Until(() => Written.Any(w => w.Contains(id, StringComparison.Ordinal)) || _killed).ConfigureAwait(false);
            if (_killed) return null;
        }
        _next++;
        return step.Line;
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The scripted CLI waited too long.");
            await Task.Delay(5).ConfigureAwait(false);
        }
    }

    public Task WriteLineAsync(string line, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (_inputClosed) throw new IOException("stdin is closed");
            _written.Add(line);
        }
        return Task.CompletedTask;
    }

    public void CloseInput() => _inputClosed = true;
    public bool InputClosed => _inputClosed;
    public string Stderr => StderrText;
    public bool HasExited => _killed || (_started && _next >= _steps.Count && (_inputClosed || _exitWithoutInputClose));
    public int? ExitCode => HasExited ? (_killed ? 143 : _exitCode) : null;
    public bool Killed => _killed;

    public Task WaitForExitAsync(CancellationToken cancellationToken) => Until(() => HasExited);

    public void Kill()
    {
        _killed = true;
        _inputClosed = true;
    }

    public void Dispose() { }

    /// <summary>A starter that hands out these processes in order (recording each one's arguments).</summary>
    public static ClaudeCodeProcessStarter Starter(params ScriptedClaudeProcess[] processes)
    {
        var queue = new Queue<ScriptedClaudeProcess>(processes);
        return (launch, args, cwd) =>
        {
            var process = queue.Dequeue();
            process.Args = args;
            process.WorkingDirectory = cwd;
            process.Launch = launch;
            return process;
        };
    }
}
