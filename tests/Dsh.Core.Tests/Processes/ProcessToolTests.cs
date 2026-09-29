using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Dsh.Core.Tests;

/// <summary>The background-process tools against a scripted backend: the logic of
/// BackgroundProcessTests in MachineTests.swift (cursor discipline, `until` waits, ring bounds, exit
/// codes, list/stop), made deterministic. The same scenarios against real processes are in
/// <see cref="ProcessLiveTests"/>.</summary>
public sealed partial class ProcessToolTests : IDisposable
{
    private readonly ToolTestContext _tools = new();
    private readonly FakeProcessBackend _backend = new();
    private readonly ProcessManager _manager;

    public ProcessToolTests()
    {
        _manager = new ProcessManager(_backend)
        {
            StartSettle = TimeSpan.FromMilliseconds(30),
            PollInterval = TimeSpan.FromMilliseconds(10),
        };
    }

    public void Dispose() => _tools.Dispose();

    private Task<string> Start(object args) => _tools.Output(new ProcessStartTool(_manager), args);
    private Task<string> Read(object args) => _tools.Output(new ProcessReadTool(_manager), args);
    private Task<string> Write(object args) => _tools.Output(new ProcessWriteTool(_manager), args);
    private Task<string> Stop(object args) => _tools.Output(new ProcessStopTool(_manager), args);
    private Task<string> List() => _tools.Output(new ProcessListTool(_manager), "{}");

    /// <summary>The process id ("p1") out of tool chatter like "Started p2 (pid 999)".</summary>
    internal static string FirstId(string output)
    {
        var match = ProcessIdPattern().Match(output);
        Assert.True(match.Success, $"no process id in: {output}");
        return match.Value;
    }

    [GeneratedRegex(@"\bp[0-9]+\b")]
    private static partial Regex ProcessIdPattern();

    // MARK: - process_start

    [Fact]
    public async Task StartReportsFirstOutputAndExitCode()
    {
        _backend.OnStart = p =>
        {
            p.Emit("hello-from-bg\r\n");
            p.Exit(3);
        };
        var start = await Start(new { command = "echo hello-from-bg; exit 3" });
        Assert.StartsWith("Started p1 (pid 4242): echo hello-from-bg; exit 3", start);
        Assert.Contains("hello-from-bg", start);
        Assert.Contains("It already exited with code 3.", start);
        Assert.Contains("First output:", start);

        // A second read must not repeat the bytes the start peek consumed.
        var read = await Read(new { id = FirstId(start) });
        Assert.DoesNotContain("hello-from-bg", read);
        Assert.Equal("p1 exited with code 3.\n(no new output)", read);
    }

    [Fact]
    public async Task StartOfALongRunningProcessPointsAtProcessRead()
    {
        var start = await Start(new { command = "serve" });
        Assert.Contains("Still running. Read output with process_read(id:\"p1\") — use `until` to wait for a specific line — and send input with process_write.", start);
        Assert.DoesNotContain("First output", start);
        Assert.True(_manager.Get("p1")!.IsRunning);
    }

    [Fact]
    public async Task StartPassesTheLaunchThrough()
    {
        _tools.Root.Write("sub/keep.txt", "x");
        var shell = new AgentShell(ShellKind.Cmd, "cmd.exe", "cmd");
        var context = _tools.Context with { Shell = shell };
        var args = Args.Json(new
        {
            command = "npm run dev",
            cwd = "sub",
            env = new Dictionary<string, object> { ["PORT"] = 5173, ["MODE"] = "dev" },
            cols = 5000,
            rows = 1,
            description = "vite dev server",
        });
        var result = await new ProcessStartTool(_manager).ExecuteAsync(args, context, CancellationToken.None);
        Assert.StartsWith("Started p1", result.Output);

        var launch = _backend.Last.Launch;
        Assert.Equal("npm run dev", launch.Command);
        Assert.Equal(_tools.Root["sub"], launch.WorkingDirectory);
        Assert.Equal("5173", launch.Environment["PORT"]);
        Assert.Equal("dev", launch.Environment["MODE"]);
        Assert.Equal(400, launch.Cols); // clamped
        Assert.Equal(8, launch.Rows);
        Assert.Same(shell, launch.Shell);
        Assert.Equal("vite dev server", _manager.Get("p1")!.Description);
    }

    [Fact]
    public async Task StartDefaultsToTheProjectFolderAndAStandardSize()
    {
        await Start(new { command = "x" });
        var launch = _backend.Last.Launch;
        Assert.Equal(_tools.Root.Path, launch.WorkingDirectory);
        Assert.Equal(160, launch.Cols);
        Assert.Equal(50, launch.Rows);
        Assert.Same(_tools.Context.Shell, launch.Shell);
    }

