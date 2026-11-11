namespace SokagimdakiSorun.Media.Domain;

/// <summary>
/// <c>media.media_files</c> satırı: yalnızca metadata; içerik dosya sisteminde (DOMAIN_MODEL §3.4).
/// <see cref="StorageKey"/> modül dışına çıkmaz (I-10). Durum geçişleri yarış güvenliği için koşullu
/// <c>UPDATE</c> ile yapılır (bkz. <c>MediaService.CommitAsync</c>, <c>OrphanMediaCleaner</c>).
/// </summary>
internal sealed class MediaFile
{
    private MediaFile(Guid id, string storageProvider, string storageKey, string contentType, long sizeBytes, string sha256, DateTimeOffset createdAt)
    {
        Id = id;
        StorageProvider = storageProvider;
        StorageKey = storageKey;
        ContentType = contentType;
        SizeBytes = sizeBytes;
        Sha256 = sha256;
        CreatedAt = createdAt;
        Status = MediaFileStatus.Temporary;
    }

    public Guid Id { get; private set; }

    /// <summary>Dosyanın hangi <c>IMediaStorage</c> uygulamasında olduğu (şimdilik yalnızca <c>local</c>).</summary>
    public string StorageProvider { get; private set; }

    /// <summary>Sunucuda üretilen rastgele anahtar (32 küçük harf hex). Kullanıcı girdisinden türetilmez.</summary>
    public string StorageKey { get; private set; }

    public string ContentType { get; private set; }

    /// <summary>Saklanan (metadata'sı ayıklanmış) içeriğin boyutu.</summary>
    public long SizeBytes { get; private set; }

    /// <summary>Saklanan içeriğin SHA-256 özeti (64 küçük harf hex).</summary>
    public string Sha256 { get; private set; }

    public MediaFileStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? CommittedAt { get; private set; }

    public DateTimeOffset? DeletedAt { get; private set; }

    public static MediaFile CreateTemporary(
        string storageProvider, string storageKey, string contentType, long sizeBytes, string sha256, DateTimeOffset now) =>
        new(Guid.CreateVersion7(), storageProvider, storageKey, contentType, sizeBytes, sha256, now);
}
