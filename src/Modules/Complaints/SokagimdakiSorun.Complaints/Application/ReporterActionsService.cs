using Microsoft.EntityFrameworkCore;
using SokagimdakiSorun.Complaints.Contracts;
using SokagimdakiSorun.Complaints.Domain;
using SokagimdakiSorun.Complaints.Infrastructure;
using SokagimdakiSorun.Media.Contracts;
using SokagimdakiSorun.SharedKernel;

namespace SokagimdakiSorun.Complaints.Application;

/// <summary>
/// <see cref="IComplaintReporterActions"/>: şikâyetçi adına (WhatsApp) #11/#23 ve #12. Idempotency anahtarı
/// <c>SourceMessageId</c>'dir ve geçmiş satırında kısmi UNIQUE olarak saklanır; tekrar çağrı aynı sonucu döndürür.
/// Şikâyetin sahibi olmayan şikâyetçi için şikâyet yokmuş gibi davranılır (<c>complaint.not_found</c>).
/// </summary>
internal sealed class ReporterActionsService(ComplaintsDbContext db, IMediaService media, TimeProvider time) : IComplaintReporterActions
{
    private const int MaxAttachments = 10;

    public async Task<Result<ReporterActionResult>> ProvideInformationAsync(ProvideInformationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var text = string.IsNullOrWhiteSpace(request.Text) ? null : request.Text.Trim();
        var mediaIds = (request.MediaFileIds ?? []).Distinct().ToList();
        if ((text is null && mediaIds.Count == 0) || text?.Length > ComplaintComment.MaxBodyLength
            || mediaIds.Count > MaxAttachments || mediaIds.Contains(Guid.Empty) || request.SourceMessageId == Guid.Empty)
        {
            return Error.Validation(ComplaintErrorCodes.ValidationFailed, "İstek doğrulanamadı.", new Dictionary<string, string[]>
            {
                ["text"] = [$"Metin (en fazla {ComplaintComment.MaxBodyLength} karakter) veya en az bir dosya zorunludur; mesaj kimliği zorunludur."],
            });
        }

        return await ExecuteAsync(request.ComplaintId, request.ReporterUserId, request.SourceMessageId, async (complaint, actor, now) =>
        {
            var result = complaint.ProvideInformation(actor, request.SourceMessageId, now);
            if (result.IsFailure)
            {
                return result;
            }

            if (text is not null)
            {
                complaint.AddComment(request.ReporterUserId, CommentAuthorKind.Reporter, text, CommentVisibility.ReporterVisible, CommentSource.WhatsApp, now);
            }

            foreach (var mediaId in mediaIds)
            {
                complaint.AddAttachment(mediaId, AttachmentSource.InformationReply, request.ReporterUserId, now);
            }

            // Geçiş bellekte geçerli bulunduktan sonra, kayıttan önce (bkz. ComplaintIntakeService).
            return await ComplaintIntakeService.CommitMediaAsync(media, mediaIds, cancellationToken);
        }, cancellationToken);
    }

    public async Task<Result<ReporterActionResult>> WithdrawAsync(WithdrawRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.SourceMessageId == Guid.Empty || request.Reason?.Length > ComplaintStatusHistoryEntry.MaxTextLength)
        {
            return Error.Validation(ComplaintErrorCodes.ValidationFailed, "İstek doğrulanamadı.", new Dictionary<string, string[]>
            {
                ["reason"] = [$"Gerekçe en fazla {ComplaintStatusHistoryEntry.MaxTextLength} karakter olabilir; mesaj kimliği zorunludur."],
            });
        }

        return await ExecuteAsync(request.ComplaintId, request.ReporterUserId, request.SourceMessageId,
            (complaint, actor, now) => Task.FromResult(complaint.Withdraw(actor, request.Reason, request.SourceMessageId, now)),
            cancellationToken);
    }

    private async Task<Result<ReporterActionResult>> ExecuteAsync(
        Guid complaintId, Guid reporterUserId, Guid sourceMessageId,
        Func<Complaint, ComplaintActor, DateTimeOffset, Task<Result>> action, CancellationToken cancellationToken)
    {
        if (await FindAppliedAsync(complaintId, reporterUserId, sourceMessageId, cancellationToken) is { } applied)
        {
            return applied;
        }

        var complaint = await db.ForUpdate().SingleOrDefaultAsync(c => c.Id == complaintId, cancellationToken);
        if (complaint is null || complaint.ReporterUserId != reporterUserId)
        {
            return ComplaintPersistence.NotFound;
        }

        var result = await action(complaint, ComplaintActor.Reporter(reporterUserId), time.GetUtcNow());
        if (result.IsFailure)
        {
            return result.Error;
        }

        try
        {
            var saved = await ComplaintPersistence.SaveAsync(db, complaint, cancellationToken);
            if (saved.IsFailure)
            {
                return saved.Error;
            }
        }
        catch (DbUpdateException ex) when (ComplaintPersistence.IsUniqueViolation(ex, ComplaintsDbContext.HistorySourceMessageUniqueIndex))
        {
            db.ChangeTracker.Clear();
            return await FindAppliedAsync(complaintId, reporterUserId, sourceMessageId, cancellationToken)
                ?? Result.Failure<ReporterActionResult>(ComplaintPersistence.ConcurrencyConflict);
        }

        return new ReporterActionResult(complaint.Id, complaint.PublicReference, complaint.Status, AlreadyApplied: false);
    }

    private async Task<Result<ReporterActionResult>?> FindAppliedAsync(
        Guid complaintId, Guid reporterUserId, Guid sourceMessageId, CancellationToken cancellationToken)
    {
        var applied = await db.StatusHistory.AsNoTracking()
            .Where(h => h.SourceMessageId == sourceMessageId)
            .Join(db.Complaints, h => h.ComplaintId, c => c.Id, (h, c) => new { c.Id, c.ReporterUserId, c.PublicReference, c.Status })
            .SingleOrDefaultAsync(cancellationToken);
        if (applied is null)
        {
            return null;
        }

        // Anahtar başka bir şikâyette veya başka bir şikâyetçide kullanıldıysa bilgi sızdırılmaz.
        return applied.Id == complaintId && applied.ReporterUserId == reporterUserId
            ? Result.Success(new ReporterActionResult(applied.Id, applied.PublicReference, applied.Status, AlreadyApplied: true))
            : Result.Failure<ReporterActionResult>(ComplaintPersistence.NotFound);
    }
}
