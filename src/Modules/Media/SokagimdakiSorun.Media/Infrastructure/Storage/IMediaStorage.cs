using System.Security.Cryptography;

namespace SokagimdakiSorun.Media.Infrastructure.Storage;

/// <summary>
/// Fiziksel saklama soyutlaması (ADR-0004 §1). Modül dışına açılmaz; anahtarlar ve yollar Media içinde kalır.
/// Object storage gerekirse yeni bir uygulama eklenir; Railway Volume için ayrı uygulama yoktur (aynı dosya sistemi).
/// </summary>
internal interface IMediaStorage
{
    /// <summary><c>media_files.storage_provider</c> değeri.</summary>
    string ProviderName { get; }

    /// <summary>
    /// İçeriği yeni bir anahtarla yazar. Anahtar zaten varsa <see cref="MediaStorageConflictException"/>: mevcut dosyanın
    /// üzerine asla yazılmaz.
    /// </summary>
    Task SaveAsync(string storageKey, Stream content, CancellationToken cancellationToken);

    /// <summary>İçeriği okumak için açar; dosya yoksa <see langword="null"/>.</summary>
    Task<Stream?> OpenReadAsync(string storageKey, CancellationToken cancellationToken);

    /// <summary>Dosyayı siler; yoksa sessizce geçer.</summary>
    Task DeleteAsync(string storageKey, CancellationToken cancellationToken);

    /// <summary>
    /// Kökteki tüm nesneleri listeler (yetim taraması için). Geçerli anahtar biçiminde olmayan kalıntılar
    /// (ör. yarım kalmış geçici dosyalar) <see cref="StoredMediaObject.StorageKey"/> = <see langword="null"/> ile döner.
    /// </summary>
    IAsyncEnumerable<StoredMediaObject> ListAsync(CancellationToken cancellationToken);

    /// <summary><see cref="ListAsync"/>'ten dönen bir nesneyi siler; yoksa sessizce geçer.</summary>
    Task DeleteObjectAsync(StoredMediaObject storedObject, CancellationToken cancellationToken);

    /// <summary>Kök yazılabilir mi; değilse <see cref="MediaStorageException"/> fırlatır (<c>/health/ready</c>).</summary>
    Task EnsureWritableAsync(CancellationToken cancellationToken);
}

/// <summary>Saklamadaki bir nesne. <see cref="Locator"/> uygulamaya özgü, opaktır ve modül dışına çıkmaz.</summary>
internal sealed record StoredMediaObject(string Locator, string? StorageKey, DateTimeOffset LastModifiedUtc);

/// <summary>Sunucuda üretilen rastgele storage key: 128 bit, 32 küçük harf hex. Kullanıcı girdisinden türetilmez.</summary>
internal static class StorageKey
{
    public const int Length = 32;

    public static string New() => RandomNumberGenerator.GetHexString(Length, lowercase: true);

    /// <summary>Yalnızca <c>[0-9a-f]{32}</c>; ayırıcı, nokta veya başka karakter içeremez (yol geçişi imkânsız).</summary>
    public static bool IsValid(string? value) =>
        value is { Length: Length } && value.AsSpan().IndexOfAnyExcept("0123456789abcdef") < 0;
}

/// <summary>Saklama katmanı hatası. Mesajı fiziksel yol veya anahtar içermez (ARCHITECTURE §11).</summary>
internal class MediaStorageException : Exception
{
    public MediaStorageException()
    {
    }

    public MediaStorageException(string message)
        : base(message)
    {
    }

    public MediaStorageException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Aynı anahtarla ikinci yazma girişimi; mevcut dosya korunur.</summary>
internal sealed class MediaStorageConflictException : MediaStorageException
{
    public MediaStorageConflictException()
        : base("Aynı storage key ile bir dosya zaten var; üzerine yazılmadı.")
    {
    }

    public MediaStorageConflictException(string message)
        : base(message)
    {
    }

    public MediaStorageConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
