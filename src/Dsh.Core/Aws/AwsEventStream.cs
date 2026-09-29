using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;

namespace Dsh.Core;

// MARK: - AWS event stream (application/vnd.amazon.eventstream)
//
// Bedrock's ConverseStream answers with binary frames rather than SSE. Each frame:
//
//   total length (u32) | headers length (u32) | prelude CRC (u32, over the first 8 bytes)
//   headers: name length (u8), name, type (u8), value (size depends on the type)
//   payload (total - headers - 16 bytes; JSON for Bedrock)
//   message CRC (u32, over everything before it)
//
// All integers are big-endian and the CRCs are CRC-32/IEEE. The :message-type header says whether a
// frame is an event (its :event-type names the ConverseStreamOutput member), an exception (modelled:
// :exception-type, e.g. throttlingException) or an error (unmodelled: :error-code, :error-message).

/// <summary>One decoded event-stream frame: its string-valued headers and raw payload.</summary>
public sealed record AwsEventMessage(IReadOnlyDictionary<string, string> Headers, byte[] Payload)
{
    /// <summary>"event", "exception" or "error".</summary>
    public string? MessageType => Headers.GetValueOrDefault(":message-type");
    public string? EventType => Headers.GetValueOrDefault(":event-type");
    public string? ExceptionType => Headers.GetValueOrDefault(":exception-type");
    public string PayloadText => Encoding.UTF8.GetString(Payload);
}

public static class AwsEventStream
{
    private const int PreludeLength = 12;
    /// <summary>botocore's limits; anything larger is a corrupt length, not a real frame.</summary>
    private const int MaxHeadersLength = 128 * 1024;
    private const int MaxPayloadLength = 24 * 1024 * 1024;

    /// <summary>Read frames from <paramref name="stream"/> as they arrive — a frame may straddle any
    /// number of reads. Ends quietly when the stream ends between frames; a stream that ends inside a
    /// frame, or a frame that fails its checks, throws <see cref="InvalidDataException"/>.</summary>
    public static async IAsyncEnumerable<AwsEventMessage> ReadAsync(Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var prelude = new byte[PreludeLength];
        while (true)
        {
            var got = await stream.ReadAtLeastAsync(prelude, PreludeLength, throwOnEndOfStream: false, cancellationToken)
                .ConfigureAwait(false);
            if (got == 0) yield break;
            if (got < PreludeLength) throw new InvalidDataException("The event stream ended inside a frame.");
            var total = CheckPrelude(prelude);
            var frame = new byte[total];
            prelude.CopyTo(frame, 0);
            got = await stream.ReadAtLeastAsync(frame.AsMemory(PreludeLength), total - PreludeLength,
                throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
            if (got < total - PreludeLength) throw new InvalidDataException("The event stream ended inside a frame.");
            yield return Decode(frame);
        }
    }

    /// <summary>Decode exactly one whole frame.</summary>
    public static AwsEventMessage Decode(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < PreludeLength) throw new InvalidDataException("Event-stream frame too short.");
        var total = CheckPrelude(frame[..PreludeLength]);
        if (frame.Length != total) throw new InvalidDataException($"Event-stream frame is {frame.Length} bytes, its prelude says {total}.");
        var expected = BinaryPrimitives.ReadUInt32BigEndian(frame[(total - 4)..]);
        var actual = Crc32.Compute(frame[..(total - 4)]);
        if (expected != actual)
            throw new InvalidDataException($"Event-stream message checksum mismatch (expected 0x{expected:x8}, got 0x{actual:x8}).");
        var headersLength = (int)BinaryPrimitives.ReadUInt32BigEndian(frame[4..]);
        var headers = ParseHeaders(frame.Slice(PreludeLength, headersLength));
        var payload = frame[(PreludeLength + headersLength)..(total - 4)].ToArray();
        return new AwsEventMessage(headers, payload);
    }

    /// <summary>Validate a prelude; the frame's total length.</summary>
    private static int CheckPrelude(ReadOnlySpan<byte> prelude)
    {
        var total = BinaryPrimitives.ReadUInt32BigEndian(prelude);
        var headersLength = BinaryPrimitives.ReadUInt32BigEndian(prelude[4..]);
        var expected = BinaryPrimitives.ReadUInt32BigEndian(prelude[8..]);
        var actual = Crc32.Compute(prelude[..8]);
        if (expected != actual)
            throw new InvalidDataException($"Event-stream prelude checksum mismatch (expected 0x{expected:x8}, got 0x{actual:x8}).");
        if (headersLength > MaxHeadersLength || total < PreludeLength + 4 + headersLength
            || total - PreludeLength - 4 - headersLength > MaxPayloadLength)
            throw new InvalidDataException($"Event-stream frame has impossible lengths (total {total}, headers {headersLength}).");
        return (int)total;
    }

    /// <summary>The string headers (type 7). Every other type is skipped by its size: 0/1 booleans (no
    /// value), 2 int8, 3 int16, 4 int32, 5 int64, 6 bytes (u16 length), 8 timestamp (int64), 9 UUID.</summary>
    private static Dictionary<string, string> ParseHeaders(ReadOnlySpan<byte> data)
    {
        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        var at = 0;
        while (at < data.Length)
        {
            var nameLength = data[at++];
            var name = Encoding.UTF8.GetString(Take(data, ref at, nameLength));
            var type = Take(data, ref at, 1)[0];
            switch (type)
            {
                case 0 or 1: break;
                case 2: Take(data, ref at, 1); break;
                case 3: Take(data, ref at, 2); break;
                case 4: Take(data, ref at, 4); break;
                case 5 or 8: Take(data, ref at, 8); break;
                case 9: Take(data, ref at, 16); break;
                case 6 or 7:
                {
                    var length = BinaryPrimitives.ReadUInt16BigEndian(Take(data, ref at, 2));
                    var value = Take(data, ref at, length);
                    if (type == 7) headers[name] = Encoding.UTF8.GetString(value);
                    break;
                }
                default:
                    throw new InvalidDataException($"Event-stream header '{name}' has unknown type {type}.");
            }
        }
        return headers;
    }

    private static ReadOnlySpan<byte> Take(ReadOnlySpan<byte> data, ref int at, int count)
    {
        if (at + count > data.Length) throw new InvalidDataException("Event-stream headers are truncated.");
        var slice = data.Slice(at, count);
        at += count;
        return slice;
    }
}

/// <summary>CRC-32/IEEE (the zip/PNG polynomial, reflected), table-driven.</summary>
internal static class Crc32
{
    private static readonly uint[] Table = MakeTable();

    private static uint[] MakeTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return ~crc;
    }
}
