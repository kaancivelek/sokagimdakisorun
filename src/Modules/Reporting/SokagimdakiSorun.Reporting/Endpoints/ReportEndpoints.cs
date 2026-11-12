using System.ComponentModel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using SokagimdakiSorun.Complaints.Contracts;
using SokagimdakiSorun.Identity.Contracts;
using SokagimdakiSorun.Reporting.Application;
using SokagimdakiSorun.SharedKernel;

namespace SokagimdakiSorun.Reporting.Endpoints;

/// <summary>Duruma göre sayı.</summary>
public sealed record ReportStatusCount(ComplaintStatus Status, int Count);

/// <summary>Özet: kapsamdaki şikâyetlerin duruma göre sayıları (12 durumun hepsi, 0 olanlar dâhil).</summary>
/// <param name="From">Başvuru tarihi alt sınırı (yerel gün, dâhil) veya <see langword="null"/>.</param>
/// <param name="To">Başvuru tarihi üst sınırı (yerel gün, dâhil) veya <see langword="null"/>.</param>
/// <param name="TimeZone">Günlerin zaman dilimi (<c>Europe/Istanbul</c>).</param>
/// <param name="Total">Toplam şikâyet.</param>
/// <param name="ByStatus">Durum sayıları, durum sırasıyla.</param>
public sealed record SummaryReport(DateOnly? From, DateOnly? To, string TimeZone, int Total, IReadOnlyList<ReportStatusCount> ByStatus);

/// <summary>Kategoriye göre sayı.</summary>
public sealed record CategoryReportItem(Guid CategoryId, string CategoryCode, string CategoryName, int Count);

/// <summary>Kategori dağılımı: en az bir şikâyeti olan kategoriler, sayıya göre azalan.</summary>
public sealed record CategoryReport(DateOnly? From, DateOnly? To, string TimeZone, int Total, IReadOnlyList<CategoryReportItem> Items);

/// <summary>Bir kovada açılan (submitted) ve kapanan (closed) şikâyet sayısı; [Start, End] yerel günleri iki uç dâhil.</summary>
public sealed record TimelineReportPoint(DateOnly Start, DateOnly End, int Opened, int Closed);

/// <summary>Zaman serisi: aralıktaki her kova (boş kovalar 0 ile).</summary>
public sealed record TimelineReport(
    DateOnly From, DateOnly To, TimelineBucket Bucket, string TimeZone, int TotalOpened, int TotalClosed, IReadOnlyList<TimelineReportPoint> Points);

/// <summary>
/// <c>/api/v1/reports</c> (API_CONTRACT_STRATEGY §8 Reporting). Panel kullanıcısı (Admin/Moderator) ister; sayılar kapsam
/// duyarlıdır: moderatör yalnızca görebildiği şikâyetleri (atanmamışlar + üyesi olduğu birimlere atananlar, OD-4), Admin
/// hepsini sayar. Kapsam kararı ve sayım Complaints'in <see cref="IComplaintStatistics"/> sözleşmesindedir; bu modül başka
/// modülün tablosuna erişmez. Tarihler <c>Europe/Istanbul</c> yerel günüdür (<c>yyyy-MM-dd</c>, iki uç dâhil); bilinmeyen
/// sorgu parametresi, hatalı tarih, ters veya 366 günden geniş aralık, bilinmeyen kova <c>400 validation.failed</c>.
/// </summary>
internal static class ReportEndpoints
{
    private const string FromDescription = "Başvuru tarihi alt sınırı: Europe/Istanbul yerel günü (yyyy-MM-dd), dâhil.";
    private const string ToDescription = "Başvuru tarihi üst sınırı: Europe/Istanbul yerel günü (yyyy-MM-dd), dâhil.";

    private static readonly HashSet<string> RangeKeys = new(StringComparer.OrdinalIgnoreCase) { "from", "to" };
    private static readonly HashSet<string> TimelineKeys = new(StringComparer.OrdinalIgnoreCase) { "from", "to", "bucket" };

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/reports")
            .WithTags("Reports")
            .RequireAuthorization(IdentityPolicies.Staff)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapGet("/summary", SummaryAsync)
            .WithName("GetReportSummary")
            .WithSummary("Kapsamdaki şikâyetlerin duruma göre sayıları (kapsamlı; isteğe bağlı başvuru tarihi aralığı).")
            .Produces<SummaryReport>();

        group.MapGet("/by-category", ByCategoryAsync)
            .WithName("GetReportByCategory")
            .WithSummary("Kapsamdaki şikâyetlerin kategori dağılımı (kapsamlı; isteğe bağlı başvuru tarihi aralığı).")
            .Produces<CategoryReport>();

