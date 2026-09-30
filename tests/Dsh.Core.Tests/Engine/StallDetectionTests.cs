namespace Dsh.Core.Tests;

/// <summary>What the stall detector makes of shell commands and of the way tools report failure: a build that keeps failing is a
/// loop however many edits come between, a quiet check after real progress is not, and a note in a call's "description" or a
/// different "Time Elapsed" line does not make a repeated call look new.</summary>
public sealed class StallDetectionTests : IDisposable
{
    private readonly TempDirectory _root = new("dsh-stalls");

    public void Dispose() => _root.Dispose();

    // MARK: - What a command's text says

    [Theory]
    [InlineData("sleep 5")]
    [InlineData("Start-Sleep 60; gh run view 123")]
    [InlineData("sleep 5 && curl http://localhost:8080/health")]
    [InlineData("timeout /t 5")]
    [InlineData("timeout /T 30 /nobreak")]
    [InlineData("ping -n 4 localhost")]
    [InlineData("powershell -Command \"Start-Sleep 5\"")]
    [InlineData("bash -c 'sleep 10; echo done'")]
    [InlineData("watch -n 2 ls")]
    [InlineData("Wait-Process -Id 4")]
    public void ACommandThatWaitsIsRecognised(string command) => Assert.True(ShellHeuristics.Waits(command), command);

    [Theory]
    [InlineData("pytest tests/watch/x.py")]
    [InlineData("go test ./watch/...")]
    [InlineData("grep -rn sleep src")]
    [InlineData("dotnet test --filter Sleep")]
    [InlineData("echo sleep")]
    [InlineData("cat watch.txt")]
    [InlineData("timeout 60 find . -name x")]
    [InlineData("ls -la")]
    [InlineData("gh run view 123")]
    public void AWordThatIsNotTheCommandIsNotAWait(string command) => Assert.False(ShellHeuristics.Waits(command), command);

    [Theory]
    [InlineData("sed -i 's/a/b/' f.txt")]
    [InlineData("sed -i.bak s/a/b/ f")]
    [InlineData("cat > f.py <<'EOF'\nprint(1)\nEOF")]
    [InlineData("echo hi > out.txt")]
    [InlineData("echo hi >> log.txt")]
    [InlineData("python gen.py 2> err.txt")]
    [InlineData("python gen.py 1>out.txt")]
    [InlineData("python gen.py &> all.txt")]
    [InlineData("tee out.txt")]
    [InlineData("mv a b")]
    [InlineData("cp a b")]
    [InlineData("rm -rf build")]
    [InlineData("mkdir -p x/y")]
    [InlineData("touch a")]
    [InlineData("del a.txt")]
    [InlineData("copy a.txt b.txt")]
    [InlineData("move a.txt b.txt")]
    [InlineData("md build")]
    [InlineData("ren a.txt b.txt")]
    [InlineData("Set-Content f.txt hi")]
    [InlineData("Get-Content a | Out-File b")]
    [InlineData("Clear-Content f.txt")]
    [InlineData("Expand-Archive x.zip -DestinationPath out")]
    [InlineData("Invoke-WebRequest https://x/y -OutFile y")]
    [InlineData("curl -o out.html https://example.com")]
    [InlineData("wget https://example.com/x.zip")]
    [InlineData("git checkout -- .")]
    [InlineData("git apply p.diff")]
    [InlineData("git -C repo reset --hard")]
    [InlineData("git stash")]
    [InlineData("npm install")]
    [InlineData("pip install x")]
    [InlineData("dotnet add package X")]
    [InlineData("dotnet format")]
    [InlineData("go get x")]
    [InlineData("go fmt ./...")]
    [InlineData("npx prettier --write .")]
    [InlineData("gofmt -w .")]
    [InlineData("eslint --fix src")]
    [InlineData("black .")]
    [InlineData("sudo rm /tmp/x")]
    [InlineData("FOO=1 mv a b")]
    [InlineData("cd src && mv a b")]
    [InlineData("bash -c 'sed -i s/a/b/ f'")]
    public void ACommandThatChangesFilesIsRecognised(string command) => Assert.True(ShellHeuristics.ChangesFiles(command), command);

    [Theory]
    [InlineData("git status")]
    [InlineData("git diff --stat HEAD")]
    [InlineData("git log --oneline -20")]
    [InlineData("git add -A")]
    [InlineData("git commit -m \"fix: rm old\"")]
    [InlineData("ls -la")]
    [InlineData("cat notes.txt")]
    [InlineData("dotnet build -v q")]
    [InlineData("dotnet test --no-build")]
    [InlineData("npm run lint")]
    [InlineData("go test ./...")]
    [InlineData("cargo check")]
    [InlineData("curl -s https://example.com/api")]
    [InlineData("python -m pyflakes .")]
    [InlineData("echo hello")]
    [InlineData("echo rm")]
    [InlineData("grep -rn \"List<Foo>\" src")]
    [InlineData("grep -rn '=> ' src")]
    [InlineData("grep -rn foo src 2>&1")]
    [InlineData("echo 'a -> b'")]
    [InlineData("awk '$1 > 5' f.txt")]
    [InlineData("echo x >> /dev/null")]
    [InlineData("ls > /dev/null 2>&1")]
    [InlineData("cat a.txt >nul")]
    [InlineData("Get-ChildItem > $null")]
    [InlineData("if [ $a -gt 3 ]; then echo yes; fi")]
    [InlineData("test -f x && echo yes")]
    [InlineData("echo \"run sed -i on it\"")]
    public void AnOrdinaryCommandIsNotTakenForAnEdit(string command) => Assert.False(ShellHeuristics.ChangesFiles(command), command);

    // MARK: - What counts as a failure

    [Theory]
    [InlineData("run_shell_command", "Error: could not start", true)]
    [InlineData("run_shell_command", "Command exited with code 2.\nerror CS1002", true)]
    [InlineData("run_shell_command", "Command timed out after 120s (it produced no output).\n", true)]
    [InlineData("run_shell_command", "Stopped after 120s limit — the command appears to be waiting", true)]
    [InlineData("run_shell_command", "Permission denied: not allowed here", true)]
    [InlineData("edit_file", "Error: old_string not found", true)]
    [InlineData("run_shell_command", "(no output)", false)]
    [InlineData("run_shell_command", "Build succeeded.", false)]
    [InlineData("read_file", "Command exited with code 2 (a line of a log the file holds)", false)]
    public void AFailureIsRecognisedHoweverTheToolSaysIt(string tool, string output, bool failed) =>
        Assert.Equal(failed, StallTracker.Failed(tool, output));