    [Fact]
    public async Task StartErrors()
    {
        Assert.Equal("Error: command is required.", await Start(new { command = " " }));
        Assert.StartsWith("Error: working directory not found:", await Start(new { command = "x", cwd = "nope/missing" }));
        _backend.FailWith = new InvalidOperationException("no pseudo console");
        Assert.Equal("Error: could not start the process (no pseudo console).", await Start(new { command = "x" }));
        Assert.Empty(_manager.All());
    }

    // MARK: - process_read

    [Fact]
    public async Task UntilMatchesALinePrintedLate()
    {
        var id = FirstId(await Start(new { command = "sleep 0.9; echo READY-LATER" }));
        var process = _backend.Last;
        var read = Read(new { id, until = "READY-LATER", timeout = 10 });
        await Task.Delay(100);
        process.Emit("noise\r\nREADY-");
        await Task.Delay(50);
        process.Emit("LATER\r\n");
        var output = await read;
        Assert.StartsWith("Matched \"READY-LATER\" from p1.\n", output);
        Assert.Contains("noise\nREADY-LATER", output);

        // Nothing new since: the follow-up read is empty, proving the cursor advanced through the
        // matched bytes.
        Assert.Equal("(no new output)", await Read(new { id }));
    }

    [Fact]
    public async Task UntilSeesOutputPrintedBeforeTheCall()
    {
        var id = FirstId(await Start(new { command = "x" }));
        _backend.Last.Emit("server listening on :8080\r\n");
        var read = await Read(new { id, until = "listening", timeout = 1 });
        Assert.StartsWith("Matched \"listening\"", read);
        Assert.Contains("server listening on :8080", read);
    }

    [Fact]
    public async Task UntilTimeoutStillReportsAndConsumes()
    {
        _backend.OnStart = p => p.Emit("NEVER-COMING-OUT\r\n");
        var id = FirstId(await Start(new { command = "echo NEVER-COMING-OUT; sleep 30" }));
        _backend.Last.Emit("more noise\r\n");
        var read = await Read(new { id, until = "NOPE", timeout = 1 });
        Assert.StartsWith("No line matching \"NOPE\" within 1s of p1.\nOutput seen while waiting:\nmore noise", read);
        Assert.Contains("It is still running — keep waiting with a longer timeout", read);
        Assert.DoesNotContain("NEVER-COMING-OUT", read); // the start peek already reported that

        var again = await Read(new { id });
        Assert.Equal("(no new output)", again); // the timed-out wait already reported its bytes
    }

    [Fact]
    public async Task UntilReturnsAsSoonAsTheProcessExits()
    {
        var id = FirstId(await Start(new { command = "x" }));
        var process = _backend.Last;
        var read = Read(new { id, until = "never", timeout = 60 });
        await Task.Delay(50);
        process.Emit("fatal: bad config\r\n");
        process.Exit(2);
        var output = await read.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("p1 exited with code 2.\nfatal: bad config\n", output);
    }

    [Fact]
    public async Task ReadersHaveTheirOwnCursors()
    {
        var id = FirstId(await Start(new { command = "x" }));
        _backend.Last.Emit("line one\r\n");
        Assert.Equal("line one\n", await Read(new { id }));
        Assert.Equal("(no new output)", await Read(new { id }));

        // Plan mode reads on its own cursor, so it still sees everything.
        var plan = _tools.Context with { Policy = new PermissionPolicy(PermissionPreset.Plan, _tools.Root.Path) };
        var planRead = await new ProcessReadTool(_manager).ExecuteAsync(Args.Json(new { id }), plan, CancellationToken.None);
        Assert.Equal("line one\n", planRead.Output);

        // `all` shows everything retained without moving any cursor.
        _backend.Last.Emit("line two\r\n");
        Assert.Equal("line one\nline two\n", await Read(new { id, all = true }));
        Assert.Equal("line two\n", await Read(new { id }));
    }

    [Fact]
    public async Task ReadOfAnUnknownIdListsTheLiveOnes()
    {
        Assert.Equal("Error: no such process", await Read(new { id = "p9" }));
        await Start(new { command = "tail -f log" });
        var read = await Read(new { id = "p9" });
        Assert.StartsWith("Error: no such process. Live processes:\n- p1  running(pid 4242)", read);
        Assert.Contains("tail -f log", read);
    }

