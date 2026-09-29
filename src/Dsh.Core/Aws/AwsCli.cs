using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Dsh.Core;

// MARK: - The AWS CLI
//
// DSH never talks to AWS's sign-in or account APIs itself: the AWS CLI (v2) owns sign-in (`aws login`,
// `aws sso login`), keeps and refreshes the tokens, and makes the account calls (Bedrock model access,
// agreements, the Anthropic use-case form). DSH runs it as a child process — never through a shell, so
// no argument is ever re-parsed — and reads its JSON output. Everything goes through IAwsCliRunner so
// the setup flow is testable with scripted output and no AWS account.

/// <summary>What one run of the AWS CLI printed and how it ended.</summary>
/// <param name="TimedOut">True when DSH stopped it because it ran past its time limit (the exit code is
/// then -1).</param>
public sealed record AwsCliResult(int ExitCode, string Stdout, string Stderr, bool TimedOut = false)
{
    public bool Succeeded => ExitCode == 0 && !TimedOut;
}

/// <summary>One finished line of output, as it arrived.</summary>
public sealed record AwsCliLine(string Text, bool IsError);

/// <summary>How to run one AWS CLI command.</summary>
public sealed record AwsCliRunOptions
{
    /// <summary>Text typed into the CLI's standard input before it is closed (ignored when
    /// <see cref="AnswerPrompt"/> is set). With neither, standard input is closed at once, so a
    /// question nobody expected ends the command instead of hanging it.</summary>
    public string? StandardInput { get; init; }

    /// <summary>Answers the CLI's interactive questions. It is called with the unfinished last line of
    /// output — prompts such as "(y/n): " never end with a newline — each time more of it arrives, and
    /// returns what to type (a newline is added) or null when that line isn't a question it knows. Once
    /// a line is answered it isn't offered again. Standard input stays open while it is set.</summary>
    public Func<string, CancellationToken, Task<string?>>? AnswerPrompt { get; init; }

    /// <summary>Extra environment variables for the CLI; a null value removes the variable.</summary>
    public IReadOnlyDictionary<string, string?>? Environment { get; init; }

    /// <summary>Each finished line of output (standard output and standard error), as it arrives, on a
    /// background thread.</summary>
    public Action<AwsCliLine>? OnLine { get; init; }

    /// <summary>Stop the CLI after this long (null = no limit). A timed-out run returns a result with
    /// <see cref="AwsCliResult.TimedOut"/> set rather than throwing.</summary>
    public TimeSpan? Timeout { get; init; }
}

/// <summary>Runs the AWS CLI. The real one starts aws.exe (<see cref="ProcessAwsCliRunner"/>); tests
/// script the answers.</summary>
public interface IAwsCliRunner
{
    /// <summary>Run <c>aws &lt;args&gt;</c>. Cancelling stops the CLI (and anything it started) and
    /// throws <see cref="OperationCanceledException"/>.</summary>
    Task<AwsCliResult> RunAsync(IReadOnlyList<string> args, AwsCliRunOptions? options = null, CancellationToken cancellationToken = default);
}

/// <summary>Runs a real AWS CLI executable. The arguments go through <see cref="ProcessStartInfo.ArgumentList"/>
/// (no shell, no quoting to get wrong), output is read as UTF-8, and the environment is set so the CLI
/// never waits on a pager, an auto-prompt or the "set up your AI agent" question.</summary>
public sealed class ProcessAwsCliRunner(string executable) : IAwsCliRunner
{
    /// <summary>The program started: aws.exe (or, for winget, any other console program).</summary>
    public string Executable { get; } = executable;

