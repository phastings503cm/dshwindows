using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dsh.Core;

// MARK: - Installing the AWS CLI on Windows
//
// The same steps as AWS's own installer script (scripts/install-v2/install.ps1 in aws/aws-cli): download
// the MSI from awscli.amazonaws.com, refuse it unless Windows says its Authenticode signature is valid
// (and, here, that the signer is Amazon — it is "Amazon Web Services, Inc." through DigiCert), run
// msiexec, and check `aws --version` at the folder the MSI installs to (this app's PATH predates the
// install). Two MSIs exist since CLI 2.35.13:
//   AWSCLIV2-User.msi → %LOCALAPPDATA%\Programs\Amazon\AWSCLIV2  (no administrator rights, no UAC prompt;
//                                                                AWS's script's default)
//   AWSCLIV2.msi      → %ProgramW6432%\Amazon\AWSCLIV2          (all users; Windows asks for approval)
// Both are x64; there is no ARM64 build, and Windows 11 on ARM runs it under x64 emulation. Either MSI
// upgrades an older install of the same kind in place. When the MSI route fails for a reason other than
// the user saying no, winget (Amazon.AWSCLI) is the fallback.

public enum AwsCliInstallScope
{
    /// <summary>Just for this Windows user: no administrator rights needed.</summary>
    CurrentUser,
    /// <summary>For everyone on the PC (Program Files): Windows asks for approval.</summary>
    AllUsers,
}

/// <summary>Where the install is. <see cref="Done"/>/<see cref="Total"/> are bytes while downloading.</summary>
public sealed record AwsCliInstallProgress(string Stage, long Done = 0, long? Total = null, string? Detail = null)
{
    public double? Fraction => Total is > 0 ? Math.Clamp((double)Done / Total.Value, 0, 1) : null;
}

public enum AwsCliInstallOutcome
{
    Installed,
    /// <summary>Installed; Windows would like a restart (the CLI normally works before it).</summary>
    InstalledRestartRecommended,
    /// <summary>The user declined Windows' prompt or cancelled the installer.</summary>
    Cancelled,
    /// <summary>Another installation is running (Windows runs one at a time).</summary>
    AnotherInstallRunning,
    /// <summary>The organization's policy blocks installing software.</summary>
    BlockedByPolicy,
    NeedsAdministrator,
    DownloadFailed,
    /// <summary>The download wasn't validly signed by Amazon, or the signature couldn't be checked; nothing ran.</summary>
    SignatureRejected,
    /// <summary>This PC can't run the AWS CLI installer (not Windows, 32-bit, Windows 10 on ARM, no winget).</summary>
    NotSupported,
    Failed,
}

/// <summary>How an install ended, in words for the guide.</summary>
/// <param name="Cli">The AWS CLI found afterwards (Ready when it worked).</param>
/// <param name="LogPath">Windows Installer's log, kept when something went wrong.</param>
public sealed record AwsCliInstallResult(AwsCliInstallOutcome Outcome, string Message, int? ExitCode = null, AwsCliStatus? Cli = null,
                                         string? LogPath = null)
{
    public bool Succeeded => Outcome is AwsCliInstallOutcome.Installed or AwsCliInstallOutcome.InstalledRestartRecommended;

    /// <summary>Worth offering <see cref="AwsCliInstaller.InstallWithWingetAsync"/> instead.</summary>
    public bool CanTryWinget => Outcome is AwsCliInstallOutcome.Failed or AwsCliInstallOutcome.NeedsAdministrator or AwsCliInstallOutcome.DownloadFailed;
}

/// <summary>What Windows says about a file's Authenticode signature (Get-AuthenticodeSignature).</summary>
/// <param name="Status">"Valid", "NotSigned", "HashMismatch", "NotTrusted", ...</param>
/// <param name="SignerSubject">The signing certificate's subject, e.g. CN="Amazon Web Services, Inc.", O="Amazon Web Services, Inc.", …</param>
public sealed record AuthenticodeSignature(string Status, string? SignerSubject, string? StatusMessage = null, string? Thumbprint = null);

/// <summary>One msiexec run: the command line and whether it needs Windows' approval prompt.</summary>
public sealed record MsiexecLaunch(string MsiPath, string Arguments, bool Elevated, string LogPath);