    [Fact]
    public async Task LongOutputKeepsTheTail()
    {
        var id = FirstId(await Start(new { command = "x" }));
        _backend.Last.Emit(new string('a', 20_000) + "THE-END");
        var read = await Read(new { id });
        Assert.StartsWith("… [truncated", read);
        Assert.EndsWith("THE-END", read);
        Assert.True(read.Length < 16_100);
    }

    [Fact]
    public async Task ASplitUtf8CharacterIsNeverReportedAsGarbage()
    {
        var id = FirstId(await Start(new { command = "x" }));
        var bytes = Encoding.UTF8.GetBytes("café ☕");
        _backend.Last.Emit(bytes[..4]); // "caf" + the first byte of "é"
        Assert.Equal("caf", await Read(new { id }));
        _backend.Last.Emit(bytes[4..]);
        Assert.Equal("é ☕", await Read(new { id }));
    }

    // MARK: - Ring buffer

    [Fact]
    public async Task RingBufferBoundsMemoryAndPeekAll()
    {
        var manager = new ProcessManager(_backend) { RingLimit = 1_000, StartSettle = TimeSpan.Zero };
        var id = FirstId(await _tools.Output(new ProcessStartTool(manager), new { command = "yes | head -c 5000" }));
        var process = manager.Get(id)!;
        for (var i = 0; i < 50; i++) _backend.Last.Emit($"{i:D3}:" + new string('x', 95) + "\n"); // 100 bytes each
        Assert.Equal(1_000, process.RetainedBytes);
        Assert.Equal(5_000, process.EndOffset);

        var all = await _tools.Output(new ProcessReadTool(manager), new { id, all = true });
        Assert.StartsWith("040:", all);
        Assert.Contains("049:", all);
        Assert.DoesNotContain("039:", all);

        // A reader whose cursor fell behind the ring gets what is still retained, then nothing more.
        var read = await _tools.Output(new ProcessReadTool(manager), new { id });
        Assert.StartsWith("040:", read);
        Assert.Equal("(no new output)", await _tools.Output(new ProcessReadTool(manager), new { id }));
    }

    [Fact]
    public void ByteRingWrapsGrowsAndClamps()
    {
        var ring = new ByteRing(10);
        ring.Append("abcd"u8);
        Assert.Equal((4, 4L, 0L), (ring.Count, ring.Written, ring.Dropped));
        ring.Append("efghij"u8);
        Assert.Equal("abcdefghij", Encoding.ASCII.GetString(ring.Copy(0, 10)));
        ring.Append("klm"u8);
        Assert.Equal((10, 13L, 3L), (ring.Count, ring.Written, ring.Dropped));
        Assert.Equal("defghijklm", Encoding.ASCII.GetString(ring.Copy(0, 100)));
        Assert.Equal("ijk", Encoding.ASCII.GetString(ring.Copy(8, 11)));
        Assert.Equal((byte)'m', ring.At(12));

        // A chunk bigger than the ring keeps only its tail.
        ring.Append("0123456789ABCDEF"u8);
        Assert.Equal((10, 29L, 19L), (ring.Count, ring.Written, ring.Dropped));
        Assert.Equal("6789ABCDEF", Encoding.ASCII.GetString(ring.Copy(0, 29)));
        Assert.Empty(ring.Copy(29, 40));

        // Growth from a small start keeps order across a wrap.
        var big = new ByteRing(100_000);
        var expected = new StringBuilder();
        for (var i = 0; i < 30_000; i++)
        {
            var piece = $"<{i}>";
            expected.Append(piece);
            big.Append(Encoding.ASCII.GetBytes(piece));
        }
        var text = expected.ToString();
        Assert.Equal(text[^100_000..], Encoding.ASCII.GetString(big.Copy(0, big.Written)));
    }

    // MARK: - process_write

    [Fact]
    public async Task WriteTypesInputAndPressesEnter()
    {
        var id = FirstId(await Start(new { command = "cat" }));
        _backend.Last.Echo = true;
        var write = await Write(new { id, input = "ping from the harness", enter = true, wait = 0 });
        Assert.Equal("Sent \"ping from the harness\" to p1; it is running. Next: process_read(id:\"p1\") — with `until` if you are waiting for a specific line.", write);
        Assert.Equal("ping from the harness\r", _backend.Last.Written);

        // The write did not touch the read cursor: the echo belongs to the next read.
        var read = await Read(new { id, until = "ping from the harness", timeout = 5 });
        Assert.Contains("ping from the harness", read);
    }

