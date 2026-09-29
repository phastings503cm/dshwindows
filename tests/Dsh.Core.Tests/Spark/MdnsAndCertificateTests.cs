using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;

namespace Dsh.Core.Tests;

public sealed class MdnsLookupTests
{
    [Fact]
    public void ReverseNames()
    {
        Assert.Equal("42.1.168.192.in-addr.arpa", MdnsLookup.ReverseName(IPAddress.Parse("192.168.1.42")));
        Assert.Throws<ArgumentException>(() => MdnsLookup.ReverseName(IPAddress.IPv6Loopback));
    }

    [Fact]
    public void BuildsAUnicastPtrQuestion()
    {
        var query = MdnsLookup.BuildQuery("42.1.168.192.in-addr.arpa", 0x1234);
        Assert.Equal([0x12, 0x34, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0], query[..12]);
        Assert.Equal(2, query[12]);            // "42"
        Assert.Equal((byte)'4', query[13]);
        Assert.Equal([0, 12, 0x80, 1], query[^4..]); // PTR, IN with the unicast-response bit
    }

    /// <summary>A reply as Avahi sends it: the question echoed, then a PTR answer whose name points
    /// back at the question (compression).</summary>
    private static byte[] Reply(ushort id, string question, string target)
    {
        var bytes = new List<byte> { (byte)(id >> 8), (byte)id, 0x84, 0x00, 0, 1, 0, 1, 0, 0, 0, 0 };
        void Name(string name)
        {
            foreach (var label in name.Split('.'))
            {
                bytes.Add((byte)label.Length);
                bytes.AddRange(System.Text.Encoding.ASCII.GetBytes(label));
            }
            bytes.Add(0);
        }
        Name(question);
        bytes.AddRange([0, 12, 0, 1]);
        bytes.AddRange([0xC0, 12]);            // the answer's name = the question's (offset 12)
        bytes.AddRange([0, 12, 0, 1, 0, 0, 0, 120]);
        var rdata = new List<byte>();
        foreach (var label in target.Split('.'))
        {
            rdata.Add((byte)label.Length);
            rdata.AddRange(System.Text.Encoding.ASCII.GetBytes(label));
        }
        rdata.Add(0);
        bytes.AddRange([(byte)(rdata.Count >> 8), (byte)rdata.Count]);
        bytes.AddRange(rdata);
        return [.. bytes];
    }

    [Fact]
    public void ParsesAPtrAnswer()
    {
        var reply = Reply(7, "42.1.168.192.in-addr.arpa", "spark-3f2a.local");
        Assert.Equal("spark-3f2a.local", MdnsLookup.ParsePtrAnswer(reply, 7));
        Assert.Null(MdnsLookup.ParsePtrAnswer(reply, 8));        // someone else's answer
        Assert.Null(MdnsLookup.ParsePtrAnswer(reply[..20]));     // truncated
        Assert.Null(MdnsLookup.ParsePtrAnswer(MdnsLookup.BuildQuery("x.local", 7))); // a question, not an answer
    }

    [Fact]
    public void RejectsPointerLoops()
    {
        // Header with one answer whose name is a pointer to itself.
        byte[] message = [0, 1, 0x84, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0xC0, 12, 0, 12, 0, 1, 0, 0, 0, 1, 0, 2, 0xC0, 12];
        Assert.Null(MdnsLookup.ParsePtrAnswer(message));
    }

    [Fact]
    public async Task AsksTheHostDirectly()
    {
        using var responder = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)responder.Client.LocalEndPoint!).Port;
        var answering = Task.Run(async () =>
        {
            var query = await responder.ReceiveAsync();
            var id = (ushort)(query.Buffer[0] << 8 | query.Buffer[1]);
            var reply = Reply(id, "1.0.0.127.in-addr.arpa", "spark-3f2a.local");
            await responder.SendAsync(reply, query.RemoteEndPoint);
        });
        var name = await MdnsLookup.ReverseAsync(IPAddress.Loopback, port, TimeSpan.FromSeconds(5));
        await answering;
        Assert.Equal("spark-3f2a.local", name);
    }

    [Fact]
    public async Task SilenceIsNull()
    {
        using var quiet = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)quiet.Client.LocalEndPoint!).Port;
        Assert.Null(await MdnsLookup.ReverseAsync(IPAddress.Loopback, port, TimeSpan.FromMilliseconds(200)));
    }
}

public sealed class CertificateProbeTests
{
    [Fact]
    public async Task ReadsASelfSignedCertificate()
    {
        await using var server = new TestHttpServer(_ => new TestHttpServer.Response(200, "{}"), tls: true);
        var details = await CertificateProbe.ProbeAsync("127.0.0.1", server.Port);

        var certificate = server.Certificate!;
        Assert.Equal(SparkSwapperClient.Fingerprint(certificate.RawData), details.Fingerprint);
        Assert.Matches("^([0-9A-F]{2}:){31}[0-9A-F]{2}$", details.Fingerprint);
        Assert.Equal("Spark Swapper (test)", details.CommonName);
        Assert.True(details.IsSelfSigned);
        Assert.False(details.IsExpired);
        Assert.False(details.TrustedBySystem);
        Assert.True((details.Errors & SslPolicyErrors.RemoteCertificateChainErrors) != 0);
        Assert.True(details.NameMatches);
        Assert.Contains("localhost", details.Names);
        Assert.Contains("127.0.0.1", details.Names);
        Assert.Equal(certificate.RawData, details.RawData);
        Assert.StartsWith("-----BEGIN CERTIFICATE-----", details.Pem);
        using var roundTrip = X509Certificate2.CreateFromPem(details.Pem);
        Assert.Equal(certificate.Thumbprint, roundTrip.Thumbprint);
    }

    [Fact]
    public async Task PlainHttpIsNotHttps()
    {
        await using var server = new TestHttpServer(_ => new TestHttpServer.Response(200, "{}"));
        await Assert.ThrowsAsync<IOException>(() => CertificateProbe.ProbeAsync("127.0.0.1", server.Port, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task NothingListening()
    {
        await Assert.ThrowsAsync<IOException>(() => CertificateProbe.ProbeAsync("127.0.0.1", FreePort.Get(), TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public void NormalizesFingerprints()
    {
        const string expected = "AB:CD:EF:01:23:45:67:89:AB:CD:EF:01:23:45:67:89:AB:CD:EF:01:23:45:67:89:AB:CD:EF:01:23:45:67:89";
        Assert.Equal(expected, CertificateProbe.NormalizeFingerprint(expected.ToLowerInvariant()));
        Assert.Equal(expected, CertificateProbe.NormalizeFingerprint("sha256 Fingerprint=" + expected));
        Assert.Equal(expected, CertificateProbe.NormalizeFingerprint(expected.Replace(":", " ")));
        Assert.Null(CertificateProbe.NormalizeFingerprint("AB:CD"));
        Assert.Null(CertificateProbe.NormalizeFingerprint(null));
        Assert.Equal("AB:CD:EF:01 … 23:45:67:AB", CertificateProbe.Shorten(expected[..^3] + ":AB"));
    }

    [Fact]
    public void CommonNames()
    {
        Assert.Equal("Spark Swapper (spark-3f2a)", CertificateProbe.CommonName("CN=Spark Swapper (spark-3f2a)"));
        Assert.Equal("host", CertificateProbe.CommonName("O=Org, CN=host"));
    }
}
