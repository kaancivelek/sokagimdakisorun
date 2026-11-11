using Microsoft.EntityFrameworkCore;
using SokagimdakiSorun.Complaints.Contracts;
using SokagimdakiSorun.Complaints.Domain;
using SokagimdakiSorun.Complaints.Endpoints;
using SokagimdakiSorun.Complaints.Infrastructure;
using SokagimdakiSorun.Identity.Contracts;

namespace SokagimdakiSorun.Complaints.Application;

/// <summary>Liste filtresi (API_CONTRACT_STRATEGY §3, §8). Sayfalama ve sıralama doğrulanmış olarak gelir.</summary>
internal sealed record ComplaintListFilter(
    IReadOnlyList<ComplaintStatus> Statuses,
    Guid? CategoryId,
    Guid? ServiceUnitId,
    Guid? AssigneeUserId,
    DateTimeOffset? SubmittedFrom,
    DateTimeOffset? SubmittedTo,
    string? Reference,
    string Sort,
    int Page,
    int PageSize);

/// <summary>Panel okuma sorguları. Her sorgu kapsam filtresinden geçer; kapsam dışı tekil kayıt <see langword="null"/> döner (→ 404).</summary>
internal sealed class ComplaintQueries(ComplaintsDbContext db, IUserDirectory users)
{
    public const int ExcerptLength = 140;

    public static readonly IReadOnlyList<string> SortFields = ["createdAt", "submittedAt", "updatedAt"];

    public async Task<ComplaintPage> ListAsync(StaffActor actor, ComplaintListFilter filter, CancellationToken ct)
    {
        var query = db.Complaints.AsNoTracking().VisibleTo(db, actor);
        if (filter.Statuses.Count > 0)
        {
            var statuses = filter.Statuses.ToArray();
            query = query.Where(c => statuses.Contains(c.Status));
        }

        if (filter.CategoryId is { } categoryId)
        {
            query = query.Where(c => c.CategoryId == categoryId);
        }

        if (filter.ServiceUnitId is { } unitId)
        {
            query = query.Where(c => db.Assignments.Any(a => a.ComplaintId == c.Id && a.UnassignedAt == null && a.ServiceUnitId == unitId));
        }

        if (filter.AssigneeUserId is { } assigneeId)
        {
            query = query.Where(c => db.Assignments.Any(a => a.ComplaintId == c.Id && a.UnassignedAt == null && a.AssigneeUserId == assigneeId));
        }

        if (filter.SubmittedFrom is { } from)
        {
            query = query.Where(c => c.SubmittedAt >= from);
        }

        if (filter.SubmittedTo is { } to)
        {
            query = query.Where(c => c.SubmittedAt < to);
        }

        if (!string.IsNullOrWhiteSpace(filter.Reference))
        {
            var reference = filter.Reference.Trim().ToUpperInvariant();
            query = query.Where(c => c.PublicReference == reference);
        }

        var total = await query.CountAsync(ct);
        query = filter.Sort switch
        {
            "createdAt" => query.OrderBy(c => c.CreatedAt).ThenBy(c => c.Id),
            "submittedAt" => query.OrderBy(c => c.SubmittedAt).ThenBy(c => c.Id),
            "-submittedAt" => query.OrderByDescending(c => c.SubmittedAt).ThenByDescending(c => c.Id),
            "updatedAt" => query.OrderBy(c => c.UpdatedAt).ThenBy(c => c.Id),
            "-updatedAt" => query.OrderByDescending(c => c.UpdatedAt).ThenByDescending(c => c.Id),
            _ => query.OrderByDescending(c => c.CreatedAt).ThenByDescending(c => c.Id),
        };

        var rows = await query
            .Skip((filter.Page - 1) * filter.PageSize).Take(filter.PageSize)
            .Select(c => new
            {
                c.Id, c.PublicReference, c.Status, c.CategoryId, c.Description, c.AddressText, c.SubmittedAt, c.UpdatedAt,
                Assignment = db.Assignments.Where(a => a.ComplaintId == c.Id && a.UnassignedAt == null)
                    .Select(a => new { a.ServiceUnitId, a.AssigneeUserId, a.AssignedAt }).FirstOrDefault(),
            })
            .ToListAsync(ct);

        var categories = await LoadCategoriesAsync(rows.Select(r => r.CategoryId), ct);
        var units = await LoadUnitsAsync(rows.Where(r => r.Assignment != null).Select(r => r.Assignment!.ServiceUnitId), ct);
        var names = await LoadUsersAsync(rows.Where(r => r.Assignment?.AssigneeUserId != null).Select(r => r.Assignment!.AssigneeUserId!.Value), ct);

        var items = rows.Select(r => new ComplaintListItem(
            r.Id, r.PublicReference, r.Status, categories[r.CategoryId], Excerpt(r.Description), r.AddressText, r.SubmittedAt, r.UpdatedAt,
            r.Assignment is null ? null : new AssignmentRef(units[r.Assignment.ServiceUnitId], ToRef(names, r.Assignment.AssigneeUserId), r.Assignment.AssignedAt)))
            .ToList();
        return new ComplaintPage(items, filter.Page, filter.PageSize, total);
    }