    [Fact]
    public async Task NewlinesInInputArePressedAsEnter()
    {
        var id = FirstId(await Start(new { command = "python" }));
        await Write(new { id, input = "x = 1\ny = 2\r\n", wait = 0 });
        Assert.Equal("x = 1\ry = 2\r", _backend.Last.Written);
    }

    [Fact]
    public async Task NamedKeysBecomeTerminalBytes()
    {
        var id = FirstId(await Start(new { command = "menu" }));
        var write = await Write(new { id, keys = new[] { "down", "Enter", "ctrl-z", "ctrl-x", "f5", "esc" }, wait = 0 });
        Assert.StartsWith("Sent down+Enter+ctrl-z+ctrl-x+f5+esc to p1; it is running.", write);
        Assert.Equal("\u001B[B\r\u001A\u0018\u001B[15~\u001B", _backend.Last.Written);
    }

    [Fact]
    public void KeyTableMatchesUpstreamWithCtrlZFixed()
    {
        Assert.Equal("\r", ProcessWriteTool.KeyCode("enter"));
        Assert.Equal("\r", ProcessWriteTool.KeyCode("return"));
        Assert.Equal("\u0003", ProcessWriteTool.KeyCode("ctrl-c"));
        Assert.Equal("\u0004", ProcessWriteTool.KeyCode("ctrl-d"));
        Assert.Equal("\u001A", ProcessWriteTool.KeyCode("ctrl-z")); // upstream sent a line feed here
        Assert.Equal("\t", ProcessWriteTool.KeyCode("tab"));
        Assert.Equal("\u001B", ProcessWriteTool.KeyCode("esc"));
        Assert.Equal("\u001B", ProcessWriteTool.KeyCode("escape"));
        Assert.Equal("\u001B[A", ProcessWriteTool.KeyCode("up"));
        Assert.Equal("\u001B[B", ProcessWriteTool.KeyCode("down"));
        Assert.Equal("\u001B[C", ProcessWriteTool.KeyCode("right"));
        Assert.Equal("\u001B[D", ProcessWriteTool.KeyCode("left"));
        Assert.Equal("\u001B[5~", ProcessWriteTool.KeyCode("pgup"));
        Assert.Equal("\u001B[6~", ProcessWriteTool.KeyCode("pgdn"));
        Assert.Equal("\u001B[H", ProcessWriteTool.KeyCode("home"));
        Assert.Equal("\u001B[F", ProcessWriteTool.KeyCode("end"));
        Assert.Equal("\u0001", ProcessWriteTool.KeyCode("ctrl+a"));
        Assert.Null(ProcessWriteTool.KeyCode("ctrl-1"));
        Assert.Null(ProcessWriteTool.KeyCode("hyper-q"));
    }

    [Fact]
    public async Task WriteErrors()
    {
        Assert.Equal("Error: no such process 'p7' — check process_list.", await Write(new { id = "p7", input = "x" }));
        var id = FirstId(await Start(new { command = "x" }));
        Assert.Equal("Error: nothing to send — give input and/or keys.", await Write(new { id }));
        var unknown = await Write(new { id, keys = new[] { "hyper-q" } });
        Assert.StartsWith("Error: unknown key 'hyper-q'. Known: ", unknown);
        Assert.Contains("ctrl-c", unknown);
        Assert.Empty(_backend.Last.Writes); // nothing half-sent

        _backend.Last.Exit(3);
        Assert.Equal("Error: p1 already exited (code 3). Start it again with process_start.", await Write(new { id, input = "x" }));
    }

    [Fact]
    public async Task CtrlCThatEndsTheProcessIsReported()
    {
        var id = FirstId(await Start(new { command = "sleep 120" }));
        _backend.Last.CtrlCExitCode = 130;
        var write = await Write(new { id, keys = new[] { "ctrl-c" }, wait = 5 });
        Assert.Equal("Sent ctrl-c to p1; it is exited(130). Next: process_read(id:\"p1\") — with `until` if you are waiting for a specific line.", write);
        Assert.False(_manager.Get(id)!.IsRunning);
    }

    // MARK: - process_stop / process_list

    [Fact]
    public async Task StopUnknownIdAndProcessList()
    {
        Assert.Equal("Error: no such process 'p99'.", await Stop(new { id = "p99" }));
        Assert.Equal("Error: id is required.", await Stop(new { }));
        Assert.Equal("No background processes.", await List());

        var id = FirstId(await Start(new { command = "sleep 20" }));
        var list = await List();
        Assert.StartsWith("Background processes:\n- p1  running(pid 4242)  ", list);
        Assert.EndsWith("s  sleep 20", list);

        Assert.Equal("Stopped p1.", await Stop(new { id }));
        Assert.Equal([false], _backend.Last.Stops);
        var after = await List();
        Assert.Contains("p1  exited(143)", after);
        Assert.Contains("s ago  sleep 20", after);

        // Exited processes stay readable.
        Assert.StartsWith("p1 exited with code 143.", await Read(new { id }));
    }

