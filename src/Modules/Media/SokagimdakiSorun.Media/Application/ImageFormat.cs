namespace SokagimdakiSorun.Media.Application;

/// <summary>İzin verilen görüntü biçimleri (ADR-0004 §3; MVP: JPEG, PNG, WebP).</summary>
internal enum ImageFormat
{
    Jpeg,
    Png,
    WebP,
}

/// <summary>İçerik tipi izin listesi ve dosya imzası (magic byte) tespiti.</summary>
internal static class ImageFormats
{
    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static string ToContentType(ImageFormat format) => format switch
    {
        ImageFormat.Jpeg => "image/jpeg",
        ImageFormat.Png => "image/png",
        ImageFormat.WebP => "image/webp",
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    /// <summary>
    /// Bildirilen içerik tipini izin listesine göre çözer. Büyük/küçük harf ve <c>; parametre</c> kısmı yok sayılır;
    /// başka hiçbir takma ad (ör. <c>image/jpg</c>) kabul edilmez.
    /// </summary>
    public static bool TryParseContentType(string? contentType, out ImageFormat format)
    {
        format = default;
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return false;
        }

        var mediaType = contentType.Split(';', 2)[0].Trim();
        foreach (var candidate in Enum.GetValues<ImageFormat>())
        {
            if (mediaType.Equals(ToContentType(candidate), StringComparison.OrdinalIgnoreCase))
            {
                format = candidate;
                return true;
            }
        }

        return false;
    }

    /// <summary>İçeriğin ilk baytlarından biçimi tespit eder; tanınmazsa <see langword="null"/>.</summary>
    public static ImageFormat? Detect(ReadOnlySpan<byte> content)
    {
        if (content.Length >= 3 && content[0] == 0xFF && content[1] == 0xD8 && content[2] == 0xFF)
        {
            return ImageFormat.Jpeg;
        }

        if (content.StartsWith(PngSignature))
        {
            return ImageFormat.Png;
        }

        if (content.Length >= 12 && content[..4].SequenceEqual("RIFF"u8) && content[8..12].SequenceEqual("WEBP"u8))
        {
            return ImageFormat.WebP;
        }

        return null;
    }
}
