using System.Text.RegularExpressions;

namespace Dsh.Core.Tests;

/// <summary>A pretend Spark: canned command output, and a shell that plays a script in reaction to
/// what is typed into it.</summary>
public sealed class FakeSparkConnection : ISparkConnection
{
    public string Host => "192.168.1.42";
    public string Username => "alice";
    public List<string> Commands { get; } = [];
    public Func<string, RemoteResult> Run { get; set; } = _ => new RemoteResult(0, "", "");
    /// <summary>Called with everything typed into the shell; returns what the shell prints back.</summary>
    public Func<string, FakeShell, IEnumerable<string>> React { get; set; } = (_, _) => [];
    public FakeShell? Shell { get; private set; }

    public Task<RemoteResult> RunAsync(string command, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Commands.Add(command);
        return Task.FromResult(Run(command));
    }

    public Task<ISparkShell> OpenShellAsync(CancellationToken cancellationToken)
    {
        Shell = new FakeShell(this);
        Shell.Emit("Welcome to DGX OS 7 (GNU/Linux 6.11.0-1016-nvidia aarch64)\r\n\r\nalice@spark-3f2a:~$ ");
        return Task.FromResult<ISparkShell>(Shell);
    }

    public void Dispose()
    {
    }
}

public sealed class FakeShell(FakeSparkConnection owner) : ISparkShell
{
    private readonly System.Threading.Channels.Channel<string?> _output = System.Threading.Channels.Channel.CreateUnbounded<string?>();
    public List<string> Typed { get; } = [];
    public bool Disposed { get; private set; }

    public void Emit(string text) => _output.Writer.TryWrite(text);
    public void Close() => _output.Writer.TryWrite(null);

    public async Task<string?> ReadAsync(CancellationToken cancellationToken) => await _output.Reader.ReadAsync(cancellationToken);

    public Task WriteAsync(string text, CancellationToken cancellationToken)
    {
        Typed.Add(text);
        foreach (var chunk in owner.React(text, this)) Emit(chunk);
        return Task.CompletedTask;
    }

    public void Dispose() => Disposed = true;
}

public sealed partial class SparkRemoteTests
{
    private const string Nonce = "n0nce";
    private static readonly string Begin = RemoteTranscript.BeginMarker(Nonce);
    private static readonly string End = RemoteTranscript.EndMarker(Nonce);

    // MARK: - Transcript

    [Fact]
    public void IgnoresEverythingBeforeTheBeginMarker()
    {
        var transcript = new RemoteTranscript(Nonce, "hunter22");
        var events = transcript.Feed($"Welcome to DGX OS\r\nalice@spark:~$ echo {Begin}; curl … | bash; echo \"{End} $?\"\r\n{Begin}\r\n==> git 2.43 present.\r\n");
        Assert.True(transcript.Started);
        Assert.Equal([new TranscriptEvent.Line("==> git 2.43 present.")], events);
    }

    [Fact]
    public void StripsColoursAndAppliesCarriageReturns()
    {
        var transcript = new RemoteTranscript(Nonce, null);
        transcript.Feed($"{Begin}\n");
        var events = transcript.Feed("\u001b[1;32m==> Cloning\u001b[0m\r\nReceiving objects:  10%\rReceiving objects: 100%\r\n");
        Assert.Equal(["==> Cloning", "Receiving objects: 100%"], events.OfType<TranscriptEvent.Line>().Select(l => l.Text));
    }

    [Fact]
    public void LinesSplitAcrossChunksArriveWhole()
    {
        var transcript = new RemoteTranscript(Nonce, null);
        transcript.Feed($"{Begin}\r");
        Assert.Empty(transcript.Feed("\n==> Instal"));
        Assert.Equal([new TranscriptEvent.Line("==> Installing")], transcript.Feed("ling\r\n"));
    }

    [Fact]
    public void AnswersASudoPromptOnceAndReportsTheExitCode()
    {
        var transcript = new RemoteTranscript(Nonce, "hunter22");
        transcript.Feed($"{Begin}\r\n");
        var prompt = transcript.Feed("[sudo] password for alice: ");
        Assert.Contains(prompt, e => e is TranscriptEvent.PasswordPrompt);
        Assert.Contains(new TranscriptEvent.Line("[sudo] password for alice: (answered by DSH)"), prompt);
        // More output on the same (still unfinished) line must not trigger a second answer.
        Assert.DoesNotContain(transcript.Feed(" "), e => e is TranscriptEvent.PasswordPrompt);
        transcript.Feed("\r\n==> installing app to /opt/spark-swapper\r\n");
        var end = transcript.Feed($"{End} 0\r\n");
        Assert.Equal([new TranscriptEvent.Finished(0)], end);
        Assert.Equal(0, transcript.ExitCode);
        Assert.DoesNotContain(transcript.Lines, l => l.Contains(End));
    }

