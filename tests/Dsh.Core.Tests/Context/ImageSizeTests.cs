using System.Buffers.Binary;
using System.IO.Compression;

namespace Dsh.Core.Tests;

/// <summary>Ported from the ImageSizeTests class in ComputerCoreTests.swift.</summary>
public sealed class ImageSizeTests
{
    [Fact]
    public void PngAndJpegDimensionsFromHeader()
    {
        Assert.Equal((320, 200), ImageSize.Dimensions(TestImages.Png(320, 200)));
        Assert.Equal((411, 97), ImageSize.Dimensions(TestImages.Jpeg(411, 97)));
        Assert.Null(ImageSize.Dimensions([1, 2, 3]));
    }

    [Fact]
    public void TokenCostFollowsPixelsNotBytes()
    {
        var shot = TestImages.Png(1400, 900);
        Assert.Equal(50 * 33, ImageSize.Tokens(shot)); // 50 x 33 patches of 28px
        Assert.Equal(1_500, ImageSize.Tokens([9, 9, 9]));
        // The estimator must not count base64 length for an image.
        var message = LlmMessage.User("look", [new MessageAttachment(AttachmentKind.Image, "a.png", shot)]);
        Assert.True(TokenEstimate.Message(message) < 3_000);
    }
}

/// <summary>New: the other header formats, the clamp, and checks on the hand-made encoders
/// themselves.</summary>
public sealed class ImageHeaderTests
{
    [Fact]
    public void GifAndWebpDimensions()
    {
        Assert.Equal((640, 480), ImageSize.Dimensions(TestImages.Gif(640, 480)));
        Assert.Equal((1920, 1080), ImageSize.Dimensions(TestImages.WebpExtended(1920, 1080)));
        Assert.Equal((300, 5000), ImageSize.Dimensions(TestImages.WebpLossless(300, 5000)));
    }

    [Fact]
    public void LargeJpegDimensionsUseBothBytes()
    {
        Assert.Equal((3840, 2160), ImageSize.Dimensions(TestImages.Jpeg(3840, 2160)));
    }

    [Fact]
    public void TruncatedOrUnknownHeadersAreNull()
    {
        Assert.Null(ImageSize.Dimensions([]));
        Assert.Null(ImageSize.Dimensions(TestImages.Png(10, 10)[..12])); // signature but no IHDR
        Assert.Null(ImageSize.Dimensions([0xFF, 0xD8, 0xFF, 0xD9]));     // JPEG with no frame
        Assert.Null(ImageSize.Dimensions("%PDF-1.7 not an image"u8.ToArray()));
    }

    [Fact]
    public void TokenCostIsClamped()
    {
        Assert.Equal(64, ImageSize.Tokens(TestImages.Gif(1, 1)));
        Assert.Equal(8_192, ImageSize.Tokens(TestImages.Gif(10_000, 10_000)));
        Assert.Equal(64, ImageSize.Tokens(TestImages.Jpeg(28 * 8, 28 * 8))); // exactly 64 patches
        Assert.Equal(65, ImageSize.Tokens(TestImages.Jpeg(28 * 65, 28)));
    }

    [Fact]
    public void Crc32MatchesTheStandardCheckValue()
    {
        Assert.Equal(0xCBF43926u, TestImages.Crc32("123456789"u8));
        Assert.Equal(0xAE426082u, TestImages.Crc32("IEND"u8)); // every PNG ends with this CRC
    }

    [Fact]
    public void GeneratedPngIsWellFormed()
    {
        var png = TestImages.Png(3, 2, 10, 20, 30);
        var span = png.AsSpan();
        Assert.Equal(13, BinaryPrimitives.ReadInt32BigEndian(span[8..]));
        Assert.Equal("IHDR"u8.ToArray(), span[12..16].ToArray());
        Assert.Equal(TestImages.Crc32(span[12..29]), BinaryPrimitives.ReadUInt32BigEndian(span[29..]));
        Assert.Equal("IEND"u8.ToArray(), span[^8..^4].ToArray());

        // The IDAT payload inflates back to the filtered scanlines.
        var idatLength = BinaryPrimitives.ReadInt32BigEndian(span[33..]);
        using var zlib = new ZLibStream(new MemoryStream(png, 41, idatLength), CompressionMode.Decompress);
        using var raw = new MemoryStream();
        zlib.CopyTo(raw);
        byte[] expected = [0, 10, 20, 30, 10, 20, 30, 10, 20, 30, 0, 10, 20, 30, 10, 20, 30, 10, 20, 30];
        Assert.Equal(expected, raw.ToArray());
    }
}
