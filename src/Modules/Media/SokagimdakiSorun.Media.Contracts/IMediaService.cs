using SokagimdakiSorun.SharedKernel;

namespace SokagimdakiSorun.Media.Contracts;

/// <summary>
/// Media modülünün süreç içi sözleşmesi (API_CONTRACT_STRATEGY §7.1; tüketenler: Messaging, Complaints).
/// Dosya, opak <see cref="MediaFileId"/> ile tanınır. Yetkilendirme kararı Media'da değildir: çağıran modül
/// (ör. Complaints) erişimi kontrol ettikten sonra <see cref="OpenReadAsync"/>'i çağırır (ADR-0004 §5).
/// </summary>
public interface IMediaService
{
    /// <summary>
    /// Dosyayı doğrular ve <c>Temporary</c> olarak saklar. Kurallar (ADR-0004 §3): içerik tipi izin listesinde,
    /// boyut akış okunurken sayılır ve sınırı aşamaz, dosya imzası bildirilen tiple uyumlu, EXIF/GPS ve benzeri
    /// gömülü metadata saklamadan önce ayıklanır. Kullanıcının dosya adı hiçbir aşamada kullanılmaz.
    /// </summary>
    /// <param name="content">Dosya içeriği; baştan sona bir kez okunur, dispose edilmez.</param>
    /// <param name="contentType">Kaynağın bildirdiği içerik tipi (ör. <c>image/jpeg</c>); güvenilmez, doğrulanır.</param>
    /// <returns>Başarıda yeni dosyanın kimliği; reddedilirse <see cref="MediaErrorCodes"/> kodlu doğrulama hatası.</returns>
    Task<Result<MediaFileId>> StoreAsync(Stream content, string contentType, CancellationToken cancellationToken);

    /// <summary>
    /// Dosyayı <c>Committed</c> yapar (bir şikâyete bağlandığında). İdempotenttir: zaten kesinleşmiş dosya için
    /// başarı döner. Silinmiş dosya <see cref="MediaErrorCodes.Deleted"/>, bilinmeyen kimlik
    /// <see cref="MediaErrorCodes.NotFound"/> döner. Süresi dolmuş <c>Temporary</c> dosyalar temizlenir; bu yüzden
    /// çağıran taraf dosyayı makul sürede kesinleştirmelidir (<c>Media__TemporaryRetention</c>).
    /// </summary>
    Task<Result> CommitAsync(MediaFileId id, CancellationToken cancellationToken);

    /// <summary>
    /// Dosyanın içeriğini akış olarak açar. Silinmiş veya bilinmeyen dosya için <see cref="MediaErrorCodes.NotFound"/>.
    /// Dönen <see cref="MediaContent"/> çağıran tarafından dispose edilir.
    /// </summary>
    Task<Result<MediaContent>> OpenReadAsync(MediaFileId id, CancellationToken cancellationToken);
}