    [Fact]
    public void TimesInAShellAnswerDoNotMakeTwoAnswersDiffer()
    {
        static string Key(string text) => StallTracker.ResultKey("run_shell_command", text);
        Assert.Equal(Key("Build FAILED.\nTime Elapsed 00:00:01.86"), Key("Build FAILED.\nTime Elapsed 00:00:02.41"));
        Assert.Equal(Key("Passed! - Failed: 0, Passed: 12, Duration: 45 ms"), Key("Passed! - Failed: 0, Passed: 12, Duration: 1 s"));
        Assert.Equal(Key("12 passed in 0.12s"), Key("12 passed in 3.40s"));
        Assert.Equal(Key("ok  \tpkg\t0.004s"), Key("ok  \tpkg\t0.031s"));
        Assert.Equal(Key("started 2026-09-29T10:15:03Z"), Key("started 2026-09-29T10:19:44Z"));
        // What changes the meaning still does.
        Assert.NotEqual(Key("Failed: 0, Passed: 12"), Key("Failed: 3, Passed: 9"));
        // And only a shell's answer is read this way: a file's text keeps its numbers.
        Assert.NotEqual(StallTracker.ResultKey("read_file", "timeout = 30 s"), StallTracker.ResultKey("read_file", "timeout = 50 s"));
    }

    [Fact]
    public void ACallsDescriptionIsNotPartOfWhatItIs()
    {
        Assert.Equal(StallTracker.Normalize("""{"command":"dotnet build","description":"Build attempt 1"}"""),
                     StallTracker.Normalize("""{"command":"dotnet build","description":"Build attempt 2"}"""));
        Assert.NotEqual(StallTracker.Normalize("""{"command":"dotnet build"}"""), StallTracker.Normalize("""{"command":"dotnet test"}"""));
    }

    // MARK: - Loops through the tracker

    private static string Shell(string command) => $$"""{"command":"{{command}}"}""";

    [Fact]
    public void AFailingCheckAfterEveryEditIsALoopWhateverTheEditsAre()
    {
        var tracker = new StallTracker(4, 8);
        var level = StallLevel.None;
        var calls = 0;
        for (var i = 0; i < 30 && level != StallLevel.Stop; i++)
        {
            tracker.Observe("edit_file", $$"""{"file_path":"f{{i}}.cs","old_string":"a","new_string":"b"}""", StallTracker.ResultKey("edit_file", "edited"), failed: false, changedFiles: true);
            level = tracker.Observe("run_shell_command", Shell("dotnet build"), "Command exited with code 1.\nerror CS1002: ; expected");
            calls += 2;
        }
        Assert.Equal(StallLevel.Stop, level);
        Assert.Equal(16, calls); // the 8th identical failure
    }

    [Fact]
    public void ARefusedWriteIsNotProgress()
    {
        var tracker = new StallTracker(4, 8);
        var level = StallLevel.None;
        for (var i = 0; i < 30 && level != StallLevel.Stop; i++)
        {
            tracker.Observe("run_shell_command", Shell($"rm -rf build{i}"), "Permission denied: deleting is not allowed here.");
            level = tracker.Observe("run_shell_command", Shell("ls build"), "a.dll\nb.dll");
        }
        Assert.Equal(StallLevel.Stop, level);
    }

    [Fact]
    public void TheSameChangingCommandRunAgainIsNotNewProgress()
    {
        // "git checkout ." changes files the first time; a model that runs it between every pair of identical builds is looping.
        var tracker = new StallTracker(4, 8);
        var level = StallLevel.None;
        var rounds = 0;
        while (rounds < 30 && level != StallLevel.Stop)
        {
            tracker.Observe("run_shell_command", Shell("git checkout ."), "(no output)");
            level = tracker.Observe("run_shell_command", Shell("go build ./..."), "(no output)");
            rounds++;
        }
        Assert.Equal(StallLevel.Stop, level);
        Assert.Equal(8, rounds);
    }

    [Fact]
    public void ADifferentChangingCommandEachTimeIsProgress()
    {
        var tracker = new StallTracker(4, 8);
        for (var i = 0; i < 25; i++)
        {
            Assert.Equal(StallLevel.None, tracker.Observe("run_shell_command", Shell($"sed -i s/a{i}/b/ f.txt"), "(no output)"));
            Assert.Equal(StallLevel.None, tracker.Observe("run_shell_command", Shell("go build ./..."), "(no output)"));
        }
    }

    // MARK: - Loops through the engine

    private sealed class Answering(string name, Func<int, ToolResult> answer) : IToolExecutor
    {
        private int _calls;
        public int Runs => _calls;
        public string Name => name;
        public ToolSpec Spec => new(name, name, """{"type":"object","properties":{}}""");

        public Task<ToolResult> ExecuteAsync(string arguments, ToolContext context, CancellationToken cancellationToken) =>
            Task.FromResult(answer(Interlocked.Increment(ref _calls)));
    }

    private Engine Make(ILlmClient client, params IToolExecutor[] tools) =>
        new(client, new ToolRegistry(tools), "system", new EngineConfig("test") { MaxIterations = 100, ToolTimeout = TimeSpan.FromSeconds(5) },
            _root.Path, new PermissionPolicy(PermissionPreset.WorkspaceWrite, _root.Path), Gates.Allow);

    private static Answering Edits(string name = "edit_file") =>
        new(name, _ => new ToolResult("edited") { Files = [new FileChange("f.txt", FileChangeKind.Modified)] });

    [Fact]
    public async Task AFailingBuildAfterEveryEditEndsARunAtTheEighthFailure()
    {
        var turns = Enumerable.Range(1, 40).SelectMany(n => new[]
        {
            Turn.Calling(new ToolCall($"e{n}", "edit_file", $$"""{"file_path":"f{{n}}.cs"}""")),
            Turn.Calling(new ToolCall($"b{n}", "run_shell_command", Shell("dotnet build"))),
        });
        var client = new ScriptedClient(turns);
        var build = new Answering("run_shell_command", _ => new ToolResult("Command exited with code 1.\nerror CS1002: ; expected"));

        var result = await Make(client, Edits(), build).RunAsync([], "fix the build");

        Assert.True(result.Stalled);
        Assert.Equal(16, client.Requests.Count);
        Assert.Equal(8, build.Runs);
    }

    [Fact]
    public async Task ABuildWhoseTimeElapsedLineChangesIsStillTheSameBuild()
    {
        var turns = Enumerable.Range(1, 40).Select(n => Turn.Calling(new ToolCall($"b{n}", "run_shell_command", Shell("dotnet build"))));
        var client = new ScriptedClient(turns);
        var build = new Answering("run_shell_command", n => new ToolResult($"Command exited with code 1.\nerror CS1002\nTime Elapsed 00:00:{n % 60:00}.{n * 7 % 100:00}"));

        var result = await Make(client, build).RunAsync([], "build it");

        Assert.True(result.Stalled);
        Assert.Equal(8, client.Requests.Count);
    }

    [Fact]
    public async Task ACallWithAChangingDescriptionIsStillTheSameCall()
    {
        File.WriteAllText(Path.Combine(_root.Path, "a.txt"), "one\ntwo\n");
        var turns = Enumerable.Range(1, 40).Select(n => Turn.Calling(new ToolCall($"r{n}", "read_file", $$"""{"file_path":"a.txt","description":"Read it, attempt {{n}}"}""")));
        var client = new ScriptedClient(turns);

        var result = await Make(client, new ReadFileTool()).RunAsync([], "read");

        Assert.True(result.Stalled);
        Assert.Equal(8, client.Requests.Count);
    }