    [Fact]
    public void AGenericPasswordPromptCountsToo()
    {
        var transcript = new RemoteTranscript(Nonce, "pw");
        transcript.Feed($"{Begin}\n");
        Assert.Contains(transcript.Feed("Password: "), e => e is TranscriptEvent.PasswordPrompt);
    }

    [Fact]
    public void WrongPasswordAndNotAnAdmin()
    {
        var wrong = new RemoteTranscript(Nonce, "pw");
        wrong.Feed($"{Begin}\n[sudo] password for alice: ");
        var events = wrong.Feed("\r\nSorry, try again.\r\n[sudo] password for alice: ");
        Assert.Contains(new TranscriptEvent.Problem(TranscriptProblem.WrongPassword), events);
        Assert.DoesNotContain(events, e => e is TranscriptEvent.PasswordPrompt);

        var notAdmin = new RemoteTranscript(Nonce, "pw");
        notAdmin.Feed($"{Begin}\n");
        Assert.Contains(new TranscriptEvent.Problem(TranscriptProblem.NotAllowed),
            notAdmin.Feed("alice is not in the sudoers file.  This incident will be reported.\r\n"));
    }

    [Fact]
    public void GivesUpAfterThreePrompts()
    {
        var transcript = new RemoteTranscript(Nonce, "pw");
        transcript.Feed($"{Begin}\n");
        for (var i = 0; i < RemoteTranscript.MaxPasswordPrompts; i++)
            Assert.Contains(transcript.Feed("\nPassword: "), e => e is TranscriptEvent.PasswordPrompt);
        var fourth = transcript.Feed("\nPassword: ");
        Assert.DoesNotContain(fourth, e => e is TranscriptEvent.PasswordPrompt);
        Assert.Contains(new TranscriptEvent.Problem(TranscriptProblem.TooManyPrompts), fourth);
        Assert.Equal(TranscriptProblem.TooManyPrompts, transcript.Problem);
    }

    [Fact]
    public void NeverShowsThePassword()
    {
        var transcript = new RemoteTranscript(Nonce, "hunter22");
        transcript.Feed($"{Begin}\n");
        var events = transcript.Feed("oops: hunter22 was echoed\n");
        Assert.Equal([new TranscriptEvent.Line("oops: •••••• was echoed")], events);
    }

