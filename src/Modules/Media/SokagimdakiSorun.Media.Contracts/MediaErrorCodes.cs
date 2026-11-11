namespace SokagimdakiSorun.Media.Contracts;

/// <summary><see cref="IMediaService"/>'in döndürdüğü kararlı hata kodları (<c>SharedKernel.Error.Code</c>).</summary>
public static class MediaErrorCodes
{
    /// <summary>İçerik tipi izin listesinde değil (MVP: JPEG, PNG, WebP).</summary>
    public const string UnsupportedType = "media.unsupported_type";

    /// <summary>Dosya boş.</summary>
    public const string Empty = "media.empty";

    /// <summary>Dosya yapılandırılmış boyut sınırını aşıyor.</summary>
    public const string TooLarge = "media.too_large";

    /// <summary>Dosya imzası (magic byte) bildirilen içerik tipiyle uyuşmuyor.</summary>
    public const string SignatureMismatch = "media.signature_mismatch";

    /// <summary>İmza doğru ama dosya yapısı bozuk; işlenemedi.</summary>
    public const string InvalidImage = "media.invalid_image";

    /// <summary>Dosya yok veya silinmiş.</summary>
    public const string NotFound = "media.not_found";

    /// <summary>Dosya silinmiş olduğu için kesinleştirilemez.</summary>
    public const string Deleted = "media.deleted";
}
