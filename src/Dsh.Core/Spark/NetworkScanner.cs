using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace Dsh.Core;

// MARK: - Network scanner
//
// Finds DGX Sparks and model servers on the local network, so the setup wizard can offer "DGX Spark
// at 192.168.1.42 — spark-3f2a" instead of asking a newcomer for an IP address.
//
// Two passes per host. First a TCP connect to each interesting port (short timeout, bounded
// concurrency, so a /24 takes a few seconds). Then, only for ports that answered, a read-only look
// at what is there: GET /v1/models, the Spark Swapper's /api/session, an SSH banner, and the host's
// name (reverse DNS, and a direct mDNS question for "spark-xxxx.local"). Nothing is ever sent but
// those anonymous GETs; HTTPS probes accept any certificate only so a self-signed Spark can be found
// and its fingerprint shown — trusting it is a separate, explicit step.

/// <summary>What answered on a port.</summary>
public enum ServiceKind
{
    Ssh,
    /// <summary>Spark Swapper, the web control panel on https://&lt;spark&gt;:8999.</summary>
    SparkSwapper,
    /// <summary>The Spark's HTTPS front for its model API (nginx on :11443).</summary>
    SparkModelFront,
    /// <summary>An OpenAI-compatible server: vLLM, SGLang, llama.cpp, ...</summary>
    ModelServer,
    Ollama,
    LmStudio,
    OpenClawGateway,
}

/// <summary>A port worth knocking on, and what usually listens there.</summary>
public sealed record ScanPort(int Port, ServiceKind Hint, string Label);

/// <summary>One service found on a host.</summary>
public sealed record FoundService(int Port, ServiceKind Kind, string Title)
{
    /// <summary>One line for people: "OpenSSH 9.6p1 · Ubuntu", "vLLM · qwen3-coder", ...</summary>
    public string? Detail { get; init; }
    /// <summary>For model APIs the OpenAI base URL (".../v1"); for the swapper its page.</summary>
    public string? BaseUrl { get; init; }
    public IReadOnlyList<string> Models { get; init; } = [];
    /// <summary>"vllm", "sglang", "llamacpp", ... from the /v1/models owned_by field.</summary>
    public string? Engine { get; init; }
    /// <summary>The server answered 401/403: it wants an API key.</summary>
    public bool NeedsKey { get; init; }
    public bool Tls { get; init; }
    /// <summary>SHA-256 of the certificate an HTTPS service presented ("AB:CD:…").</summary>
    public string? CertificateFingerprint { get; init; }
    /// <summary>Spark Swapper only: nobody has created the admin login yet.</summary>
    public bool? SetupNeeded { get; init; }
    /// <summary>False until the second pass looked at what the open port really is.</summary>
    public bool Identified { get; init; }

    public bool IsModelApi => Kind is ServiceKind.SparkModelFront or ServiceKind.ModelServer or ServiceKind.Ollama or ServiceKind.LmStudio;
}

/// <summary>A machine that answered on at least one interesting port.</summary>
public sealed record FoundHost(IPAddress Address, IReadOnlyList<FoundService> Services)
{
    /// <summary>"spark-3f2a.local", a DNS name, or null.</summary>
    public string? HostName { get; init; }
    /// <summary>All services identified (the last update for this host).</summary>
    public bool Identified { get; init; }

    public bool IsLoopback => IPAddress.IsLoopback(Address);

    public FoundService? Service(ServiceKind kind) => Services.FirstOrDefault(s => s.Kind == kind);

    public FoundService? Swapper => Service(ServiceKind.SparkSwapper);
    public FoundService? ModelFront => Service(ServiceKind.SparkModelFront);
    public FoundService? Ssh => Service(ServiceKind.Ssh);
    public IReadOnlyList<FoundService> ModelApis => Services.Where(s => s.IsModelApi).ToList();

    /// <summary>The name without ".local" or a domain: "spark-3f2a".</summary>
    public string? ShortName
    {
        get
        {
            if (string.IsNullOrWhiteSpace(HostName)) return null;
            var name = HostName.TrimEnd('.');
            var dot = name.IndexOf('.');
            return dot > 0 ? name[..dot] : name;
        }
    }

