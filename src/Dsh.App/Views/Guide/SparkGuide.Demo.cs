using System.Net;
using System.Net.Security;
using System.Text.Json;
using Dsh.Core;

namespace Dsh.App.Views.Guide;

// MARK: - Sample state for the self-test's screenshots

public sealed partial class SparkGuide
{
    private const string DemoFingerprint = "3F:A2:9C:41:7B:E0:55:12:C8:6D:0A:F3:94:2E:B7:61:D5:08:4A:9E:23:CB:70:1F:66:E2:3D:A8:5C:91:04:BE";

    /// <summary>Show <paramref name="page"/> filled with sample state, touching no network — the
    /// self-test renders the guide's pages with it.</summary>
    internal void ShowDemo(GuidePage page)
    {
        _demo = true;
        OnHidden();
        _state.Start = GuideStart.New;
        _state.Host = "192.168.1.42";
        _state.HostName = "spark-3f2a.local";
        _state.SshUser = "alice";

        var spark = new FoundHost(IPAddress.Parse("192.168.1.42"),
        [
            new FoundService(22, ServiceKind.Ssh, "Remote login (SSH)") { Detail = "OpenSSH 9.6p1 · Ubuntu", Identified = true },
            new FoundService(8999, ServiceKind.SparkSwapper, "Spark Swapper") { SetupNeeded = true, Tls = true, Identified = true, Detail = "Installed — waiting for its admin login" },
            new FoundService(11443, ServiceKind.SparkModelFront, "Spark model API") { NeedsKey = true, Tls = true, Identified = true, Detail = "Answering — needs its API key" },
        ]) { HostName = "spark-3f2a.local", Identified = true };
        var desktop = new FoundHost(IPAddress.Parse("192.168.1.23"),
            [new FoundService(22, ServiceKind.Ssh, "Remote login (SSH)") { Detail = "OpenSSH 9.6p1 · Ubuntu", Identified = true }])
        { HostName = "media-server.lan", Identified = true };
        var hosts = new[] { spark, desktop };
        _scanner.ShowDemo(hosts, page >= GuidePage.Find ? spark : null, "Found 2 devices.");

        switch (page)
        {
            case GuidePage.Download or GuidePage.WriteUsb:
                _state.Start = GuideStart.Fresh;
                _source = new RecoverySource(@"C:\Users\alice\Downloads\dgx-spark-recovery-image.tar.gz", RecoverySourceKind.TarGz,
                    "dgx-spark-recovery/usb", 9_800_000_000, 214, 3_900_000_000, "images/rootfs-part-02.img", true);
                _disks = RecoveryUsb.ParseDisks("""
                    [{"Number":0,"FriendlyName":"Samsung SSD 990 PRO 2TB","Size":2000398934016,"BusType":"NVMe","IsBoot":true,"IsSystem":true,"DriveLetters":["C"]},
                     {"Number":1,"FriendlyName":"SanDisk Ultra","Size":32015679488,"SerialNumber":"4C530001","BusType":"USB","DriveLetters":["E"],"Labels":["SANDISK"]},
                     {"Number":2,"FriendlyName":"WD Elements 25A2","Size":2000365289472,"SerialNumber":"WX12","BusType":"USB","DriveLetters":["F"],"Labels":["Backup"]}]
                    """).ToList();
                _chosenDisk = _disks[1];
                break;
            case GuidePage.Install:
                _machine = SparkMachineState.Parse("@@os\nPRETTY_NAME=\"Ubuntu 24.04.3 LTS\"\n@@dgx\nDGX_NAME=\"DGX Spark\"\n@@groups\nalice sudo docker\n@@docker\nDocker version 28.3.3, build 980b856\n@@gpu\nNVIDIA GB10, 580.95.05\n@@tools\ncurl\n@@end\n");
                foreach (var line in new[]
                         {
                             "Signed in to spark-3f2a (192.168.1.42) as alice.",
                             "Downloading and running the Spark Swapper installer…",
                             "==> git 2.43.0 present.",
                             "==> Cloning https://github.com/gnubyte/DGX-Spark-Swapper → /home/alice/spark-swapper …",
                             "==> Installing the app (systemd service, cert, config)…",
                             "[sudo] password for alice: (answered by DSH)",
                             "==> installing app to /opt/spark-swapper",
                             "==> rendering config: user=alice host=192.168.1.42 openclaw=/home/alice/.npm-global/bin/openclaw",
                             "==> generating a self-signed certificate (10 years)",
                             "  Spark Swapper is up:  https://192.168.1.42:8999",
                             "Setting up the secure model address (HTTPS on port 11443)…",
                             "==> Installing nginx (a small web server)...",
                             "==> Writing /etc/nginx/conf.d/spark-model-front.conf",
                             "==> The model API is served at https://192.168.1.42:11443/v1",
                             "Checking Spark Swapper answers at https://192.168.1.42:8999 …",
                             "It answers. Done.",
                         })
                    _installLog.Append(line);
                _installOk = true;
                _installPhase = InstallPhase.Done;
                break;
            case GuidePage.Certificate:
                _cert = new CertificateDetails(DemoFingerprint, "CN=Spark Swapper (spark-3f2a)", "CN=Spark Swapper (spark-3f2a)",
                    DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(10), SslPolicyErrors.RemoteCertificateChainErrors,
                    ["localhost", "spark-3f2a", "127.0.0.1", "192.168.1.42"], []);
                _certMatchesSpark = true;
                _windowsTrusted = false;
                break;
            case GuidePage.Admin:
                _session = new SwapperSession(true, null, null, false);
                break;
            case GuidePage.Model:
                _status = JsonSerializer.Deserialize<SwapperStatus>("""
                    {"active": null, "loading": "standard", "busy": true,
                     "models": {
                       "standard": {"key": "standard", "title": "Qwen3.8 27B", "tagline": "The dependable one: biggest context, steady tool use.",
                                    "engine": "SGLang · NVFP4 · EAGLE speculative decoding", "context": 1000000, "served_id": "qwen3.8-27b-sglang",
                                    "vision": true, "running": true, "healthy": false, "order": 1},
                       "flash": {"key": "flash", "title": "Qwen3.8 Flash Next", "tagline": "The quick one: faster decode, vision and video, 512K context.",
                                 "engine": "vLLM · NVFP4 · MTP-3 · PLE offload", "context": 524288, "served_id": "qwen3.8-flash-next",
                                 "vision": true, "running": false, "healthy": false, "order": 2}},
                     "job": {"id": "a1", "target": "standard", "source": null, "state": "running", "error": null, "note": null,
                             "started": 0, "finished": null,
                             "steps": [{"key": "prepare", "label": "Getting ready", "state": "done"},
                                       {"key": "stop", "label": "Stopping the current model", "state": "skipped"},
                                       {"key": "memory", "label": "Waiting for GPU memory to free up", "state": "done"},
                                       {"key": "start", "label": "Loading the new model", "state": "running"},
                                       {"key": "verify", "label": "Checking it answers", "state": "pending"},
                                       {"key": "openclaw", "label": "Pointing OpenClaw at it", "state": "pending"}]}}
                    """, SwapperStatus.Json);
                _job = _status!.Job! with { Started = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 754 };
                _jobTitle = "Starting Qwen3.8 27B";
                _chosenModel = "standard";
                _provision = SparkSwapperClient.ParseProvisioning(System.Text.Json.Nodes.JsonNode.Parse("""
                    {"git": {"present": true}, "docker": {"present": true}, "sglang_recipe": {"present": true},
                     "flash_recipe": {"present": false}, "openclaw": {"present": false}, "ssl": {"present": true}}
                    """)!.AsObject());
                foreach (var line in new[]
                         {
                             "── Starting Qwen3.8 27B ──",
                             "Host memory: 118 GiB available of 128 GiB.",
                             "Container qwen3.8-27b-sglang is missing — it will be recreated from the saved recipe.",
                             "Nothing else is running.",
                             "Memory available: 118 GiB of 128 GiB. Good to go.",
                             "…still loading (600s, 96 GiB free)  Downloading model shards: 62%",
                         })
                    _modelLog.Append(line);
                break;
            case GuidePage.Connect:
                _connect[0] = (StepState.Done, "Got it.");
                _connect[1] = (StepState.Done, "https://192.168.1.42:11443 answers, with the same certificate you trusted.");
                _connect[2] = (StepState.Done, "DSH now talks to https://192.168.1.42:11443/v1 · qwen3.8-27b-sglang");
                _connect[3] = (StepState.Done, "It answered:");
                _hello = "Hello and welcome to your new DGX Spark — it's great to meet you! What shall we build first?";
                _state.Connected = true;
                break;
        }
        _state.Page = page;
        _host.Render();
    }
}
