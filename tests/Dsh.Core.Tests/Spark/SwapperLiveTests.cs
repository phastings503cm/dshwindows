using System.Diagnostics;
using System.Net.Sockets;

namespace Dsh.Core.Tests;

/// <summary>Runs only when DSH_SWAPPER_SRC points at a Spark Swapper checkout (and python3 and
/// openssl are on PATH): starts the real swapper/app.py on a spare port with throwaway config and
/// state folders, and drives it with <see cref="SparkSwapperClient"/> the way the setup guide does.</summary>
public sealed class SwapperLiveFactAttribute : FactAttribute
{
    public SwapperLiveFactAttribute()
    {
        var source = Environment.GetEnvironmentVariable("DSH_SWAPPER_SRC");
        if (string.IsNullOrEmpty(source) || !File.Exists(Path.Combine(source, "swapper", "app.py")))
            Skip = "Set DSH_SWAPPER_SRC to a Spark Swapper checkout to run.";
    }
}

public sealed class SwapperLiveTests
{
    [SwapperLiveFact]
    public async Task TheGuidesCallsAgainstTheRealService()
    {
        using var temp = new TempDirectory("swapper-live");
        var port = FreePort.Get();
        var info = new ProcessStartInfo("python3", Path.Combine(Environment.GetEnvironmentVariable("DSH_SWAPPER_SRC")!, "swapper", "app.py"))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        info.Environment["SWAPPER_CONF"] = temp["conf"];
        info.Environment["SWAPPER_STATE"] = temp["state"];
        info.Environment["SWAPPER_PORT"] = port.ToString();
        info.Environment["SWAPPER_BIND"] = "127.0.0.1";
        using var process = Process.Start(info)!;
        try
        {
            for (var i = 0; i < 100; i++)
            {
                try
                {
                    using var probe = new TcpClient();
                    await probe.ConnectAsync("127.0.0.1", port);
                    break;
                }
                catch (SocketException)
                {
                    await Task.Delay(100);
                }
            }
            var url = new Uri($"https://127.0.0.1:{port}");
            var found = await NetworkScanner.ProbeHostAsync("127.0.0.1", new ScanOptions
            {
                Ports = [new ScanPort(port, ServiceKind.SparkSwapper, "Spark Swapper")], ResolveNames = false,
            });
            var panel = found?.Swapper;
            Assert.NotNull(panel);
            Assert.True(panel.SetupNeeded);

            var fingerprint = await SparkSwapperClient.ProbeFingerprintAsync(url);
            Assert.Equal(panel.CertificateFingerprint, fingerprint);
            Assert.NotNull(fingerprint);
            var certificate = await CertificateProbe.ProbeAsync("127.0.0.1", port);
            Assert.Equal(fingerprint, certificate.Fingerprint);
            Assert.StartsWith("Spark Swapper (", certificate.CommonName);

            using var client = new SparkSwapperClient(url, "admin", "correct horse battery", fingerprint);
            Assert.True((await client.SessionAsync()).SetupNeeded);
            await client.SetupAdminAsync();
            var session = await client.SessionAsync();
            Assert.False(session.SetupNeeded);
            Assert.Equal("admin", session.User);
            Assert.Equal("admin", session.Role);

            // A second client must sign in with the same login.
            using var second = new SparkSwapperClient(url, "admin", "correct horse battery", fingerprint);
            var status = await second.StatusAsync();
            Assert.Contains("standard", status.AllModels.Keys);
            var items = await second.ProvisioningAsync();
            Assert.Contains(items, i => i.Action == "git");
            var credentials = await second.CredentialsAsync();
            Assert.Equal("vllm-local", credentials.ApiKey);
            using var downloaded = await second.DownloadCertificateAsync();
            Assert.Equal(fingerprint, SparkSwapperClient.Fingerprint(downloaded.RawData));

            // Installing "git" on a box that has it just reports it present; follow its log to the end.
            var job = await second.InstallAsync("git");
            Assert.NotNull(job);
            var tail = new SwapperLogTail();
            var lines = new List<string>();
            for (var i = 0; i < 100 && job is { IsRunning: true }; i++)
            {
                await Task.Delay(100);
                job = await second.ProvisioningJobAsync();
                lines.AddRange(tail.Next(job).Select(l => l.M));
            }
            Assert.True(job?.IsDone, string.Join("\n", lines));
            Assert.Contains(lines, l => l.Contains("git"));

            // Setup again is refused: the first visitor was the admin.
            using var late = new SparkSwapperClient(url, "mallory", "password123", fingerprint);
            var refused = await Assert.ThrowsAsync<SwapperException>(() => late.SetupAdminAsync());
            Assert.Equal(409, refused.StatusCode);
        }
        finally
        {
            try { process.Kill(entireProcessTree: true); } catch (Exception) { }
        }
    }
}
