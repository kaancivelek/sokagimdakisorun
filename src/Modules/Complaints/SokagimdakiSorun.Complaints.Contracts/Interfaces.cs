using SokagimdakiSorun.SharedKernel;

namespace SokagimdakiSorun.Complaints.Contracts;

// ---------------------------------------------------------------------------------------------------------------
// IComplaintIntake — tüketen: Messaging
// ---------------------------------------------------------------------------------------------------------------

/// <summary>Onaylı intake'ten şikâyet oluşturma isteği. Konum: koordinat çifti veya adres metninden en az biri zorunlu.</summary>
/// <param name="IntakeSessionId">Idempotency anahtarı (Messaging'in intake kimliği).</param>
/// <param name="ReporterUserId">Şikâyetçi (Identity'de <c>Reporter</c> türünde kullanıcı).</param>
/// <param name="CategoryId">Etkin kategori.</param>
/// <param name="Description">Açıklama (zorunlu, en fazla <see cref="IComplaintIntake.MaxDescriptionLength"/>).</param>
/// <param name="Latitude">Enlem (−90..90) veya <see langword="null"/>.</param>
/// <param name="Longitude">Boylam (−180..180) veya <see langword="null"/>.</param>
/// <param name="AddressText">Adres metni veya <see langword="null"/>.</param>
/// <param name="LocationSource">Konumun kaynağı.</param>
/// <param name="MediaFileIds">Media modülündeki <c>MediaFileId</c> değerleri (Guid); dosyalar şikâyete bağlanınca kesinleşir.</param>
public sealed record SubmitFromIntakeRequest(
    Guid IntakeSessionId,
    Guid ReporterUserId,
    Guid CategoryId,
    string Description,
    double? Latitude,
    double? Longitude,
    string? AddressText,
    LocationSource LocationSource,
    IReadOnlyList<Guid> MediaFileIds);

/// <summary><see cref="IComplaintIntake.SubmitFromIntakeAsync"/> sonucu.</summary>
/// <param name="ComplaintId">Şikâyet kimliği.</param>
/// <param name="PublicReference">İnsanlar için referans (<c>SS-XXXX-XXXX</c>).</param>
/// <param name="Created">Bu çağrı şikâyeti oluşturduysa <see langword="true"/>; aynı intake daha önce gönderildiyse <see langword="false"/>.</param>
public sealed record SubmittedComplaint(Guid ComplaintId, string PublicReference, bool Created);

/// <summary>Intake onaylandığında şikâyet oluşturur (ADR-0003 §7, geçiş #1).</summary>
public interface IComplaintIntake
{
    /// <summary>Açıklamanın azami uzunluğu.</summary>
    const int MaxDescriptionLength = 2000;

    /// <summary>Adres metninin azami uzunluğu.</summary>
    const int MaxAddressLength = 500;

    /// <summary>
    /// Idempotent ve eşzamanlılığa dayanıklı: aynı <see cref="SubmitFromIntakeRequest.IntakeSessionId"/> ile yapılan tüm
    /// çağrılar — eşzamanlı olanlar dâhil — aynı şikâyeti döndürür (kısmi UNIQUE <c>source_intake_session_id</c>). Tekrar
    /// çağrıda istek alanları yeniden doğrulanmaz, mevcut kayıt döner. Şikâyet + durum geçmişi + <see cref="ComplaintSubmitted"/>
    /// tek transaction'dır. Hatalar: <see cref="ComplaintErrorCodes.ValidationFailed"/>, <see cref="ComplaintErrorCodes.CategoryInvalid"/>,
    /// <see cref="ComplaintErrorCodes.ReporterInvalid"/>, <see cref="ComplaintErrorCodes.AttachmentUnavailable"/>.
    /// </summary>
    Task<Result<SubmittedComplaint>> SubmitFromIntakeAsync(SubmitFromIntakeRequest request, CancellationToken cancellationToken);
}

