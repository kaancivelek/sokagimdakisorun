using Microsoft.EntityFrameworkCore;
using SokagimdakiSorun.Complaints.Contracts;
using SokagimdakiSorun.Complaints.Domain;
using SokagimdakiSorun.Complaints.Infrastructure;

namespace SokagimdakiSorun.Complaints.Application;

/// <summary>
/// Kapsam kuralının (ARCHITECTURE §6, OD-4) tek uygulaması: Admin her şikâyeti görür; moderatör aktif ataması olmayan
/// (triage havuzu) şikâyetleri ve aktif ataması üyesi olduğu hizmet birimindeki şikâyetleri görür. Liste filtresi,
/// tekil erişim, istatistikler ve <see cref="IComplaintAccessPolicy"/> hepsi bu sorguyu kullanır.
/// </summary>
internal static class ComplaintScopeQuery
{
    public static IQueryable<Complaint> VisibleTo(this IQueryable<Complaint> query, ComplaintsDbContext db, StaffActor actor)
    {
        if (actor.IsAdmin)
        {
            return query;
        }

        var userId = actor.UserId;
        return query.Where(c =>
            !db.Assignments.Any(a => a.ComplaintId == c.Id && a.UnassignedAt == null)
            || db.Assignments.Any(a => a.ComplaintId == c.Id && a.UnassignedAt == null
                && db.ServiceUnitMembers.Any(m => m.ServiceUnitId == a.ServiceUnitId && m.UserId == userId)));
    }
}

internal sealed class ComplaintAccessPolicy(ComplaintsDbContext db) : IComplaintAccessPolicy
{
    public Task<bool> CanViewAsync(StaffActor actor, Guid complaintId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return db.Complaints.AsNoTracking().Where(c => c.Id == complaintId).VisibleTo(db, actor).AnyAsync(cancellationToken);
    }

    public async Task<ComplaintScope> GetScopeAsync(StaffActor actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (actor.IsAdmin)
        {
            return new ComplaintScope(true, []);
        }

        var units = await db.ServiceUnitMembers.AsNoTracking()
            .Where(m => m.UserId == actor.UserId)
            .Select(m => m.ServiceUnitId)
            .OrderBy(id => id)
            .ToListAsync(cancellationToken);
        return new ComplaintScope(false, units);
    }
}