    [Fact]
    public async Task ForceStopKillsAtOnce()
    {
        var id = FirstId(await Start(new { command = "game" }));
        Assert.Equal("Stopped p1.", await Stop(new { id, force = true }));
        Assert.Equal([true], _backend.Last.Stops);
        Assert.Equal(137, _manager.Get(id)!.ExitCode);
    }

    [Fact]
    public async Task StopAllKillsEveryRunningProcess()
    {
        _backend.OnStart = p => { if (p.ProcessId == 4243) p.Exit(0); };
        await Start(new { command = "a" });
        await Start(new { command = "b" });
        await Start(new { command = "c" });
        _manager.StopAll();
        var started = _backend.Started;
        Assert.Equal([true], started[0].Stops);
        Assert.Empty(started[1].Stops); // already exited: nothing to stop
        Assert.Equal([true], started[2].Stops);
        Assert.All(_manager.All(), p => Assert.False(p.IsRunning));
        Assert.Equal(["p1", "p2", "p3"], _manager.All().Select(p => p.Id));
    }

    [Fact]
    public async Task ExitedProcessesArePrunedAfterTheRetention()
    {
        var manager = new ProcessManager(_backend) { Retention = TimeSpan.Zero, StartSettle = TimeSpan.Zero };
        await _tools.Output(new ProcessStartTool(manager), new { command = "a" });
        await _tools.Output(new ProcessStartTool(manager), new { command = "b" });
        _backend.Started[0].Exit(0);
        await Task.Delay(20);
        Assert.Equal(["p2"], manager.All().Select(p => p.Id));
    }

    // MARK: - Specs and wiring

    [Fact]
    public void ProcessToolsCoverTheGatedNames()
    {
        var names = ProcessTools.All(_manager).Select(t => t.Name).ToHashSet();
        Assert.Equal(new HashSet<string> { "process_start", "process_read", "process_write", "process_stop", "process_list" }, names);
        Assert.Equal(names, ProcessTools.Names.ToHashSet());
        Assert.All(ProcessTools.All(_manager), t => Assert.Equal(t.Name, t.Spec.Name));
    }

    [Fact]
    public void EveryToolSpecParametersAreValidJson()
    {
        // Regression (upstream bcc8f01): an OpenAI-compatible server (SGLang) rejects a request whose
        // tools[i].function.parameters is not a JSON object, and five 0.8.0 specs shipped broken.
        var registry = ToolRegistry.Standard().Adding(ProcessTools.All(_manager));
        Assert.NotEmpty(registry.Specs);
        foreach (var spec in registry.Specs)
        {
            using var doc = JsonDocument.Parse(spec.Parameters);
            Assert.True(doc.RootElement.ValueKind == JsonValueKind.Object, $"{spec.Name}: parameters is not an object");
            Assert.True(doc.RootElement.TryGetProperty("type", out var type) && type.GetString() == "object",
                $"{spec.Name}: parameters is not an object schema");
            if (doc.RootElement.TryGetProperty("properties", out var properties))
                Assert.True(properties.ValueKind == JsonValueKind.Object, $"{spec.Name}: properties must be an object");
        }
    }

    [Fact]
    public void ParameterNamesMatchUpstreamAndTheEngineGates()
    {
        static string[] Properties(IToolExecutor tool)
        {
            using var doc = JsonDocument.Parse(tool.Spec.Parameters);
            return [.. doc.RootElement.GetProperty("properties").EnumerateObject().Select(p => p.Name)];
        }
        var tools = ProcessTools.All(_manager).ToDictionary(t => t.Name);
        Assert.Equal(["command", "cwd", "env", "cols", "rows", "description"], Properties(tools["process_start"]));
        Assert.Equal(["id", "until", "timeout", "all"], Properties(tools["process_read"]));
        Assert.Equal(["id", "input", "keys", "enter", "wait"], Properties(tools["process_write"]));
        Assert.Equal(["id", "force"], Properties(tools["process_stop"]));
        Assert.Empty(Properties(tools["process_list"]));
    }

    [Fact]
    public void DefaultToolsUseTheSharedManager()
    {
        // What the app wires up: no manager argument, the shared registry, this platform's backend.
        Assert.Equal(ProcessTools.Names, ProcessTools.All().Select(t => t.Name));
    }
}