// ---------------------------------------------------------------------------------------------------------------
// IComplaintReporterActions — tüketen: Messaging
// ---------------------------------------------------------------------------------------------------------------

/// <summary>Şikâyetçinin ek bilgi cevabı (geçiş #11 veya #23; hedef sunucuda kalıcı veriden belirlenir).</summary>
/// <param name="ComplaintId">Şikâyet.</param>
/// <param name="ReporterUserId">Şikâyetçi; şikâyetin sahibi olmalıdır, değilse <see cref="ComplaintErrorCodes.NotFound"/>.</param>
/// <param name="SourceMessageId">Idempotency anahtarı (ör. Messaging'in gelen mesaj kimliği). Aynı anahtarla ikinci çağrı aynı sonucu döner.</param>
/// <param name="Text">Cevap metni (yorum olarak, şikâyetçiye görünür); metin veya dosyadan en az biri zorunlu.</param>
/// <param name="MediaFileIds">Eklenecek dosyalar (<c>InformationReply</c> kaynağıyla).</param>
public sealed record ProvideInformationRequest(
    Guid ComplaintId, Guid ReporterUserId, Guid SourceMessageId, string? Text, IReadOnlyList<Guid> MediaFileIds);

/// <summary>Şikâyetçinin geri çekmesi (geçiş #12; yalnızca <c>AwaitingInformation</c> durumunda).</summary>
/// <param name="ComplaintId">Şikâyet.</param>
/// <param name="ReporterUserId">Şikâyetçi; şikâyetin sahibi olmalıdır.</param>
/// <param name="SourceMessageId">Idempotency anahtarı.</param>
/// <param name="Reason">İsteğe bağlı gerekçe (iç kayıt).</param>
public sealed record WithdrawRequest(Guid ComplaintId, Guid ReporterUserId, Guid SourceMessageId, string? Reason);

/// <summary>Şikâyetçi eyleminin sonucu.</summary>
/// <param name="ComplaintId">Şikâyet.</param>
/// <param name="PublicReference">Referans.</param>
/// <param name="Status">Eylem sonrası durum.</param>
/// <param name="AlreadyApplied">Aynı <c>SourceMessageId</c> daha önce işlendiyse <see langword="true"/>.</param>
public sealed record ReporterActionResult(Guid ComplaintId, string PublicReference, ComplaintStatus Status, bool AlreadyApplied);

/// <summary>Şikâyetçi adına (WhatsApp) yapılan eylemler. Aktör açıkça parametre olarak geçer (HTTP bağlamı yok).</summary>
public interface IComplaintReporterActions
{
    /// <summary>
    /// Ek bilgiyi kaydeder ve şikâyeti bilgi istenmeden önceki duruma (<c>UnderReview</c> veya <c>InProgress</c>) döndürür.
    /// Şikâyet <c>AwaitingInformation</c> değilse <see cref="ComplaintErrorCodes.InvalidTransition"/>.
    /// </summary>
    Task<Result<ReporterActionResult>> ProvideInformationAsync(ProvideInformationRequest request, CancellationToken cancellationToken);

    /// <summary>Şikâyeti geri çeker (<c>Withdrawn</c>, terminal).</summary>
    Task<Result<ReporterActionResult>> WithdrawAsync(WithdrawRequest request, CancellationToken cancellationToken);
}

// ---------------------------------------------------------------------------------------------------------------
// IComplaintCatalog — tüketen: Messaging
// ---------------------------------------------------------------------------------------------------------------

/// <summary>Etkin kategori (intake menüsü için).</summary>
public sealed record ComplaintCategorySummary(Guid Id, string Code, string Name, string? Description, int SortOrder);

/// <summary>Kategori kataloğu.</summary>
public interface IComplaintCatalog
{
    /// <summary>Etkin kategoriler, <c>sort_order</c> ve ada göre sıralı.</summary>
    Task<IReadOnlyList<ComplaintCategorySummary>> GetActiveCategoriesAsync(CancellationToken cancellationToken);
}

