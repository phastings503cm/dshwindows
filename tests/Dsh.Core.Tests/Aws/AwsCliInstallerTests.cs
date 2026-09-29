using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace Dsh.Core.Tests;

public sealed class AwsCliInstallerTests
{
    /// <summary>The certificate that signs AWSCLIV2-User.msi (downloaded from awscli.amazonaws.com on
    /// 2026-09-29; public, valid until April 2027). Its subject is what Windows' Get-AuthenticodeSignature
    /// reports as the signer.</summary>
    private const string AmazonSigningCertificate = """
        -----BEGIN CERTIFICATE-----
        MIIHYzCCBUugAwIBAgIQBZDEzw2wFBmZU6aM/Omw5zANBgkqhkiG9w0BAQsFADBp
        MQswCQYDVQQGEwJVUzEXMBUGA1UEChMORGlnaUNlcnQsIEluYy4xQTA/BgNVBAMT
        OERpZ2lDZXJ0IFRydXN0ZWQgRzQgQ29kZSBTaWduaW5nIFJTQTQwOTYgU0hBMzg0
        IDIwMjEgQ0ExMB4XDTI2MDQxMzAwMDAwMFoXDTI3MDQxMjIzNTk1OVowgesxEzAR
        BgsrBgEEAYI3PAIBAxMCVVMxGTAXBgsrBgEEAYI3PAIBAhMIRGVsYXdhcmUxHTAb
        BgNVBA8MFFByaXZhdGUgT3JnYW5pemF0aW9uMRAwDgYDVQQFEwc0MTUyOTU0MQsw
        CQYDVQQGEwJVUzETMBEGA1UECBMKV2FzaGluZ3RvbjEQMA4GA1UEBxMHU2VhdHRs
        ZTEiMCAGA1UEChMZQW1hem9uIFdlYiBTZXJ2aWNlcywgSW5jLjEMMAoGA1UECxMD
        QVdTMSIwIAYDVQQDExlBbWF6b24gV2ViIFNlcnZpY2VzLCBJbmMuMIIBojANBgkq
        hkiG9w0BAQEFAAOCAY8AMIIBigKCAYEAnZ1esO7Ve9uz2gnk5QGpkg7s6TLUSATo
        Sddl66D0ZSNAxF+eGJVDhvfHQarDKy8ip/xbXP89NGYzgdbMlaZ2gg3a98IaAdpU
        DCFW5S0qqEiYwpFZiV/Gjai7sCWWfJDyDKF6z4nwkEe4zCX6diQmhRwl1QlN6yVC
        7+15DATnr/1tHxsTcwDiSdIqzud803sUgA2FA1mLiePDADcGx3vHvJ76OdDNC/2F
        j1h8tbOa6VLZwSj5rgtVOVHP6KGUGtekgJDNyEes6hkxfuyQvtTTIqT6yL5j3vrg
        d/T80X/qKzYkr8uxgt3gJxx+4SrBK0DmHHQrunkI3rpfOkMs2+/5Syy8sQpYtaor
        6Y7z07ff65FFTFWkz0Q2c2SaJzXm7O1QRXJByTuknoYz/1mBthcNOfNl3gYAcfpP
        alr5IS3Clsua8UmVeD9W/t0waXczqCQ5VicJ1SI1xpYGbaZ23GCzzmh8uGeY9pTK
        FrGxF66JivcUC5YSzGDaQdDmRKPT2FXVAgMBAAGjggICMIIB/jAfBgNVHSMEGDAW
        gBRoN+Drtjv4XxGG+/5hewiIZfROQjAdBgNVHQ4EFgQU42MT0Q71gb/5ilFq8mAf
        1kYe1LUwPQYDVR0gBDYwNDAyBgVngQwBAzApMCcGCCsGAQUFBwIBFhtodHRwOi8v
        d3d3LmRpZ2ljZXJ0LmNvbS9DUFMwDgYDVR0PAQH/BAQDAgeAMBMGA1UdJQQMMAoG
        CCsGAQUFBwMDMIG1BgNVHR8Ega0wgaowU6BRoE+GTWh0dHA6Ly9jcmwzLmRpZ2lj
        ZXJ0LmNvbS9EaWdpQ2VydFRydXN0ZWRHNENvZGVTaWduaW5nUlNBNDA5NlNIQTM4
        NDIwMjFDQTEuY3JsMFOgUaBPhk1odHRwOi8vY3JsNC5kaWdpY2VydC5jb20vRGln
        aUNlcnRUcnVzdGVkRzRDb2RlU2lnbmluZ1JTQTQwOTZTSEEzODQyMDIxQ0ExLmNy
        bDCBlAYIKwYBBQUHAQEEgYcwgYQwJAYIKwYBBQUHMAGGGGh0dHA6Ly9vY3NwLmRp
        Z2ljZXJ0LmNvbTBcBggrBgEFBQcwAoZQaHR0cDovL2NhY2VydHMuZGlnaWNlcnQu
        Y29tL0RpZ2lDZXJ0VHJ1c3RlZEc0Q29kZVNpZ25pbmdSU0E0MDk2U0hBMzg0MjAy
        MUNBMS5jcnQwCQYDVR0TBAIwADANBgkqhkiG9w0BAQsFAAOCAgEAqz8yW852TJxQ
        6xyVP7Gd4l3IcNMyWeT1yUn/PsPPyx9mgqfO7SQqkSlHtlVr7FA9nA90D92k/sHT
        A8ZZMniqrkg91zfCJ0HL20ee/m5iQ7g9kaWvTTXUVHmn1ZB7okAzlKRM1nR9toe0
        o2gsKJE3vtHXuaKVAxWpNV6r0+bDICLZH+r2dKDm01E5T2IX7H4yZZRdcfgAEYUE
        R8f9XX5f8Yeq5hlVIRz2+6nKuA+wcUZqUxhtcnqKXTLpMYvc0H/13X09N0UirVQB
        mRHlxKMhTM9dC/j8M+SBuEWQzo1wmeFhB+CDWeZObbTRq4NjQXdlRsT34is9zb9B
        9rgtTSS1xQ+pk6f8esqOKcMhKJWzFlFZ0Etu5WmhrQYK4zUYxr+rKLSyc8l8yXcG
        BBmdHhH4Wr3iGFRwfSt557uKpKOCY2inTZMOibaVP5ktrq8an1AWHH3Jkwu9jF1H
        jNlucVDd1tRl/5Idxg8vJQhHIv7a9IEX8P9EeDAw/gbKqEcg7oqLq/3hUesaQRTt
        t0XQGVhn2O4yymeFrym6rJUIGppVzxPfJnXzEpoEVmijzVrowg802uSvCCQK1T5i
        y/Wd6a08lLHSy//R0nv0Of5hVr6zvcHAsSvCT9+JSBkSgMXUmX395cbyE0O1ku3W
        UhYCcIcy1v2KpT80okt/ggPfxEM0Iug=
        -----END CERTIFICATE-----
        """;

