using System.Diagnostics;
using System.Net;

namespace Dsh.Core.Tests;

/// <summary>The scanner's pure helpers, and whole scans against real sockets on 127.0.0.1.</summary>
public sealed class NetworkScannerTests
{
    // MARK: - Where to look

    [Theory]
    [InlineData("Ethernet", "Intel(R) Ethernet Connection I219-V", false)]
    [InlineData("Wi-Fi", "Intel(R) Wi-Fi 6E AX211 160MHz", false)]
    [InlineData("eth0", "", false)]
    [InlineData("enp3s0", "", false)]
    [InlineData("wlan0", "", false)]
    [InlineData("vEthernet (WSL (Hyper-V firewall))", "Hyper-V Virtual Ethernet Adapter", true)]
    [InlineData("vEthernet (Default Switch)", "Hyper-V Virtual Ethernet Adapter #2", true)]
    [InlineData("Ethernet 3", "TAP-Windows Adapter V9", true)]
    [InlineData("Tailscale", "Tailscale Tunnel", true)]
    [InlineData("VirtualBox Host-Only Network", "VirtualBox Host-Only Ethernet Adapter", true)]
    [InlineData("VMware Network Adapter VMnet8", "VMware Virtual Ethernet Adapter for VMnet8", true)]
    [InlineData("docker0", "", true)]
    [InlineData("veth1a2b3c", "", true)]
    [InlineData("br-5f1e2d", "", true)]
    [InlineData("tun0", "", true)]
    [InlineData("wg0", "", true)]
    [InlineData("Bluetooth Network Connection", "Bluetooth Device (Personal Area Network)", true)]
    public void SkipsVirtualAdapters(string name, string description, bool isVirtual) =>
        Assert.Equal(isVirtual, NetworkScanner.IsVirtualAdapter(name, description));