    [Theory]
    [InlineData("worker", false)]
    [InlineData("general", false)]
    [InlineData("explore", true)]
    public async Task AWorkerThatEditsIsProgressAndAReadOnlyOneIsNot(string agentType, bool stalls)
    {
        var turns = Enumerable.Range(1, 30).SelectMany(n => new[]
        {
            Turn.Calling(new ToolCall($"a{n}", "agent", $$"""{"description":"module {{n}}","prompt":"port module {{n}}","agent_type":"{{agentType}}"}""")),
            Turn.Calling(new ToolCall($"b{n}", "run_shell_command", Shell("go build ./..."))),
        }).Append(new Turn("done"));
        var client = new ScriptedClient(turns);
        var agent = new Answering("agent", n => new ToolResult($"Subagent 'module {n}' finished.\n\nported"));
        var build = new Answering("run_shell_command", _ => new ToolResult("(no output)"));

        var result = await Make(client, agent, build).RunAsync([], "port them all");

        Assert.Equal(stalls, result.Stalled);
        if (!stalls) Assert.Equal("done", result.FinalText);
    }

    [Fact]
    public async Task AWorkerThatEditsThroughSubagentsAndProcessesCountsAsProgressToo()
    {
        var turns = Enumerable.Range(1, 20).SelectMany(n => new[]
        {
            Turn.Calling(new ToolCall($"p{n}", "process_start", $$"""{"command":"npm run codegen -- --module {{n}}"}""")),
            Turn.Calling(new ToolCall($"b{n}", "run_shell_command", Shell("tsc --noEmit"))),
        }).Append(new Turn("done"));
        var client = new ScriptedClient(turns);

        var result = await Make(client, new Answering("process_start", n => new ToolResult($"Started process p{n}")),
            new Answering("run_shell_command", _ => new ToolResult("(no output)"))).RunAsync([], "generate");

        Assert.False(result.Stalled);
        Assert.Equal("done", result.FinalText);
    }

    // MARK: - Each shell read as itself

    [Theory]
    [InlineData("C:\\Windows\\System32\\timeout.exe /t 3", ShellKind.Cmd)]
    [InlineData("C:\\Windows\\System32\\timeout.exe /t 3", ShellKind.PowerShell)]
    [InlineData("while ($true) { Start-Sleep 5; gh run view 1 }", ShellKind.PowerShell)]
    [InlineData("Set-Location \"C:\\proj\\\"; Start-Sleep 30; gh run view 1", ShellKind.PowerShell)]
    [InlineData("cmd /c timeout /t 30 /nobreak >nul & gh run view 1", ShellKind.PowerShell)]
    [InlineData("cmd /c timeout /t 30 /nobreak >nul & gh run view 1", ShellKind.Cmd)]
    [InlineData("powershell -Command Start-Sleep 5", ShellKind.Bash)]
    [InlineData("powershell -NoProfile -ExecutionPolicy Bypass -Command Start-Sleep 5", ShellKind.Cmd)]
    [InlineData("wsl sleep 30; gh run view 1", ShellKind.PowerShell)]
    [InlineData("[System.Threading.Thread]::Sleep(5000)", ShellKind.PowerShell)]
    [InlineData("until nc -z h 5432; do sleep 1; done", ShellKind.Bash)]
    [InlineData("while ! curl -s localhost:3000; do sleep 2; done", ShellKind.Bash)]
    [InlineData("if [ -d x ]; then sleep 1; fi", ShellKind.Bash)]
    [InlineData("timeout 30 sleep 5", ShellKind.Bash)]
    [InlineData("sudo -u root sleep 5", ShellKind.Bash)]
    [InlineData("nohup sleep 3 &", ShellKind.Bash)]
    [InlineData("ping -c 5 localhost", ShellKind.Bash)]
    [InlineData("gh run watch 123", ShellKind.Bash)]
    [InlineData("kubectl rollout status deploy/api", ShellKind.Bash)]
    [InlineData("bash -c \"powershell -Command 'Start-Sleep 5'\"", ShellKind.Bash)]
    public void ACommandThatWaitsIsRecognisedInItsOwnShell(string command, ShellKind kind) =>
        Assert.True(ShellHeuristics.Waits(command, kind), command);

    [Theory]
    [InlineData("timeout 30 /tmp/run.sh", ShellKind.Bash)]
    [InlineData("timeout 60 /tools/x", ShellKind.Bash)]
    [InlineData("ping -c 1 host", ShellKind.Bash)]
    [InlineData("ping -n 1 host", ShellKind.Cmd)]
    [InlineData("cat <<'EOF'\nsleep 5\nEOF", ShellKind.Bash)]
    [InlineData("echo hi # sleep 5", ShellKind.Bash)]
    [InlineData("# sleep 5\nls", ShellKind.Bash)]
    [InlineData("Write-Output @'\nStart-Sleep 5\n'@", ShellKind.PowerShell)]
    [InlineData("git log --grep sleep", ShellKind.Bash)]
    // In bash a backslash before the quote keeps the string open, so the wait is inside it; in PowerShell it is a plain character.
    [InlineData("cd \"C:\\proj\\\"; sleep 5", ShellKind.Bash)]
    public void ACommandThatOnlyMentionsAWaitIsNotOne(string command, ShellKind kind) =>
        Assert.False(ShellHeuristics.Waits(command, kind), command);