// ---------------------------------------------------------------------------------------------------------------
// IComplaintAccessPolicy — tüketen: Messaging, Reporting
// ---------------------------------------------------------------------------------------------------------------

/// <summary>Panel kullanıcısı (aktör). Arka plan işleyicileri ve diğer modüller aktörü açıkça geçirir.</summary>
/// <param name="UserId">Kullanıcı kimliği.</param>
/// <param name="IsAdmin">Admin rolünde mi; değilse moderatör kabul edilir.</param>
public sealed record StaffActor(Guid UserId, bool IsAdmin);

/// <summary>
/// Bir panel kullanıcısının görebildiği şikâyet kümesi (ARCHITECTURE §6, OD-4): Admin hepsini; moderatör atanmamış
/// (triage havuzu) şikâyetleri ve aktif ataması üyesi olduğu birimlerde olanları.
/// </summary>
/// <param name="IsUnrestricted">Admin ise <see langword="true"/>.</param>
/// <param name="MemberServiceUnitIds">Moderatörün üyesi olduğu hizmet birimleri.</param>
public sealed record ComplaintScope(bool IsUnrestricted, IReadOnlyList<Guid> MemberServiceUnitIds);

/// <summary>Şikâyet erişim yetkisi; karar Complaints modülündedir.</summary>
public interface IComplaintAccessPolicy
{
    /// <summary>Aktör bu şikâyeti görebilir mi. Şikâyet yoksa <see langword="false"/>.</summary>
    Task<bool> CanViewAsync(StaffActor actor, Guid complaintId, CancellationToken cancellationToken);

    /// <summary>Aktörün kapsamı (liste filtresi için).</summary>
    Task<ComplaintScope> GetScopeAsync(StaffActor actor, CancellationToken cancellationToken);
}

// ---------------------------------------------------------------------------------------------------------------
// IComplaintStatistics — tüketen: Reporting
// ---------------------------------------------------------------------------------------------------------------

/// <summary>İstatistik filtresi: <c>submitted_at</c> aralığı [From, To) (UTC); <see langword="null"/> sınırsız.</summary>
public sealed record ComplaintStatisticsFilter(DateTimeOffset? From, DateTimeOffset? To);

/// <summary>Duruma göre sayım.</summary>
public sealed record StatusCount(ComplaintStatus Status, int Count);

/// <summary>Kategoriye göre sayım.</summary>
public sealed record CategoryCount(Guid CategoryId, string CategoryCode, string CategoryName, int Count);

/// <summary>Bir UTC gününde açılan (submitted) ve kapanan (closed) şikâyet sayısı.</summary>
public sealed record TimelinePoint(DateOnly Date, int Opened, int Closed);

/// <summary>Kapsam duyarlı sayım sorguları: aktör yalnızca görebildiği şikâyetleri sayar.</summary>
public interface IComplaintStatistics
{
    /// <summary>Duruma göre sayılar; sayısı 0 olan durumlar da listelenir (12 satır).</summary>
    Task<IReadOnlyList<StatusCount>> CountByStatusAsync(StaffActor actor, ComplaintStatisticsFilter filter, CancellationToken cancellationToken);

    /// <summary>Kategoriye göre sayılar (en az bir şikâyeti olan kategoriler), sayıya göre azalan.</summary>
    Task<IReadOnlyList<CategoryCount>> CountByCategoryAsync(StaffActor actor, ComplaintStatisticsFilter filter, CancellationToken cancellationToken);

    /// <summary>
    /// [<paramref name="from"/>, <paramref name="to"/>] (UTC günleri, iki uç dâhil) aralığındaki her gün için açılan ve
    /// kapanan sayısı; boş günler 0 ile döner. Aralık en fazla 366 gün olabilir.
    /// </summary>
    Task<Result<IReadOnlyList<TimelinePoint>>> GetTimelineAsync(StaffActor actor, DateOnly from, DateOnly to, CancellationToken cancellationToken);
}