/// <summary>Downloads, verifies and installs the AWS CLI v2 on Windows. Every outside effect — HTTP,
/// the signature check, msiexec, winget, finding the result — is a replaceable property, so the flow is
/// tested without Windows.</summary>
public sealed class AwsCliInstaller
{
    public const string SystemMsiUrl = "https://awscli.amazonaws.com/AWSCLIV2.msi";
    public const string UserMsiUrl = "https://awscli.amazonaws.com/AWSCLIV2-User.msi";
    public const string WingetPackageId = "Amazon.AWSCLI";

    /// <summary>`winget install --id Amazon.AWSCLI -e --silent --accept-package-agreements --accept-source-agreements`.</summary>
    public static IReadOnlyList<string> WingetArguments { get; } =
        ["install", "--id", WingetPackageId, "-e", "--silent", "--accept-package-agreements", "--accept-source-agreements"];

    /// <summary>Every MSI (an OLE compound file) starts with these bytes.</summary>
    private static readonly byte[] MsiSignature = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    /// <summary>The download's HTTP handler (null = the default, which honours the system proxy).</summary>
    public HttpMessageHandler? HttpHandler { get; init; }

    /// <summary>The MSI to download for a scope.</summary>
    public Func<AwsCliInstallScope, Uri> MsiUrl { get; init; } = scope => new Uri(scope == AwsCliInstallScope.AllUsers ? SystemMsiUrl : UserMsiUrl);

    /// <summary>Reads the downloaded file's Authenticode signature. Throwing means "couldn't check", and
    /// DSH then refuses to install.</summary>
    public Func<string, CancellationToken, Task<AuthenticodeSignature>> CheckSignature { get; init; } = PowerShellSignatureAsync;

    /// <summary>Runs msiexec and returns its exit code (1223 when Windows' approval prompt was declined).</summary>
    public Func<MsiexecLaunch, CancellationToken, Task<int>> RunMsiexec { get; init; } = MsiexecAsync;

    /// <summary>Runs winget with the given arguments, reporting its output lines; returns its exit code.
    /// Null = find winget.exe on this PC.</summary>
    public Func<IReadOnlyList<string>, Action<string>?, CancellationToken, Task<int>>? RunWinget { get; init; }

    /// <summary>Finds and checks the AWS CLI after installing (the known install folders included).</summary>
    public Func<CancellationToken, Task<AwsCliStatus>> CheckInstalled { get; init; } = cancellationToken => AwsCli.CheckAsync(cancellationToken: cancellationToken);

    /// <summary>Why this PC can't run the installer, or null.</summary>
    public string? PlatformProblem { get; init; } = PlatformProblemFor(OperatingSystem.IsWindows(), RuntimeInformation.OSArchitecture,
        OperatingSystem.IsWindows() ? Environment.OSVersion.Version.Build : 0);

    /// <summary>Where downloads go (a fresh folder inside it per install).</summary>
    public string WorkFolder { get; init; } = Path.Combine(Path.GetTempPath(), "dsh-aws-cli");

    public static string? PlatformProblemFor(bool isWindows, Architecture architecture, int windowsBuild)
    {
        if (!isWindows)
            return "DSH installs the AWS CLI on Windows only. Install it with your system's package manager, or from https://aws.amazon.com/cli/.";
        if (architecture is Architecture.X86 or Architecture.Arm)
            return "The AWS CLI needs 64-bit Windows.";
        if (architecture == Architecture.Arm64 && windowsBuild < 22000)
            return "The AWS CLI for Windows is built for x64 PCs. An ARM PC needs Windows 11 to run it (Windows 10 on ARM can't run x64 programs).";
        return null;
    }

    /// <summary>`/i "&lt;msi&gt;" /passive /norestart /log "&lt;log&gt;"`: a progress bar but no questions,
    /// never restarting Windows by itself, and a log to show if it fails.</summary>
    public static string MsiexecArguments(string msiPath, string logPath) => $"/i \"{msiPath}\" /passive /norestart /log \"{logPath}\"";