    [Theory]
    [InlineData("echo hello>out.txt", ShellKind.Bash)]
    [InlineData("echo \"hello\">out.txt", ShellKind.Bash)]
    [InlineData("echo '{\"a\":1}'>config.json", ShellKind.Bash)]
    [InlineData("type nul>empty.txt", ShellKind.Cmd)]
    [InlineData("dir>listing.txt", ShellKind.Cmd)]
    [InlineData("python gen.py>out.txt", ShellKind.Bash)]
    [InlineData("printf \"x\\n\">>log", ShellKind.Bash)]
    [InlineData("(echo hi)>out.txt", ShellKind.Bash)]
    [InlineData("Get-Process>procs.txt", ShellKind.PowerShell)]
    [InlineData("Get-ChildItem | ForEach-Object { Remove-Item $_ }", ShellKind.PowerShell)]
    [InlineData("if (Test-Path build) { Remove-Item build -Recurse }", ShellKind.PowerShell)]
    [InlineData("try { Remove-Item x } catch {}", ShellKind.PowerShell)]
    [InlineData("foreach ($i in 1..3) { New-Item \"f$i\" }", ShellKind.PowerShell)]
    [InlineData("1..3 | % { Remove-Item \"f$_\" }", ShellKind.PowerShell)]
    [InlineData("Write-Host \"it's\" ; Remove-Item x", ShellKind.PowerShell)]
    [InlineData("if [ -d build ]; then rm -rf build; fi", ShellKind.Bash)]
    [InlineData("for f in a b; do rm $f; done", ShellKind.Bash)]
    [InlineData("! test -f x && rm y", ShellKind.Bash)]
    [InlineData("(cd build && rm -rf x)", ShellKind.Bash)]
    [InlineData("{ rm x; }", ShellKind.Bash)]
    [InlineData("cmd /c del build\\*.obj", ShellKind.Cmd)]
    [InlineData("cmd /c del build\\*.obj", ShellKind.PowerShell)]
    [InlineData("powershell -Command Remove-Item build -Recurse", ShellKind.Bash)]
    [InlineData("wsl rm -rf build", ShellKind.PowerShell)]
    [InlineData("if exist build rmdir /s /q build", ShellKind.Cmd)]
    [InlineData("if not exist build mkdir build", ShellKind.Cmd)]
    [InlineData("for %i in (a b) do del %i", ShellKind.Cmd)]
    [InlineData("& \"C:\\Program Files\\Git\\bin\\git.exe\" checkout .", ShellKind.PowerShell)]
    [InlineData("\"C:\\Program Files\\Git\\bin\\git.exe\" checkout .", ShellKind.Cmd)]
    [InlineData("& 'C:\\tools\\7z.exe' x a.zip", ShellKind.PowerShell)]
    [InlineData("git \\\n checkout .", ShellKind.Bash)]
    [InlineData("git `\n checkout .", ShellKind.PowerShell)]
    [InlineData("git ^\n checkout .", ShellKind.Cmd)]
    [InlineData("git \\\r\n checkout .", ShellKind.Bash)]
    [InlineData("git `\r\n checkout .", ShellKind.PowerShell)]
    [InlineData("git ^\r\n checkout .", ShellKind.Cmd)]
    [InlineData("cd \"C:\\proj\\\" ; Remove-Item out -Recurse", ShellKind.PowerShell)]
    [InlineData("echo \"a\\\"b\" && rm x", ShellKind.Bash)]
    [InlineData("echo 'a\\' && rm x", ShellKind.Bash)]
    [InlineData("# it's a note\nrm build", ShellKind.Bash)]
    [InlineData("cat <<EOF\nrm x\nEOF\nrm y", ShellKind.Bash)]
    [InlineData("Copy-Item a b", ShellKind.PowerShell)]
    [InlineData("ri build -Recurse", ShellKind.PowerShell)]
    [InlineData("[IO.File]::WriteAllText($p, $c)", ShellKind.PowerShell)]
    [InlineData("Invoke-WebRequest -Uri https://example.com -OutFile y", ShellKind.PowerShell)]
    public void AWriteIsSeenThroughItsShellsSyntax(string command, ShellKind kind) =>
        Assert.True(ShellHeuristics.ChangesFiles(command, kind), command);

    [Theory]
    [InlineData("perl -pi -e 's/a/b/' f")]
    [InlineData("perl -i.bak -pe 's/a/b/' f")]
    [InlineData("find . -name '*.o' -delete")]
    [InlineData("find . -name x -exec rm {} \\;")]
    [InlineData("find . -type f -exec sed -i s/a/b/ {} +")]
    [InlineData("git clone https://example.com/x.git")]
    [InlineData("ls | xargs rm")]
    [InlineData("xargs -I {} rm {}")]
    [InlineData("tar -xf a.tgz")]
    [InlineData("tar xzf a.tgz")]
    [InlineData("tar -czf out.tgz dir")]
    [InlineData("dotnet new console")]
    [InlineData("timeout 30 npm install")]
    [InlineData("sudo -u root mv a b")]
    [InlineData("env FOO=1 sed -i s/a/b/ f")]
    [InlineData("nice -n 5 rm x")]
    [InlineData("nuget restore")]
    [InlineData("python -m pip install x")]
    [InlineData("python3.12 -m pip install x")]
    [InlineData("python -m black .")]
    [InlineData("unzip a.zip")]
    [InlineData("go mod tidy")]
    [InlineData("go generate ./...")]
    [InlineData("dotnet tool install x")]
    [InlineData("uv pip install x")]
    [InlineData("yarn")]
    [InlineData("cargo fmt")]
    [InlineData("curl -O https://example.com/y.zip")]
    [InlineData("python3 - <<'EOF'\nopen('f','w').write('x')\nEOF")]
    [InlineData("python -c \"open('f','w').write('x')\"")]
    [InlineData("python3 -c \"import os; os.remove('x')\"")]
    [InlineData("node -e \"require('fs').writeFileSync('f','x')\"")]
    public void WritersThatAreNotOnTheShortListAreSeenToo(string command) => Assert.True(ShellHeuristics.ChangesFiles(command), command);

    [Theory]
    [InlineData("python3 - <<'EOF'\nx = 5\nif x > 3: print(x)\nEOF", ShellKind.Bash)]
    [InlineData("cat <<'EOF'\nrm -rf x\ndel a\ncopy b c\nEOF", ShellKind.Bash)]
    [InlineData("cat <<-EOF\n\trm x\n\tEOF\nls", ShellKind.Bash)]
    [InlineData("cat <<A <<B\nrm one\nA\nrm two\nB\nls", ShellKind.Bash)]
    [InlineData("cat <<< \"rm x\"", ShellKind.Bash)]
    [InlineData("black --check .", ShellKind.Bash)]
    [InlineData("isort --check-only .", ShellKind.Bash)]
    [InlineData("git stash list", ShellKind.Bash)]
    [InlineData("git stash show", ShellKind.Bash)]
    [InlineData("git clean -n", ShellKind.Bash)]
    [InlineData("git clean --dry-run -fd", ShellKind.Bash)]
    [InlineData("Remove-Item build -WhatIf", ShellKind.PowerShell)]
    [InlineData("curl -s -o /dev/null -w \"%{http_code}\" http://localhost:3000", ShellKind.Bash)]
    [InlineData("cargo fmt -- --check", ShellKind.Bash)]
    [InlineData("dotnet format --verify-no-changes", ShellKind.Bash)]
    [InlineData("terraform fmt -check", ShellKind.Bash)]
    [InlineData("echo a->b", ShellKind.Bash)]
    [InlineData("echo x=>y", ShellKind.Bash)]
    [InlineData("echo <br>", ShellKind.Bash)]
    [InlineData("echo List<Foo>", ShellKind.Bash)]
    [InlineData("echo a>=b", ShellKind.Bash)]
    [InlineData("echo 2>&1", ShellKind.Bash)]
    [InlineData("echo hi # > out.txt", ShellKind.Bash)]
    [InlineData("tar -tf a.tgz", ShellKind.Bash)]
    [InlineData("unzip -l a.zip", ShellKind.Bash)]
    [InlineData("cd \"C:\\proj\\\"; ls", ShellKind.PowerShell)]
    [InlineData("Write-Output @'\nRemove-Item x\n'@", ShellKind.PowerShell)]
    [InlineData("Write-Host `\"hi`\"", ShellKind.PowerShell)]
    [InlineData("echo hi ^& del x", ShellKind.Cmd)]
    [InlineData("echo $(rm x)", ShellKind.Bash)]
    [InlineData("cd ${HOME}/src && ls", ShellKind.Bash)]
    [InlineData("echo ${#arr[@]}", ShellKind.Bash)]
    [InlineData("ls # Remove-Item x", ShellKind.PowerShell)]
    [InlineData("<# rm x #> ls", ShellKind.PowerShell)]
    [InlineData("echo a\\;b", ShellKind.Bash)]
    [InlineData("find . -type f -exec grep -l foo {} \\;", ShellKind.Bash)]
    [InlineData("xargs -n 1 grep foo", ShellKind.Bash)]
    [InlineData("python --version", ShellKind.Bash)]
    [InlineData("python -m http.server", ShellKind.Bash)]
    [InlineData("echo \"never closed", ShellKind.Bash)]
    [InlineData("cat <<EOF", ShellKind.Bash)]
    public void ThingsThatOnlyLookLikeWritesAreNot(string command, ShellKind kind) =>
        Assert.False(ShellHeuristics.ChangesFiles(command, kind), command);

