using System.Text;

namespace Dsh.Core.Tests;

/// <summary>New: how each shell is launched. PowerShell gets the command as -EncodedCommand (no quoting
/// rules to get wrong) wrapped so failures become exit codes; very long scripts go through a file.</summary>
public sealed class AgentShellTests
{
    private static readonly string[] CommonPowerShellFlags =
        ["-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-OutputFormat", "Text"];

    private static string Decode(string encoded) => Encoding.Unicode.GetString(Convert.FromBase64String(encoded));

    [Fact]
    public void EncodedCommandDecodesToTheCommandInsideTheExitCodeWrapper()
    {
        const string command = "Get-ChildItem | Select-Object -First 3";
        var args = AgentShell.PowerShellArguments(command).ToArray();

        string[] flags = [.. CommonPowerShellFlags, "-EncodedCommand"];
        Assert.Equal(flags, args[..^1]);
        var script = Decode(args[^1]);
        var lines = script.Split(Environment.NewLine);
        Assert.Contains(command, lines);
        Assert.Contains("$__dshOk = $?", lines);
        Assert.Contains("if ($LASTEXITCODE -is [int] -and $LASTEXITCODE -ne 0) { exit $LASTEXITCODE }", lines);
        Assert.Contains("if (-not $__dshOk) { exit 1 }", lines);
        Assert.Contains("exit 0", lines);
        Assert.Contains("$ProgressPreference = 'SilentlyContinue'", lines);
        // The command runs first, then its status is captured.
        Assert.True(Array.IndexOf(lines, command) < Array.IndexOf(lines, "$__dshOk = $?"));
        Assert.Contains("[Console]::OutputEncoding = [System.Text.Encoding]::UTF8", script);
        Assert.Contains("if ($PSStyle) { $PSStyle.OutputRendering = 'PlainText' }", lines);
    }

    [Fact]
    public void ErrorsComeBackAsTextNotClixml()
    {
        // With -EncodedCommand and redirected streams PowerShell defaults to CLIXML on stderr.
        var args = AgentShell.PowerShellArguments("Write-Error boom").ToArray();
        var format = Array.IndexOf(args, "-OutputFormat");
        Assert.True(format >= 0);
        Assert.Equal("Text", args[format + 1]);
        Assert.True(format < Array.IndexOf(args, "-EncodedCommand"));
    }

    [Fact]
    public void EncodingPreservesQuotesNewlinesAndUnicode()
    {
        const string command = "Write-Output 'it''s \"quoted\" — naïve 😀'\n$x = 1\nWrite-Output \"$x & `$y | %\"";
        var script = Decode(AgentShell.PowerShellArguments(command).Last());
        Assert.Contains(command, script);
    }