    /// <summary>The scope that upgrades <paramref name="current"/> in place: all users when it is in the
    /// system install folder, otherwise just this user (the per-user MSI installs beside anything
    /// unofficial, and DSH then finds the new one).</summary>
    public static AwsCliInstallScope ScopeFor(AwsCliStatus current, string? systemFolder = null)
    {
        var path = current switch
        {
            AwsCliStatus.TooOld old => old.Path,
            AwsCliStatus.Ready ready => ready.Path,
            AwsCliStatus.Broken broken => broken.Path,
            _ => null,
        };
        if (path is null) return AwsCliInstallScope.CurrentUser;
        var folder = (systemFolder ?? AwsCliSearch.SystemInstallFolder()).TrimEnd('\\', '/');
        return path.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
               || path.StartsWith(folder + "\\", StringComparison.OrdinalIgnoreCase)
            ? AwsCliInstallScope.AllUsers
            : AwsCliInstallScope.CurrentUser;
    }

    /// <summary>Update an installed AWS CLI that is too old (or broken): the same MSI, which upgrades in place.</summary>
    public Task<AwsCliInstallResult> UpgradeAsync(AwsCliStatus current, IProgress<AwsCliInstallProgress>? progress = null,
                                                  CancellationToken cancellationToken = default) =>
        InstallAsync(ScopeFor(current), progress, cancellationToken);

