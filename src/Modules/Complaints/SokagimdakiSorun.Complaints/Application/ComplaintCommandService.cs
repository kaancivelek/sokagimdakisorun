using Microsoft.EntityFrameworkCore;
using SokagimdakiSorun.Complaints.Contracts;
using SokagimdakiSorun.Complaints.Domain;
using SokagimdakiSorun.Complaints.Infrastructure;
using SokagimdakiSorun.Identity.Contracts;
using SokagimdakiSorun.SharedKernel;

namespace SokagimdakiSorun.Complaints.Application;

/// <summary>
/// Panelden gelen durum komutlarının ortak akışı: kapsamlı yükleme (kapsam dışı → 404) → istemci <c>version</c>'ı ile
/// karşılaştırma (farklı → 409 <c>concurrency.conflict</c>) → aggregate komutu (politika) → tek transaction'da kayıt.
/// İstemcinin sürümü EF'in özgün değeri yapılır; böylece okuma ile yazma arasındaki eşzamanlı değişiklik de <c>xmin</c>
/// koşullu UPDATE'te yakalanır.
/// </summary>
internal sealed class ComplaintCommandService(ComplaintsDbContext db, IUserDirectory users, TimeProvider time)
{
    public const int MaxTextLength = ComplaintStatusHistoryEntry.MaxTextLength;

    public async Task<Result> ExecuteAsync(
        Guid complaintId,
        StaffActor actor,
        string? version,
        Func<Complaint, ComplaintActor, DateTimeOffset, Task<Result>> command,
        CancellationToken ct)
    {
        if (!ComplaintPersistence.TryParseVersion(version, out var expected))
        {
            return Validation("version", "Sürüm (version) zorunludur; şikâyet detayındaki değeri gönderin.");
        }

        var complaint = await db.ForUpdate().Where(c => c.Id == complaintId).VisibleTo(db, actor).SingleOrDefaultAsync(ct);
        if (complaint is null)
        {
            return ComplaintPersistence.NotFound;
        }

        if (complaint.Version != expected)
        {
            return ComplaintPersistence.ConcurrencyConflict;
        }

        db.Entry(complaint).Property(c => c.Version).OriginalValue = expected;

        var result = await command(complaint, ComplaintActor.Staff(actor.UserId, actor.IsAdmin), time.GetUtcNow());
        if (result.IsFailure)
        {
            return result;
        }

        return await ComplaintPersistence.SaveAsync(db, complaint, ct);
    }

    /// <summary>Tekrar hedefi: aktörün görebildiği şikâyet; görünmüyorsa <see langword="null"/> (→ 422, varlık sızdırılmaz).</summary>
    public Task<Complaint?> FindDuplicateTargetAsync(Guid targetId, StaffActor actor, CancellationToken ct) =>
        db.Complaints.AsNoTracking().Where(c => c.Id == targetId).VisibleTo(db, actor).SingleOrDefaultAsync(ct);

    /// <summary>Atama hedefi: etkin birim; kişi verildiyse birimin üyesi ve etkin panel kullanıcısı.</summary>
    public async Task<Result> ValidateAssignmentTargetAsync(Guid serviceUnitId, Guid? assigneeUserId, CancellationToken ct)
    {
        if (!await db.ServiceUnits.AnyAsync(u => u.Id == serviceUnitId && u.IsActive, ct))
        {
            return Error.BusinessRule(ComplaintErrorCodes.ServiceUnitInvalid, "Hizmet birimi bulunamadı veya etkin değil.");
        }

        if (assigneeUserId is { } assignee)
        {
            var isMember = await db.ServiceUnitMembers.AnyAsync(m => m.ServiceUnitId == serviceUnitId && m.UserId == assignee, ct);
            if (!isMember || await users.GetUserAsync(assignee, ct) is not { Kind: UserKind.Staff, Status: UserStatus.Active })
            {
                return Error.BusinessRule(ComplaintErrorCodes.AssigneeNotMember, "Atanan kişi bu hizmet biriminin etkin üyesi değil.");
            }
        }

        return Result.Success();
    }

    /// <summary>Panel yorumu. Durum değiştirmez; kapsam kontrolü yapılır.</summary>
    public async Task<Result<Guid>> AddCommentAsync(Guid complaintId, StaffActor actor, string body, CommentVisibility visibility, CancellationToken ct)
    {
        var complaint = await db.Complaints.Where(c => c.Id == complaintId).VisibleTo(db, actor).SingleOrDefaultAsync(ct);
        if (complaint is null)
        {
            return ComplaintPersistence.NotFound;
        }

        complaint.AddComment(actor.UserId, CommentAuthorKind.Staff, body, visibility, CommentSource.Panel, time.GetUtcNow());
        var added = complaint.Comments.Last();
        await db.SaveChangesAsync(ct);
        return added.Id;
    }

    public static Error Validation(string field, string message) =>
        Error.Validation(ComplaintErrorCodes.ValidationFailed, "İstek doğrulanamadı.", new Dictionary<string, string[]> { [field] = [message] });
}
