using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Dsh.Core.Tests;

/// <summary>Tiny encoders for header/size tests. The BCL has no image encoder (the Swift tests used
/// ImageIO), and ImageSize only reads headers, so these write just enough of each format: a real,
/// decodable PNG (signature, IHDR, zlib IDAT, IEND with CRCs) and a baseline JPEG header (SOI, APP0
/// JFIF, DQT, DHT, SOF0, EOI) without scan data.</summary>
public static class TestImages
{
    /// <summary>A flat-colour 8-bit RGB PNG.</summary>
    public static byte[] Png(int width, int height, byte red = 51, byte green = 102, byte blue = 204)
    {
        var stride = 1 + width * 3;
        var raw = new byte[height * stride];
        for (var y = 0; y < height; y++)
        {
            var row = y * stride; // raw[row] = 0: filter type "None"
            for (var x = 0; x < width; x++)
            {
                raw[row + 1 + x * 3] = red;
                raw[row + 2 + x * 3] = green;
                raw[row + 3 + x * 3] = blue;
            }
        }
        byte[] compressed;
        using (var buffer = new MemoryStream())
        {
            using (var zlib = new ZLibStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
                zlib.Write(raw);
            compressed = buffer.ToArray();
        }

        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0), width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;  // bit depth
        header[9] = 2;  // colour type: truecolour
        // compression, filter and interlace methods stay 0

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        WriteChunk(png, "IHDR", header);
        WriteChunk(png, "IDAT", compressed);
        WriteChunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        var length = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        stream.Write(length);
        var typeAndData = new byte[4 + data.Length];
        Encoding.ASCII.GetBytes(type, typeAndData);
        data.CopyTo(typeAndData, 4);
        stream.Write(typeAndData);
        var crc = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(typeAndData));
        stream.Write(crc);
    }

    /// <summary>A baseline JPEG header. The DHT segment (marker C4) sits inside the C0–CF
    /// start-of-frame range on purpose: a header walker must skip it.</summary>
    public static byte[] Jpeg(int width, int height)
    {
        using var jpeg = new MemoryStream();
        jpeg.Write([0xFF, 0xD8]); // SOI
        // APP0: length 16, "JFIF\0", version 1.1, no units, 1x1 density, no thumbnail.
        jpeg.Write([0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00]);
        // DQT: length 67, table 0 with 8-bit precision, 64 entries.
        jpeg.Write([0xFF, 0xDB, 0x00, 0x43, 0x00]);
        jpeg.Write(Enumerable.Repeat((byte)1, 64).ToArray());
        // DHT: length 19, DC table 0, sixteen zero code counts (no symbols).
        jpeg.Write([0xFF, 0xC4, 0x00, 0x13, 0x00]);
        jpeg.Write(new byte[16]);
        // SOF0: length 17, 8-bit precision, height, width, 3 components (Y 2x2, Cb, Cr).
        jpeg.Write([0xFF, 0xC0, 0x00, 0x11, 0x08,
                    (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width,
                    0x03, 0x01, 0x22, 0x00, 0x02, 0x11, 0x01, 0x03, 0x11, 0x01]);
        jpeg.Write([0xFF, 0xD9]); // EOI
        return jpeg.ToArray();
    }

    /// <summary>A GIF89a logical screen descriptor (little-endian size) and trailer.</summary>
    public static byte[] Gif(int width, int height) =>
    [
        .. "GIF89a"u8,
        (byte)width, (byte)(width >> 8), (byte)height, (byte)(height >> 8),
        0x00, 0x00, 0x00, // no global colour table, background, aspect
        0x3B,             // trailer
    ];

    /// <summary>An extended-format WebP header (VP8X chunk with the canvas size minus one, 24-bit LE).</summary>
    public static byte[] WebpExtended(int width, int height)
    {
        var w = width - 1;
        var h = height - 1;
        return
        [
            .. "RIFF"u8, 0x16, 0x00, 0x00, 0x00, .. "WEBP"u8,
            .. "VP8X"u8, 0x0A, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, // flags + reserved
            (byte)w, (byte)(w >> 8), (byte)(w >> 16),
            (byte)h, (byte)(h >> 8), (byte)(h >> 16),
        ];
    }

    /// <summary>A lossless WebP header (VP8L: signature 0x2F then 14-bit width-1 and height-1).</summary>
    public static byte[] WebpLossless(int width, int height)
    {
        var bits = (uint)(width - 1) | (uint)(height - 1) << 14;
        return
        [
            .. "RIFF"u8, 0x1A, 0x00, 0x00, 0x00, .. "WEBP"u8,
            .. "VP8L"u8, 0x05, 0x00, 0x00, 0x00,
            0x2F, (byte)bits, (byte)(bits >> 8), (byte)(bits >> 16), (byte)(bits >> 24),
            0x00, 0x00, 0x00, 0x00, 0x00,
        ];
    }

    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    /// <summary>CRC-32 (ISO-HDLC / zlib / PNG), by hand: System.IO.Hashing is not part of the BCL.</summary>
    public static uint Crc32(ReadOnlySpan<byte> data)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
}