    /// <summary>A Spark Swapper or the Spark's model front answered, or the box is named like a Spark.</summary>
    public bool LooksLikeSpark => !IsLoopback && (Swapper is not null || ModelFront is not null || NetworkScanner.NameLooksLikeSpark(HostName));

    /// <summary>"DGX Spark", "Model server", "This PC", ...</summary>
    public string Title => IsLoopback ? "This PC"
        : LooksLikeSpark ? "DGX Spark"
        : ModelApis.Count > 0 ? "Model server"
        : Ssh is not null ? "Computer with remote login (SSH)"
        : "Device";

    /// <summary>"DGX Spark at 192.168.1.42 — spark-3f2a".</summary>
    public string Headline => IsLoopback
        ? "This PC (localhost)"
        : ShortName is { } name ? $"{Title} at {Address} — {name}" : $"{Title} at {Address}";
}

public sealed record ScanProgress(int Done, int Total, int Found)
{
    public double Fraction => Total == 0 ? 1 : Math.Clamp((double)Done / Total, 0, 1);
}

/// <summary>A network this PC is on (one IPv4 address of one adapter).</summary>
public sealed record LocalNetwork(string Name, string Description, IPAddress Address, int PrefixLength, bool HasGateway);

public sealed record ScanOptions
{
    /// <summary>Addresses to scan; null = the /24 around each of this PC's addresses.</summary>
    public IReadOnlyList<IPAddress>? Targets { get; init; }
    public IReadOnlyList<ScanPort> Ports { get; init; } = NetworkScanner.DefaultPorts;
    /// <summary>Also look at this PC itself (a local Ollama or LM Studio).</summary>
    public bool IncludeLoopback { get; init; } = true;
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromMilliseconds(700);
    public TimeSpan ProbeTimeout { get; init; } = TimeSpan.FromSeconds(3);
    /// <summary>Connection attempts in flight at once.</summary>
    public int Concurrency { get; init; } = 384;
    /// <summary>A run never knocks on more hosts than this (four /24s).</summary>
    public int MaxHosts { get; init; } = 1024;
    public bool ResolveNames { get; init; } = true;
    /// <summary>Where hosts answer mDNS questions (tests point it elsewhere).</summary>
    public int MdnsPort { get; init; } = 5353;
}

public static class NetworkScanner
{
    /// <summary>Ports on a DGX Spark (SSH, the swapper, its HTTPS model front, OpenClaw) and the usual
    /// homes of vLLM, SGLang, llama.cpp, Ollama and LM Studio.</summary>
    public static IReadOnlyList<ScanPort> DefaultPorts { get; } =
    [
        new(22, ServiceKind.Ssh, "SSH"),
        new(8999, ServiceKind.SparkSwapper, "Spark Swapper"),
        new(11443, ServiceKind.SparkModelFront, "Spark model API (HTTPS)"),
        new(8000, ServiceKind.ModelServer, "vLLM"),
        new(8002, ServiceKind.ModelServer, "vLLM / SGLang"),
        new(8888, ServiceKind.ModelServer, "Model server"),
        new(30000, ServiceKind.ModelServer, "SGLang"),
        new(11434, ServiceKind.Ollama, "Ollama"),
        new(1234, ServiceKind.LmStudio, "LM Studio"),
        new(8080, ServiceKind.ModelServer, "llama.cpp"),
        new(18789, ServiceKind.OpenClawGateway, "OpenClaw gateway"),
    ];

    // MARK: - Where to look