    /// <summary>Download, verify and install the AWS CLI. Cancelling works until Windows Installer starts;
    /// from then on it runs to the end (its own window has a Cancel button).</summary>
    public async Task<AwsCliInstallResult> InstallAsync(AwsCliInstallScope scope = AwsCliInstallScope.CurrentUser,
                                                        IProgress<AwsCliInstallProgress>? progress = null,
                                                        CancellationToken cancellationToken = default)
    {
        if (PlatformProblem is { } problem) return new(AwsCliInstallOutcome.NotSupported, problem);
        var folder = Path.Combine(WorkFolder, Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(folder);
        var url = MsiUrl(scope);
        var msi = Path.Combine(folder, Path.GetFileName(url.AbsolutePath) is { Length: > 0 } name ? name : "AWSCLIV2.msi");
        var log = Path.Combine(folder, "install.log");
        var keepLog = false;
        try
        {
            try
            {
                await DownloadAsync(url, msi, progress, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                return new(AwsCliInstallOutcome.DownloadFailed,
                    $"DSH couldn't download the AWS CLI installer ({ex.Message}). Check your internet connection and try again.");
            }
            if (!HasMsiHeader(msi))
                return new(AwsCliInstallOutcome.DownloadFailed,
                    "The download isn't a Windows Installer file (a proxy or firewall may have replaced it). Nothing was installed.");

            progress?.Report(new AwsCliInstallProgress("Checking the installer's digital signature"));
            AuthenticodeSignature signature;
            try
            {
                signature = await CheckSignature(msi, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new(AwsCliInstallOutcome.SignatureRejected,
                    $"DSH couldn't check the installer's digital signature ({ex.Message}), so it didn't install it.");
            }
            if (!IsAmazonSignature(signature, out var reason))
                return new(AwsCliInstallOutcome.SignatureRejected,
                    $"The downloaded installer isn't validly signed by Amazon ({reason}), so DSH didn't run it.");

            cancellationToken.ThrowIfCancellationRequested();
            var elevated = scope == AwsCliInstallScope.AllUsers;
            progress?.Report(new AwsCliInstallProgress(elevated
                ? "Installing the AWS CLI — approve Windows' prompt to continue"
                : "Installing the AWS CLI"));
            int exitCode;
            try
            {
                exitCode = await RunMsiexec(new MsiexecLaunch(msi, MsiexecArguments(msi, log), elevated, log), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or Win32Exception or PlatformNotSupportedException or InvalidOperationException)
            {
                keepLog = true;
                return new(AwsCliInstallOutcome.Failed, $"Windows Installer didn't start ({ex.Message}).", LogPath: ExistingLog(log));
            }

            var outcome = MapMsiexecExitCode(exitCode);
            if (!outcome.Succeeded)
            {
                keepLog = true;
                return outcome with { LogPath = ExistingLog(log) };
            }

            progress?.Report(new AwsCliInstallProgress("Checking the installation"));
            var status = await CheckInstalled(cancellationToken).ConfigureAwait(false);
            if (status is AwsCliStatus.Ready ready)
            {
                var message = outcome.Outcome == AwsCliInstallOutcome.InstalledRestartRecommended
                    ? $"AWS CLI {ready.Version.ToString(3)} is installed. Windows would like a restart to finish, but you can carry on now."
                    : $"AWS CLI {ready.Version.ToString(3)} is installed.";
                return outcome with { Message = message, Cli = status };
            }
            keepLog = true;
            return new(AwsCliInstallOutcome.Failed, $"The installer finished, but DSH still can't use the AWS CLI: {status.Summary}",
                exitCode, status, ExistingLog(log));
        }
        finally
        {
            Cleanup(folder, keepLog ? log : null);
        }
    }

    /// <summary>The fallback: install with winget (Windows Package Manager), which fetches the same
    /// official MSI and handles Windows' approval prompt itself.</summary>
    public async Task<AwsCliInstallResult> InstallWithWingetAsync(IProgress<AwsCliInstallProgress>? progress = null,
                                                                  CancellationToken cancellationToken = default)
    {
        if (PlatformProblem is { } problem) return new(AwsCliInstallOutcome.NotSupported, problem);
        var run = RunWinget ?? DefaultWinget();
        if (run is null)
            return new(AwsCliInstallOutcome.NotSupported,
                $"winget (App Installer) isn't available on this PC. Download and run the installer yourself: {SystemMsiUrl}");
        const string stage = "Installing the AWS CLI with winget";
        progress?.Report(new AwsCliInstallProgress(stage));
        int exitCode;
        try
        {
            exitCode = await run(WingetArguments, line => progress?.Report(new AwsCliInstallProgress(stage, Detail: line)), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            return new(AwsCliInstallOutcome.Failed, $"winget didn't start ({ex.Message}).");
        }
        progress?.Report(new AwsCliInstallProgress("Checking the installation"));
        var status = await CheckInstalled(cancellationToken).ConfigureAwait(false);
        if (status is AwsCliStatus.Ready ready)
            return new(AwsCliInstallOutcome.Installed, $"AWS CLI {ready.Version.ToString(3)} is installed.", exitCode, status);
        return new(AwsCliInstallOutcome.Failed,
            $"winget couldn't install the AWS CLI (exit code 0x{exitCode:X8}). {status.Summary}", exitCode, status);
    }

    /// <summary>What msiexec's exit code means (Windows Installer error codes).</summary>
    public static AwsCliInstallResult MapMsiexecExitCode(int code) => code switch
    {
        0 => new(AwsCliInstallOutcome.Installed, "The AWS CLI is installed.", code),
        1641 => new(AwsCliInstallOutcome.InstalledRestartRecommended, "The AWS CLI is installed; Windows is restarting to finish.", code),
        3010 => new(AwsCliInstallOutcome.InstalledRestartRecommended, "The AWS CLI is installed; Windows would like a restart to finish.", code),
        1602 => new(AwsCliInstallOutcome.Cancelled, "The installation was cancelled, so the AWS CLI wasn't installed.", code),
        1223 => new(AwsCliInstallOutcome.Cancelled, "Windows' approval prompt was declined, so the AWS CLI wasn't installed.", code),
        1618 => new(AwsCliInstallOutcome.AnotherInstallRunning,
            "Another program is being installed or updated right now, and Windows installs one thing at a time. Wait for it to finish (Windows Update can take a while), then try again.", code),
        1625 => new(AwsCliInstallOutcome.BlockedByPolicy,
            "Your organization's settings don't allow installing software on this PC. Ask your IT team to install the AWS CLI for you.", code),
        1925 or 1730 => new(AwsCliInstallOutcome.NeedsAdministrator,
            "Installing for everyone on this PC needs administrator rights. Install it just for you instead, or ask an administrator.", code),
        1633 => new(AwsCliInstallOutcome.NotSupported, "This installer doesn't support this PC's processor.", code),
        1638 => new(AwsCliInstallOutcome.Failed,
            "Another version of the AWS CLI is already installed in a way this installer can't update. Remove \"AWS Command Line Interface v2\" in Settings › Apps, then try again.", code),
        _ => new(AwsCliInstallOutcome.Failed, $"The AWS CLI installer stopped with error {code}.", code),
    };

    /// <summary>Valid, and signed by Amazon: the certificate's organization (or common name) is Amazon Web
    /// Services or Amazon.com. <paramref name="reason"/> says why not.</summary>
    public static bool IsAmazonSignature(AuthenticodeSignature signature, out string reason)
    {
        if (!signature.Status.Equals("Valid", StringComparison.OrdinalIgnoreCase))
        {
            reason = $"Windows reports the signature as {(signature.Status.Length > 0 ? signature.Status : "missing")}";
            return false;
        }
        if (string.IsNullOrWhiteSpace(signature.SignerSubject))
        {
            reason = "it has no signing certificate";
            return false;
        }
        var names = DistinguishedName(signature.SignerSubject);
        var signer = names.GetValueOrDefault("O") ?? names.GetValueOrDefault("CN");
        foreach (var key in new[] { "O", "CN" })
        {
            if (names.GetValueOrDefault(key) is { } value
                && (value.StartsWith("Amazon Web Services", StringComparison.Ordinal) || value.StartsWith("Amazon.com", StringComparison.Ordinal)))
            {
                reason = "";
                return true;
            }
        }
        reason = $"it is signed by {signer ?? signature.SignerSubject}";
        return false;
    }

    /// <summary>"CN=\"Amazon Web Services, Inc.\", OU=AWS, O=…" → { CN: "Amazon Web Services, Inc.", … }
    /// (first value of each attribute; quoted values may contain commas).</summary>
    public static Dictionary<string, string> DistinguishedName(string subject)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var part = new System.Text.StringBuilder();
        var quoted = false;
        void Flush()
        {
            var text = part.ToString().Trim();
            part.Clear();
            var eq = text.IndexOf('=');
            if (eq <= 0) return;
            var key = text[..eq].Trim();
            var value = text[(eq + 1)..].Trim();
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"') value = value[1..^1].Replace("\"\"", "\"");
            result.TryAdd(key, value);
        }
        foreach (var c in subject)
        {
            if (c == '"') quoted = !quoted;
            if ((c == ',' || c == ';') && !quoted)
            {
                Flush();
                continue;
            }
            part.Append(c);
        }
        Flush();
        return result;
    }

    /// <summary>The PowerShell that prints a file's signature as JSON. The path is quoted for PowerShell
    /// and escaped against wildcards ("[" in a user name would otherwise be a pattern).</summary>
    public static string SignatureScript(string path) => $$"""
        $ErrorActionPreference = 'Stop'
        $sig = Get-AuthenticodeSignature -FilePath ([Management.Automation.WildcardPattern]::Escape('{{path.Replace("'", "''")}}'))
        $cert = $sig.SignerCertificate
        [pscustomobject]@{
          Status = [string]$sig.Status
          StatusMessage = [string]$sig.StatusMessage
          Subject = $(if ($cert) { [string]$cert.Subject } else { $null })
          Thumbprint = $(if ($cert) { [string]$cert.Thumbprint } else { $null })
        } | ConvertTo-Json -Compress
        """;

    /// <summary>Read <see cref="SignatureScript"/>'s output; null when it isn't that JSON.</summary>
    public static AuthenticodeSignature? ParseSignature(string json)
    {
        JsonObject? obj;
        try
        {
            obj = JsonNode.Parse(json.Trim()) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
        if (obj is null) return null;
        // [string] makes Status the enum's name; a bare number means an older shape (0 = Valid).
        var status = obj["Status"] is JsonValue value && JsonNumbers.TryGetInt(value, out var number)
            ? number switch { 0 => "Valid", 1 => "UnknownError", 2 => "NotSigned", 3 => "HashMismatch", 4 => "NotTrusted", 5 => "NotSupportedFileFormat", 6 => "Incompatible", _ => number.ToString(System.Globalization.CultureInfo.InvariantCulture) }
            : JsonArgs.String(obj, "Status") ?? "";
        return new AuthenticodeSignature(status, JsonArgs.String(obj, "Subject"), JsonArgs.String(obj, "StatusMessage"), JsonArgs.String(obj, "Thumbprint"));
    }

    /// <summary>The real signature check: Windows PowerShell's Get-AuthenticodeSignature. Throws when
    /// PowerShell isn't there or doesn't answer — DSH then refuses to install rather than install unchecked.</summary>
    public static async Task<AuthenticodeSignature> PowerShellSignatureAsync(string path, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Checking a digital signature needs Windows.");
        var (_, output, error) = await RecoveryUsb.PowerShellAsync(SignatureScript(path), TimeSpan.FromMinutes(2), cancellationToken).ConfigureAwait(false);
        return ParseSignature(output)
               ?? throw new IOException($"Windows PowerShell didn't report the signature: {TextUtil.Prefix((error + " " + output).Trim(), 300)}");
    }

    /// <summary>The real msiexec: started through the shell so "runas" can show Windows' approval prompt
    /// (declining it is exit code 1223). Once running it isn't stopped — half an installation is worse.</summary>
    public static async Task<int> MsiexecAsync(MsiexecLaunch launch, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Installing an MSI needs Windows.");
        var msiexec = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "msiexec.exe");
        var info = new ProcessStartInfo(msiexec, launch.Arguments)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(launch.MsiPath) ?? "",
        };
        if (launch.Elevated) info.Verb = "runas";
        Process? process;
        try
        {
            process = Process.Start(info);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return 1223;
        }
        if (process is null) throw new IOException("Windows Installer didn't start.");
        using (process)
        {
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            return process.ExitCode;
        }
    }

    /// <summary>winget.exe on PATH, or the App Installer alias in %LOCALAPPDATA%\Microsoft\WindowsApps.</summary>
    private static Func<IReadOnlyList<string>, Action<string>?, CancellationToken, Task<int>>? DefaultWinget()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var winget = AgentShell.FindOnPath("winget.exe");
        var alias = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "winget.exe");
        winget ??= File.Exists(alias) ? alias : null;
        if (winget is null) return null;
        return async (args, onLine, cancellationToken) =>
        {
            var result = await new ProcessAwsCliRunner(winget).RunAsync(args,
                new AwsCliRunOptions { OnLine = line => onLine?.Invoke(line.Text), Timeout = TimeSpan.FromMinutes(20) }, cancellationToken).ConfigureAwait(false);
            return result.ExitCode;
        };
    }