    [Fact]
    public void TheEchoedCommandLineIsNotTheEnd()
    {
        var wrapped = RemoteTranscript.Wrap("true", Nonce);
        Assert.EndsWith("\n", wrapped);
        Assert.Single(wrapped.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        var transcript = new RemoteTranscript(Nonce, null);
        transcript.Feed($"{Begin}\n");
        // If a shell ever echoed the command after the begin marker, "$?" is not an exit code.
        Assert.DoesNotContain(transcript.Feed(wrapped.Replace("\n", "\r\n")), e => e is TranscriptEvent.Finished);
        Assert.Null(transcript.ExitCode);
    }

    // MARK: - What is on the Spark

    private const string ProbeOutput = """
        @@os
        PRETTY_NAME="Ubuntu 24.04.3 LTS"
        NAME="Ubuntu"
        VERSION_ID="24.04"
        @@dgx
        DGX_NAME="DGX Spark"
        DGX_OTA_VERSION="7.2.3"
        @@host
        spark-3f2a
        @@ips
        192.168.1.42 172.17.0.1 fe80::1
        @@arch
        aarch64
        @@groups
        alice adm cdrom sudo dip plugdev docker
        @@swapper
        inactive
        @@swapper_unit
        @@docker
        Docker version 28.3.3, build 980b856
        @@gpu
        NVIDIA GB10, 580.95.05
        @@front
        @@tools
        curl
        git
        python3
        @@end
        """;

    [Fact]
    public void ParsesTheProbe()
    {
        var state = SparkMachineState.Parse(ProbeOutput);
        Assert.Equal("Ubuntu 24.04.3 LTS", state.OsName);
        Assert.Equal("24.04", state.OsVersion);
        Assert.True(state.IsDgxOs);
        Assert.Equal("spark-3f2a", state.HostName);
        Assert.Equal(["192.168.1.42", "172.17.0.1"], state.Addresses);
        Assert.Equal("aarch64", state.Architecture);
        Assert.True(state.CanSudo);
        Assert.False(state.SwapperActive);
        Assert.False(state.SwapperInstalled);
        Assert.Equal("Docker version 28.3.3, build 980b856", state.DockerVersion);
        Assert.Equal("NVIDIA GB10, 580.95.05", state.Gpu);
        Assert.False(state.ModelFrontListening);
        Assert.True(state.HasCurl);
    }

    [Fact]
    public void ParsesAnInstalledSpark()
    {
        var output = ProbeOutput.Replace("@@swapper\ninactive", "@@swapper\nactive").Replace("@@front\n", "@@front\nlistening\n")
            .Replace("alice adm cdrom sudo dip plugdev docker", "bob users");
        var state = SparkMachineState.Parse(output);
        Assert.True(state.SwapperActive);
        Assert.True(state.SwapperInstalled);
        Assert.True(state.ModelFrontListening);
        Assert.False(state.CanSudo);
    }

    [Fact]
    public void TheProbeScriptIsOneLineOfShell()
    {
        Assert.DoesNotContain('\n', SparkMachineState.ProbeScript);
        Assert.Contains("printf '@@os\\n'", SparkMachineState.ProbeScript);
        Assert.EndsWith("printf '@@end\\n'", SparkMachineState.ProbeScript);
    }

    // MARK: - The installer, end to end against a pretend Spark

    [GeneratedRegex(@"__DSH_BEGIN_(\w+)__")]
    private static partial Regex BeginNonce();

    /// <summary>A Spark whose shell behaves like bash running install-swapper.sh.</summary>
    private static FakeSparkConnection Spark(string password, Func<string, IEnumerable<string>> afterPassword, bool asksPassword = true)
    {
        var spark = new FakeSparkConnection();
        string? nonce = null;
        spark.React = (typed, shell) =>
        {
            if (typed.StartsWith("exec env PS1=", StringComparison.Ordinal))
            {
                var ready = Regex.Match(typed, @"READY_(\w+)__").Groups[1].Value;
                return [$"__DSH_READY_{ready}__ "];
            }
            if (BeginNonce().Match(typed) is { Success: true } begin)
            {
                nonce = begin.Groups[1].Value;
                var output = new List<string> { $"{RemoteTranscript.BeginMarker(nonce)}\r\n", "\u001b[1;32m==> git 2.43.0 present.\u001b[0m\r\n" };
                if (asksPassword) output.Add("[sudo] password for alice: ");
                else output.AddRange(afterPassword(nonce));
                return output;
            }
            if (typed == password + "\n") return afterPassword(nonce!);
            if (typed.EndsWith('\n') && typed.TrimEnd().Length > 0 && typed != "exit\n") return ["\r\nSorry, try again.\r\n[sudo] password for alice: "];
            return [];
        };
        return spark;
    }

    [Fact]
    public async Task InstallsTheSwapperAnsweringSudo()
    {
        var spark = Spark("hunter22", nonce =>
        [
            "\r\n==> installing app to /opt/spark-swapper\r\n",
            "  Spark Swapper is up:  https://192.168.1.42:8999\r\n",
            $"{RemoteTranscript.EndMarker(nonce)} 0\r\n",
        ]);
        var installer = new SparkInstaller(spark, "hunter22");
        var lines = new List<string>();
        installer.Output += lines.Add;

        var outcome = await installer.InstallSwapperAsync();

        Assert.True(outcome.Success, outcome.Summary);
        Assert.Equal(0, outcome.ExitCode);
        var typed = spark.Shell!.Typed;
        Assert.StartsWith("exec env PS1='__DSH_''READY_", typed[0]);
        Assert.Contains(SparkInstaller.SwapperInstallCommand, typed[1]);
        Assert.Single(typed, t => t == "hunter22\n");
        Assert.Equal("exit\n", typed[^1]);
        Assert.Contains("==> git 2.43.0 present.", lines);
        Assert.Contains("[sudo] password for alice: (answered by DSH)", lines);
        Assert.Contains(lines, l => l.Contains("Spark Swapper is up"));
        Assert.DoesNotContain(lines, l => l.Contains("hunter22"));
        Assert.DoesNotContain(lines, l => l.Contains("__DSH_"));
    }

    [Fact]
    public async Task AWrongSudoPasswordStopsTheInstall()
    {
        var spark = Spark("right", _ => []);
        var outcome = await new SparkInstaller(spark, "wrong").InstallSwapperAsync();
        Assert.False(outcome.Success);
        Assert.Equal(TranscriptProblem.WrongPassword, outcome.Problem);
        Assert.Contains("password", outcome.Summary);
        Assert.Contains("\u0003", spark.Shell!.Typed);
        Assert.Single(spark.Shell.Typed, t => t == "wrong\n");
    }

    [Fact]
    public async Task AFailingInstallerIsReportedWithItsExitCode()
    {
        var spark = Spark("pw", nonce => ["Service did not answer; see: journalctl -u spark-swapper -n 50\r\n", $"{RemoteTranscript.EndMarker(nonce)} 1\r\n"]);
        spark.Run = _ => new RemoteResult(3, "failed\n", "");
        var outcome = await new SparkInstaller(spark, "pw").InstallSwapperAsync();
        Assert.False(outcome.Success);
        Assert.Equal(1, outcome.ExitCode);
        Assert.Contains("exit code 1", outcome.Summary);
    }

    [Fact]
    public async Task ExitZeroWithoutTheBannerIsCheckedWithSystemd()
    {
        var spark = Spark("pw", nonce => [$"{RemoteTranscript.EndMarker(nonce)} 0\r\n"], asksPassword: false);
        spark.Run = command => command.Contains("is-active") ? new RemoteResult(0, "active\n", "") : new RemoteResult(0, "", "");
        var outcome = await new SparkInstaller(spark, "pw").InstallSwapperAsync();
        Assert.True(outcome.Success);
        Assert.Contains(spark.Commands, c => c.Contains("systemctl is-active spark-swapper"));
    }

    [Fact]
    public async Task TheShellClosingEarlyIsAFailure()
    {
        var spark = Spark("pw", _ => [], asksPassword: false);
        spark.React = (typed, shell) =>
        {
            if (typed.StartsWith("exec env", StringComparison.Ordinal)) return [$"__DSH_READY_{Regex.Match(typed, @"READY_(\w+)__").Groups[1].Value}__ "];
            shell.Close();
            return [];
        };
        spark.Run = _ => new RemoteResult(3, "inactive\n", "");
        var outcome = await new SparkInstaller(spark, "pw").InstallSwapperAsync();
        Assert.False(outcome.Success);
        Assert.Null(outcome.ExitCode);
    }

    [Fact]
    public async Task SetsUpTheModelFrontWithAnUploadedScript()
    {
        var spark = Spark("pw", nonce => ["==> Writing /etc/nginx/conf.d/spark-model-front.conf\r\n", $"{RemoteTranscript.EndMarker(nonce)} 0\r\n"]);
        var outcome = await new SparkInstaller(spark, "pw").SetUpModelFrontAsync();
        Assert.True(outcome.Success, outcome.Summary);
        var upload = Assert.Single(spark.Commands);
        var encoded = Regex.Match(upload, "echo '([A-Za-z0-9+/=]+)' \\| base64 -d").Groups[1].Value;
        var script = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
        Assert.Equal(SparkInstaller.ModelFrontScript, script);
        Assert.Contains("listen 11443 ssl;", script);
        Assert.Contains("proxy_pass http://127.0.0.1:8888;", script);
        Assert.Contains("proxy_buffering off;", script);
        Assert.Contains("\nNGINX\n", script); // the heredoc terminator sits at column 0
        Assert.Contains("sudo bash ~/.cache/dsh/spark-model-front.sh", spark.Shell!.Typed[1]);
    }

    [Fact]
    public async Task DetectRunsTheProbe()
    {
        var spark = new FakeSparkConnection { Run = _ => new RemoteResult(0, ProbeOutput, "") };
        var state = await new SparkInstaller(spark, "pw").DetectAsync();
        Assert.Equal("spark-3f2a", state.HostName);
        Assert.Equal(SparkMachineState.ProbeScript, Assert.Single(spark.Commands));
    }

    // MARK: - Known hosts

    [Fact]
    public void KnownHostsRememberAcrossInstances()
    {
        using var temp = new TempDirectory();
        var path = temp["known_hosts.json"];
        var hosts = new KnownHosts(path);
        Assert.Null(hosts.Lookup("192.168.1.42", 22));
        hosts.Remember(new SshHostKey("192.168.1.42", 22, "ssh-ed25519", "SHA256:abc"));

        var again = new KnownHosts(path);
        var entry = again.Lookup("192.168.1.42", 22);
        Assert.Equal("SHA256:abc", entry?.Fingerprint);
        Assert.Equal("ssh-ed25519", entry?.Algorithm);
        Assert.Null(again.Lookup("192.168.1.42", 2222));

        again.Forget("192.168.1.42", 22);
        Assert.Null(new KnownHosts(path).Lookup("192.168.1.42", 22));
    }

    [Fact]
    public void HostKeyMessagesSayWhatHappened()
    {
        var key = new SshHostKey("192.168.1.42", 22, "ssh-ed25519", "SHA256:new");
        var first = new SshHostKeyException(key, null);
        Assert.False(first.IsChanged);
        Assert.Contains("SHA256:new", first.Message);
        var changed = new SshHostKeyException(key, "SHA256:old");
        Assert.True(changed.IsChanged);
        Assert.Contains("SHA256:old", changed.Message);
    }
}