    [Theory]
    [InlineData("python tools/gen.py --module m1", ShellKind.Bash)]
    [InlineData("python3 gen.py", ShellKind.Bash)]
    [InlineData("python -m mypackage.cli", ShellKind.Bash)]
    [InlineData(".venv/bin/python gen.py", ShellKind.Bash)]
    [InlineData("C:\\Python\\python.exe gen.py", ShellKind.PowerShell)]
    [InlineData("node build.js", ShellKind.Bash)]
    [InlineData("./gen.sh", ShellKind.Bash)]
    [InlineData(".\\build.ps1", ShellKind.PowerShell)]
    [InlineData("scripts\\gen.bat", ShellKind.Cmd)]
    [InlineData("tools/gen.py", ShellKind.Bash)]
    [InlineData("bash deploy.sh", ShellKind.Bash)]
    [InlineData("sh deploy.sh", ShellKind.Bash)]
    [InlineData("cd /tmp && ./run.sh", ShellKind.Bash)]
    [InlineData("FOO=1 BAR=2 ./run.sh", ShellKind.Bash)]
    [InlineData("npm run codegen", ShellKind.Bash)]
    [InlineData("npm run generate:types", ShellKind.Bash)]
    [InlineData("yarn codegen", ShellKind.Bash)]
    [InlineData("pnpm run gen", ShellKind.Bash)]
    [InlineData("bun run gen", ShellKind.Bash)]
    [InlineData("make gen", ShellKind.Bash)]
    [InlineData("just release", ShellKind.Bash)]
    [InlineData("dotnet run", ShellKind.Bash)]
    [InlineData("dotnet run -- --module 3", ShellKind.Bash)]
    [InlineData("go run ./cmd/gen", ShellKind.Bash)]
    [InlineData("cargo run --bin gen", ShellKind.Bash)]
    [InlineData("java -jar app.jar", ShellKind.Bash)]
    [InlineData("npx create-react-app x", ShellKind.Bash)]
    [InlineData("npx ts-node script.ts", ShellKind.Bash)]
    [InlineData("pnpm dlx create-vite", ShellKind.Bash)]
    [InlineData("bundle exec rake gen", ShellKind.Bash)]
    [InlineData("poetry run python gen.py", ShellKind.Bash)]
    [InlineData("uv run python gen.py", ShellKind.Bash)]
    [InlineData("php artisan migrate", ShellKind.Bash)]
    [InlineData("bash -c 'python gen.py'", ShellKind.Bash)]
    public void ARunOfSomeonesScriptMayHaveChangedFiles(string command, ShellKind kind) =>
        Assert.True(ShellHeuristics.MightChangeFiles(command, kind), command);

    [Theory]
    [InlineData("npm run build")]
    [InlineData("npm test")]
    [InlineData("npm run lint")]
    [InlineData("npm start")]
    [InlineData("yarn test")]
    [InlineData("yarn build")]
    [InlineData("pnpm build")]
    [InlineData("pnpm dev")]
    [InlineData("pnpm exec tsc")]
    [InlineData("npm exec eslint .")]
    [InlineData("make")]
    [InlineData("make test")]
    [InlineData("make -j4 all")]
    [InlineData("gradle build")]
    [InlineData("gradle clean build")]
    [InlineData("./gradlew test")]
    [InlineData("mvn test")]
    [InlineData("python -m pytest")]
    [InlineData("python -c \"print(1)\"")]
    [InlineData("python3 -m pyflakes .")]
    [InlineData("python")]
    [InlineData("python -")]
    [InlineData("pytest -q")]
    [InlineData("node -e \"console.log(1)\"")]
    [InlineData("node --version")]
    [InlineData("node")]
    [InlineData("deno test")]
    [InlineData("bun run build")]
    [InlineData("java -version")]
    [InlineData("dotnet build")]
    [InlineData("dotnet test --filter add")]
    [InlineData("go test ./...")]
    [InlineData("go build ./...")]
    [InlineData("cargo test")]
    [InlineData("cargo check")]
    [InlineData("npx tsc --noEmit")]
    [InlineData("npx eslint .")]
    [InlineData("npx prettier --check .")]
    [InlineData("bundle exec rspec")]
    [InlineData("poetry run pytest")]
    [InlineData("git status")]
    [InlineData("ls")]
    [InlineData("./node_modules/.bin/tsc")]
    [InlineData(".venv/bin/pytest")]
    [InlineData("bash -c 'ls'")]
    [InlineData("source venv/bin/activate")]
    [InlineData("/usr/bin/git status")]
    [InlineData("/usr/local/bin/mytool --flag")]
    [InlineData("echo hi")]
    [InlineData("docker compose up -d")]
    [InlineData("kubectl apply -f x.yaml")]
    public void OrdinaryChecksAndBuildsAreNotUnseenCode(string command) => Assert.False(ShellHeuristics.MightChangeFiles(command), command);

    [Fact]
    public void AVeryLongCommandIsReadFromItsStart()
    {
        // A 24 KB heredoc: the write is at the top, the rest is its body.
        var command = "cat > big.py <<'EOF'\n" + new string('x', 24_000) + "\nEOF";
        Assert.True(ShellHeuristics.ChangesFiles(command));
        Assert.False(ShellHeuristics.ChangesFiles("cat <<'EOF'\n" + new string('x', 24_000) + "\nrm big\nEOF"));
    }