    /// <summary>
    /// Detay. <paramref name="ignoreScope"/> yalnızca komut yanıtı içindir: kullanıcı az önce işlem yaptığı (ve o anda görebildiği)
    /// kaydın son hâlini alır; kapsam dışına çıktıysa <c>allowedActions</c> boştur.
    /// </summary>
    public async Task<ComplaintDetail?> GetDetailAsync(Guid id, StaffActor actor, CancellationToken ct, bool ignoreScope = false)
    {
        var visible = await db.Complaints.AsNoTracking().Where(c => c.Id == id).VisibleTo(db, actor).AnyAsync(ct);
        if (!visible && !ignoreScope)
        {
            return null;
        }

        var complaint = await db.Complaints.AsNoTracking()
            .Include(c => c.Assignments.Where(a => a.UnassignedAt == null))
            .SingleOrDefaultAsync(c => c.Id == id, ct);
        if (complaint is null)
        {
            return null;
        }

        var category = (await LoadCategoriesAsync([complaint.CategoryId], ct))[complaint.CategoryId];
        var active = complaint.ActiveAssignment;
        var suggested = await db.RoutingRules.AsNoTracking()
            .Where(r => r.CategoryId == complaint.CategoryId && r.IsActive && r.District == null)
            .Join(db.ServiceUnits.Where(u => u.IsActive), r => r.ServiceUnitId, u => u.Id, (r, u) => new { r.Priority, u.Id, u.Code, u.Name })
            .OrderBy(x => x.Priority).ThenBy(x => x.Code)
            .Select(x => new ServiceUnitRef(x.Id, x.Code, x.Name))
            .FirstOrDefaultAsync(ct);

        ComplaintRef? duplicateOf = null;
        if (complaint.DuplicateOfComplaintId is { } targetId)
        {
            duplicateOf = await db.Complaints.AsNoTracking().Where(c => c.Id == targetId).VisibleTo(db, actor)
                .Select(c => new ComplaintRef(c.Id, c.PublicReference)).SingleOrDefaultAsync(ct);
        }

        var userIds = new List<Guid> { complaint.ReporterUserId };
        if (active?.AssigneeUserId is { } assigneeId)
        {
            userIds.Add(assigneeId);
        }

        var names = await LoadUsersAsync(userIds, ct);
        AssignmentRef? activeRef = null;
        if (active is not null)
        {
            var unit = (await LoadUnitsAsync([active.ServiceUnitId], ct))[active.ServiceUnitId];
            activeRef = new AssignmentRef(unit, ToRef(names, active.AssigneeUserId), active.AssignedAt);
        }

        return new ComplaintDetail(
            complaint.Id,
            complaint.PublicReference,
            complaint.Status,
            category,
            complaint.Description,
            new LocationDto(complaint.Latitude, complaint.Longitude, complaint.AddressText, complaint.LocationSource),
            ToRef(names, complaint.ReporterUserId)!,
            complaint.SourceChannel,
            duplicateOf,
            activeRef,
            suggested,
            complaint.SubmittedAt,
            complaint.ResolvedAt,
            complaint.ClosedAt,
            complaint.CreatedAt,
            complaint.UpdatedAt,
            ComplaintPersistence.FormatVersion(complaint.Version),
            visible ? ComplaintTransitionPolicy.AllowedStaffActions(complaint.Status, complaint.AwaitingInformationEntryStatus, actor.IsAdmin) : []);
    }

    public async Task<ComplaintHistory?> GetHistoryAsync(Guid id, StaffActor actor, CancellationToken ct)
    {
        if (!await IsVisibleAsync(id, actor, ct))
        {
            return null;
        }

        var history = await db.StatusHistory.AsNoTracking().Where(h => h.ComplaintId == id)
            .OrderBy(h => h.OccurredAt).ThenBy(h => h.Id).ToListAsync(ct);
        var assignments = await db.Assignments.AsNoTracking().Where(a => a.ComplaintId == id)
            .OrderBy(a => a.AssignedAt).ThenBy(a => a.Id).ToListAsync(ct);

        var names = await LoadUsersAsync(
            history.Where(h => h.ActorUserId != null).Select(h => h.ActorUserId!.Value)
                .Concat(assignments.SelectMany(a => new[] { a.AssigneeUserId, a.AssignedByUserId, a.UnassignedByUserId }).OfType<Guid>()),
            ct);
        var units = await LoadUnitsAsync(assignments.Select(a => a.ServiceUnitId), ct);

        return new ComplaintHistory(
            [.. history.Select(h => new StatusHistoryItem(
                h.Id, h.FromStatus, h.ToStatus, h.ActorKind, ToRef(names, h.ActorUserId), h.Source, h.Reason, h.ReporterNote, h.OccurredAt))],
            [.. assignments.Select(a => new AssignmentHistoryItem(
                a.Id, units[a.ServiceUnitId], ToRef(names, a.AssigneeUserId), ToRef(names, a.AssignedByUserId)!, a.AssignedAt, a.Reason,
                a.UnassignedAt, ToRef(names, a.UnassignedByUserId), a.UnassignReason))]);
    }