    private async Task DownloadAsync(Uri url, string destination, IProgress<AwsCliInstallProgress>? progress, CancellationToken cancellationToken)
    {
        const string stage = "Downloading the AWS CLI installer";
        using var client = HttpHandler is null ? new HttpClient() : new HttpClient(HttpHandler, disposeHandler: false);
        client.Timeout = Timeout.InfiniteTimeSpan;
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"the server answered {(int)response.StatusCode} {response.ReasonPhrase}");
        var total = response.Content.Headers.ContentLength;
        var partial = destination + ".partial";
        progress?.Report(new AwsCliInstallProgress(stage, 0, total));
        try
        {
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                var buffer = new byte[1 << 16];
                long done = 0, reported = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    done += read;
                    if (done - reported >= 256 * 1024)
                    {
                        reported = done;
                        progress?.Report(new AwsCliInstallProgress(stage, done, total));
                    }
                }
                if (total is { } expected && done != expected)
                    throw new IOException($"the download stopped early ({done:N0} of {expected:N0} bytes)");
                progress?.Report(new AwsCliInstallProgress(stage, done, total ?? done));
            }
            File.Move(partial, destination, overwrite: true);
        }
        finally
        {
            TryDelete(partial);
        }
    }

    private static bool HasMsiHeader(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            Span<byte> head = stackalloc byte[8];
            return file.ReadAtLeast(head, 8, throwOnEndOfStream: false) == 8 && head.SequenceEqual(MsiSignature);
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static string? ExistingLog(string log) => File.Exists(log) ? log : null;

    /// <summary>Remove the download; a failed install's log stays for the user to look at.</summary>
    private static void Cleanup(string folder, string? keep)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder))
                if (keep is null || !string.Equals(file, keep, StringComparison.OrdinalIgnoreCase) || !File.Exists(keep)) TryDelete(file);
            if (!Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Temp files; Windows' disk cleanup gets them eventually.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort.
        }
    }
}