    /// <summary>Set for every run. AWS_PAGER="" keeps output from being piped to `more`; the agent
    /// toolkit hint (CLI 2.36.50+) would otherwise ask a y/n question after a first `aws login`;
    /// auto-prompt would open an interactive command builder; UTF-8 so names with accents survive
    /// (AWS_CLI_OUTPUT_ENCODING is the CLI's own switch, PYTHONUTF8/PYTHONIOENCODING the older ones);
    /// the legacy error format is the one plain "An error occurred (Code) ..." line every 2.x prints.</summary>
    public static IReadOnlyDictionary<string, string> StandardEnvironment { get; } = new Dictionary<string, string>
    {
        ["AWS_PAGER"] = "",
        ["AWS_CLI_AGENT_TOOLKIT_HINT_DISABLED"] = "true",
        ["AWS_CLI_AUTO_PROMPT"] = "off",
        ["AWS_CLI_OUTPUT_ENCODING"] = "UTF-8",
        ["AWS_CLI_ERROR_FORMAT"] = "legacy",
        ["PYTHONUTF8"] = "1",
        ["PYTHONIOENCODING"] = "utf-8",
    };

    public async Task<AwsCliResult> RunAsync(IReadOnlyList<string> args, AwsCliRunOptions? options = null,
                                             CancellationToken cancellationToken = default)
    {
        options ??= new AwsCliRunOptions();
        var info = new ProcessStartInfo(Executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            // No BOM: the CLI would read it as part of the first answer ("﻿y" is not "y").
            StandardInputEncoding = TextUtil.Utf8NoBom,
        };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        foreach (var (name, value) in StandardEnvironment) info.Environment[name] = value;
        if (options.Environment is { } extra)
        {
            foreach (var (name, value) in extra)
            {
                if (value is null) info.Environment.Remove(name);
                else info.Environment[name] = value;
            }
        }

        using var process = new Process { StartInfo = info };
        try
        {
            if (!process.Start()) throw new IOException($"Couldn't start {Executable}.");
        }
        catch (Win32Exception ex)
        {
            throw new IOException($"Couldn't start the AWS CLI ({Executable}): {ex.Message}", ex);
        }

        using var run = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (options.Timeout is { } timeout) run.CancelAfter(timeout);

        var answering = options.AnswerPrompt;
        if (answering is null)
        {
            try
            {
                if (options.StandardInput is { } input) await process.StandardInput.WriteAsync(input).ConfigureAwait(false);
                process.StandardInput.Close();
            }
            catch (IOException)
            {
                // It exited before reading; its output says why.
            }
        }

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var outPump = PumpAsync(process.StandardOutput, stdout, false, options.OnLine, answering, process, run.Token);
        var errPump = PumpAsync(process.StandardError, stderr, true, options.OnLine, null, process, run.Token);
        try
        {
            await process.WaitForExitAsync(run.Token).ConfigureAwait(false);
            // A grandchild (a browser the CLI opened) can inherit the pipes and hold them open, so the
            // last output is waited for only briefly.
            await Task.WhenAny(Task.WhenAll(outPump, errPump), Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            KillTree(process);
            await Task.WhenAny(Task.WhenAll(outPump, errPump), Task.Delay(TimeSpan.FromSeconds(1), CancellationToken.None)).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException(cancellationToken);
            if (!run.IsCancellationRequested) throw;
            lock (stdout) lock (stderr) return new AwsCliResult(-1, stdout.ToString(), stderr.ToString(), TimedOut: true);
        }
        // The prompt answerer failed: the CLI was stopped; say why.
        if (outPump.IsFaulted) await outPump.ConfigureAwait(false);
        lock (stdout) lock (stderr) return new AwsCliResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    /// <summary>Read one stream in chunks: keep all of it, hand finished lines to <paramref name="onLine"/>,
    /// and offer the unfinished last line to <paramref name="answer"/> (stdout only).</summary>
    private static async Task PumpAsync(StreamReader reader, StringBuilder all, bool isError, Action<AwsCliLine>? onLine,
                                        Func<string, CancellationToken, Task<string?>>? answer, Process process, CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        var line = new StringBuilder();
        var answered = false;
        try
        {
            while (true)
            {
                var read = await reader.ReadAsync(buffer.AsMemory(), CancellationToken.None).ConfigureAwait(false);
                if (read <= 0) break;
                lock (all) all.Append(buffer, 0, read);
                for (var i = 0; i < read; i++)
                {
                    var c = buffer[i];
                    if (c == '\n')
                    {
                        onLine?.Invoke(new AwsCliLine(TextUtil.StripAnsi(line.ToString().TrimEnd('\r')), isError));
                        line.Clear();
                        answered = false;
                    }
                    else line.Append(c);
                }
                if (answer is null || answered || line.Length == 0) continue;
                string? reply;
                try
                {
                    reply = await answer(TextUtil.StripAnsi(line.ToString()), cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Nobody can answer the question now; don't leave the CLI waiting for it.
                    KillTree(process);
                    throw;
                }
                if (reply is null) continue;
                answered = true;
                try
                {
                    await process.StandardInput.WriteAsync(reply + "\n").ConfigureAwait(false);
                    await process.StandardInput.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (IOException)
                {
                    // It exited meanwhile.
                }
            }
            if (line.Length > 0) onLine?.Invoke(new AwsCliLine(TextUtil.StripAnsi(line.ToString().TrimEnd('\r')), isError));
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // Pipe closed.
        }
    }

    private static void KillTree(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // Already gone.
        }
    }
}

// MARK: - Finding it and checking its version

/// <summary>Where DSH looks for the AWS CLI. <see cref="ForThisMachine"/> is the real list; tests build
/// their own.</summary>
public sealed record AwsCliSearch
{
    /// <summary>A path the user picked in Settings; tried first.</summary>
    public string? ExplicitPath { get; init; }
    /// <summary>Folders searched for <see cref="FileName"/>, in order (PATH).</summary>
    public IReadOnlyList<string> PathDirectories { get; init; } = [];
    /// <summary>Full paths where the official installers put it.</summary>
    public IReadOnlyList<string> KnownLocations { get; init; } = [];
    public string FileName { get; init; } = "aws.exe";
    public Func<string, bool> FileExists { get; init; } = File.Exists;
    /// <summary>Windows paths compare without case.</summary>
    public bool IgnoreCase { get; init; } = true;

    /// <summary>The AWS CLI v2 MSI for all users installs to %ProgramW6432%\Amazon\AWSCLIV2 (the 64-bit
    /// Program Files even from a 32-bit process, as AWS's own install.ps1 resolves it); the per-user MSI
    /// (CLI 2.35.8+) to %LOCALAPPDATA%\Programs\Amazon\AWSCLIV2.</summary>
    public static string SystemInstallFolder()
    {
        var programFiles = Environment.GetEnvironmentVariable("ProgramW6432");
        if (string.IsNullOrEmpty(programFiles)) programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        return Path.Combine(programFiles, "Amazon", "AWSCLIV2");
    }

    public static string UserInstallFolder() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Amazon", "AWSCLIV2");

    /// <summary>The real search. On Windows PATH is read three ways: this process's copy (what DSH
    /// started with) and the user and machine values Windows has now — an installer that ran after DSH
    /// started only changed the latter.</summary>
    public static AwsCliSearch ForThisMachine(string? explicitPath = null)
    {
        static IEnumerable<string> Split(string? path) =>
            (path ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(d => d.Trim('"')).Where(d => d.Length > 0);

        if (OperatingSystem.IsWindows())
        {
            var dirs = Split(Environment.GetEnvironmentVariable("PATH"))
                .Concat(Split(Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User)))
                .Concat(Split(Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.Machine)))
                .Select(Environment.ExpandEnvironmentVariables)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var x86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            List<string> known =
            [
                Path.Combine(SystemInstallFolder(), "aws.exe"),
                Path.Combine(UserInstallFolder(), "aws.exe"),
            ];
            if (!string.IsNullOrEmpty(x86)) known.Add(Path.Combine(x86, "Amazon", "AWSCLIV2", "aws.exe"));
            return new AwsCliSearch { ExplicitPath = explicitPath, PathDirectories = dirs, KnownLocations = known, FileName = "aws.exe" };
        }
        return new AwsCliSearch
        {
            ExplicitPath = explicitPath,
            PathDirectories = Split(Environment.GetEnvironmentVariable("PATH")).Distinct(StringComparer.Ordinal).ToList(),
            KnownLocations = ["/usr/local/bin/aws", "/usr/local/aws-cli/v2/current/bin/aws", "/opt/homebrew/bin/aws"],
            FileName = "aws",
            IgnoreCase = false,
        };
    }
}

/// <summary>Whether the AWS CLI is there and new enough for DSH.</summary>
public abstract record AwsCliStatus
{
    /// <summary>A sentence for the setup guide.</summary>
    public abstract string Summary { get; }

    /// <summary>No aws.exe anywhere DSH looks.</summary>
    public sealed record Missing : AwsCliStatus
    {
        public override string Summary => "The AWS CLI isn't installed. DSH can install it for you.";
    }

    /// <summary>Found, but older than <see cref="AwsCli.MinimumVersion"/> (AWS CLI v1 included).</summary>
    public sealed record TooOld(Version Version, string Path) : AwsCliStatus
    {
        public override string Summary => Version.Major < 2
            ? $"AWS CLI {Version.ToString(3)} is installed, but DSH needs version 2. DSH can install it for you."
            : $"AWS CLI {Version.ToString(3)} is installed, but signing in from the browser needs {AwsCli.MinimumVersion.ToString(3)} or newer. DSH can update it.";
    }

    /// <summary>Found, but it didn't run or didn't say which version it is.</summary>
    public sealed record Broken(string Path, string Problem) : AwsCliStatus
    {
        public override string Summary => $"The AWS CLI at {Path} didn't start properly ({Problem}). DSH can reinstall it.";
    }

    public sealed record Ready(Version Version, string Path) : AwsCliStatus
    {
        public override string Summary => $"AWS CLI {Version.ToString(3)} is ready.";
    }
}

/// <summary>The AWS CLI at one path: running commands, reading their JSON, and turning failures into
/// plain-language errors.</summary>
public sealed partial class AwsCli(IAwsCliRunner runner)
{
    /// <summary>`aws login` (sign in with the browser, using the AWS console sign-in) arrived in 2.32.0.</summary>
    public static readonly Version MinimumVersion = new(2, 32, 0);

    /// <summary>How long an account call may take before DSH gives up on it.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(2);

    public IAwsCliRunner Runner { get; } = runner;

    public static AwsCli ForPath(string path) => new(new ProcessAwsCliRunner(path));

    /// <summary>The CLI a <see cref="AwsCliStatus.Ready"/> status points at.</summary>
    public static AwsCli From(AwsCliStatus.Ready ready) => ForPath(ready.Path);

    // MARK: Locating

    /// <summary>Every existing aws executable, most preferred first: the explicit path, then PATH (what
    /// the user's own terminal runs), then the installers' known folders.</summary>
    public static IReadOnlyList<string> Candidates(AwsCliSearch search)
    {
        var comparer = search.IgnoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var seen = new HashSet<string>(comparer);
        var found = new List<string>();
        void Consider(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            string full;
            try
            {
                full = Path.GetFullPath(path);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return;
            }
            if (seen.Add(full) && search.FileExists(full)) found.Add(full);
        }
        Consider(search.ExplicitPath);
        foreach (var dir in search.PathDirectories)
        {
            try
            {
                Consider(Path.Combine(dir, search.FileName));
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry.
            }
        }
        foreach (var known in search.KnownLocations) Consider(known);
        return found;
    }

    /// <summary>The first aws executable found, or null.</summary>
    public static string? Locate(AwsCliSearch? search = null) => Candidates(search ?? AwsCliSearch.ForThisMachine()).FirstOrDefault();

    /// <summary>Find the AWS CLI and check it: the first candidate that is new enough wins; otherwise the
    /// best of the rest (the newest too-old one) is reported.</summary>
    public static async Task<AwsCliStatus> CheckAsync(AwsCliSearch? search = null, Func<string, IAwsCliRunner>? runnerFor = null,
                                                      CancellationToken cancellationToken = default)
    {
        runnerFor ??= path => new ProcessAwsCliRunner(path);
        AwsCliStatus best = new AwsCliStatus.Missing();
        foreach (var path in Candidates(search ?? AwsCliSearch.ForThisMachine()))
        {
            AwsCliStatus status;
            try
            {
                var result = await runnerFor(path).RunAsync(["--version"], new AwsCliRunOptions { Timeout = TimeSpan.FromSeconds(60) },
                    cancellationToken).ConfigureAwait(false);
                status = ParseVersion(result.Stdout + "\n" + result.Stderr) is { } version
                    ? version >= MinimumVersion ? new AwsCliStatus.Ready(version, path) : new AwsCliStatus.TooOld(version, path)
                    : new AwsCliStatus.Broken(path, result.TimedOut ? "it didn't answer" : Describe(result));
            }
            catch (IOException ex)
            {
                status = new AwsCliStatus.Broken(path, ex.Message);
            }
            if (status is AwsCliStatus.Ready) return status;
            best = (best, status) switch
            {
                (AwsCliStatus.Missing, _) => status,
                (AwsCliStatus.Broken, AwsCliStatus.TooOld) => status,
                (AwsCliStatus.TooOld old, AwsCliStatus.TooOld newer) when newer.Version > old.Version => status,
                _ => best,
            };
        }
        return best;

        static string Describe(AwsCliResult result)
        {
            var text = TextUtil.FirstLine((result.Stderr.Trim().Length > 0 ? result.Stderr : result.Stdout).Trim());
            return text.Length > 0 ? TextUtil.Prefix(text, 200) : $"exit code {result.ExitCode}";
        }
    }

    /// <summary>"aws-cli/2.33.4 Python/3.13.9 Windows/11 exe/AMD64" → 2.33.4; v1 prints "aws-cli/1.x.y".</summary>
    public static Version? ParseVersion(string text)
    {
        var match = VersionRegex().Match(text);
        if (!match.Success) return null;
        int Part(int group) => int.Parse(match.Groups[group].Value, NumberStyles.None, CultureInfo.InvariantCulture);
        try
        {
            return new Version(Part(1), Part(2), Part(3));
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"aws-cli/(\d{1,6})\.(\d{1,6})\.(\d{1,6})", RegexOptions.CultureInvariant)]
    private static partial Regex VersionRegex();

    /// <summary>`aws --version` through this CLI; null when it doesn't say.</summary>
    public async Task<Version?> VersionAsync(CancellationToken cancellationToken = default)
    {
        var result = await Runner.RunAsync(["--version"], new AwsCliRunOptions { Timeout = TimeSpan.FromSeconds(60) }, cancellationToken)
            .ConfigureAwait(false);
        return ParseVersion(result.Stdout + "\n" + result.Stderr);
    }

    // MARK: Running commands

    public Task<AwsCliResult> RunAsync(IReadOnlyList<string> args, AwsCliRunOptions? options = null, CancellationToken cancellationToken = default) =>
        Runner.RunAsync(args, options, cancellationToken);

    /// <summary><c>&lt;command&gt; [--profile P] [--region R] [--output json]</c>. The CLI's own paging
    /// (it fetches every page and merges them) stays on.</summary>
    public static List<string> Arguments(IEnumerable<string> command, string? profile, string? region, bool json = true)
    {
        var args = command.ToList();
        if (!string.IsNullOrEmpty(profile)) args.AddRange(["--profile", profile]);
        if (!string.IsNullOrEmpty(region)) args.AddRange(["--region", region]);
        if (json) args.AddRange(["--output", "json"]);
        return args;
    }

    /// <summary>Run a command that must succeed. Failures become <see cref="AwsSignInRequiredException"/>
    /// (sign in again) or <see cref="AwsCliException"/> with a plain-language message.</summary>
    public async Task<AwsCliResult> RunCheckedAsync(IEnumerable<string> command, string? profile, string? region,
                                                    TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var args = Arguments(command, profile, region);
        var result = await Runner.RunAsync(args, new AwsCliRunOptions { Timeout = timeout ?? DefaultTimeout }, cancellationToken)
            .ConfigureAwait(false);
        if (!result.Succeeded) throw AwsCliErrors.ToException(AwsCliErrors.Classify(result, args.FirstOrDefault()), profile);
        return result;
    }

    /// <summary>Run a command and parse what it printed as JSON (an empty object when it printed
    /// nothing, as commands without a response body do).</summary>
    public async Task<JsonNode> RunJsonAsync(IEnumerable<string> command, string? profile, string? region,
                                             TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var result = await RunCheckedAsync(command, profile, region, timeout, cancellationToken).ConfigureAwait(false);
        var text = result.Stdout.Trim();
        if (text.Length == 0) return new JsonObject();
        try
        {
            return JsonNode.Parse(text) ?? new JsonObject();
        }
        catch (JsonException ex)
        {
            throw new AwsCliException(new AwsCliError(AwsCliFailure.Other,
                "The AWS CLI answered with something DSH couldn't read.", Details: TextUtil.Prefix(text, 500)), ex);
        }
    }
}

// MARK: - Errors in plain language

public enum AwsCliFailure
{
    /// <summary>Not signed in, or the sign-in ran out: sign in again.</summary>
    SignInRequired,
    /// <summary>An IAM policy doesn't allow the call (<see cref="AwsCliError.Permission"/> says which).</summary>
    AccessDenied,
    /// <summary>This CLI doesn't know the command or option: it is too old.</summary>
    NotSupported,
    /// <summary>AWS couldn't be reached (offline, proxy, VPN, TLS inspection).</summary>
    Network,
    Throttled,
    NotFound,
    Conflict,
    /// <summary>AWS rejected the request's contents (ValidationException, bad parameters, no Region).</summary>
    Invalid,
    TimedOut,
    Other,
}

/// <summary>What went wrong, for people (<see cref="Message"/>) and for the log (<see cref="Details"/>).</summary>
public sealed record AwsCliError(AwsCliFailure Kind, string Message, string? Code = null, string? Operation = null,
                                 string? Permission = null, string Details = "");

/// <summary>An AWS CLI command failed for a reason other than the sign-in.</summary>
public sealed class AwsCliException(AwsCliError error, Exception? inner = null) : Exception(error.Message, inner)
{
    public AwsCliError Error { get; } = error;
    public AwsCliFailure Kind => Error.Kind;
}

/// <summary>Reads the AWS CLI's error output. The texts matched are the CLI's and botocore's own
/// (awscli/errorhandler.py, botocore/exceptions.py, the login and SSO credential providers).</summary>
public static partial class AwsCliErrors
{
    /// <summary>Classify a failed run. <paramref name="service"/> is the command's first word
    /// ("bedrock", "sts"), used to name the IAM permission when AWS's message doesn't.</summary>
    public static AwsCliError Classify(AwsCliResult result, string? service = null)
    {
        var raw = (result.Stderr.Trim().Length > 0 ? result.Stderr : result.Stdout).Trim();
        var text = TextUtil.NormalizeNewlines(raw);
        if (result.TimedOut)
            return new(AwsCliFailure.TimedOut, "AWS took too long to answer. Check your internet connection and try again.", Details: raw);

        var client = ClientErrorRegex().Match(text);
        var code = client.Success ? client.Groups["code"].Value : null;
        var operation = client.Success && client.Groups["op"].Success ? client.Groups["op"].Value : null;
        var awsMessage = client.Success ? client.Groups["msg"].Value.Trim() : null;
        bool Has(string phrase) => text.Contains(phrase, StringComparison.OrdinalIgnoreCase);
        bool CodeIs(params string[] codes) => code is not null && codes.Contains(code, StringComparer.OrdinalIgnoreCase);

        // `aws login` succeeded in the browser but the identity may not create CLI tokens.
        if (Has("insufficient permissions") && Has("signin:CreateOAuth2Token"))
            return new(AwsCliFailure.AccessDenied,
                "Your AWS permissions don't allow signing in from the AWS CLI (signin:CreateOAuth2Token). Ask whoever manages your AWS account to attach the AWS managed policy SignInLocalDevelopmentAccess to you.",
                code, operation, "signin:CreateOAuth2Token", raw);
        if (Has("change in your password"))
            return SignIn("Your AWS password changed since you signed in. Sign in again with the new password.");
        if (Has("Your session has expired") || Has("reauthenticate using 'aws login'") || Has("reauthenticate with 'aws login'")
            || Has("Error loading login session token") || Has("refreshing a login session profile"))
            return SignIn("Your AWS sign-in has expired. Sign in again — DSH opens your browser.");
        if (Has("Error loading SSO Token") || Has("SSO session associated with this profile has expired")
            || Has("Token has expired and refresh failed") || Has("retrieving token from sso") || Has("UnauthorizedSSOTokenError"))
            return SignIn("Your AWS IAM Identity Center (SSO) sign-in has expired. Sign in again.");
        if (Has("Unable to locate credentials") || Has("no credentials found") || Has("NoCredentials")
            || (Has("config profile") && Has("could not be found")))
            return SignIn("DSH isn't signed in to AWS yet. Sign in to continue.");
        if (CodeIs("ExpiredToken", "ExpiredTokenException", "RequestExpired") || Has("security token included in the request is expired"))
            return SignIn("Your AWS sign-in has expired. Sign in again.");
        if (CodeIs("UnrecognizedClientException", "InvalidClientTokenId", "InvalidSignatureException")
            || Has("security token included in the request is invalid"))
            return SignIn("AWS didn't accept the saved sign-in. Sign in again.");

        if (CodeIs("AccessDenied", "AccessDeniedException", "UnauthorizedOperation", "AuthorizationError") || Has("not authorized to perform"))
        {
            var permission = PermissionRegex().Match(text) is { Success: true } p
                ? p.Groups[1].Value
                : operation is not null && !string.IsNullOrEmpty(service) ? $"{service}:{operation}" : null;
            var what = permission ?? "this";
            var message = Has("explicit deny")
                ? $"An AWS policy explicitly blocks {what} for your sign-in (for example a policy set by your organization). Ask whoever manages your AWS account."
                : $"Your AWS permissions don't allow {what}. Ask whoever manages your AWS account to allow it — DSH can show the exact policy to send them.";
            return new(AwsCliFailure.AccessDenied, message, code, operation, permission, raw);
        }
        if (Has("Found invalid choice") || Has("Invalid choice") || Has("Unknown options"))
            return new(AwsCliFailure.NotSupported,
                "This version of the AWS CLI doesn't have that command. Update the AWS CLI — DSH can do it for you.", code, operation, Details: raw);
        if (Has("Could not connect to the endpoint URL") || Has("Connect timeout on endpoint URL") || Has("Read timeout on endpoint URL")
            || Has("SSL validation failed") || Has("Failed to connect to proxy") || Has("ProxyConnectionError") || Has("EndpointConnectionError"))
            return new(AwsCliFailure.Network,
                "DSH couldn't reach AWS. Check your internet connection (and any proxy or VPN), then try again.", code, operation, Details: raw);
        if (CodeIs("ThrottlingException", "Throttling", "TooManyRequestsException", "ServiceQuotaExceededException") || Has("Rate exceeded"))
            return new(AwsCliFailure.Throttled, "AWS is busy right now (too many requests). Wait a moment and try again.", code, operation, Details: raw);
        if (Has("You must specify a region"))
            return new(AwsCliFailure.Invalid, "No AWS Region is set. Pick a Region and try again.", code, operation, Details: raw);
        if (CodeIs("ResourceNotFoundException", "NotFoundException", "NoSuchEntity"))
            return new(AwsCliFailure.NotFound, awsMessage ?? "AWS couldn't find that.", code, operation, Details: raw);
        if (CodeIs("ConflictException"))
            return new(AwsCliFailure.Conflict, awsMessage ?? "AWS says this is already in progress.", code, operation, Details: raw);
        if (CodeIs("ValidationException", "ParamValidation", "InvalidParameterException"))
            return new(AwsCliFailure.Invalid, $"AWS didn't accept the request: {awsMessage ?? CleanMessage(text)}", code, operation, Details: raw);
        return new(AwsCliFailure.Other, awsMessage ?? CleanMessage(text), code, operation, Details: raw);

        AwsCliError SignIn(string message) => new(AwsCliFailure.SignInRequired, message, code, operation, Details: raw);
    }

    /// <summary>The exception a caller should see: sign-in problems as <see cref="AwsSignInRequiredException"/>
    /// (the app opens the sign-in), the rest as <see cref="AwsCliException"/>.</summary>
    public static Exception ToException(AwsCliError error, string? profile) => error.Kind == AwsCliFailure.SignInRequired
        ? new AwsSignInRequiredException(string.IsNullOrEmpty(profile) ? "default" : profile, error.Message, new AwsCliException(error))
        : new AwsCliException(error);

    /// <summary>The CLI's own words without its "aws: [ERROR]:" prefix and usage text.</summary>
    public static string CleanMessage(string text)
    {
        var lines = TextUtil.NormalizeNewlines(text).Split('\n')
            .Select(l => l.Trim())
            .Select(l => l.StartsWith("aws: [ERROR]:", StringComparison.Ordinal) ? l["aws: [ERROR]:".Length..].Trim() : l)
            .TakeWhile(l => !l.StartsWith("usage:", StringComparison.OrdinalIgnoreCase))
            .Where(l => l.Length > 0 && !l.Equals("Additional error details:", StringComparison.Ordinal));
        var message = string.Join(" ", lines);
        message = ClientErrorPrefixRegex().Replace(message, "");
        return message.Length == 0 ? "The AWS CLI stopped with an error." : TextUtil.Prefix(message, 500);
    }

    /// <summary>botocore's ClientError: "An error occurred (Code) when calling the Op operation (reached
    /// max retries: 4): message".</summary>
    [GeneratedRegex(@"An error occurred \((?<code>[^)]+)\)(?: when calling the (?<op>\w+) operation)?(?: \(reached max retries: \d+\))?: (?<msg>[^\n]*)", RegexOptions.CultureInvariant)]
    private static partial Regex ClientErrorRegex();

    [GeneratedRegex(@"An error occurred \([^)]+\)(?: when calling the \w+ operation)?(?: \(reached max retries: \d+\))?: ", RegexOptions.CultureInvariant)]
    private static partial Regex ClientErrorPrefixRegex();

    /// <summary>"... is not authorized to perform: bedrock:InvokeModel on resource ..."</summary>
    [GeneratedRegex(@"not authorized to perform:?\s+([A-Za-z0-9-]+:[A-Za-z0-9*]+)", RegexOptions.CultureInvariant)]
    private static partial Regex PermissionRegex();
}