    [Fact]
    public void LongCommandsSwitchToATemporaryScriptFile()
    {
        var command = "Write-Output '" + new string('x', 20_000) + "'";
        var args = AgentShell.PowerShellArguments(command).ToArray();
        var file = args[^1];
        try
        {
            string[] flags = [.. CommonPowerShellFlags, "-File"];
            Assert.Equal(flags, args[..^1]);
            Assert.DoesNotContain("-EncodedCommand", args);
            Assert.True(File.Exists(file));
            Assert.Equal(".ps1", Path.GetExtension(file));
            Assert.Equal(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), Path.GetDirectoryName(file));
            // UTF-8 with a BOM: Windows PowerShell 5.1 reads a BOM-less script as the ANSI code page.
            var bytes = File.ReadAllBytes(file);
            Assert.Equal([0xEF, 0xBB, 0xBF], bytes.Take(3));
            var script = File.ReadAllText(file);
            Assert.Contains(command, script);
            Assert.Contains("$__dshOk = $?", script);
            // The script removes itself first, so %TEMP% doesn't collect one per long command.
            Assert.StartsWith("Remove-Item -LiteralPath $PSCommandPath", script);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void CommandsUpToTheCommandLineBudgetStayEncoded()
    {
        var args = AgentShell.PowerShellArguments(new string('x', 9_000)).ToArray();
        Assert.Equal("-EncodedCommand", args[^2]);
        Assert.True(args[^1].Length < 28_000);
    }

    [Fact]
    public void BashGetsTheCommandAsOneArgument()
    {
        using var dir = new TempDirectory();
        var psi = new AgentShell(ShellKind.Bash, "/bin/bash", "bash").CreateStartInfo("echo 'a b' && ls", dir.Path);

        Assert.Equal("/bin/bash", psi.FileName);
        Assert.Equal(["-c", "echo 'a b' && ls"], psi.ArgumentList);
        Assert.Equal(dir.Path, psi.WorkingDirectory);
        AssertCommonStartInfo(psi);
    }

    [Fact]
    public void CmdGetsTheCommandVerbatimAfterAUtf8CodePageSwitch()
    {
        var psi = new AgentShell(ShellKind.Cmd, @"C:\Windows\System32\cmd.exe", "Command Prompt (cmd.exe)")
            .CreateStartInfo("dir /b \"My Files\"", Path.GetTempPath());

        Assert.Equal("/d /s /c \"chcp 65001>nul & dir /b \"My Files\"\"", psi.Arguments);
        Assert.Empty(psi.ArgumentList);
        AssertCommonStartInfo(psi);
    }

    [Fact]
    public void PowerShellGetsTheEncodedScript()
    {
        var psi = new AgentShell(ShellKind.PowerShell, "pwsh.exe", "PowerShell 7 (pwsh)").CreateStartInfo("Get-Date", Path.GetTempPath());

        Assert.Equal("pwsh.exe", psi.FileName);
        Assert.Equal("-EncodedCommand", psi.ArgumentList[^2]);
        Assert.Contains("Get-Date", Decode(psi.ArgumentList[^1]));
        AssertCommonStartInfo(psi);
    }

    private static void AssertCommonStartInfo(System.Diagnostics.ProcessStartInfo psi)
    {
        Assert.False(psi.UseShellExecute);
        Assert.True(psi.CreateNoWindow);
        Assert.True(psi.RedirectStandardInput); // closed by the runner: nothing can wait for input
        Assert.True(psi.RedirectStandardOutput);
        Assert.True(psi.RedirectStandardError);
        Assert.Equal("1", psi.Environment["DSH_AGENT"]);
        Assert.Equal("utf-8", psi.Environment["PYTHONIOENCODING"]);
    }

    [Fact]
    public void SyntaxHintsMatchTheShell()
    {
        Assert.Contains("NOT available in 5.1",
            new AgentShell(ShellKind.PowerShell, @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", "Windows PowerShell 5.1").SyntaxHint);
        Assert.Contains("PowerShell 7", new AgentShell(ShellKind.PowerShell, @"C:\Program Files\PowerShell\7\pwsh.exe", "PowerShell 7").SyntaxHint);
        Assert.Contains("cmd.exe /c", new AgentShell(ShellKind.Cmd, "cmd.exe", "cmd").SyntaxHint);
        Assert.Contains("bash -c", new AgentShell(ShellKind.Bash, "/bin/bash", "bash").SyntaxHint);
    }

    [Fact]
    public void FindOnPathReturnsNullForUnknownTools()
    {
        Assert.Null(AgentShell.FindOnPath($"dsh-no-such-tool-{Guid.NewGuid():N}.exe"));
    }

    [UnixFact]
    public void DefaultShellElsewhereIsBash()
    {
        var shell = AgentShell.Default;
        Assert.Equal(ShellKind.Bash, shell.Kind);
        Assert.True(File.Exists(shell.Executable));
        // Windows preferences fall back to bash off Windows.
        Assert.Equal(ShellKind.Bash, AgentShell.Resolve(AgentShell.CmdPreference).Kind);
        Assert.Equal(shell.Executable, AgentShell.DefaultTerminalCommandLine());
    }

    [WindowsFact]
    public void DefaultShellOnWindowsIsPowerShell()
    {
        Assert.Equal(ShellKind.PowerShell, AgentShell.Default.Kind);
        Assert.Equal(ShellKind.PowerShell, AgentShell.Resolve(AgentShell.WindowsPowerShellPreference).Kind);
        Assert.Equal("Windows PowerShell 5.1", AgentShell.Resolve(AgentShell.WindowsPowerShellPreference).DisplayName);
        var cmd = AgentShell.Resolve(AgentShell.CmdPreference);
        Assert.Equal(ShellKind.Cmd, cmd.Kind);
        Assert.EndsWith("cmd.exe", cmd.Executable, StringComparison.OrdinalIgnoreCase);
        // Anything that isn't a keyword is a command line the user typed.
        Assert.Equal("my-shell.exe --flag", AgentShell.DefaultTerminalCommandLine("my-shell.exe --flag"));
    }
}
