using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace Dsh.Core.Tests;

/// <summary>Builds application/vnd.amazon.eventstream frames and Bedrock ConverseStream replies. The
/// encoder (and its bit-by-bit CRC) is independent of the decoder under test; AwsEventStreamTests checks
/// it against a frame botocore decoded.</summary>
public static class EventStreamFrames
{
    public static byte[] Frame(IEnumerable<(string Name, string Value)> headers, byte[] payload)
    {
        var head = new List<byte>();
        foreach (var (name, value) in headers)
        {
            var n = Encoding.UTF8.GetBytes(name);
            var v = Encoding.UTF8.GetBytes(value);
            head.Add((byte)n.Length);
            head.AddRange(n);
            head.Add(7);
            head.Add((byte)(v.Length >> 8));
            head.Add((byte)v.Length);
            head.AddRange(v);
        }
        var total = 12 + head.Count + payload.Length + 4;
        var frame = new byte[total];
        BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)total);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(4), (uint)head.Count);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(8), Crc(frame.AsSpan(0, 8)));
        head.CopyTo(frame, 12);
        payload.CopyTo(frame, 12 + head.Count);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(total - 4), Crc(frame.AsSpan(0, total - 4)));
        return frame;
    }

    /// <summary>CRC-32/IEEE, one bit at a time.</summary>
    private static uint Crc(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return ~crc;
    }

    public static byte[] Event(string eventType, JsonObject payload) =>
        Frame([(":event-type", eventType), (":content-type", "application/json"), (":message-type", "event")],
              Encoding.UTF8.GetBytes(payload.ToJsonString()));

    public static byte[] Exception(string exceptionType, string message) =>
        Frame([(":exception-type", exceptionType), (":content-type", "application/json"), (":message-type", "exception")],
              Encoding.UTF8.GetBytes(new JsonObject { ["message"] = message }.ToJsonString()));

    // MARK: ConverseStream events

    public static byte[] MessageStart() => Event("messageStart", new JsonObject { ["role"] = "assistant", ["p"] = "abc" });

    public static byte[] TextDelta(int index, string text) => Event("contentBlockDelta", new JsonObject
    {
        ["contentBlockIndex"] = index,
        ["delta"] = new JsonObject { ["text"] = text },
        ["p"] = "abcdef",
    });

    public static byte[] ToolStart(int index, string id, string name) => Event("contentBlockStart", new JsonObject
    {
        ["contentBlockIndex"] = index,
        ["start"] = new JsonObject { ["toolUse"] = new JsonObject { ["toolUseId"] = id, ["name"] = name } },
    });

    public static byte[] ToolInput(int index, string input) => Event("contentBlockDelta", new JsonObject
    {
        ["contentBlockIndex"] = index,
        ["delta"] = new JsonObject { ["toolUse"] = new JsonObject { ["input"] = input } },
    });

    public static byte[] Reasoning(int index, string? text = null, string? signature = null, string? redacted = null)
    {
        var reasoning = new JsonObject();
        if (text is not null) reasoning["text"] = text;
        if (signature is not null) reasoning["signature"] = signature;
        if (redacted is not null) reasoning["redactedContent"] = redacted;
        return Event("contentBlockDelta", new JsonObject
        {
            ["contentBlockIndex"] = index,
            ["delta"] = new JsonObject { ["reasoningContent"] = reasoning },
        });
    }

    public static byte[] BlockStop(int index) => Event("contentBlockStop", new JsonObject { ["contentBlockIndex"] = index });

    public static byte[] Stop(string reason) => Event("messageStop", new JsonObject { ["stopReason"] = reason });

    public static byte[] Metadata(int input, int output, int cacheRead = 0, int cacheWrite = 0) => Event("metadata", new JsonObject
    {
        ["usage"] = new JsonObject
        {
            ["inputTokens"] = input, ["outputTokens"] = output, ["totalTokens"] = input + output,
            ["cacheReadInputTokens"] = cacheRead, ["cacheWriteInputTokens"] = cacheWrite,
        },
        ["metrics"] = new JsonObject { ["latencyMs"] = 123 },
    });

    /// <summary>A 200 ConverseStream response carrying <paramref name="frames"/>.</summary>
    public static HttpResponseMessage Reply(params byte[][] frames)
    {
        var content = new ByteArrayContent(frames.SelectMany(f => f).ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/vnd.amazon.eventstream");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    /// <summary>An AWS JSON error response: {"message"} with the type in x-amzn-ErrorType.</summary>
    public static HttpResponseMessage Error(HttpStatusCode status, string type, string message)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(new JsonObject { ["message"] = message }.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        response.Headers.TryAddWithoutValidation("x-amzn-ErrorType", type);
        return response;
    }
}