    public async Task<IReadOnlyList<CommentItem>?> GetCommentsAsync(Guid id, StaffActor actor, CancellationToken ct)
    {
        if (!await IsVisibleAsync(id, actor, ct))
        {
            return null;
        }

        var comments = await db.Comments.AsNoTracking().Where(c => c.ComplaintId == id)
            .OrderBy(c => c.CreatedAt).ThenBy(c => c.Id).ToListAsync(ct);
        var names = await LoadUsersAsync(comments.Select(c => c.AuthorUserId), ct);
        return [.. comments.Select(c => new CommentItem(
            c.Id, ToRef(names, c.AuthorUserId)!, c.AuthorKind, c.Body, c.Visibility, c.Source, c.CreatedAt))];
    }

    public async Task<IReadOnlyList<AttachmentItem>?> GetAttachmentsAsync(Guid id, StaffActor actor, CancellationToken ct)
    {
        if (!await IsVisibleAsync(id, actor, ct))
        {
            return null;
        }

        var attachments = await db.Attachments.AsNoTracking().Where(a => a.ComplaintId == id)
            .OrderBy(a => a.CreatedAt).ThenBy(a => a.Id).ToListAsync(ct);
        var names = await LoadUsersAsync(attachments.Select(a => a.AddedByUserId), ct);
        return [.. attachments.Select(a => new AttachmentItem(
            a.Id, a.Source, ToRef(names, a.AddedByUserId)!, a.CreatedAt, $"/api/v1/complaints/{id}/attachments/{a.Id}/content"))];
    }

    /// <summary>Ek içeriği için: kapsam kontrolünden sonra ekin <c>media_file_id</c>'si; yoksa <see langword="null"/>.</summary>
    public async Task<Guid?> FindAttachmentMediaAsync(Guid id, Guid attachmentId, StaffActor actor, CancellationToken ct)
    {
        if (!await IsVisibleAsync(id, actor, ct))
        {
            return null;
        }

        return await db.Attachments.AsNoTracking()
            .Where(a => a.Id == attachmentId && a.ComplaintId == id)
            .Select(a => (Guid?)a.MediaFileId)
            .SingleOrDefaultAsync(ct);
    }

    public Task<bool> IsVisibleAsync(Guid id, StaffActor actor, CancellationToken ct) =>
        db.Complaints.AsNoTracking().Where(c => c.Id == id).VisibleTo(db, actor).AnyAsync(ct);

    internal async Task<IReadOnlyDictionary<Guid, UserSummary>> LoadUsersAsync(IEnumerable<Guid> ids, CancellationToken ct)
    {
        var distinct = ids.Distinct().ToList();
        return distinct.Count == 0 ? new Dictionary<Guid, UserSummary>() : await users.GetUsersAsync(distinct, ct);
    }

    internal static UserRef? ToRef(IReadOnlyDictionary<Guid, UserSummary> names, Guid? id) =>
        id is { } value ? new UserRef(value, names.TryGetValue(value, out var user) ? user.DisplayName : "Bilinmeyen kullanıcı") : null;

    private static string Excerpt(string description) =>
        description.Length <= ExcerptLength ? description : string.Concat(description.AsSpan(0, ExcerptLength), "…");

    private async Task<Dictionary<Guid, CategoryRef>> LoadCategoriesAsync(IEnumerable<Guid> ids, CancellationToken ct)
    {
        var distinct = ids.Distinct().ToArray();
        return await db.Categories.AsNoTracking().Where(c => distinct.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => new CategoryRef(c.Id, c.Code, c.Name), ct);
    }

    private async Task<Dictionary<Guid, ServiceUnitRef>> LoadUnitsAsync(IEnumerable<Guid> ids, CancellationToken ct)
    {
        var distinct = ids.Distinct().ToArray();
        return await db.ServiceUnits.AsNoTracking().Where(u => distinct.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => new ServiceUnitRef(u.Id, u.Code, u.Name), ct);
    }
}
