using Microsoft.EntityFrameworkCore;
using SokagimdakiSorun.Complaints.Contracts;
using SokagimdakiSorun.Complaints.Infrastructure;
using SokagimdakiSorun.SharedKernel;

namespace SokagimdakiSorun.Complaints.Application;

internal sealed class ComplaintCatalog(ComplaintsDbContext db) : IComplaintCatalog
{
    public async Task<IReadOnlyList<ComplaintCategorySummary>> GetActiveCategoriesAsync(CancellationToken cancellationToken) =>
        await db.Categories.AsNoTracking()
            .Where(c => c.IsActive)
            .OrderBy(c => c.SortOrder).ThenBy(c => c.Name)
            .Select(c => new ComplaintCategorySummary(c.Id, c.Code, c.Name, c.Description, c.SortOrder))
            .ToListAsync(cancellationToken);
}

/// <summary>
/// Kapsam duyarlı sayımlar (<see cref="ComplaintScopeQuery"/>). Durum ve kategori sayımları veritabanında gruplanır; zaman
/// serisi, aralık 366 günle sınırlı olduğu için yalnızca zaman damgaları okunup bellekte (UTC veya istenen zaman diliminin) yerel gününe göre gruplanır.
/// </summary>
internal sealed class ComplaintStatistics(ComplaintsDbContext db) : IComplaintStatistics
{
    public const int MaxTimelineDays = 366;

    public async Task<IReadOnlyList<StatusCount>> CountByStatusAsync(StaffActor actor, ComplaintStatisticsFilter filter, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var counts = await Filtered(actor, filter)
            .GroupBy(c => c.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Status, x => x.Count, cancellationToken);
        return [.. Enum.GetValues<ComplaintStatus>().Select(s => new StatusCount(s, counts.GetValueOrDefault(s)))];
    }

    public async Task<IReadOnlyList<CategoryCount>> CountByCategoryAsync(StaffActor actor, ComplaintStatisticsFilter filter, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var counts = await Filtered(actor, filter)
            .GroupBy(c => c.CategoryId)
            .Select(g => new { CategoryId = g.Key, Count = g.Count() })
            .Join(db.Categories, x => x.CategoryId, c => c.Id, (x, c) => new { x.CategoryId, c.Code, c.Name, x.Count })
            .ToListAsync(cancellationToken);
        return [.. counts
            .OrderByDescending(x => x.Count).ThenBy(x => x.Name, StringComparer.Ordinal)
            .Select(x => new CategoryCount(x.CategoryId, x.Code, x.Name, x.Count))];
    }

    public Task<Result<IReadOnlyList<TimelinePoint>>> GetTimelineAsync(StaffActor actor, DateOnly from, DateOnly to, CancellationToken cancellationToken) =>
        GetTimelineAsync(actor, from, to, TimeZoneInfo.Utc, cancellationToken);

    public async Task<Result<IReadOnlyList<TimelinePoint>>> GetTimelineAsync(
        StaffActor actor, DateOnly from, DateOnly to, TimeZoneInfo timeZone, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(timeZone);
        if (to < from || to.DayNumber - from.DayNumber + 1 > MaxTimelineDays)
        {
            return Error.Validation(ComplaintErrorCodes.ValidationFailed, "İstek doğrulanamadı.", new Dictionary<string, string[]>
            {
                ["to"] = [$"Aralık geçersiz: bitiş başlangıçtan önce olamaz ve en fazla {MaxTimelineDays} gün olabilir."],
            });
        }

        var start = StartOfLocalDay(from, timeZone);
        var end = StartOfLocalDay(to.AddDays(1), timeZone);
        var visible = db.Complaints.AsNoTracking().VisibleTo(db, actor);

        var opened = await visible.Where(c => c.SubmittedAt >= start && c.SubmittedAt < end).Select(c => c.SubmittedAt).ToListAsync(cancellationToken);
        var closed = await visible.Where(c => c.ClosedAt >= start && c.ClosedAt < end).Select(c => c.ClosedAt!.Value).ToListAsync(cancellationToken);

        var openedByDay = opened.GroupBy(d => LocalDay(d, timeZone)).ToDictionary(g => g.Key, g => g.Count());
        var closedByDay = closed.GroupBy(d => LocalDay(d, timeZone)).ToDictionary(g => g.Key, g => g.Count());

        var points = new List<TimelinePoint>(to.DayNumber - from.DayNumber + 1);
        for (var day = from; day <= to; day = day.AddDays(1))
        {
            points.Add(new TimelinePoint(day, openedByDay.GetValueOrDefault(day), closedByDay.GetValueOrDefault(day)));
        }

        return Result.Success<IReadOnlyList<TimelinePoint>>(points);
    }

    /// <summary>Yerel günün ilk anı (UTC). Gece yarısı yaz saati boşluğuna düşüyorsa boşluğun bittiği an.</summary>
    private static DateTimeOffset StartOfLocalDay(DateOnly day, TimeZoneInfo timeZone)
    {
        var local = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        while (timeZone.IsInvalidTime(local))
        {
            local = local.AddMinutes(30);
        }

        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, timeZone), TimeSpan.Zero);
    }

    private static DateOnly LocalDay(DateTimeOffset instant, TimeZoneInfo timeZone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, timeZone).DateTime);

    private IQueryable<Domain.Complaint> Filtered(StaffActor actor, ComplaintStatisticsFilter filter)
    {
        var query = db.Complaints.AsNoTracking().VisibleTo(db, actor);
        if (filter.From is { } from)
        {
            query = query.Where(c => c.SubmittedAt >= from);
        }

        if (filter.To is { } to)
        {
            query = query.Where(c => c.SubmittedAt < to);
        }

        return query;
    }
}
