using SokagimdakiSorun.SharedKernel;

namespace SokagimdakiSorun.Complaints.Contracts;

/// <summary>
/// Şikâyet oluşturuldu (onaylı intake'ten). Şikâyetin ilk durumu <see cref="ComplaintStatus.Submitted"/>'tır; bu geçiş için
/// ayrıca <see cref="ComplaintStatusChanged"/> yayımlanmaz. Dinleyen: Messaging ("Başvurunuz alındı").
/// </summary>
/// <param name="EventId">Benzersiz event kimliği (handler idempotency anahtarı).</param>
/// <param name="OccurredAt">UTC.</param>
/// <param name="ComplaintId">Şikâyet kimliği.</param>
/// <param name="PublicReference">İnsanlar için referans (<c>SS-XXXX-XXXX</c>).</param>
/// <param name="ReporterUserId">Şikâyetçinin kullanıcı kimliği.</param>
public sealed record ComplaintSubmitted(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid ComplaintId,
    string PublicReference,
    Guid ReporterUserId) : IIntegrationEvent;

/// <summary>
/// Şikâyetin durumu değişti. İç gerekçeyi (<c>reason</c>) <b>taşımaz</b>; yalnızca şikâyetçiye gösterilebilir
/// <paramref name="ReporterNote"/> taşır (API_CONTRACT_STRATEGY §7.2). <see cref="ComplaintStatus.AwaitingInformation"/>
/// için <paramref name="ReporterNote"/> şikâyetçiye sorulacak sorudur ve her zaman doludur.
/// </summary>
/// <param name="EventId">Benzersiz event kimliği (handler idempotency anahtarı).</param>
/// <param name="OccurredAt">UTC.</param>
/// <param name="ComplaintId">Şikâyet kimliği.</param>
/// <param name="PublicReference">İnsanlar için referans.</param>
/// <param name="ReporterUserId">Şikâyetçinin kullanıcı kimliği.</param>
/// <param name="FromStatus">Önceki durum.</param>
/// <param name="ToStatus">Yeni durum.</param>
/// <param name="ReporterNote">Şikâyetçiye iletilecek metin; yoksa <see langword="null"/>.</param>
public sealed record ComplaintStatusChanged(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid ComplaintId,
    string PublicReference,
    Guid ReporterUserId,
    ComplaintStatus FromStatus,
    ComplaintStatus ToStatus,
    string? ReporterNote) : IIntegrationEvent;
