using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Dsh.Core;

// MARK: - mDNS reverse lookup
//
// A DGX Spark (like any Ubuntu box running Avahi) answers for its own "spark-3f2a.local" name, but a
// home router's DNS usually doesn't know it. Asking the host directly — a DNS PTR question for
// 42.1.168.192.in-addr.arpa sent to its UDP port 5353 — gets the name back as a "legacy unicast"
// reply (RFC 6762 §6.7), with no multicast group to join and no firewall rule on this PC.

public static class MdnsLookup
{
    private const ushort TypePtr = 12;
    private const ushort ClassIn = 1;
    /// <summary>The "unicast response please" bit on the question class.</summary>
    private const ushort UnicastResponse = 0x8000;

    /// <summary>"42.1.168.192.in-addr.arpa" for 192.168.1.42.</summary>
    public static string ReverseName(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily != AddressFamily.InterNetwork || bytes.Length != 4)
            throw new ArgumentException("Only IPv4 addresses are looked up.", nameof(address));
        return $"{bytes[3]}.{bytes[2]}.{bytes[1]}.{bytes[0]}.in-addr.arpa";
    }

    /// <summary>A one-question PTR query for <paramref name="name"/>.</summary>
    public static byte[] BuildQuery(string name, ushort id)
    {
        var buffer = new List<byte>(64);
        void U16(ushort value)
        {
            buffer.Add((byte)(value >> 8));
            buffer.Add((byte)value);
        }
        U16(id);
        U16(0);     // flags: standard query
        U16(1);     // one question
        U16(0);
        U16(0);
        U16(0);
        foreach (var label in name.TrimEnd('.').Split('.'))
        {
            var bytes = Encoding.ASCII.GetBytes(label);
            buffer.Add((byte)bytes.Length);
            buffer.AddRange(bytes);
        }
        buffer.Add(0);
        U16(TypePtr);
        U16(ClassIn | UnicastResponse);
        return [.. buffer];
    }

    /// <summary>The first PTR answer in a DNS response, or null. Tolerates compression pointers and
    /// rejects anything malformed rather than throwing.</summary>
    public static string? ParsePtrAnswer(ReadOnlySpan<byte> message, ushort? expectedId = null)
    {
        if (message.Length < 12) return null;
        if (expectedId is { } id && BinaryPrimitives.ReadUInt16BigEndian(message) != id) return null;
        var flags = BinaryPrimitives.ReadUInt16BigEndian(message[2..]);
        if ((flags & 0x8000) == 0) return null; // not a response
        int questions = BinaryPrimitives.ReadUInt16BigEndian(message[4..]);
        int answers = BinaryPrimitives.ReadUInt16BigEndian(message[6..]);
        var offset = 12;
        for (var i = 0; i < questions; i++)
        {
            if (ReadName(message, ref offset) is null || offset + 4 > message.Length) return null;
            offset += 4;
        }
        for (var i = 0; i < answers; i++)
        {
            if (ReadName(message, ref offset) is null || offset + 10 > message.Length) return null;
            var type = BinaryPrimitives.ReadUInt16BigEndian(message[offset..]);
            var length = BinaryPrimitives.ReadUInt16BigEndian(message[(offset + 8)..]);
            offset += 10;
            if (offset + length > message.Length) return null;
            if (type == TypePtr)
            {
                var at = offset;
                return ReadName(message, ref at);
            }
            offset += length;
        }
        return null;
    }

    /// <summary>Read a (possibly compressed) domain name at <paramref name="offset"/>, advancing past it.</summary>
    private static string? ReadName(ReadOnlySpan<byte> message, ref int offset)
    {
        var labels = new List<string>();
        var position = offset;
        var jumped = false;
        for (var hops = 0; hops < 64; hops++)
        {
            if (position >= message.Length) return null;
            var length = message[position];
            if (length == 0)
            {
                if (!jumped) offset = position + 1;
                return string.Join('.', labels);
            }
            if ((length & 0xC0) == 0xC0)
            {
                if (position + 1 >= message.Length) return null;
                var target = ((length & 0x3F) << 8) | message[position + 1];
                if (!jumped) offset = position + 2;
                jumped = true;
                position = target;
                continue;
            }
            if ((length & 0xC0) != 0 || position + 1 + length > message.Length) return null;
            labels.Add(Encoding.UTF8.GetString(message.Slice(position + 1, length)));
            position += 1 + length;
        }
        return null; // a pointer loop
    }

    /// <summary>Ask <paramref name="address"/> for its own name over mDNS. Null when it doesn't
    /// answer within <paramref name="timeout"/> (Windows PCs and most routers won't).</summary>
    public static async Task<string?> ReverseAsync(IPAddress address, int port = 5353, TimeSpan? timeout = null,
                                                   CancellationToken cancellationToken = default)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return null;
        var id = (ushort)Random.Shared.Next(1, ushort.MaxValue);
        var query = BuildQuery(ReverseName(address), id);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(1));
        try
        {
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            await udp.SendAsync(query, new IPEndPoint(address, port), cts.Token).ConfigureAwait(false);
            while (true)
            {
                var reply = await udp.ReceiveAsync(cts.Token).ConfigureAwait(false);
                if (!reply.RemoteEndPoint.Address.Equals(address)) continue;
                if (ParsePtrAnswer(reply.Buffer, id) is { Length: > 0 } name) return name.TrimEnd('.');
            }
        }
        catch (Exception ex) when (ex is SocketException || ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }
}
