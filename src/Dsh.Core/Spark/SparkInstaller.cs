using System.Text;
using System.Text.RegularExpressions;

namespace Dsh.Core;

// MARK: - Installing Spark Swapper over SSH
//
// The Swapper's one-line installer (curl … | bash) calls sudo, and sudo asks for the user's password
// on the terminal. So the command runs in a real terminal (a PTY shell), and a transcript reader
// watches the output: it hands lines to the UI, notices "[sudo] password for …:" and answers it with
// the password the user already gave (never echoed, never logged), and spots the end marker that
// carries the command's exit code.

/// <summary>Something the transcript reader noticed.</summary>
public abstract record TranscriptEvent
{
    /// <summary>A finished line of output, cleaned of colours and cursor tricks.</summary>
    public sealed record Line(string Text) : TranscriptEvent;
    /// <summary>sudo is asking for the password: send it now.</summary>
    public sealed record PasswordPrompt : TranscriptEvent;
    /// <summary>The command finished with this exit code.</summary>
    public sealed record Finished(int ExitCode) : TranscriptEvent;
    public sealed record Problem(TranscriptProblem Kind) : TranscriptEvent;
}

public enum TranscriptProblem
{
    /// <summary>sudo said "Sorry, try again." — the password we have is wrong for sudo.</summary>
    WrongPassword,
    /// <summary>"… is not in the sudoers file": this account can't install software.</summary>
    NotAllowed,
    /// <summary>More password prompts than any installer should need.</summary>
    TooManyPrompts,
}

/// <summary>Reads a PTY shell's output for one wrapped command (see <see cref="Wrap"/>). Output before
/// the begin marker (the login banner, the shell's own echo) is ignored; the end marker's line carries
/// the exit code and is never shown.</summary>
public sealed partial class RemoteTranscript
{
    private readonly string _begin;
    private readonly Regex _end;
    private readonly string? _secret;
    private readonly StringBuilder _partial = new();
    private bool _pendingCarriageReturn;
    private bool _answeredThisLine;
    private int _prompts;

    public bool Started { get; private set; }
    public int? ExitCode { get; private set; }
    public TranscriptProblem? Problem { get; private set; }
    /// <summary>Every line shown so far (for success checks and the saved log).</summary>
    public List<string> Lines { get; } = [];
    public const int MaxPasswordPrompts = 3;

    /// <param name="nonce">Makes the markers impossible to print by accident.</param>
    /// <param name="secret">Redacted from every line, should a program ever echo it.</param>
    public RemoteTranscript(string nonce, string? secret)
    {
        _begin = BeginMarker(nonce);
        _end = new Regex($@"^{Regex.Escape(EndMarker(nonce))} (\d+)\s*$", RegexOptions.CultureInvariant);
        _secret = string.IsNullOrEmpty(secret) ? null : secret;
    }

    public static string BeginMarker(string nonce) => $"__DSH_BEGIN_{nonce}__";
    public static string EndMarker(string nonce) => $"__DSH_END_{nonce}__";

    /// <summary>One shell line that prints the begin marker, runs <paramref name="command"/> (a
    /// pipeline fails if any part fails) and prints the end marker with its exit code. It must be a
    /// single line: anything typed after it would sit in the terminal and could be read by sudo as
    /// the password.</summary>
    public static string Wrap(string command, string nonce) =>
        $"export PS1='' PS2='' LC_ALL=C.UTF-8 DEBIAN_FRONTEND=noninteractive; set -o pipefail; " +
        $"echo {BeginMarker(nonce)}; {command}; echo \"{EndMarker(nonce)} $?\"\n";

    /// <summary>Feed the next chunk of output; returns what happened in it, in order.</summary>
    public IReadOnlyList<TranscriptEvent> Feed(string chunk)
    {
        var events = new List<TranscriptEvent>();
        foreach (var c in chunk)
        {
            if (_pendingCarriageReturn)
            {
                _pendingCarriageReturn = false;
                // A lone CR returns to the start of the line: what follows overwrites it (progress bars).
                if (c != '\n') _partial.Clear();
            }
            if (c == '\r')
            {
                _pendingCarriageReturn = true;
                continue;
            }
            if (c == '\n')
            {
                CompleteLine(events);
                continue;
            }
            _partial.Append(c);
        }
        CheckPrompt(events);
        return events;
    }

