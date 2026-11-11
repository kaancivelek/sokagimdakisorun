using Microsoft.EntityFrameworkCore;
using Npgsql;
using SokagimdakiSorun.Complaints.Contracts;
using SokagimdakiSorun.Complaints.Domain;
using SokagimdakiSorun.Complaints.Infrastructure;
using SokagimdakiSorun.Platform.Outbox;
using SokagimdakiSorun.SharedKernel;

namespace SokagimdakiSorun.Complaints.Application;

/// <summary>Aggregate yükleme ve "durum + geçmiş + outbox tek transaction" kaydı.</summary>
internal static class ComplaintPersistence
{
    public static readonly Error ConcurrencyConflict = Error.Conflict(
        ComplaintErrorCodes.ConcurrencyConflict, "Şikâyet siz işlem yaparken değişti; güncel hâlini yükleyip yeniden deneyin.");

    public static readonly Error NotFound = Error.NotFound(ComplaintErrorCodes.NotFound, "Şikâyet bulunamadı.");

    /// <summary>Komut için izlenen aggregate; yalnızca aktif atama yüklenir.</summary>
    public static IQueryable<Complaint> ForUpdate(this ComplaintsDbContext db) =>
        db.Complaints.Include(c => c.Assignments.Where(a => a.UnassignedAt == null));

    /// <summary>
    /// Aggregate'in bekleyen geçişlerini outbox event'ine çevirir ve tek <c>SaveChangesAsync</c> (tek transaction) ile kaydeder:
    /// şikâyet satırı, yeni geçmiş/atama/yorum/ek satırları ve outbox satırları birlikte yazılır ya da hiçbiri yazılmaz.
    /// <c>xmin</c> uyuşmazlığı veya tek-aktif-atama ihlali (eşzamanlı komut) <see cref="ConcurrencyConflict"/> döner.
    /// </summary>
    public static async Task<Result> SaveAsync(ComplaintsDbContext db, Complaint complaint, CancellationToken cancellationToken)
    {
        WriteEvents(db, complaint);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return Result.Success();
        }
        catch (DbUpdateConcurrencyException)
        {
            return ConcurrencyConflict;
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex, ComplaintsDbContext.ActiveAssignmentUniqueIndex))
        {
            return ConcurrencyConflict;
        }
    }

    public static void WriteEvents(ComplaintsDbContext db, Complaint complaint)
    {
        foreach (var change in complaint.PendingChanges)
        {
            if (change.From is null)
            {
                db.AddIntegrationEvent(new ComplaintSubmitted(
                    Guid.CreateVersion7(), change.OccurredAt, complaint.Id, complaint.PublicReference, complaint.ReporterUserId));
            }
            else
            {
                db.AddIntegrationEvent(new ComplaintStatusChanged(
                    Guid.CreateVersion7(), change.OccurredAt, complaint.Id, complaint.PublicReference, complaint.ReporterUserId,
                    change.From.Value, change.To, change.ReporterNote));
            }
        }

        complaint.ClearPendingChanges();
    }

    public static bool IsUniqueViolation(DbUpdateException ex, string constraintName) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg
        && string.Equals(pg.ConstraintName, constraintName, StringComparison.Ordinal);

    /// <summary>İstemcinin <c>version</c> metni (opak; <c>xmin</c>).</summary>
    public static bool TryParseVersion(string? text, out uint version) =>
        uint.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out version);

    public static string FormatVersion(uint version) => version.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
