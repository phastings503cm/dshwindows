using System.Text;

namespace Dsh.Core.Tests;

/// <summary>New: how a background command is put on a pseudo console's command line in each agent shell.
/// Pure string work, so it is checked on every OS even though only Windows launches it.</summary>
public sealed class BackgroundCommandLineTests
{
    private static string Decode(string encoded) => Encoding.Unicode.GetString(Convert.FromBase64String(encoded));

    [Fact]
    public void PowerShellGetsTheEncodedCommandWithoutNonInteractive()
    {
        const string command = "$name = Read-Host 'name'; Write-Output \"hi $name\"";
        var args = BackgroundCommandLine.PowerShellArguments(command).ToArray();
        Assert.Equal(["-NoLogo", "-NoProfile", "-ExecutionPolicy", "Bypass", "-EncodedCommand"], args[..^1]);
        // A background process may prompt: -NonInteractive would make Read-Host fail.
        Assert.DoesNotContain("-NonInteractive", args);

        var lines = Decode(args[^1]).Split(Environment.NewLine);
        Assert.Contains(command, lines);
        Assert.Contains("$ProgressPreference = 'SilentlyContinue'", lines);
        Assert.Contains("if ($LASTEXITCODE -is [int] -and $LASTEXITCODE -ne 0) { exit $LASTEXITCODE }", lines);
        Assert.Contains("if (-not $__dshOk) { exit 1 }", lines);
        Assert.True(Array.IndexOf(lines, command) < Array.IndexOf(lines, "$__dshOk = $?"));
    }

    [Fact]
    public void AVeryLongPowerShellCommandGoesThroughASelfDeletingFile()
    {
        var command = "Write-Output '" + new string('x', 20_000) + "'";
        var args = BackgroundCommandLine.PowerShellArguments(command).ToArray();
        Assert.Equal("-File", args[^2]);
        var file = args[^1];
        try
        {
            var script = File.ReadAllText(file);
            Assert.StartsWith("Remove-Item -LiteralPath $PSCommandPath", script);
            Assert.Contains(command, script);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void CommandLinePerShell()
    {
        var pwsh = new AgentShell(ShellKind.PowerShell, @"C:\Program Files\PowerShell\7\pwsh.exe", "PowerShell 7");
        var line = BackgroundCommandLine.For(pwsh, "npm run dev");
        Assert.StartsWith("\"C:\\Program Files\\PowerShell\\7\\pwsh.exe\" -NoLogo -NoProfile -ExecutionPolicy Bypass -EncodedCommand ", line);
        Assert.Contains("npm run dev", Decode(line.Split(' ')[^1]));

        var cmd = new AgentShell(ShellKind.Cmd, @"C:\Windows\system32\cmd.exe", "cmd");
        Assert.Equal("C:\\Windows\\system32\\cmd.exe /d /s /c \"chcp 65001>nul & echo \"hi\" && dir\"",
                     BackgroundCommandLine.For(cmd, "echo \"hi\" && dir"));

        var bash = new AgentShell(ShellKind.Bash, @"C:\Program Files\Git\bin\bash.exe", "Git Bash");
        Assert.Equal("\"C:\\Program Files\\Git\\bin\\bash.exe\" -c \"echo \\\"a b\\\" | grep a\"",
                     BackgroundCommandLine.For(bash, "echo \"a b\" | grep a"));
    }

    [Theory]
    [InlineData("simple")]
    [InlineData("")]
    [InlineData("two words")]
    [InlineData("say \"hi\"")]
    [InlineData(@"C:\path with space\")]
    [InlineData(@"a\\""b")]
    [InlineData(@"trailing\\")]
    [InlineData("tab\there")]
    [InlineData(@"\\server\share\""quoted"" dir\")]
    public void QuotedArgumentsSplitBackUnchanged(string argument)
    {
        var line = "prog.exe " + BackgroundCommandLine.QuoteArgument(argument) + " next";
        Assert.Equal(["prog.exe", argument, "next"], SplitLikeWindows(line));
    }

    [Fact]
    public void EnvironmentMarksTheAgentAndLetsTheModelOverride()
    {
        var launch = new ProcessLaunch("x", "/", new Dictionary<string, string> { ["DSH_AGENT"] = "custom", ["PORT"] = "3000" },
                                       80, 24, AgentShell.Default);
        var environment = BackgroundCommandLine.EnvironmentFor(launch, [new("TERM", "dumb")]);
        Assert.Equal("utf-8", environment["PYTHONIOENCODING"]);
        Assert.Equal("custom", environment["DSH_AGENT"]);
        Assert.Equal("3000", environment["PORT"]);
        Assert.Equal("dumb", environment["TERM"]);
    }

    /// <summary>The MSVC runtime's argument splitting (what CommandLineToArgvW and Git Bash do).</summary>
    private static List<string> SplitLikeWindows(string commandLine)
    {
        var args = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        var hasArgument = false;
        for (var i = 0; i < commandLine.Length; i++)
        {
            var c = commandLine[i];
            if (c == '\\')
            {
                var start = i;
                while (i < commandLine.Length && commandLine[i] == '\\') i++;
                var count = i - start;
                if (i < commandLine.Length && commandLine[i] == '"')
                {
                    current.Append('\\', count / 2);
                    if (count % 2 == 1) current.Append('"');
                    else inQuotes = !inQuotes;
                }
                else
                {
                    current.Append('\\', count);
                    i--;
                }
                hasArgument = true;
            }
            else if (c == '"')
            {
                if (inQuotes && i + 1 < commandLine.Length && commandLine[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
                hasArgument = true;
            }
            else if (!inQuotes && c is ' ' or '\t')
            {
                if (hasArgument) args.Add(current.ToString());
                current.Clear();
                hasArgument = false;
            }
            else
            {
                current.Append(c);
                hasArgument = true;
            }
        }
        if (hasArgument) args.Add(current.ToString());
        return args;
    }
}