    private void CompleteLine(List<TranscriptEvent> events)
    {
        var text = Clean(_partial.ToString());
        _partial.Clear();
        _answeredThisLine = false;
        if (ExitCode is not null) return;
        if (!Started)
        {
            if (text.Trim() == _begin) Started = true;
            return;
        }
        if (_end.Match(text.Trim()) is { Success: true } end)
        {
            ExitCode = int.Parse(end.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            events.Add(new TranscriptEvent.Finished(ExitCode.Value));
            return;
        }
        if (text.Trim().Length == 0) return;
        if (text.Contains("Sorry, try again", StringComparison.OrdinalIgnoreCase)) Flag(TranscriptProblem.WrongPassword, events);
        if (text.Contains("not in the sudoers file", StringComparison.OrdinalIgnoreCase)
            || text.Contains("is not allowed to run sudo", StringComparison.OrdinalIgnoreCase))
            Flag(TranscriptProblem.NotAllowed, events);
        Lines.Add(text);
        events.Add(new TranscriptEvent.Line(text));
    }

    /// <summary>A password prompt never ends with a newline: look at the unfinished line.</summary>
    private void CheckPrompt(List<TranscriptEvent> events)
    {
        if (!Started || ExitCode is not null || _answeredThisLine || _partial.Length == 0) return;
        var text = Clean(_partial.ToString());
        if (!PromptRegex().IsMatch(text)) return;
        _answeredThisLine = true;
        if (Problem is not null) return;
        if (++_prompts > MaxPasswordPrompts)
        {
            Flag(TranscriptProblem.TooManyPrompts, events);
            return;
        }
        var shown = $"{text.Trim()} (answered by DSH)";
        Lines.Add(shown);
        events.Add(new TranscriptEvent.Line(shown));
        events.Add(new TranscriptEvent.PasswordPrompt());
    }

    private void Flag(TranscriptProblem problem, List<TranscriptEvent> events)
    {
        if (Problem is not null) return;
        Problem = problem;
        events.Add(new TranscriptEvent.Problem(problem));
    }

    private string Clean(string text)
    {
        var clean = TextUtil.StripAnsi(text).TrimEnd();
        return _secret is null ? clean : clean.Replace(_secret, "••••••", StringComparison.Ordinal);
    }

    [GeneratedRegex(@"(\[sudo\] password for [^\s:]+:|^\s*password( for [^\s:]+)?:)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PromptRegex();
}

/// <summary>How a remote step ended.</summary>
public sealed record InstallOutcome(bool Success, string Summary, int? ExitCode = null, TranscriptProblem? Problem = null);

/// <summary>Installs Spark Swapper (and, when missing, the HTTPS front for the model API) on a Spark
/// through an <see cref="ISparkConnection"/>.</summary>
public sealed class SparkInstaller(ISparkConnection connection, string password)
{
    /// <summary>The Swapper's documented one-line install.</summary>
    public const string SwapperInstallCommand =
        "curl -fsSL https://raw.githubusercontent.com/gnubyte/DGX-Spark-Swapper/main/install-swapper.sh | bash";

    /// <summary>What install-swapper.sh prints when the service answers.</summary>
    public const string SwapperSuccessLine = "Spark Swapper is up";

    public const int ModelFrontPort = 11443;

    /// <summary>Output lines as they arrive (from a background thread).</summary>
    public event Action<string>? Output;

    /// <summary>Waits between output chunks before giving up on a silent command.</summary>
    public TimeSpan QuietTimeout { get; init; } = TimeSpan.FromMinutes(15);

    public async Task<SparkMachineState> DetectAsync(CancellationToken cancellationToken = default)
    {
        var result = await connection.RunAsync(SparkMachineState.ProbeScript, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        return SparkMachineState.Parse(result.Output);
    }

    /// <summary>Run the Swapper's installer. Safe to run again: it updates an existing install and
    /// keeps its settings, logins and certificate.</summary>
    public async Task<InstallOutcome> InstallSwapperAsync(CancellationToken cancellationToken = default)
    {
        Say("Downloading and running the Spark Swapper installer…");
        var (exit, problem, lines) = await RunInShellAsync(SwapperInstallCommand, cancellationToken).ConfigureAwait(false);
        if (problem is { } p) return new InstallOutcome(false, Describe(p), exit, p);
        var up = lines.Any(l => l.Contains(SwapperSuccessLine, StringComparison.Ordinal));
        if (exit == 0 && up) return new InstallOutcome(true, "Spark Swapper is installed and running.", exit);
        var verify = await connection.RunAsync("systemctl is-active spark-swapper", TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
        if (exit == 0 && verify.Output.Trim() == "active") return new InstallOutcome(true, "Spark Swapper is installed and running.", exit);
        return new InstallOutcome(false, exit is null
            ? "The installer stopped without finishing."
            : $"The installer stopped with an error (exit code {exit}). The last lines of the log above say why.", exit);
    }

    /// <summary>Put an HTTPS front on :11443 for the model API (nginx, with the Swapper's own
    /// certificate), unless one is already there. The model servers only listen on the Spark itself
    /// (127.0.0.1:8888); this is how DSH on your PC reaches them.</summary>
    public async Task<InstallOutcome> SetUpModelFrontAsync(CancellationToken cancellationToken = default)
    {
        Say($"Setting up the secure model address (HTTPS on port {ModelFrontPort})…");
        var script = Convert.ToBase64String(Encoding.UTF8.GetBytes(ModelFrontScript));
        var upload = await connection.RunAsync(
            $"mkdir -p ~/.cache/dsh && echo '{script}' | base64 -d > ~/.cache/dsh/spark-model-front.sh && chmod 700 ~/.cache/dsh/spark-model-front.sh",
            TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        if (upload.ExitCode != 0) return new InstallOutcome(false, $"Couldn't copy the setup script to the Spark: {upload.Error.Trim()}", upload.ExitCode);
        var (exit, problem, _) = await RunInShellAsync("sudo bash ~/.cache/dsh/spark-model-front.sh", cancellationToken).ConfigureAwait(false);
        if (problem is { } p) return new InstallOutcome(false, Describe(p), exit, p);
        return exit == 0
            ? new InstallOutcome(true, $"The model API is reachable over HTTPS on port {ModelFrontPort}.", exit)
            : new InstallOutcome(false, $"Setting up the HTTPS front failed (exit code {exit?.ToString() ?? "unknown"}).", exit);
    }

    /// <summary>Run one command in a PTY shell, answering sudo's password prompts. Returns its exit
    /// code (null if the shell closed first), any problem noticed, and the lines it printed.</summary>
    public async Task<(int? Exit, TranscriptProblem? Problem, IReadOnlyList<string> Lines)> RunInShellAsync(string command, CancellationToken cancellationToken)
    {
        var nonce = Guid.NewGuid().ToString("N")[..12];
        var transcript = new RemoteTranscript(nonce, password);
        using var shell = await connection.OpenShellAsync(cancellationToken).ConfigureAwait(false);
        // Swap the login shell for a plain bash — no readline, no rc files, nothing echoed — and wait
        // for its prompt before typing the command: input typed while the login shell hands over could
        // be thrown away, or worse, read later by sudo as a password.
        await shell.WriteAsync(ReadyCommand(nonce), cancellationToken).ConfigureAwait(false);
        await WaitForReadyAsync(shell, nonce, cancellationToken).ConfigureAwait(false);
        await shell.WriteAsync(RemoteTranscript.Wrap(command, nonce), cancellationToken).ConfigureAwait(false);
        try
        {
            while (transcript.ExitCode is null)
            {
                using var quiet = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                quiet.CancelAfter(QuietTimeout);
                string? chunk;
                try
                {
                    chunk = await shell.ReadAsync(quiet.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    Say($"No output for {QuietTimeout.TotalMinutes:0} minutes — giving up.");
                    break;
                }
                if (chunk is null) break;
                foreach (var e in transcript.Feed(chunk))
                {
                    switch (e)
                    {
                        case TranscriptEvent.Line line:
                            Output?.Invoke(line.Text);
                            break;
                        case TranscriptEvent.PasswordPrompt:
                            await shell.WriteAsync(password + "\n", cancellationToken).ConfigureAwait(false);
                            break;
                        case TranscriptEvent.Problem:
                            // Stop the command (Ctrl+C) rather than let sudo ask again.
                            await shell.WriteAsync("\u0003", cancellationToken).ConfigureAwait(false);
                            return (null, transcript.Problem, transcript.Lines);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            try { await shell.WriteAsync("\u0003", CancellationToken.None).ConfigureAwait(false); } catch (Exception) { }
            throw;
        }
        try { await shell.WriteAsync("exit\n", CancellationToken.None).ConfigureAwait(false); } catch (Exception) { }
        return (transcript.ExitCode, transcript.Problem, transcript.Lines);
    }

    /// <summary>"__DSH_READY_…__", the prompt of the plain bash — written in two quoted halves on the
    /// command line so the login shell's echo of that line never contains it.</summary>
    public static string ReadyMarker(string nonce) => $"__DSH_READY_{nonce}__";

    public static string ReadyCommand(string nonce) =>
        $"exec env PS1='__DSH_''READY_{nonce}__ ' bash --noprofile --norc --noediting\n";

    private static async Task WaitForReadyAsync(ISparkShell shell, string nonce, CancellationToken cancellationToken)
    {
        var marker = ReadyMarker(nonce);
        var seen = new StringBuilder();
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        wait.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            while (!seen.ToString().Contains(marker, StringComparison.Ordinal))
            {
                var chunk = await shell.ReadAsync(wait.Token).ConfigureAwait(false);
                if (chunk is null) throw new SparkRemoteException("The Spark closed the remote session before the install could start.");
                seen.Append(chunk);
                // The login banner can be long; only the tail matters.
                if (seen.Length > 64 * 1024) seen.Remove(0, seen.Length - 1024);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // No prompt seen (an unusual shell setup): carry on; the begin marker still guards the output.
        }
    }

    private void Say(string text) => Output?.Invoke(text);

    public static string Describe(TranscriptProblem problem) => problem switch
    {
        TranscriptProblem.WrongPassword => "The Spark didn't accept the password when installing needed administrator rights (sudo). Check the password and try again.",
        TranscriptProblem.NotAllowed => "This account isn't allowed to install software on the Spark (it isn't an administrator). Sign in with the account you created when you first set up the Spark.",
        _ => "The installer kept asking for the password, so DSH stopped it.",
    };

    /// <summary>Installs nginx if needed and serves https://&lt;spark&gt;:11443 — /v1/ goes to the model
    /// API on 127.0.0.1:8888 (unbuffered, so replies stream), everything else to the OpenClaw gateway
    /// (the Swapper's "dashboard" link points there). Uses the Swapper's certificate so one fingerprint
    /// covers both. Leaves an existing :11443 setup alone.</summary>
    public const string ModelFrontScript = """
        #!/usr/bin/env bash
        # Written by DSH for Windows: an HTTPS front on :11443 for the Spark's model API.
        set -euo pipefail
        CERT=/etc/spark-swapper/tls.crt
        KEY=/etc/spark-swapper/tls.key
        PORT=11443
        CONF=/etc/nginx/conf.d/spark-model-front.conf
        say() { printf '==> %s\n' "$*"; }
        if [[ ! -s $CERT || ! -s $KEY ]]; then
          echo "Spark Swapper's certificate is missing ($CERT). Install Spark Swapper first." >&2
          exit 2
        fi
        if [[ ! -f $CONF ]] && grep -rqsE -- "listen[^;]*$PORT" /etc/nginx/sites-enabled /etc/nginx/conf.d 2>/dev/null; then
          say "nginx already serves port $PORT - leaving it as it is."
          exit 0
        fi
        fresh=0
        if ! command -v nginx >/dev/null 2>&1; then
          say "Installing nginx (a small web server)..."
          apt-get -o DPkg::Lock::Timeout=300 update -qq
          DEBIAN_FRONTEND=noninteractive apt-get -o DPkg::Lock::Timeout=300 install -y -qq nginx
          fresh=1
        fi
        say "Writing $CONF"
        cat > "$CONF" <<'NGINX'
        # Written by DSH for Windows. HTTPS front for the Spark's model API and the OpenClaw gateway.
        map $http_upgrade $dsh_connection_upgrade { default upgrade; '' close; }
        server {
            listen 11443 ssl;
            server_name _;
            ssl_certificate     /etc/spark-swapper/tls.crt;
            ssl_certificate_key /etc/spark-swapper/tls.key;
            ssl_protocols TLSv1.2 TLSv1.3;
            client_max_body_size 100m;
            location /v1/ {
                proxy_pass http://127.0.0.1:8888;
                proxy_http_version 1.1;
                proxy_set_header Host $host;
                proxy_set_header Connection "";
                proxy_buffering off;
                proxy_request_buffering off;
                proxy_read_timeout 3600s;
                proxy_send_timeout 3600s;
            }
            location / {
                proxy_pass http://127.0.0.1:18789;
                proxy_http_version 1.1;
                proxy_set_header Host $host;
                proxy_set_header Upgrade $http_upgrade;
                proxy_set_header Connection $dsh_connection_upgrade;
                proxy_read_timeout 3600s;
            }
        }
        NGINX
        # A freshly installed nginx also serves a placeholder page on port 80; nobody asked for that.
        if [[ $fresh == 1 && -L /etc/nginx/sites-enabled/default ]]; then rm -f /etc/nginx/sites-enabled/default; fi
        nginx -t
        systemctl enable nginx >/dev/null 2>&1 || true
        systemctl restart nginx
        if command -v ufw >/dev/null 2>&1 && ufw status 2>/dev/null | grep -q "Status: active"; then
          ufw allow "$PORT/tcp" >/dev/null && say "Firewall: allowed $PORT/tcp"
        fi
        say "The model API is served at https://$(hostname -I | awk '{print $1}'):$PORT/v1"
        """;
}
