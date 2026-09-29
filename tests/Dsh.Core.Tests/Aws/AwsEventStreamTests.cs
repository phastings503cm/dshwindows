using System.Text;
using System.Text.Json.Nodes;

namespace Dsh.Core.Tests;

/// <summary>The application/vnd.amazon.eventstream decoder: frames from botocore's own decoder, every
/// header type, frames split across reads, and corrupt or cut-off streams.</summary>
public sealed class AwsEventStreamTests
{
    /// <summary>A contentBlockDelta event, as botocore's EventStreamBuffer decodes it.</summary>
    private const string DeltaHex =
        "0000009f00000057c37babff0b3a6576656e742d74797065070011636f6e74656e74426c6f636b44656c74610d3a636f6e74656e742d74797065" +
        "0700106170706c69636174696f6e2f6a736f6e0d3a6d6573736167652d747970650700056576656e747b22636f6e74656e74426c6f636b496e" +
        "646578223a302c2264656c7461223a7b2274657874223a224869227d2c2270223a2261626364227df2ace5ff";

    /// <summary>A throttlingException frame whose headers use every type (true, false, int8, int16, int32,
    /// int64, bytes, timestamp, uuid) before the two string ones — also decoded by botocore.</summary>
    private const string MixedHex =
        "000000ae00000087d9e9e81d0174000166010162027f017303010201690401020304016c050102030405060708017906000378797a01640800" +
        "00019200000000017509000102030405060708090a0b0c0d0e0f0d3a6d6573736167652d74797065070009657863657074696f6e0f3a6578" +
        "63657074696f6e2d747970650700137468726f74746c696e67457863657074696f6e7b226d657373616765223a22736c6f7720646f776e22" +
        "7d82d4ac6b";

    private static async Task<List<AwsEventMessage>> ReadAllAsync(Stream stream)
    {
        var messages = new List<AwsEventMessage>();
        await foreach (var message in AwsEventStream.ReadAsync(stream)) messages.Add(message);
        return messages;
    }

    [Fact]
    public void DecodesAFrameBotocoreDecodes()
    {
        var message = AwsEventStream.Decode(Convert.FromHexString(DeltaHex));
        Assert.Equal("event", message.MessageType);
        Assert.Equal("contentBlockDelta", message.EventType);
        Assert.Equal("application/json", message.Headers[":content-type"]);
        Assert.Equal("""{"contentBlockIndex":0,"delta":{"text":"Hi"},"p":"abcd"}""", message.PayloadText);
    }

    [Fact]
    public void TheTestEncoderProducesBotocoresBytes()
    {
        var frame = EventStreamFrames.Frame(
            [(":event-type", "contentBlockDelta"), (":content-type", "application/json"), (":message-type", "event")],
            Encoding.UTF8.GetBytes("""{"contentBlockIndex":0,"delta":{"text":"Hi"},"p":"abcd"}"""));
        Assert.Equal(DeltaHex, Convert.ToHexStringLower(frame));
    }

    [Fact]
    public void NonStringHeadersAreSkippedBySize()
    {
        var message = AwsEventStream.Decode(Convert.FromHexString(MixedHex));
        Assert.Equal(2, message.Headers.Count);
        Assert.Equal("exception", message.MessageType);
        Assert.Equal("throttlingException", message.ExceptionType);
        Assert.Equal("""{"message":"slow down"}""", message.PayloadText);
    }

    [Fact]
    public async Task FramesStraddlingReadsAreReassembled()
    {
        var frames = new[]
        {
            EventStreamFrames.TextDelta(0, "Hel"),
            Convert.FromHexString(MixedHex),
            EventStreamFrames.TextDelta(0, "lo — ✓"),
        };
        var messages = await ReadAllAsync(new TrickleStream(frames.SelectMany(f => f).ToArray(), chunk: 3));
        Assert.Equal(3, messages.Count);
        Assert.Contains("Hel", messages[0].PayloadText);
        Assert.Equal("throttlingException", messages[1].ExceptionType);
        Assert.Equal("lo — ✓", JsonNode.Parse(messages[2].Payload)!["delta"]!["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task EmptyStreamHasNoFrames()
    {
        Assert.Empty(await ReadAllAsync(new MemoryStream()));
    }

    [Fact]
    public void PayloadCorruptionFailsTheMessageChecksum()
    {
        var frame = Convert.FromHexString(DeltaHex);
        frame[^10] ^= 0x01;
        var error = Assert.Throws<InvalidDataException>(() => AwsEventStream.Decode(frame));
        Assert.Contains("message checksum", error.Message);
    }

    [Fact]
    public async Task PreludeCorruptionFailsThePreludeChecksum()
    {
        var frame = Convert.FromHexString(DeltaHex);
        frame[5] ^= 0x01; // headers length
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => ReadAllAsync(new MemoryStream(frame)));
        Assert.Contains("prelude checksum", error.Message);
    }

    [Fact]
    public async Task StreamCutInsideAFrameIsAnError()
    {
        var bytes = EventStreamFrames.TextDelta(0, "Hello").Concat(EventStreamFrames.TextDelta(0, "again")[..20]).ToArray();
        var messages = new List<AwsEventMessage>();
        var error = await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await foreach (var message in AwsEventStream.ReadAsync(new TrickleStream(bytes, chunk: 7))) messages.Add(message);
        });
        Assert.Single(messages);
        Assert.Contains("ended inside a frame", error.Message);
    }

    [Fact]
    public void Crc32MatchesTheStandardCheckValue()
    {
        Assert.Equal(0xCBF43926u, Crc32.Compute("123456789"u8));
    }

    /// <summary>Hands out at most <c>chunk</c> bytes per read, like a slow network.</summary>
    private sealed class TrickleStream(byte[] data, int chunk) : Stream
    {
        private int _position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = Math.Min(Math.Min(count, chunk), data.Length - _position);
            Array.Copy(data, _position, buffer, offset, n);
            _position += n;
            return n;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            var n = Math.Min(Math.Min(buffer.Length, chunk), data.Length - _position);
            data.AsMemory(_position, n).CopyTo(buffer);
            _position += n;
            return n;
        }
    }
}