    [Theory]
    [InlineData("192.168.1.20", true)]
    [InlineData("10.0.0.7", true)]
    [InlineData("172.20.1.9", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("169.254.10.2", false)]
    [InlineData("100.101.102.103", false)]
    [InlineData("224.0.0.251", false)]
    public void ScannableAddresses(string address, bool expected) =>
        Assert.Equal(expected, NetworkScanner.IsScannableAddress(IPAddress.Parse(address)));

    private static LocalNetwork Net(string address, int prefix, bool gateway = true) =>
        new("test", "test", IPAddress.Parse(address), prefix, gateway);

    [Fact]
    public void A24IsEveryHostButThisPc()
    {
        var targets = NetworkScanner.TargetsFor([Net("192.168.1.20", 24)]);
        Assert.Equal(253, targets.Count);
        Assert.Equal(IPAddress.Parse("192.168.1.1"), targets[0]);
        Assert.Equal(IPAddress.Parse("192.168.1.254"), targets[^1]);
        Assert.DoesNotContain(IPAddress.Parse("192.168.1.20"), targets);
        Assert.DoesNotContain(IPAddress.Parse("192.168.1.0"), targets);
        Assert.DoesNotContain(IPAddress.Parse("192.168.1.255"), targets);
    }

    [Fact]
    public void WideNetworksAreNarrowedToTheSurrounding24()
    {
        var targets = NetworkScanner.TargetsFor([Net("10.20.30.40", 16)]);
        Assert.Equal(253, targets.Count);
        Assert.All(targets, t => Assert.StartsWith("10.20.30.", t.ToString()));
    }

    [Fact]
    public void SmallSubnetsStaySmall()
    {
        // A /30 has two usable addresses; one is this PC.
        Assert.Equal([IPAddress.Parse("192.168.5.2")], NetworkScanner.TargetsFor([Net("192.168.5.1", 30)]));
        Assert.Empty(NetworkScanner.TargetsFor([Net("192.168.5.1", 32)]));
    }

    [Fact]
    public void NetworksAreDeduplicatedAndCapped()
    {
        var same = NetworkScanner.TargetsFor([Net("192.168.1.20", 24), Net("192.168.1.30", 24)]);
        Assert.Equal(254, same.Count); // both PCs' addresses excluded once each, but each is the other's target
        Assert.Equal(same.Count, same.Distinct().Count());

        var many = Enumerable.Range(0, 8).Select(i => Net($"10.0.{i}.5", 24)).ToList();
        Assert.Equal(1024, NetworkScanner.TargetsFor(many).Count);
        Assert.Equal(300, NetworkScanner.TargetsFor(many, 300).Count);
    }

    [Theory]
    [InlineData("spark-3f2a.local", true)]
    [InlineData("DGX-SPARK", true)]
    [InlineData("dgx-01.lan", true)]
    [InlineData("desktop-7h2k", false)]
    [InlineData(null, false)]
    public void SparkishNames(string? name, bool expected) => Assert.Equal(expected, NetworkScanner.NameLooksLikeSpark(name));

    [Theory]
    [InlineData("SSH-2.0-OpenSSH_9.6p1 Ubuntu-3ubuntu13.5", "OpenSSH 9.6p1 · Ubuntu")]
    [InlineData("SSH-2.0-OpenSSH_for_Windows_9.5", "OpenSSH for Windows 9.5")]
    [InlineData("SSH-2.0-dropbear_2022.83", "dropbear 2022.83")]
    public void DescribesSshBanners(string banner, string expected) => Assert.Equal(expected, NetworkScanner.DescribeSshBanner(banner));

    // MARK: - What answered

    private static readonly ScanPort VllmPort = new(8000, ServiceKind.ModelServer, "vLLM");
    private static readonly ScanPort FrontPort = new(11443, ServiceKind.SparkModelFront, "Spark model API (HTTPS)");

    [Fact]
    public void ClassifiesAVllmModelList()
    {
        var service = NetworkScanner.ClassifyModelsResponse(VllmPort, 200,
            """{"object":"list","data":[{"id":"qwen3-coder-30b","object":"model","owned_by":"vllm","max_model_len":262144}]}""");
        Assert.NotNull(service);
        Assert.Equal(ServiceKind.ModelServer, service.Kind);
        Assert.Equal("vLLM server", service.Title);
        Assert.Equal(["qwen3-coder-30b"], service.Models);
        Assert.Equal("vLLM · qwen3-coder-30b", service.Detail);
        Assert.False(service.NeedsKey);
    }

    [Fact]
    public void RecognizesOllamaAndLmStudioByOwner()
    {
        var ollama = NetworkScanner.ClassifyModelsResponse(VllmPort, 200, """{"data":[{"id":"qwen3:8b","owned_by":"library"}]}""");
        Assert.Equal(ServiceKind.Ollama, ollama?.Kind);
        var lmStudio = NetworkScanner.ClassifyModelsResponse(VllmPort, 200,
            """{"data":[{"id":"a","owned_by":"organization_owner"},{"id":"b","owned_by":"organization_owner"}]}""");
        Assert.Equal(ServiceKind.LmStudio, lmStudio?.Kind);
        Assert.Equal("2 models", lmStudio?.Detail);
    }

    [Fact]
    public void KeyProtectedAndIdleSparkFronts()
    {
        var locked = NetworkScanner.ClassifyModelsResponse(FrontPort, 401, """{"error":"Unauthorized"}""");
        Assert.NotNull(locked);
        Assert.True(locked.NeedsKey);
        Assert.Equal(ServiceKind.SparkModelFront, locked.Kind);

        var idle = NetworkScanner.ClassifyModelsResponse(FrontPort, 502, "<html>502 Bad Gateway</html>");
        Assert.Equal("Reached — no model loaded right now", idle?.Detail);

        // Elsewhere a 502 or a web page means "not a model API".
        Assert.Null(NetworkScanner.ClassifyModelsResponse(VllmPort, 502, "<html>502</html>"));
        Assert.Null(NetworkScanner.ClassifyModelsResponse(VllmPort, 200, "<html>router login</html>"));
        Assert.Null(NetworkScanner.ClassifyModelsResponse(VllmPort, 404, """{"detail":"Not Found"}"""));
    }

    // MARK: - Whole scans on loopback

    private static ScanOptions LoopbackOnly(params ScanPort[] ports) => new()
    {
        Targets = [IPAddress.Loopback],
        Ports = ports,
        IncludeLoopback = false,
        ResolveNames = false,
        ConnectTimeout = TimeSpan.FromSeconds(2),
        ProbeTimeout = TimeSpan.FromSeconds(5),
    };

    private static async Task<List<FoundHost>> Collect(ScanOptions options, CancellationToken cancellationToken = default)
    {
        var updates = new List<FoundHost>();
        await foreach (var host in NetworkScanner.ScanAsync(options, null, cancellationToken)) updates.Add(host);
        return updates;
    }

    [Fact]
    public async Task FindsAndIdentifiesServicesOnAHost()
    {
        await using var ssh = new BannerServer("SSH-2.0-OpenSSH_9.6p1 Ubuntu-3ubuntu13.5");
        await using var vllm = new TestHttpServer(r => r.Path == "/v1/models"
            ? new TestHttpServer.Response(200, """{"data":[{"id":"qwen3.8-27b-sglang","owned_by":"sglang"}]}""")
            : new TestHttpServer.Response(404, "{}"));
        await using var swapper = new TestHttpServer(r => r.Path == "/api/session"
            ? new TestHttpServer.Response(200, """{"setup_needed": true, "user": null, "role": null, "demo": false}""")
            : new TestHttpServer.Response(404, "{}"), tls: true);
        var closed = FreePort.Get();

        var updates = await Collect(LoopbackOnly(
            new ScanPort(ssh.Port, ServiceKind.Ssh, "SSH"),
            new ScanPort(vllm.Port, ServiceKind.ModelServer, "vLLM"),
            new ScanPort(swapper.Port, ServiceKind.SparkSwapper, "Spark Swapper"),
            new ScanPort(closed, ServiceKind.Ollama, "Ollama")));

        // First the open ports (not yet identified), then the identified host.
        Assert.Equal(2, updates.Count);
        Assert.False(updates[0].Identified);
        Assert.Equal(3, updates[0].Services.Count);
        var host = updates[^1];
        Assert.True(host.Identified);
        Assert.Equal(IPAddress.Loopback, host.Address);

        Assert.Equal("OpenSSH 9.6p1 · Ubuntu", host.Ssh?.Detail);

        var model = Assert.Single(host.ModelApis);
        Assert.Equal($"http://127.0.0.1:{vllm.Port}/v1", model.BaseUrl);
        Assert.Equal(["qwen3.8-27b-sglang"], model.Models);
        Assert.Equal("SGLang server", model.Title);

        var panel = host.Swapper;
        Assert.NotNull(panel);
        Assert.True(panel.SetupNeeded);
        Assert.True(panel.Tls);
        Assert.Equal($"https://127.0.0.1:{swapper.Port}", panel.BaseUrl);
        Assert.Equal(SparkSwapperClient.Fingerprint(swapper.Certificate!.RawData), panel.CertificateFingerprint);
    }

    [Fact]
    public async Task FallsBackToHttpsForTheSparkFront()
    {
        await using var front = new TestHttpServer(_ => new TestHttpServer.Response(401, """{"error":"Unauthorized"}"""), tls: true);
        var host = (await Collect(LoopbackOnly(new ScanPort(front.Port, ServiceKind.SparkModelFront, "front"))))[^1];
        var service = Assert.Single(host.Services);
        Assert.Equal(ServiceKind.SparkModelFront, service.Kind);
        Assert.True(service.NeedsKey);
        Assert.True(service.Tls);
        Assert.Equal($"https://127.0.0.1:{front.Port}/v1", service.BaseUrl);
        Assert.NotNull(service.CertificateFingerprint);
    }

    [Fact]
    public async Task ForgetsAHostWhoseOpenPortIsSomethingElse()
    {
        // Port open, but a router's web page rather than a model API.
        await using var web = new TestHttpServer(_ => new TestHttpServer.Response(200, "<html>login</html>", "text/html"));
        var updates = await Collect(LoopbackOnly(new ScanPort(web.Port, ServiceKind.ModelServer, "llama.cpp")));
        Assert.Equal(2, updates.Count);
        Assert.Empty(updates[^1].Services);
        Assert.True(updates[^1].Identified);
    }

    [Fact]
    public async Task NothingListeningFindsNothing()
    {
        var updates = await Collect(LoopbackOnly(new ScanPort(FreePort.Get(), ServiceKind.ModelServer, "x")));
        Assert.Empty(updates);
    }

    [Fact]
    public async Task ReportsProgressForEveryHost()
    {
        var reports = new List<ScanProgress>();
        var progress = new SynchronousProgress<ScanProgress>(reports.Add);
        var targets = Enumerable.Range(2, 20).Select(i => IPAddress.Parse($"127.0.0.{i}")).ToList();
        await foreach (var _ in NetworkScanner.ScanAsync(new ScanOptions
                       {
                           Targets = targets, Ports = [new ScanPort(FreePort.Get(), ServiceKind.ModelServer, "x")],
                           ResolveNames = false, ConnectTimeout = TimeSpan.FromSeconds(1),
                       }, progress)) { }
        Assert.Equal(new ScanProgress(0, 20, 0), reports[0]);
        Assert.Contains(new ScanProgress(20, 20, 0), reports);
        Assert.Equal(21, reports.Count);
    }

    [Fact]
    public async Task A24OfClosedPortsIsQuick()
    {
        // 254 hosts × as many ports as a default scan. Loopback refuses at once, so this measures the
        // scanner's own overhead, which must leave room for a real LAN's timeouts within a few seconds.
        // Free ports rather than the default ones: every 127.x address is this machine, which may well
        // run an SSH server or a model server itself (the Windows CI runner has OpenSSH on port 22).
        var targets = Enumerable.Range(1, 254).Select(i => IPAddress.Parse($"127.0.1.{i}")).ToList();
        var ports = NetworkScanner.DefaultPorts.Select(p => p with { Port = FreePort.Get() }).ToList();
        var watch = Stopwatch.StartNew();
        var found = await Collect(new ScanOptions { Targets = targets, Ports = ports, ResolveNames = false, IncludeLoopback = false });
        watch.Stop();
        Assert.Empty(found);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"took {watch.Elapsed}");
    }