    /// <summary>This PC's IPv4 networks worth scanning: adapters that are up, minus loopback,
    /// link-local, and the virtual adapters of Hyper-V, WSL, Docker and VPN clients. Adapters with a
    /// default gateway (the real LAN) come first.</summary>
    public static IReadOnlyList<LocalNetwork> LocalNetworks()
    {
        var found = new List<LocalNetwork>();
        NetworkInterface[] adapters;
        try
        {
            adapters = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (NetworkInformationException)
        {
            return found;
        }
        foreach (var adapter in adapters)
        {
            try
            {
                if (adapter.OperationalStatus != OperationalStatus.Up) continue;
                if (adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                if (IsVirtualAdapter(adapter.Name, adapter.Description)) continue;
                var properties = adapter.GetIPProperties();
                var hasGateway = properties.GatewayAddresses.Any(g =>
                    g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
                foreach (var unicast in properties.UnicastAddresses)
                {
                    var address = unicast.Address;
                    if (address.AddressFamily != AddressFamily.InterNetwork || !IsScannableAddress(address)) continue;
                    var prefix = unicast.PrefixLength;
                    if (prefix is <= 0 or > 32) prefix = MaskBits(unicast.IPv4Mask);
                    found.Add(new LocalNetwork(adapter.Name, adapter.Description, address, prefix, hasGateway));
                }
            }
            catch (NetworkInformationException)
            {
                // An adapter that vanished mid-enumeration.
            }
        }
        return found.OrderByDescending(n => n.HasGateway).ToList();
    }

    /// <summary>Adapters that lead to virtual machines, containers or a VPN rather than the room
    /// the Spark is in.</summary>
    public static bool IsVirtualAdapter(string name, string description)
    {
        var text = $"{name} {description}".ToLowerInvariant();
        string[] markers =
        [
            "vethernet", "hyper-v", "wsl", "virtualbox", "vmware", "vmnet", "docker", "vpn", "tap-", "tap adapter",
            "wireguard", "wintun", "tailscale", "zerotier", "npcap", "loopback", "bluetooth", "hamachi", "anyconnect",
            "globalprotect", "pangp", "fortinet", "nordlynx", "openvpn", "virtual adapter", "virbr", "lxcbr", "cni0",
            "flannel",
        ];
        if (markers.Any(text.Contains)) return true;
        // Linux-style names for bridges and container interfaces.
        var lower = name.ToLowerInvariant();
        return lower.StartsWith("veth", StringComparison.Ordinal) || lower.StartsWith("br-", StringComparison.Ordinal)
            || lower.StartsWith("tun", StringComparison.Ordinal) || lower.StartsWith("wg", StringComparison.Ordinal)
            || lower.StartsWith("zt", StringComparison.Ordinal) || lower is "lo";
    }

    /// <summary>Loopback, link-local (169.254/16, no DHCP answer) and carrier-grade NAT (100.64/10,
    /// usually Tailscale) are not "the local network".</summary>
    public static bool IsScannableAddress(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address)) return false;
        var bytes = address.GetAddressBytes();
        if (bytes[0] == 169 && bytes[1] == 254) return false;
        if (bytes[0] == 100 && bytes[1] is >= 64 and <= 127) return false;
        return bytes[0] is not (0 or >= 224);
    }

    /// <summary>The hosts to knock on: every address of each network's subnet, but never wider than
    /// the /24 around this PC's address, skipping this PC itself, capped at <paramref name="maxHosts"/>.</summary>
    public static IReadOnlyList<IPAddress> TargetsFor(IEnumerable<LocalNetwork> networks, int maxHosts = 1024)
    {
        var result = new List<IPAddress>();
        var seen = new HashSet<uint>();
        foreach (var network in networks)
        {
            if (network.Address.AddressFamily != AddressFamily.InterNetwork) continue;
            var own = ToUInt(network.Address);
            var prefix = Math.Max(network.PrefixLength, 24);
            if (prefix >= 31) continue;
            var mask = uint.MaxValue << (32 - prefix);
            var first = own & mask;
            var last = first | ~mask;
            for (var host = first + 1; host < last; host++)
            {
                if (host == own || !seen.Add(host)) continue;
                result.Add(FromUInt(host));
                if (result.Count >= maxHosts) return result;
            }
        }
        return result;
    }

    private static uint ToUInt(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
    }

    private static IPAddress FromUInt(uint value) =>
        new([(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value]);

    private static int MaskBits(IPAddress? mask)
    {
        if (mask is null) return 24;
        var bits = 0;
        foreach (var b in mask.GetAddressBytes()) bits += System.Numerics.BitOperations.PopCount(b);
        return bits == 0 ? 24 : bits;
    }

    /// <summary>First-boot names look like "spark-3f2a"; people also call them "dgx-…".</summary>
    public static bool NameLooksLikeSpark(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && (name.Contains("spark", StringComparison.OrdinalIgnoreCase) || name.Contains("dgx", StringComparison.OrdinalIgnoreCase));

    // MARK: - Scanning

    /// <summary>Scan the local network (or <see cref="ScanOptions.Targets"/>). A host is yielded as
    /// soon as a port answers (not yet identified) and again when identification finishes — consumers
    /// replace earlier results for the same address. A host whose open ports all turn out to be
    /// something else is yielded once more with no services, meaning "forget it".</summary>
    public static async IAsyncEnumerable<FoundHost> ScanAsync(ScanOptions? options = null, IProgress<ScanProgress>? progress = null,
                                                              [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        options ??= new ScanOptions();
        var targets = new List<IPAddress>();
        if (options.IncludeLoopback && options.Targets is null) targets.Add(IPAddress.Loopback);
        targets.AddRange(options.Targets ?? TargetsFor(LocalNetworks(), options.MaxHosts));
        targets = targets.Distinct().Take(options.MaxHosts + 1).ToList();

        var channel = Channel.CreateUnbounded<FoundHost>(new UnboundedChannelOptions { SingleReader = true });
        using var gate = new SemaphoreSlim(Math.Max(1, options.Concurrency));
        var done = 0;
        var found = 0;
        progress?.Report(new ScanProgress(0, targets.Count, 0));
        var producer = Task.Run(async () =>
        {
            try
            {
                await Task.WhenAll(targets.Select(async address =>
                {
                    try
                    {
                        var host = await ScanHostAsync(address, null, options, gate, h => channel.Writer.TryWrite(h), cancellationToken)
                            .ConfigureAwait(false);
                        if (host is { Services.Count: > 0 }) Interlocked.Increment(ref found);
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    catch (Exception)
                    {
                        // One odd host never ends the scan.
                    }
                    finally
                    {
                        var count = Interlocked.Increment(ref done);
                        progress?.Report(new ScanProgress(count, targets.Count, Volatile.Read(ref found)));
                    }
                })).ConfigureAwait(false);
            }
            finally
            {
                channel.Writer.TryComplete();
            }
        }, CancellationToken.None);

        await foreach (var host in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            yield return host;
        await producer.ConfigureAwait(false);
    }

    /// <summary>Check one address or name ("192.168.1.42", "spark-3f2a.local") the user typed.
    /// Returns null when nothing answers or the name doesn't resolve.</summary>
    public static async Task<FoundHost?> ProbeHostAsync(string hostOrAddress, ScanOptions? options = null,
                                                        CancellationToken cancellationToken = default)
    {
        options ??= new ScanOptions();
        var text = hostOrAddress.Trim();
        if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Host.Length > 0) text = uri.Host;
        text = text.Trim('[', ']');
        string? name = null;
        if (!IPAddress.TryParse(text, out var address))
        {
            name = text;
            try
            {
                var addresses = await Dns.GetHostAddressesAsync(text, cancellationToken)
                    .WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                address = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses.FirstOrDefault();
            }
            catch (Exception ex) when (ex is SocketException or TimeoutException or ArgumentException)
            {
                return null;
            }
            if (address is null) return null;
        }
        using var gate = new SemaphoreSlim(Math.Max(1, options.Concurrency));
        return await ScanHostAsync(address, name, options, gate, _ => { }, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<FoundHost?> ScanHostAsync(IPAddress address, string? knownName, ScanOptions options, SemaphoreSlim gate,
                                                        Action<FoundHost> emit, CancellationToken cancellationToken)
    {
        var open = new ConcurrentBag<ScanPort>();
        await Task.WhenAll(options.Ports.Select(async port =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (await IsOpenAsync(address, port.Port, options.ConnectTimeout, cancellationToken).ConfigureAwait(false)) open.Add(port);
            }
            finally
            {
                gate.Release();
            }
        })).ConfigureAwait(false);
        if (open.IsEmpty) return null;

        var ports = open.OrderBy(p => p.Port).ToList();
        var first = new FoundHost(address, ports.Select(p => new FoundService(p.Port, p.Hint, p.Label)).ToList()) { HostName = knownName };
        emit(first);

        var naming = knownName is null && options.ResolveNames && !IPAddress.IsLoopback(address)
            ? ResolveNameAsync(address, options.MdnsPort, cancellationToken)
            : Task.FromResult(knownName);
        var identified = await Task.WhenAll(ports.Select(p => IdentifyAsync(address, p, options, cancellationToken))).ConfigureAwait(false);
        var final = first with
        {
            Services = identified.OfType<FoundService>().ToList(),
            HostName = await naming.ConfigureAwait(false),
            Identified = true,
        };
        emit(final);
        return final;
    }

    /// <summary>Does anything accept a TCP connection within <paramref name="timeout"/>?</summary>
    public static async Task<bool> IsOpenAsync(IPAddress address, int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, port), cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    // MARK: - Identification

    private static async Task<FoundService?> IdentifyAsync(IPAddress address, ScanPort port, ScanOptions options, CancellationToken cancellationToken)
    {
        try
        {
            return port.Hint switch
            {
                ServiceKind.Ssh => await IdentifySshAsync(address, port, options.ProbeTimeout, cancellationToken).ConfigureAwait(false),
                ServiceKind.SparkSwapper => await IdentifySwapperAsync(address, port, options.ProbeTimeout, cancellationToken).ConfigureAwait(false)
                                            ?? await IdentifyModelApiAsync(address, port, options.ProbeTimeout, cancellationToken).ConfigureAwait(false),
                ServiceKind.OpenClawGateway => await IdentifyGatewayAsync(address, port, options.ProbeTimeout, cancellationToken).ConfigureAwait(false),
                _ => await IdentifyModelApiAsync(address, port, options.ProbeTimeout, cancellationToken).ConfigureAwait(false),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private static async Task<FoundService?> IdentifySshAsync(IPAddress address, ScanPort port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var banner = await ReadBannerAsync(address, port.Port, timeout, cancellationToken).ConfigureAwait(false);
        if (banner is null || !banner.StartsWith("SSH-", StringComparison.Ordinal)) return null;
        return new FoundService(port.Port, ServiceKind.Ssh, "Remote login (SSH)") { Detail = DescribeSshBanner(banner), Identified = true };
    }

    /// <summary>The first line an SSH server sends ("SSH-2.0-OpenSSH_9.6p1 Ubuntu-3ubuntu13.5").</summary>
    public static async Task<string?> ReadBannerAsync(IPAddress address, int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, port), cts.Token).ConfigureAwait(false);
            var buffer = new byte[256];
            var length = 0;
            while (length < buffer.Length)
            {
                var read = await socket.ReceiveAsync(buffer.AsMemory(length), SocketFlags.None, cts.Token).ConfigureAwait(false);
                if (read == 0) break;
                length += read;
                if (Array.IndexOf(buffer, (byte)'\n', 0, length) >= 0) break;
            }
            var text = Encoding.ASCII.GetString(buffer, 0, length);
            var end = text.IndexOf('\n');
            return (end >= 0 ? text[..end] : text).TrimEnd('\r');
        }
        catch (Exception ex) when (ex is SocketException or IOException || ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>"SSH-2.0-OpenSSH_9.6p1 Ubuntu-3ubuntu13.5" → "OpenSSH 9.6p1 · Ubuntu".</summary>
    public static string DescribeSshBanner(string banner)
    {
        var rest = banner.StartsWith("SSH-", StringComparison.Ordinal) ? banner[4..] : banner;
        var dash = rest.IndexOf('-');
        if (dash >= 0) rest = rest[(dash + 1)..];
        var space = rest.IndexOf(' ');
        var software = (space >= 0 ? rest[..space] : rest).Replace('_', ' ');
        var comment = space >= 0 ? rest[(space + 1)..].Trim() : "";
        var os = comment.Split('-', ' ')[0];
        return os.Length > 0 ? $"{software} · {os}" : software;
    }

    private static async Task<FoundService?> IdentifySwapperAsync(IPAddress address, ScanPort port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var url = new Uri($"https://{Authority(address, port.Port)}/api/session");
        var probe = await GetAsync(url, timeout, cancellationToken).ConfigureAwait(false);
        if (probe is not { Status: 200 } || Parse(probe.Body) is not { } session || !session.ContainsKey("setup_needed")) return null;
        var setupNeeded = session["setup_needed"] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
        return new FoundService(port.Port, ServiceKind.SparkSwapper, "Spark Swapper")
        {
            BaseUrl = $"https://{Authority(address, port.Port)}",
            Tls = true,
            CertificateFingerprint = probe.Fingerprint,
            SetupNeeded = setupNeeded,
            Detail = setupNeeded ? "Installed — waiting for its admin login" : "Installed and set up",
            Identified = true,
        };
    }

    private static async Task<FoundService?> IdentifyGatewayAsync(IPAddress address, ScanPort port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var probe = await GetAsync(new Uri($"http://{Authority(address, port.Port)}/"), timeout, cancellationToken).ConfigureAwait(false)
                    ?? await GetAsync(new Uri($"https://{Authority(address, port.Port)}/"), timeout, cancellationToken).ConfigureAwait(false);
        return new FoundService(port.Port, ServiceKind.OpenClawGateway, "OpenClaw gateway")
        {
            Detail = probe is null ? "Port open" : "Answering",
            Tls = probe?.Fingerprint is not null,
            CertificateFingerprint = probe?.Fingerprint,
            Identified = true,
        };
    }

    private static async Task<FoundService?> IdentifyModelApiAsync(IPAddress address, ScanPort port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        // The Spark's front is HTTPS; everything else is usually plain HTTP. Try the likely one first.
        string[] schemes = port.Hint == ServiceKind.SparkModelFront ? ["https", "http"] : ["http", "https"];
        foreach (var scheme in schemes)
        {
            var baseUrl = $"{scheme}://{Authority(address, port.Port)}/v1";
            var probe = await GetAsync(new Uri(baseUrl + "/models"), timeout, cancellationToken).ConfigureAwait(false);
            if (probe is null) continue;
            if (ClassifyModelsResponse(port, probe.Status, probe.Body) is not { } service) return null;
            return service with
            {
                BaseUrl = baseUrl,
                Tls = scheme == "https",
                CertificateFingerprint = scheme == "https" ? probe.Fingerprint : null,
                Identified = true,
            };
        }
        return null;
    }

    /// <summary>What a GET /v1/models answer says about the server: its models and engine, that it
    /// wants a key, or (for the Spark's front) that it is up but no model is loaded. Null = not a
    /// model API.</summary>
    public static FoundService? ClassifyModelsResponse(ScanPort port, int status, string body)
    {
        if (status is 401 or 403)
        {
            return new FoundService(port.Port, port.Hint, TitleFor(port.Hint, null))
            {
                NeedsKey = true,
                Detail = "Answering — needs its API key",
            };
        }
        if (port.Hint == ServiceKind.SparkModelFront && status is 502 or 503 or 504)
        {
            // nginx is up but nothing listens behind it: no model is loaded right now.
            return new FoundService(port.Port, port.Hint, TitleFor(port.Hint, null)) { Detail = "Reached — no model loaded right now" };
        }
        if (status != 200 || Parse(body) is not { } obj || obj["data"] is not JsonArray data) return null;
        var entries = data.OfType<JsonObject>().ToList();
        var models = entries.Select(e => JsonArgs.String(e, "id")).OfType<string>().ToList();
        var owner = entries.Select(e => JsonArgs.String(e, "owned_by")).FirstOrDefault(o => !string.IsNullOrEmpty(o));
        var kind = port.Hint switch
        {
            ServiceKind.SparkModelFront => ServiceKind.SparkModelFront,
            _ when owner == "library" => ServiceKind.Ollama,
            _ when owner == "organization_owner" => ServiceKind.LmStudio,
            ServiceKind.Ollama or ServiceKind.LmStudio => port.Hint,
            _ => ServiceKind.ModelServer,
        };
        var engine = owner switch
        {
            "vllm" => "vLLM",
            "sglang" => "SGLang",
            "llamacpp" => "llama.cpp",
            _ => null,
        };
        var modelText = models.Count switch
        {
            0 => "no model loaded",
            1 => models[0],
            _ => $"{models.Count} models",
        };
        return new FoundService(port.Port, kind, TitleFor(kind, engine))
        {
            Models = models,
            Engine = owner,
            Detail = engine is null ? modelText : $"{engine} · {modelText}",
        };
    }

    private static string TitleFor(ServiceKind kind, string? engine) => kind switch
    {
        ServiceKind.SparkModelFront => "Spark model API",
        ServiceKind.Ollama => "Ollama",
        ServiceKind.LmStudio => "LM Studio",
        ServiceKind.SparkSwapper => "Spark Swapper",
        ServiceKind.Ssh => "Remote login (SSH)",
        ServiceKind.OpenClawGateway => "OpenClaw gateway",
        _ => engine is null ? "Model server" : $"{engine} server",
    };

    private static string Authority(IPAddress address, int port) =>
        address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{address}]:{port}" : $"{address}:{port}";

    private static JsonObject? Parse(string body)
    {
        try
        {
            return JsonNode.Parse(body) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record HttpProbe(int Status, string Body, string? Fingerprint);

    /// <summary>One anonymous GET with a hard deadline. HTTPS accepts any certificate here — this is a
    /// read-only look, nothing is sent — and records its fingerprint to show the user.</summary>
    private static async Task<HttpProbe?> GetAsync(Uri url, TimeSpan timeout, CancellationToken cancellationToken)
    {
        string? fingerprint = null;
        using var handler = new SocketsHttpHandler
        {
            ConnectTimeout = timeout,
            AllowAutoRedirect = false,
            UseCookies = false,
            // A system proxy must not see (or answer for) addresses on the LAN.
            UseProxy = false,
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                {
                    if (certificate is not null) fingerprint = SparkSwapperClient.Fingerprint(certificate.GetRawCertData());
                    return true;
                },
            },
        };
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            await using var stream = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            var buffer = new byte[256 * 1024];
            var length = 0;
            while (length < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(length), cts.Token).ConfigureAwait(false);
                if (read == 0) break;
                length += read;
            }
            return new HttpProbe((int)response.StatusCode, Encoding.UTF8.GetString(buffer, 0, length), fingerprint);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException || ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    // MARK: - Names

    /// <summary>The host's name: its own answer to an mDNS question ("spark-3f2a.local", what Avahi on
    /// Ubuntu announces), else reverse DNS from the router. Null when neither knows.</summary>
    public static async Task<string?> ResolveNameAsync(IPAddress address, int mdnsPort = 5353, CancellationToken cancellationToken = default)
    {
        var mdns = MdnsLookup.ReverseAsync(address, mdnsPort, TimeSpan.FromMilliseconds(900), cancellationToken);
        var dns = ReverseDnsAsync(address, TimeSpan.FromSeconds(1.5), cancellationToken);
        return await mdns.ConfigureAwait(false) ?? await dns.ConfigureAwait(false);
    }

    private static async Task<string?> ReverseDnsAsync(IPAddress address, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            var entry = await Dns.GetHostEntryAsync(address.ToString(), cancellationToken).WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            var name = entry.HostName;
            return string.IsNullOrWhiteSpace(name) || IPAddress.TryParse(name, out _) ? null : name;
        }
        catch (Exception ex) when (ex is SocketException or TimeoutException or ArgumentException)
        {
            return null;
        }
    }
}