    [Fact]
    public void AHostileCommandOfEveryShellIsReadQuickly()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        string[] units = ["a | ", "sed -i ", "' \" ", "> ", "&& ", "bash -c '", "( ", "{ ", "${", "$(", "<<a\n", "@\"\n", "\\", "`", "^", "2>&1 ", "<< ", "# ", "sudo ", "find -exec ", "xargs -I "];
        foreach (var kind in Enum.GetValues<ShellKind>())
        {
            foreach (var unit in units)
            {
                var command = string.Concat(Enumerable.Repeat(unit, 3_000));
                ShellHeuristics.Read(command, kind);
            }
        }
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"took {clock.Elapsed}");
    }

    [Fact]
    public void NoCommandInAnyShellCanMakeTheReaderThrowOrCrawl()
    {
        // A fixed seed: the same 30,000 odd command lines every run, built from the bits the reader looks at, in every order.
        var random = new Random(20260930);
        string[] pieces =
        [
            "sudo ", "timeout ", "xargs ", "find ", "-exec ", "-delete ", "tar ", "git ", "checkout ", "npm ", "run ", "install ", "python ",
            "-c ", "-m ", "pip ", "bash ", "sh ", "cmd ", "/c ", "powershell ", "-Command ", "wsl ", "if ", "then ", "do ", "done ", "for ",
            "exist ", "not ", "rm ", "sleep 5", "Start-Sleep ", "Remove-Item ", "curl ", "-o ", "-u ", "dotnet ", "run ", "build ", "make ", "npx ",
            "\"", "'", "`", "\\", "^", "\n", "\r\n", "; ", " && ", " || ", " | ", " & ", "{ ", " }", "(", ")", "$(", "${", "@(", "@{", "@\"\n", "\n\"@",
            "<<EOF\n", "EOF\n", "<<-'X'\n", "<<\"", "<<<", "<< ", "#", "# ", "<#", "#>", ">", ">>", "2>&1", "&>", "> /dev/null", ">nul", " x ",
            "a", "1", "-", "=", ":", "$", "%", "*", "/", ".", "é", "中", "😀", "\uD83D", "\t", "  ", "FOO=bar ", "-- ", "-p ", "-e ",
        ];
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var kinds = Enum.GetValues<ShellKind>();
        for (var i = 0; i < 30_000; i++)
        {
            var text = string.Concat(Enumerable.Range(0, random.Next(0, 40)).Select(_ => pieces[random.Next(pieces.Length)]));
            ShellHeuristics.Read(text, kinds[i % kinds.Length]);
        }
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), $"took {clock.Elapsed}");

        // And the tracker, fed the same lines as commands (JSON or not) with every kind of answer.
        var tracker = new StallTracker(4, 8, ShellKind.PowerShell);
        string[] answers = ["(no output)", "Command exited with code 1.\n", "Command exited with code 2.\nboom", "Error: no", "Command timed out after 5s (it produced no output).\n"];
        for (var i = 0; i < 5_000; i++)
        {
            var text = string.Concat(Enumerable.Range(0, random.Next(0, 20)).Select(_ => pieces[random.Next(pieces.Length)]));
            var arguments = random.Next(3) == 0 ? text : Json(text);
            tracker.Observe("run_shell_command", arguments, answers[random.Next(answers.Length)]);
        }
    }

    [Fact]
    public void HalfASurrogatePairInACallsArgumentsDoesNotBreakTheTracker()
    {
        // The model's JSON can carry half an emoji (a truncated escape): the parser calls that an ArgumentException, not a JsonException.
        var tracker = new StallTracker(4, 8);
        var arguments = "{\"command\":\"echo hi \uD83D\"}";
        Assert.Equal(StallLevel.None, tracker.Observe("run_shell_command", arguments, "(no output)"));
        Assert.Equal(StallTracker.Normalize(arguments), StallTracker.Normalize(arguments));
        var level = StallLevel.None;
        for (var i = 0; i < 8; i++) level = tracker.Observe("run_shell_command", arguments, "(no output)");
        Assert.Equal(StallLevel.Stop, level);
    }

    [Fact]
    public void HalfASurrogatePairInToolArgumentsIsMalformedNotFatal()
    {
        var cutOffInAnEmoji = "{\"command\":\"echo \uD83D";
        Assert.False(Engine.ValidArguments(cutOffInAnEmoji));
        Assert.Empty(JsonArgs.Object(cutOffInAnEmoji));
        var cache = new ToolCache();
        var call = "{\"pattern\":\"a\uD83D\"}";
        Assert.Null(cache.TryGet("grep", call, _root.Path, path => path, 1));
        cache.Observe("grep", call, _root.Path, path => path, new ToolResult("match"), 1);
        Assert.NotNull(cache.TryGet("grep", call, _root.Path, path => path, 2));
    }

    // MARK: - Loops through the tracker: longer cycles, waits, failures, unseen writers

    private static string Json(string command) => System.Text.Json.JsonSerializer.Serialize(new { command });

    [Fact]
    public void AFourStepCycleOfEditsAndFailingBuildsStopsTooAndFasterThanTheStepLimit()
    {
        var tracker = new StallTracker(4, 8);
        var edited = StallTracker.ResultKey("edited");
        var level = StallLevel.None;
        var calls = 0;
        for (var round = 0; round < 60 && level != StallLevel.Stop; round++)
        {
            tracker.Observe("edit_file", """{"file_path":"a.cs","old_string":"A","new_string":"B"}""", edited, failed: false, changedFiles: true);
            tracker.Observe("run_shell_command", Shell("dotnet test"), "Command exited with code 1.\nFailed: X");
            tracker.Observe("edit_file", """{"file_path":"a.cs","old_string":"B","new_string":"A"}""", edited, failed: false, changedFiles: true);
            level = tracker.Observe("run_shell_command", Shell("dotnet test"), "Command exited with code 1.\nFailed: Y");
            calls += 4;
        }
        Assert.Equal(StallLevel.Stop, level);
        Assert.Equal(48, calls);          // the 12th time round for the first call of the cycle was call 45
        Assert.Equal(12, tracker.Repeats);
        Assert.Equal(StallTracker.LongWindow, tracker.Span);
    }

    [Fact]
    public void AnEightStepCycleOfReadsWithTheSameAnswersStops()
    {
        var tracker = new StallTracker(4, 8);
        var level = StallLevel.None;
        var calls = 0;
        while (calls < 200 && level != StallLevel.Stop)
        {
            level = tracker.Observe("read_file", $$"""{"file_path":"f{{calls % 8}}.txt"}""", StallTracker.ResultKey($"text {calls % 8}"), failed: false);
            calls++;
        }
        Assert.Equal(StallLevel.Stop, level);
        Assert.Equal(89, calls);          // the 12th sighting of f0: 11 * 8 + 1
    }

    [Fact]
    public void ReadingTheSameFileAgainAfterEachRealEditIsNotALoopHoweverLongTheSession()
    {
        var tracker = new StallTracker(4, 8);
        for (var i = 0; i < 200; i++)
        {
            Assert.Equal(StallLevel.None, tracker.Observe("read_file", """{"file_path":"a.cs"}""", StallTracker.ResultKey("same text"), failed: false));
            Assert.Equal(StallLevel.None, tracker.Observe("edit_file", $$"""{"file_path":"b{{i}}.cs","old_string":"a","new_string":"b"}""",
                StallTracker.ResultKey("edited"), failed: false, changedFiles: true));
        }
    }

    [Theory]
    [InlineData("process_read", """{"id":"p1"}""", "Error: no such process p1")]
    [InlineData("agent_status", """{"id":"bg-9"}""", "Error: no background agent bg-9. agent_status without an id lists them.")]
    [InlineData("process_list", "{}", "Error: the process service is not answering")]
    public void APollThatKeepsFailingIsNotWaiting(string tool, string arguments, string output)
    {
        var tracker = new StallTracker(4, 8);
        var level = StallLevel.None;
        var calls = 0;
        while (calls < 100 && level != StallLevel.Stop)
        {
            level = tracker.Observe(tool, arguments, output);
            calls++;
        }
        Assert.Equal(8, calls);
    }

    [Fact]
    public void AWaitingCommandThatKeepsFailingIsNotWaitingEither()
    {
        // `sleep 30; curl …` exiting with "connection refused" every time: the server is not coming up.
        var tracker = new StallTracker(4, 8);
        var level = StallLevel.None;
        var calls = 0;
        while (calls < 100 && level != StallLevel.Stop)
        {
            level = tracker.Observe("run_shell_command", Shell("sleep 30; curl -s localhost:3000"), "Command exited with code 7.\n");
            calls++;
        }
        Assert.Equal(8, calls);
    }

    [Fact]
    public void AWaitThatNeverChangesItsAnswerIsWarnedAboutAndThenStoppedFarOut()
    {
        var tracker = new StallTracker(4, 8);
        for (var i = 1; i < 20; i++)
            Assert.Equal(StallLevel.None, tracker.Observe("process_read", """{"id":"p1","until":"ready"}""", "(no new output)"));
        Assert.Equal(StallLevel.Nudge, tracker.Observe("process_read", """{"id":"p1","until":"ready"}""", "(no new output)"));
        Assert.Equal(StallKind.Waiting, tracker.Kind);
        Assert.Equal(20, tracker.Repeats);
        Assert.Contains("while waiting", StallTracker.NudgeText("process_read", tracker.Repeats, tracker.Kind, tracker.Span));
        var level = StallLevel.None;
        for (var i = 21; i <= 40; i++) level = tracker.Observe("process_read", """{"id":"p1","until":"ready"}""", "(no new output)");
        Assert.Equal(StallLevel.Stop, level);
        Assert.Contains("waited on the same `process_read` call 40 times", StallTracker.StopText("process_read", tracker.Repeats, tracker.Kind, tracker.Span));
    }

    [Fact]
    public void WaitingThatKeepsGettingNewAnswersNeverCounts()
    {
        var tracker = new StallTracker(4, 8);
        for (var i = 0; i < 200; i++)
        {
            Assert.Equal(StallLevel.None, tracker.Observe("run_shell_command", Shell("sleep 30; gh run view 1"), $"in progress, {i}% of the jobs done"));
            Assert.Equal(StallLevel.None, tracker.Observe("agent_status", """{"id":"bg-1","wait_seconds":60}""", $"still running ({i}s)"));
        }
    }

    [Fact]
    public void AnEditStartsTheCountOfIdenticalPollsOver()
    {
        var tracker = new StallTracker(4, 8);
        for (var round = 0; round < 5; round++)
        {
            for (var i = 0; i < 15; i++) Assert.Equal(StallLevel.None, tracker.Observe("process_read", """{"id":"p1","until":"ready"}""", "(no new output)"));
            tracker.Observe("edit_file", $$"""{"file_path":"f{{round}}.cs"}""", StallTracker.ResultKey("edited"), failed: false, changedFiles: true);
        }
    }

    [Fact]
    public void TwelveDifferentSilentFailuresAreTwelveQuestionsNotOneError()
    {
        var tracker = new StallTracker(4, 8);
        for (var i = 0; i < 30; i++)
            Assert.Equal(StallLevel.None, tracker.Observe("run_shell_command", Shell($"grep -rn 'symbol{i}' src"), "Command exited with code 1.\n"));
        for (var i = 0; i < 30; i++)
        {
            Assert.Equal(StallLevel.None, tracker.Observe("run_shell_command", Shell($"test -f config{i}.json"), "Command exited with code 1.\n"));
            Assert.Equal(StallLevel.None, tracker.Observe("read_file", $$"""{"file_path":"f{{i}}.txt"}""", $"text {i}"));
        }

        // The same question asked again and again is still a loop, and so is a failure that says something.
        var level = StallLevel.None;
        for (var i = 0; i < 8; i++) level = tracker.Observe("run_shell_command", Shell("grep -rn 'symbolA' src"), "Command exited with code 1.\n");
        Assert.Equal(StallLevel.Stop, level);

        var said = new StallTracker(4, 8);
        var calls = 0;
        level = StallLevel.None;
        while (calls < 100 && level != StallLevel.Stop)
            level = said.Observe("run_shell_command", Shell($"make target{calls++}"), "Command exited with code 2.\nmake: *** No rule to make target. Stop.");
        Assert.Equal(12, calls);
        Assert.True(said.SameFailure);
    }

    [Fact]
    public void AFileWrittenWithAnUnspacedRedirectIsProgress()
    {
        var tracker = new StallTracker(4, 8);
        for (var i = 0; i < 25; i++)
        {
            Assert.Equal(StallLevel.None, tracker.Observe("run_shell_command", Json($"echo \"v{i}\">f{i}.txt"), "(no output)"));
            Assert.Equal(StallLevel.None, tracker.Observe("run_shell_command", Shell("dotnet build -v q"), "(no output)"));
        }
    }

    [Fact]
    public void AWriteInAHeredocOfTwentyFourKilobytesIsProgressToo()
    {
        var tracker = new StallTracker(4, 8);
        var body = new string('x', 24_000);
        for (var i = 0; i < 25; i++)
        {
            Assert.Equal(StallLevel.None, tracker.Observe("run_shell_command", Json($"cat > part{i}.py <<'EOF'\n# {i}\n{body}\nEOF"), "(no output)"));
            Assert.Equal(StallLevel.None, tracker.Observe("run_shell_command", Shell("python -m pyflakes ."), "(no output)"));
        }
    }

    [Fact]
    public void ARunOfAHeredocScriptThatReadsIsNotProgressButOneThatWritesIs()
    {
        var reads = new StallTracker(4, 8);
        var level = StallLevel.None;
        for (var i = 0; i < 40 && level != StallLevel.Stop; i++)
        {
            reads.Observe("run_shell_command", Json($"python3 - <<'EOF'\nx = {i}\nif x > 3: print(x)\nEOF"), $"{i}");
            level = reads.Observe("run_shell_command", Shell("git status --short"), "(no output)");
        }
        Assert.Equal(StallLevel.Stop, level);

        var writes = new StallTracker(4, 8);
        for (var i = 0; i < 40; i++)
        {
            writes.Observe("run_shell_command", Json($"python3 - <<'EOF'\nopen('f{i}.txt', 'w').write('x')\nEOF"), "(no output)");
            Assert.Equal(StallLevel.None, writes.Observe("run_shell_command", Shell("git status --short"), "(no output)"));
        }
    }

    [Fact]
    public void ADirectoryPathEndingInABackslashDoesNotHideWhatComesAfterItInPowerShell()
    {
        var tracker = new StallTracker(4, 8, ShellKind.PowerShell);
        for (var i = 0; i < 15; i++)
            Assert.Equal(StallLevel.None, tracker.Observe("run_shell_command", Json("Set-Location \"C:\\proj\\\"; Start-Sleep 5; gh run view 1"), "in progress"));

        var editing = new StallTracker(4, 8, ShellKind.PowerShell);
        for (var i = 0; i < 25; i++)
        {
            Assert.Equal(StallLevel.None, editing.Observe("run_shell_command", Json($"cd \"C:\\p\\\" ; Remove-Item out{i} -Recurse"), "(no output)"));
            Assert.Equal(StallLevel.None, editing.Observe("run_shell_command", Shell("dotnet build -v q"), "(no output)"));
        }
    }

    [Fact]
    public void AGeneratorRunWithANewArgumentEachTimeIsProgressButTheSameOneAgainIsNot()
    {
        var tracker = new StallTracker(4, 8);
        for (var i = 0; i < 30; i++)
        {
            Assert.Equal(StallLevel.None, tracker.Observe("run_shell_command", Shell($"python tools/gen.py --module m{i}"), "generated"));
            Assert.Equal(StallLevel.None, tracker.Observe("run_shell_command", Shell("go build ./..."), "(no output)"));
        }

        var same = new StallTracker(4, 8);
        var level = StallLevel.None;
        var rounds = 0;
        while (rounds < 30 && level != StallLevel.Stop)
        {
            same.Observe("run_shell_command", Shell("python tools/gen.py --module m1"), "generated");
            level = same.Observe("run_shell_command", Shell("go build ./..."), "(no output)");
            rounds++;
        }
        Assert.Equal(StallLevel.Stop, level);
        Assert.Equal(8, rounds);
    }

    [Fact]
    public void AFailingGeneratorIsNotProgress()
    {
        var tracker = new StallTracker(4, 8);
        var level = StallLevel.None;
        for (var i = 0; i < 12 && level != StallLevel.Stop; i++)
        {
            tracker.Observe("run_shell_command", Shell($"python tools/gen.py --module m{i}"), "Command exited with code 1.\nTraceback: boom");
            level = tracker.Observe("run_shell_command", Shell("go build ./..."), "(no output)");
        }
        Assert.Equal(StallLevel.Stop, level);
    }

    [Fact]
    public void TwoLongFailingRunsThatDifferOnlyAtTheEndAreNotTheSameResult()
    {
        var warnings = string.Join("\n", Enumerable.Range(1, 100).Select(n => $"warning CS{1000 + n}: unused variable 'x{n}' in Program.cs({n},1)"));
        string Output(int actual) => "Command exited with code 1.\n" + warnings + $"\nExpected: 42, Actual: {actual}\nFailed!  - Failed: 1, Passed: 11, Skipped: 0, Total: 12";
        Assert.True(Output(31).Length > 4_000);
        Assert.NotEqual(StallTracker.ResultKey("run_shell_command", Output(31)), StallTracker.ResultKey("run_shell_command", Output(32)));
        Assert.Equal(StallTracker.ResultKey("run_shell_command", Output(31)), StallTracker.ResultKey("run_shell_command", Output(31)));

        var tracker = new StallTracker(4, 8);
        for (var attempt = 31; attempt < 60; attempt++)
        {
            tracker.Observe("edit_file", $$"""{"file_path":"a.cs","old_string":"x","new_string":"{{attempt}}"}""", StallTracker.ResultKey("edited"), failed: false, changedFiles: true);
            Assert.NotEqual(StallLevel.Stop, tracker.Observe("run_shell_command", Shell("dotnet test"), Output(attempt)));
        }
    }

    [Fact]
    public void SubagentsThatKeepFailingTheSameWayAreALoopWhateverTheyWereAsked()
    {
        var tracker = new StallTracker(4, 8);
        var level = StallLevel.None;
        var calls = 0;
        while (calls < 60 && level != StallLevel.Stop)
        {
            calls++;
            level = tracker.Observe("agent", $$"""{"description":"module {{calls}}","prompt":"port module {{calls}}"}""",
                $"Subagent 'module {calls}' failed: the model server is not answering");
        }
        Assert.Equal(12, calls);
        Assert.True(tracker.SameFailure);
        Assert.Equal(StallTracker.ResultKey("agent", "Subagent 'one' finished.\n\nreport"), StallTracker.ResultKey("agent", "Subagent 'two' finished.\n\nreport"));
        Assert.NotEqual(StallTracker.ResultKey("agent", "Subagent 'one' finished.\n\nreport"), StallTracker.ResultKey("agent", "Subagent 'one' finished.\n\nanother report"));
    }

    [Theory]
    [InlineData("Subagent 'port it' failed: the server said no", true)]
    [InlineData("Subagent 'port it' finished.\n\nreport", false)]
    [InlineData("Subagent 'a' finished.\n\nthe build failed: see above", false)]
    public void AFailedSubagentIsAFailure(string output, bool failed) => Assert.Equal(failed, StallTracker.Failed("agent", output));

    // MARK: - Loops through the engine

    [Fact]
    public async Task AFourStepLoopEndsARunLongBeforeTheStepLimit()
    {
        var turns = Enumerable.Range(1, 40).SelectMany(n => new[]
        {
            Turn.Calling(new ToolCall($"e{n}", "edit_file", """{"file_path":"f.cs","old_string":"A","new_string":"B"}""")),
            Turn.Calling(new ToolCall($"t{n}", "run_shell_command", Shell("dotnet test"))),
            Turn.Calling(new ToolCall($"r{n}", "edit_file", """{"file_path":"f.cs","old_string":"B","new_string":"A"}""")),
            Turn.Calling(new ToolCall($"u{n}", "run_shell_command", Shell("dotnet test --no-build"))),
        });
        var client = new ScriptedClient(turns);
        var build = new Answering("run_shell_command", n => new ToolResult($"Command exited with code 1.\nFailed: {n % 2}"));

        var result = await Make(client, Edits(), build).RunAsync([], "fix the test");

        Assert.True(result.Stalled);
        Assert.Equal(45, client.Requests.Count);
        var answers = result.Messages.Where(m => m.Role == MessageRole.Tool).Select(m => m.Content ?? "").ToList();
        Assert.Contains(answers, a => a.Contains("Harness note") && a.Contains("last 24 steps"));
    }

    [Fact]
    public async Task AFailedWorkerIsNotProgress()
    {
        var turns = Enumerable.Range(1, 30).SelectMany(n => new[]
        {
            Turn.Calling(new ToolCall($"a{n}", "agent", $$"""{"description":"module {{n}}","prompt":"port module {{n}}","agent_type":"worker"}""")),
            Turn.Calling(new ToolCall($"b{n}", "run_shell_command", Shell("go build ./..."))),
        });
        var client = new ScriptedClient(turns);
        var agent = new Answering("agent", n => new ToolResult($"Subagent 'module {n}' failed: the model server is not answering"));
        var build = new Answering("run_shell_command", _ => new ToolResult("(no output)"));

        var result = await Make(client, agent, build).RunAsync([], "port them all");

        Assert.True(result.Stalled);
        Assert.Equal(16, client.Requests.Count); // the 8th quiet build
    }
}