    [Fact]
    public async Task CancellingStopsTheScan()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Collect(new ScanOptions { Targets = [IPAddress.Loopback], ResolveNames = false }, cts.Token));
    }

    [Fact]
    public async Task ProbesOneTypedAddress()
    {
        await using var vllm = new TestHttpServer(_ => new TestHttpServer.Response(200, """{"data":[{"id":"m","owned_by":"vllm"}]}"""));
        var options = LoopbackOnly(new ScanPort(vllm.Port, ServiceKind.ModelServer, "vLLM"));
        var host = await NetworkScanner.ProbeHostAsync($"http://127.0.0.1:{vllm.Port}/v1", options);
        Assert.NotNull(host);
        Assert.Equal(["m"], Assert.Single(host.ModelApis).Models);
        Assert.Null(await NetworkScanner.ProbeHostAsync("no-such-host.invalid", options));
    }

    [Fact]
    public void HeadlinesForPeople()
    {
        var spark = new FoundHost(IPAddress.Parse("192.168.1.42"), [new FoundService(8999, ServiceKind.SparkSwapper, "Spark Swapper")])
        {
            HostName = "spark-3f2a.local",
        };
        Assert.True(spark.LooksLikeSpark);
        Assert.Equal("DGX Spark at 192.168.1.42 — spark-3f2a", spark.Headline);

        var server = new FoundHost(IPAddress.Parse("192.168.1.9"), [new FoundService(8000, ServiceKind.ModelServer, "vLLM server")]);
        Assert.Equal("Model server at 192.168.1.9", server.Headline);

        var local = new FoundHost(IPAddress.Loopback, [new FoundService(11434, ServiceKind.Ollama, "Ollama")]);
        Assert.False(local.LooksLikeSpark);
        Assert.Equal("This PC (localhost)", local.Headline);
    }
}

/// <summary>IProgress that runs the callback inline (Progress&lt;T&gt; posts to the thread pool).</summary>
public sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
{
    private readonly Lock _lock = new();

    public void Report(T value)
    {
        lock (_lock) report(value);
    }
}
