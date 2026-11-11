using SokagimdakiSorun.Media.Application;
using SokagimdakiSorun.Media.TestSupport;

namespace SokagimdakiSorun.Media.Tests;

/// <summary>İçerik tipi izin listesi, imza tespiti ve EXIF/GPS ayıklama (ADR-0004 §3, §6). Bağımlılıksız.</summary>
public sealed class ImageValidationTests
{
    [Theory]
    [InlineData("image/jpeg", "image/jpeg")]
    [InlineData("IMAGE/JPEG", "image/jpeg")]
    [InlineData("image/png; charset=binary", "image/png")]
    [InlineData(" image/webp ", "image/webp")]
    public void Allowlisted_content_types_are_accepted(string contentType, string canonical)
    {
        Assert.True(ImageFormats.TryParseContentType(contentType, out var format));
        Assert.Equal(canonical, ImageFormats.ToContentType(format));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("image/jpg")]
    [InlineData("image/gif")]
    [InlineData("image/svg+xml")]
    [InlineData("application/octet-stream")]
    [InlineData("text/html")]
    public void Other_content_types_are_rejected(string? contentType) =>
        Assert.False(ImageFormats.TryParseContentType(contentType, out _));

    [Fact]
    public void Signatures_are_detected_from_content_not_from_declared_type()
    {
        Assert.Equal(ImageFormat.Jpeg, ImageFormats.Detect(TestImages.Jpeg(withMetadata: false)));
        Assert.Equal(ImageFormat.Png, ImageFormats.Detect(TestImages.Png(withMetadata: false)));
        Assert.Equal(ImageFormat.WebP, ImageFormats.Detect(TestImages.WebP(withMetadata: false)));
        Assert.Null(ImageFormats.Detect("GIF89a......"u8));
        Assert.Null(ImageFormats.Detect("<svg xmlns="u8));
        Assert.Null(ImageFormats.Detect([]));
    }

    public static TheoryData<string> Formats => ["image/jpeg", "image/png", "image/webp"];

    [Theory]
    [MemberData(nameof(Formats))]
    public void Embedded_metadata_and_trailing_data_are_stripped_and_image_structure_is_kept(string contentType)
    {
        var format = Parse(contentType);
        var withMetadata = Build(format, withMetadata: true);
        Assert.True(TestImages.ContainsMarker(withMetadata)); // önkoşul

        using var output = new MemoryStream();
        Assert.True(ImageMetadataStripper.TryStrip(format, withMetadata, output));

        Assert.False(TestImages.ContainsMarker(output.ToArray()));
        Assert.Equal(Build(format, withMetadata: false), output.ToArray());
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void Image_without_metadata_is_unchanged(string contentType)
    {
        var format = Parse(contentType);
        var clean = Build(format, withMetadata: false);
        using var output = new MemoryStream();

        Assert.True(ImageMetadataStripper.TryStrip(format, clean, output));
        Assert.Equal(clean, output.ToArray());
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void Truncated_image_is_rejected(string contentType)
    {
        var format = Parse(contentType);
        var image = Build(format, withMetadata: false);
        using var output = new MemoryStream();

        Assert.False(ImageMetadataStripper.TryStrip(format, image.AsSpan(0, image.Length - 6), output));
    }

    [Fact]
    public void Png_whose_first_chunk_is_not_ihdr_is_rejected()
    {
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0, .. "IEND"u8, 0, 0, 0, 0];
        using var output = new MemoryStream();

        Assert.False(ImageMetadataStripper.TryStrip(ImageFormat.Png, png, output));
    }

    [Fact]
    public void Jpeg_segment_length_beyond_end_is_rejected()
    {
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE1, 0xFF, 0xF0, 0x45, 0x78];
        using var output = new MemoryStream();

        Assert.False(ImageMetadataStripper.TryStrip(ImageFormat.Jpeg, jpeg, output));
    }

    private static ImageFormat Parse(string contentType)
    {
        Assert.True(ImageFormats.TryParseContentType(contentType, out var format));
        return format;
    }

    private static byte[] Build(ImageFormat format, bool withMetadata) => format switch
    {
        ImageFormat.Jpeg => TestImages.Jpeg(withMetadata),
        ImageFormat.Png => TestImages.Png(withMetadata),
        ImageFormat.WebP => TestImages.WebP(withMetadata),
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };
}
