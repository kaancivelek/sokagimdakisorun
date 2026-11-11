using Microsoft.EntityFrameworkCore;
using SokagimdakiSorun.Complaints.Contracts;
using SokagimdakiSorun.Complaints.Domain;
using SokagimdakiSorun.Complaints.Infrastructure;
using SokagimdakiSorun.Identity.Contracts;
using SokagimdakiSorun.Media.Contracts;
using SokagimdakiSorun.SharedKernel;

namespace SokagimdakiSorun.Complaints.Application;

/// <summary>
/// <see cref="IComplaintIntake"/> (geçiş #1). Idempotency veritabanındadır: kısmi UNIQUE <c>source_intake_session_id</c>.
/// Eşzamanlı ikinci çağrı 23505 alır, değişikliklerini atar ve kazanan kaydı döndürür. Dosyalar kayıttan <b>önce</b>
/// kesinleştirilir (<see cref="IMediaService.CommitAsync"/> idempotenttir): kayıt başarısız olursa tekrar çağrı güvenlidir;
/// başarılı kaydın dosyaları ise geçici-dosya temizliğine yakalanmaz.
/// </summary>
internal sealed class ComplaintIntakeService(
    ComplaintsDbContext db,
    IUserDirectory users,
    IMediaService media,
    TimeProvider time) : IComplaintIntake
{
    private const int MaxReferenceAttempts = 5;
    private const int MaxAttachments = 10;

    public async Task<Result<SubmittedComplaint>> SubmitFromIntakeAsync(SubmitFromIntakeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (await FindExistingAsync(request.IntakeSessionId, cancellationToken) is { } existing)
        {
            return existing;
        }

        var validation = Validate(request);
        if (validation is not null)
        {
            return validation;
        }

        var category = await db.Categories.AsNoTracking().SingleOrDefaultAsync(c => c.Id == request.CategoryId, cancellationToken);
        if (category is not { IsActive: true })
        {
            return Error.Validation(ComplaintErrorCodes.CategoryInvalid, "Kategori bulunamadı veya etkin değil.",
                new Dictionary<string, string[]> { ["categoryId"] = ["Kategori bulunamadı veya etkin değil."] });
        }

        if (await users.GetUserAsync(request.ReporterUserId, cancellationToken) is not { Kind: UserKind.Reporter })
        {
            return Error.Validation(ComplaintErrorCodes.ReporterInvalid, "Şikâyetçi kaydı geçersiz.",
                new Dictionary<string, string[]> { ["reporterUserId"] = ["Şikâyetçi kaydı bulunamadı."] });
        }

        var mediaIds = request.MediaFileIds.Distinct().ToList();
        var committed = await CommitMediaAsync(media, mediaIds, cancellationToken);
        if (committed.IsFailure)
        {
            return committed.Error;
        }

        var now = time.GetUtcNow();
        var complaint = Complaint.SubmitFromIntake(
            request.IntakeSessionId, PublicReferenceGenerator.Next(), request.ReporterUserId, request.CategoryId,
            request.Description.Trim(), request.Latitude, request.Longitude,
            string.IsNullOrWhiteSpace(request.AddressText) ? null : request.AddressText.Trim(), request.LocationSource, now);
        foreach (var mediaId in mediaIds)
        {
            complaint.AddAttachment(mediaId, AttachmentSource.Intake, request.ReporterUserId, now);
        }

        db.Complaints.Add(complaint);
        ComplaintPersistence.WriteEvents(db, complaint);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return new SubmittedComplaint(complaint.Id, complaint.PublicReference, Created: true);
            }
            catch (DbUpdateException ex) when (ComplaintPersistence.IsUniqueViolation(ex, ComplaintsDbContext.IntakeSessionUniqueIndex))
            {
                // Aynı intake eşzamanlı gönderildi: kazanan kaydı döndür.
                db.ChangeTracker.Clear();
                return await FindExistingAsync(request.IntakeSessionId, cancellationToken)
                    ?? throw new InvalidOperationException("Benzersizlik ihlalinden sonra intake'in şikâyeti bulunamadı.");
            }
            catch (DbUpdateException ex) when (attempt < MaxReferenceAttempts
                && ComplaintPersistence.IsUniqueViolation(ex, ComplaintsDbContext.PublicReferenceUniqueIndex))
            {
                complaint.ReplacePublicReference(PublicReferenceGenerator.Next());
                ResetOutboxReference(complaint);
            }
            catch (DbUpdateException ex) when (ComplaintPersistence.IsUniqueViolation(ex, ComplaintsDbContext.AttachmentMediaUniqueIndex))
            {
                db.ChangeTracker.Clear();
                return Error.BusinessRule(ComplaintErrorCodes.AttachmentUnavailable, "Dosya başka bir şikâyete bağlı.");
            }
        }
    }

    internal static async Task<Result> CommitMediaAsync(IMediaService media, IReadOnlyList<Guid> mediaIds, CancellationToken cancellationToken)
    {
        foreach (var id in mediaIds)
        {
            var result = await media.CommitAsync(new MediaFileId(id), cancellationToken);
            if (result.IsFailure)
            {
                return Error.BusinessRule(ComplaintErrorCodes.AttachmentUnavailable, "Eklenecek dosya bulunamadı veya silinmiş.");
            }
        }

        return Result.Success();
    }

    private static Error? Validate(SubmitFromIntakeRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        if (request.IntakeSessionId == Guid.Empty)
        {
            errors["intakeSessionId"] = ["Intake kimliği zorunludur."];
        }

        if (string.IsNullOrWhiteSpace(request.Description) || request.Description.Trim().Length > IComplaintIntake.MaxDescriptionLength)
        {
            errors["description"] = [$"Açıklama zorunludur ve en fazla {IComplaintIntake.MaxDescriptionLength} karakter olabilir."];
        }

        if (request.Latitude.HasValue != request.Longitude.HasValue)
        {
            errors["location"] = ["Koordinat çifti eksik."];
        }
        else if (request.Latitude is { } lat && (double.IsNaN(lat) || lat is < -90 or > 90))
        {
            errors["latitude"] = ["Enlem −90 ile 90 arasında olmalıdır."];
        }
        else if (request.Longitude is { } lon && (double.IsNaN(lon) || lon is < -180 or > 180))
        {
            errors["longitude"] = ["Boylam −180 ile 180 arasında olmalıdır."];
        }

        if (request.Latitude is null && string.IsNullOrWhiteSpace(request.AddressText))
        {
            errors["location"] = ["Koordinat veya adres metninden en az biri zorunludur."];
        }

        if (request.AddressText is { } address && address.Trim().Length > IComplaintIntake.MaxAddressLength)
        {
            errors["addressText"] = [$"Adres en fazla {IComplaintIntake.MaxAddressLength} karakter olabilir."];
        }

        if (!Enum.IsDefined(request.LocationSource))
        {
            errors["locationSource"] = ["Geçersiz konum kaynağı."];
        }

        if (request.MediaFileIds is null || request.MediaFileIds.Count > MaxAttachments || request.MediaFileIds.Contains(Guid.Empty))
        {
            errors["mediaFileIds"] = [$"En fazla {MaxAttachments} geçerli dosya eklenebilir."];
        }

        return errors.Count == 0 ? null : Error.Validation(ComplaintErrorCodes.ValidationFailed, "İstek doğrulanamadı.", errors);
    }

    private async Task<SubmittedComplaint?> FindExistingAsync(Guid intakeSessionId, CancellationToken cancellationToken) =>
        await db.Complaints.AsNoTracking()
            .Where(c => c.SourceIntakeSessionId == intakeSessionId)
            .Select(c => new SubmittedComplaint(c.Id, c.PublicReference, false))
            .SingleOrDefaultAsync(cancellationToken);

    // Referans değişince outbox'taki ComplaintSubmitted da güncel referansı taşımalı: eklenmiş (henüz yazılmamış) outbox
    // satırları atılır ve yeniden üretilir.
    private void ResetOutboxReference(Complaint complaint)
    {
        foreach (var entry in db.ChangeTracker.Entries().Where(e => e.State == EntityState.Added && e.Metadata.GetTableName() == "outbox_messages").ToList())
        {
            entry.State = EntityState.Detached;
        }

        db.AddIntegrationEventForSubmitted(complaint);
    }
}

internal static class SubmittedEventExtensions
{
    public static void AddIntegrationEventForSubmitted(this ComplaintsDbContext db, Complaint complaint) =>
        Platform.Outbox.OutboxDbContextExtensions.AddIntegrationEvent(db, new ComplaintSubmitted(
            Guid.CreateVersion7(), complaint.SubmittedAt, complaint.Id, complaint.PublicReference, complaint.ReporterUserId));
}