        group.MapGet("/timeline", TimelineAsync)
            .WithName("GetReportTimeline")
            .WithSummary("Zaman içinde açılan/kapanan şikâyetler (kapsamlı). Varsayılan: bugünle biten 30 gün, günlük kova; en fazla 366 gün.")
            .Produces<TimelineReport>();
    }

    private static async Task<IResult> SummaryAsync(
        HttpContext context,
        ICurrentUser user,
        IComplaintStatistics statistics,
        CancellationToken ct,
        [Description(FromDescription)] string? from = null,
        [Description(ToDescription)] string? to = null)
    {
        if (Prepare(context, user, RangeKeys) is { } problem)
        {
            return problem;
        }

        var range = ReportParameters.ParseOptionalRange(from, to);
        if (range.IsFailure)
        {
            return Problem(range.Error);
        }

        var counts = await statistics.CountByStatusAsync(
            Actor(user), ReportParameters.ToFilter(range.Value, ReportParameters.TimeZone), ct);
        return TypedResults.Ok(new SummaryReport(
            range.Value.From, range.Value.To, ReportParameters.TimeZoneId, counts.Sum(c => c.Count),
            [.. counts.Select(c => new ReportStatusCount(c.Status, c.Count))]));
    }

    private static async Task<IResult> ByCategoryAsync(
        HttpContext context,
        ICurrentUser user,
        IComplaintStatistics statistics,
        CancellationToken ct,
        [Description(FromDescription)] string? from = null,
        [Description(ToDescription)] string? to = null)
    {
        if (Prepare(context, user, RangeKeys) is { } problem)
        {
            return problem;
        }

        var range = ReportParameters.ParseOptionalRange(from, to);
        if (range.IsFailure)
        {
            return Problem(range.Error);
        }

        var counts = await statistics.CountByCategoryAsync(
            Actor(user), ReportParameters.ToFilter(range.Value, ReportParameters.TimeZone), ct);
        return TypedResults.Ok(new CategoryReport(
            range.Value.From, range.Value.To, ReportParameters.TimeZoneId, counts.Sum(c => c.Count),
            [.. counts.Select(c => new CategoryReportItem(c.CategoryId, c.CategoryCode, c.CategoryName, c.Count))]));
    }

    private static async Task<IResult> TimelineAsync(
        HttpContext context,
        ICurrentUser user,
        IComplaintStatistics statistics,
        TimeProvider time,
        CancellationToken ct,
        [Description("Aralık başlangıcı: Europe/Istanbul yerel günü (yyyy-MM-dd), dâhil. Varsayılan: bitişle biten 30 gün.")] string? from = null,
        [Description("Aralık bitişi: Europe/Istanbul yerel günü (yyyy-MM-dd), dâhil. Varsayılan: bugün.")] string? to = null,
        [Description("Kova: Day (varsayılan) veya Week (pazartesi başlar; uçtaki kısmi haftalar aralığa kırpılır).")] string? bucket = null)
    {
        if (Prepare(context, user, TimelineKeys) is { } problem)
        {
            return problem;
        }

        var today = ReportParameters.LocalDay(time.GetUtcNow(), ReportParameters.TimeZone);
        var request = ReportParameters.ParseTimeline(from, to, bucket, today);
        if (request.IsFailure)
        {
            return Problem(request.Error);
        }

        var (start, end, size) = request.Value;
        var days = await statistics.GetTimelineAsync(Actor(user), start, end, ReportParameters.TimeZone, ct);
        if (days.IsFailure)
        {
            return Problem(days.Error);
        }

        var points = ReportParameters.Bucket(days.Value, size);
        return TypedResults.Ok(new TimelineReport(
            start, end, size, ReportParameters.TimeZoneId, points.Sum(p => p.Opened), points.Sum(p => p.Closed),
            [.. points.Select(p => new TimelineReportPoint(p.Start, p.End, p.Opened, p.Closed))]));
    }

    /// <summary>Bilinmeyen sorgu parametresi → 400; kimliği okunamayan kullanıcı → 401 (yetki politikası bunu zaten önler).</summary>
    private static ProblemHttpResult? Prepare(HttpContext context, ICurrentUser user, HashSet<string> allowedKeys)
    {
        if (user.UserId is null)
        {
            return Problem(Error.Unauthorized("auth.unauthenticated", "Kimlik doğrulanmadı."));
        }

        return context.Request.Query.Keys.FirstOrDefault(k => !allowedKeys.Contains(k)) is { } unknown
            ? Problem(Error.Validation(ReportParameters.ValidationFailed, "İstek doğrulanamadı.",
                new Dictionary<string, string[]> { [unknown] = ["Bilinmeyen parametre."] }))
            : null;
    }

    private static StaffActor Actor(ICurrentUser user) => new(user.UserId!.Value, user.IsInRole(IdentityRoles.Admin));

    /// <summary>
    /// <see cref="Error"/>'u Problem Details'e çevirir; <c>code</c> uzantısı kararlı kodu taşır, <c>type</c> ve <c>traceId</c>
    /// host'un merkezi normalizasyonunca eklenir (API_CONTRACT_STRATEGY §4).
    /// </summary>
    private static ProblemHttpResult Problem(Error error)
    {
        var status = error.Type switch
        {
            ErrorType.Validation => StatusCodes.Status400BadRequest,
            ErrorType.Unauthorized => StatusCodes.Status401Unauthorized,
            ErrorType.Forbidden => StatusCodes.Status403Forbidden,
            ErrorType.NotFound => StatusCodes.Status404NotFound,
            ErrorType.Conflict => StatusCodes.Status409Conflict,
            ErrorType.BusinessRule => StatusCodes.Status422UnprocessableEntity,
            _ => StatusCodes.Status500InternalServerError,
        };

        if (error.ValidationErrors is { Count: > 0 } errors)
        {
            var validation = new HttpValidationProblemDetails(errors.ToDictionary(e => e.Key, e => e.Value))
            {
                Status = status,
                Title = error.Message,
            };
            validation.Extensions["code"] = error.Code;
            return TypedResults.Problem(validation);
        }

        return TypedResults.Problem(
            statusCode: status,
            title: error.Message,
            extensions: new Dictionary<string, object?> { ["code"] = error.Code });
    }
}