    private static readonly AwsCliStatus ReadyAfter = new AwsCliStatus.Ready(new Version(2, 37, 5), @"C:\Users\me\AppData\Local\Programs\Amazon\AWSCLIV2\aws.exe");

    private static readonly AuthenticodeSignature AmazonSigned = new("Valid",
        "CN=\"Amazon Web Services, Inc.\", OU=AWS, O=\"Amazon Web Services, Inc.\", L=Seattle, S=Washington, C=US, SERIALNUMBER=4152954, OID.2.5.4.15=Private Organization, OID.1.3.6.1.4.1.311.60.2.1.2=Delaware, OID.1.3.6.1.4.1.311.60.2.1.3=US");

    /// <summary>A fake MSI: the OLE header every MSI starts with, then filler.</summary>
    private static byte[] Msi(int size = 1_000_000)
    {
        var bytes = new byte[size];
        new Random(7).NextBytes(bytes);
        byte[] header = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];
        header.CopyTo(bytes, 0);
        return bytes;
    }

    /// <summary>An installer whose every outside effect is scripted and recorded.</summary>
    private sealed class Harness : IDisposable
    {
        public TempDirectory Work { get; } = new("dsh-aws-install");
        public List<Uri> Requested { get; } = [];
        public List<string> SignatureChecks { get; } = [];
        public List<MsiexecLaunch> Launches { get; } = [];
        public byte[]? InstalledBytes { get; private set; }
        public byte[] Download { get; set; } = Msi();
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public Func<AuthenticodeSignature> Signature { get; set; } = () => AmazonSigned;
        public int MsiexecExitCode { get; set; }
        public bool WriteLog { get; set; } = true;
        public AwsCliStatus After { get; set; } = ReadyAfter;

        public AwsCliInstaller Installer() => new()
        {
            WorkFolder = Work.Path,
            PlatformProblem = null,
            HttpHandler = new FakeHttpHandler(request =>
            {
                lock (Requested) Requested.Add(request.RequestUri!);
                return new HttpResponseMessage(Status) { Content = new ByteArrayContent(Status == HttpStatusCode.OK ? Download : []) };
            }),
            CheckSignature = (path, _) =>
            {
                SignatureChecks.Add(path);
                return Task.FromResult(Signature());
            },
            RunMsiexec = (launch, _) =>
            {
                Launches.Add(launch);
                InstalledBytes = File.ReadAllBytes(launch.MsiPath);
                if (WriteLog) File.WriteAllText(launch.LogPath, "=== Logging started ===\nMSI (s) (A0:B4) ...");
                return Task.FromResult(MsiexecExitCode);
            },
            CheckInstalled = _ => Task.FromResult(After),
        };

        public string[] FilesLeft() => Directory.Exists(Work.Path) ? Directory.GetFiles(Work.Path, "*", SearchOption.AllDirectories) : [];

        public void Dispose() => Work.Dispose();
    }

    // MARK: - The whole install

    [Fact]
    public async Task InstallsJustForThisUserWithoutAPrompt()
    {
        using var h = new Harness();
        var progress = new ListProgress<AwsCliInstallProgress>();
        var result = await h.Installer().InstallAsync(AwsCliInstallScope.CurrentUser, progress);

        Assert.Equal(AwsCliInstallOutcome.Installed, result.Outcome);
        Assert.True(result.Succeeded);
        Assert.Equal("AWS CLI 2.37.5 is installed.", result.Message);
        Assert.Same(ReadyAfter, result.Cli);
        Assert.Equal([new Uri(AwsCliInstaller.UserMsiUrl)], h.Requested);

        var launch = Assert.Single(h.Launches);
        Assert.False(launch.Elevated);
        Assert.EndsWith("AWSCLIV2-User.msi", launch.MsiPath);
        Assert.Equal($"/i \"{launch.MsiPath}\" /passive /norestart /log \"{launch.LogPath}\"", launch.Arguments);
        Assert.Equal(h.Download, h.InstalledBytes);                   // what was checked is what was installed
        Assert.Equal([launch.MsiPath], h.SignatureChecks);
        Assert.Empty(h.FilesLeft());                                  // download and log removed after success

        var stages = progress.Items.Select(p => p.Stage).Distinct().ToList();
        Assert.Equal(["Downloading the AWS CLI installer", "Checking the installer's digital signature", "Installing the AWS CLI", "Checking the installation"], stages);
        var downloads = progress.Items.Where(p => p.Stage.StartsWith("Downloading", StringComparison.Ordinal)).ToList();
        Assert.True(downloads.Count >= 4);
        Assert.All(downloads, p => Assert.Equal(h.Download.Length, p.Total));
        Assert.Equal(downloads.Select(p => p.Done).Order(), downloads.Select(p => p.Done)); // only goes forward
        Assert.Equal(0, downloads[0].Done);
        Assert.Equal(h.Download.Length, downloads[^1].Done);
        Assert.Equal(1.0, downloads[^1].Fraction);
    }

    [Fact]
    public async Task InstallingForEveryoneAsksWindowsForApproval()
    {
        using var h = new Harness { MsiexecExitCode = 3010 };
        var progress = new ListProgress<AwsCliInstallProgress>();
        var result = await h.Installer().InstallAsync(AwsCliInstallScope.AllUsers, progress);
        Assert.Equal(AwsCliInstallOutcome.InstalledRestartRecommended, result.Outcome);
        Assert.Contains("2.37.5 is installed", result.Message);
        Assert.Contains("restart", result.Message);
        Assert.Equal([new Uri(AwsCliInstaller.SystemMsiUrl)], h.Requested);
        Assert.True(Assert.Single(h.Launches).Elevated);
        Assert.Contains(progress.Items, p => p.Stage.Contains("approve Windows' prompt", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("NotSigned", null)]
    [InlineData("HashMismatch", "CN=\"Amazon Web Services, Inc.\", O=\"Amazon Web Services, Inc.\"")]
    [InlineData("NotTrusted", "CN=\"Amazon Web Services, Inc.\", O=\"Amazon Web Services, Inc.\"")]
    [InlineData("Valid", "CN=Totally Legit Software, O=Totally Legit Software, C=US")]
    [InlineData("Valid", "CN=Not Amazon Web Services, O=Mallory Ltd")]
    public async Task RefusesAnythingNotValidlySignedByAmazon(string status, string? subject)
    {
        using var h = new Harness { Signature = () => new AuthenticodeSignature(status, subject) };
        var result = await h.Installer().InstallAsync();
        Assert.Equal(AwsCliInstallOutcome.SignatureRejected, result.Outcome);
        Assert.Contains("didn't run it", result.Message);
        Assert.Empty(h.Launches);
        Assert.Empty(h.FilesLeft());
        Assert.False(result.CanTryWinget);
    }

    [Fact]
    public async Task RefusesWhenTheSignatureCantBeChecked()
    {
        using var h = new Harness { Signature = () => throw new PlatformNotSupportedException("Windows PowerShell isn't available") };
        var result = await h.Installer().InstallAsync();
        Assert.Equal(AwsCliInstallOutcome.SignatureRejected, result.Outcome);
        Assert.Contains("couldn't check", result.Message);
        Assert.Empty(h.Launches);
    }

    [Fact]
    public async Task ADownloadThatIsntAnMsiIsNotRun()
    {
        using var h = new Harness { Download = "<html><body>Access denied by your proxy</body></html>"u8.ToArray() };
        var result = await h.Installer().InstallAsync();
        Assert.Equal(AwsCliInstallOutcome.DownloadFailed, result.Outcome);
        Assert.Empty(h.SignatureChecks);
        Assert.Empty(h.Launches);
        Assert.True(result.CanTryWinget);
    }

    [Fact]
    public async Task DownloadErrorsAreExplained()
    {
        using var h = new Harness { Status = HttpStatusCode.NotFound };
        var result = await h.Installer().InstallAsync();
        Assert.Equal(AwsCliInstallOutcome.DownloadFailed, result.Outcome);
        Assert.Contains("404", result.Message);
        Assert.Empty(h.FilesLeft());
    }

    [Fact]
    public async Task CancellingTheDownloadInstallsNothingAndLeavesNothing()
    {
        using var h = new Harness { Download = Msi(4_000_000) };
        using var cts = new CancellationTokenSource();
        var progress = new ListProgress<AwsCliInstallProgress> { OnReport = p => { if (p.Done > 0) cts.Cancel(); } };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Installer().InstallAsync(AwsCliInstallScope.CurrentUser, progress, cts.Token));
        Assert.Empty(h.SignatureChecks);
        Assert.Empty(h.Launches);
        Assert.Empty(h.FilesLeft());
    }

    [Fact]
    public async Task AFailedInstallKeepsItsLog()
    {
        using var h = new Harness { MsiexecExitCode = 1618 };
        var result = await h.Installer().InstallAsync();
        Assert.Equal(AwsCliInstallOutcome.AnotherInstallRunning, result.Outcome);
        Assert.Equal(1618, result.ExitCode);
        Assert.NotNull(result.LogPath);
        Assert.True(File.Exists(result.LogPath));
        Assert.Equal([result.LogPath], h.FilesLeft());   // the MSI itself is gone
    }

    [Fact]
    public async Task InstalledButStillNotUsableIsAFailure()
    {
        using var h = new Harness { After = new AwsCliStatus.Missing() };
        var result = await h.Installer().InstallAsync();
        Assert.Equal(AwsCliInstallOutcome.Failed, result.Outcome);
        Assert.Contains("still can't use the AWS CLI", result.Message);
        Assert.True(result.CanTryWinget);
    }

    [Fact]
    public async Task DeclinedApprovalIsCancelled()
    {
        using var h = new Harness { MsiexecExitCode = 1223, WriteLog = false };
        var result = await h.Installer().InstallAsync(AwsCliInstallScope.AllUsers);
        Assert.Equal(AwsCliInstallOutcome.Cancelled, result.Outcome);
        Assert.Null(result.LogPath);
        Assert.False(result.CanTryWinget);
    }

    [Fact]
    public async Task NothingHappensWhereItCantWork()
    {
        using var h = new Harness();
        var installer = new AwsCliInstaller { WorkFolder = h.Work.Path, PlatformProblem = "The AWS CLI needs 64-bit Windows." };
        Assert.Equal(AwsCliInstallOutcome.NotSupported, (await installer.InstallAsync()).Outcome);
        Assert.Equal(AwsCliInstallOutcome.NotSupported, (await installer.InstallWithWingetAsync()).Outcome);
        Assert.Empty(h.Requested);
    }

    // MARK: - Exit codes, platforms, signatures

    [Theory]
    [InlineData(0, AwsCliInstallOutcome.Installed, false)]
    [InlineData(3010, AwsCliInstallOutcome.InstalledRestartRecommended, false)]
    [InlineData(1641, AwsCliInstallOutcome.InstalledRestartRecommended, false)]
    [InlineData(1602, AwsCliInstallOutcome.Cancelled, false)]
    [InlineData(1223, AwsCliInstallOutcome.Cancelled, false)]
    [InlineData(1618, AwsCliInstallOutcome.AnotherInstallRunning, false)]
    [InlineData(1625, AwsCliInstallOutcome.BlockedByPolicy, false)]
    [InlineData(1925, AwsCliInstallOutcome.NeedsAdministrator, true)]
    [InlineData(1633, AwsCliInstallOutcome.NotSupported, false)]
    [InlineData(1638, AwsCliInstallOutcome.Failed, true)]
    [InlineData(1603, AwsCliInstallOutcome.Failed, true)]
    public void MapsWindowsInstallerExitCodes(int code, AwsCliInstallOutcome outcome, bool tryWinget)
    {
        var result = AwsCliInstaller.MapMsiexecExitCode(code);
        Assert.Equal(outcome, result.Outcome);
        Assert.Equal(code, result.ExitCode);
        Assert.Equal(tryWinget, result.CanTryWinget);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
        if (code == 1603) Assert.Contains("1603", result.Message);
    }

    [Theory]
    [InlineData(false, Architecture.X64, 0, true)]
    [InlineData(true, Architecture.X86, 19045, true)]
    [InlineData(true, Architecture.Arm64, 19045, true)]   // Windows 10 on ARM can't run x64 programs
    [InlineData(true, Architecture.Arm64, 26100, false)]  // Windows 11 on ARM emulates x64
    [InlineData(true, Architecture.X64, 19045, false)]
    public void KnowsWhereTheInstallerRuns(bool windows, Architecture architecture, int build, bool problem) =>
        Assert.Equal(problem, AwsCliInstaller.PlatformProblemFor(windows, architecture, build) is not null);

    [Fact]
    public void AcceptsAmazonsRealSigningCertificate()
    {
        var certificate = X509Certificate2.CreateFromPem(AmazonSigningCertificate);
        Assert.True(AwsCliInstaller.IsAmazonSignature(new AuthenticodeSignature("Valid", certificate.Subject), out var reason), reason);
        Assert.True(AwsCliInstaller.IsAmazonSignature(AmazonSigned, out _));
        Assert.True(AwsCliInstaller.IsAmazonSignature(new AuthenticodeSignature("Valid", "CN=Amazon.com Services LLC, O=Amazon.com Services LLC, L=Seattle, S=Washington, C=US"), out _));
        Assert.Equal("Amazon Web Services, Inc.", AwsCliInstaller.DistinguishedName(certificate.Subject)["O"]);
        Assert.False(AwsCliInstaller.IsAmazonSignature(new AuthenticodeSignature("Valid", "CN=Mallory, O=Mallory"), out reason));
        Assert.Contains("Mallory", reason);
    }

    [Fact]
    public void ReadsPowerShellsAnswer()
    {
        var signature = AwsCliInstaller.ParseSignature("""{"Status":"Valid","StatusMessage":"Signature verified.","Subject":"CN=\"Amazon Web Services, Inc.\", O=\"Amazon Web Services, Inc.\"","Thumbprint":"81A9EFB93199F2C7441EE14050360EDAB7E8242A"}""");
        Assert.Equal("Valid", signature!.Status);
        Assert.Equal("Signature verified.", signature.StatusMessage);
        Assert.Equal("81A9EFB93199F2C7441EE14050360EDAB7E8242A", signature.Thumbprint);
        Assert.True(AwsCliInstaller.IsAmazonSignature(signature, out _));
        Assert.Equal("Valid", AwsCliInstaller.ParseSignature("""{"Status":0,"Subject":null}""")!.Status);
        Assert.Equal("NotSigned", AwsCliInstaller.ParseSignature("""{"Status":2}""")!.Status);
        Assert.Null(AwsCliInstaller.ParseSignature("Get-AuthenticodeSignature : Cannot find path"));
    }

    [Fact]
    public void TheSignatureScriptQuotesThePath()
    {
        var script = AwsCliInstaller.SignatureScript(@"C:\Users\O'Brien [Work]\AppData\Local\Temp\dsh-aws-cli\AWSCLIV2.msi");
        Assert.Contains(@"Escape('C:\Users\O''Brien [Work]\AppData\Local\Temp\dsh-aws-cli\AWSCLIV2.msi')", script);
        Assert.Contains("Get-AuthenticodeSignature -FilePath", script);
        Assert.Contains("ConvertTo-Json -Compress", script);
    }

    [Fact]
    public void UpgradesInTheScopeItWasInstalledIn()
    {
        var system = Path.GetFullPath("/pf/Amazon/AWSCLIV2");
        Assert.Equal(AwsCliInstallScope.AllUsers,
            AwsCliInstaller.ScopeFor(new AwsCliStatus.TooOld(new Version(2, 31, 0), Path.Combine(system, "aws.exe")), system));
        Assert.Equal(AwsCliInstallScope.CurrentUser,
            AwsCliInstaller.ScopeFor(new AwsCliStatus.TooOld(new Version(2, 31, 0), Path.GetFullPath("/home/me/AppData/Local/Programs/Amazon/AWSCLIV2/aws.exe")), system));
        Assert.Equal(AwsCliInstallScope.CurrentUser,
            AwsCliInstaller.ScopeFor(new AwsCliStatus.TooOld(new Version(1, 29, 0), Path.GetFullPath("/pf/Amazon/AWSCLI/bin/aws.exe")), system));
        Assert.Equal(AwsCliInstallScope.CurrentUser, AwsCliInstaller.ScopeFor(new AwsCliStatus.Missing(), system));
    }

    [Fact]
    public async Task UpgradeUsesTheMatchingInstaller()
    {
        using var h = new Harness();
        var current = new AwsCliStatus.TooOld(new Version(2, 31, 0), Path.Combine(AwsCliSearch.SystemInstallFolder(), "aws.exe"));
        var result = await h.Installer().UpgradeAsync(current);
        Assert.True(result.Succeeded);
        Assert.Equal([new Uri(AwsCliInstaller.SystemMsiUrl)], h.Requested);
        Assert.True(Assert.Single(h.Launches).Elevated);
    }

    // MARK: - winget

    [Fact]
    public async Task WingetIsTheFallback()
    {
        IReadOnlyList<string>? ran = null;
        var progress = new ListProgress<AwsCliInstallProgress>();
        var installer = new AwsCliInstaller
        {
            PlatformProblem = null,
            RunWinget = (args, onLine, _) =>
            {
                ran = args;
                onLine?.Invoke("Found AWS Command Line Interface v2 [Amazon.AWSCLI] Version 2.37.5");
                onLine?.Invoke("Successfully installed");
                return Task.FromResult(0);
            },
            CheckInstalled = _ => Task.FromResult(ReadyAfter),
        };
        var result = await installer.InstallWithWingetAsync(progress);
        Assert.Equal(AwsCliInstallOutcome.Installed, result.Outcome);
        Assert.Equal(["install", "--id", "Amazon.AWSCLI", "-e", "--silent", "--accept-package-agreements", "--accept-source-agreements"], ran);
        Assert.Contains(progress.Items, p => p.Detail == "Successfully installed");
    }

    [Fact]
    public async Task WingetFailuresShowTheCode()
    {
        var installer = new AwsCliInstaller
        {
            PlatformProblem = null,
            RunWinget = (_, _, _) => Task.FromResult(unchecked((int)0x8A150011)),
            CheckInstalled = _ => Task.FromResult<AwsCliStatus>(new AwsCliStatus.Missing()),
        };
        var result = await installer.InstallWithWingetAsync();
        Assert.Equal(AwsCliInstallOutcome.Failed, result.Outcome);
        Assert.Contains("0x8A150011", result.Message);
    }
}
