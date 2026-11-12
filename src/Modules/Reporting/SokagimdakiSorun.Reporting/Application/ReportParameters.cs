using System.Globalization;
using System.Text.Json.Serialization;
using SokagimdakiSorun.Complaints.Contracts;
using SokagimdakiSorun.SharedKernel;

namespace SokagimdakiSorun.Reporting.Application;

/// <summary>Zaman serisi kovası.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<TimelineBucket>))]
public enum TimelineBucket
{
    /// <summary>Yerel gün.</summary>
    Day,

    /// <summary>Pazartesi–pazar yerel haftası; aralığın başındaki ve sonundaki kısmi haftalar aralığa kırpılır.</summary>
    Week,
}

/// <summary>İsteğe bağlı yerel gün aralığı [From, To] (iki uç dâhil). <see langword="null"/> sınırsız.</summary>
internal sealed record OptionalDateRange(DateOnly? From, DateOnly? To);

/// <summary>Doğrulanmış zaman serisi isteği.</summary>
internal sealed record TimelineRequest(DateOnly From, DateOnly To, TimelineBucket Bucket);

/// <summary>Bir kovanın sayıları; [Start, End] yerel günleri iki uç dâhil.</summary>
internal sealed record BucketCount(DateOnly Start, DateOnly End, int Opened, int Closed);

/// <summary>
/// Rapor parametrelerinin ayrıştırılması ve doğrulanması (saf, bağımlılıksız). Tarihler <c>Europe/Istanbul</c> yerel günüdür
/// (<c>yyyy-MM-dd</c>), aralıklar iki uç dâhildir. Hatalar tek bir <c>400 validation.failed</c> içinde alan bazında toplanır.
/// </summary>
internal static class ReportParameters
{
    /// <summary>Rapor günlerinin zaman dilimi (panel tarihleri yerel gündür).</summary>
    public const string TimeZoneId = "Europe/Istanbul";

    /// <summary>Zaman serisinin azami uzunluğu (gün); Complaints sözleşmesinin sınırıyla aynıdır.</summary>
    public const int MaxTimelineDays = 366;

    /// <summary>Zaman serisinde başlangıç verilmezse bitişle biten varsayılan uzunluk (gün).</summary>
    public const int DefaultTimelineDays = 30;

    /// <summary>Doğrulama hatası kodu (API_CONTRACT_STRATEGY §4).</summary>
    public const string ValidationFailed = "validation.failed";

    /// <summary>Kabul edilen en erken tarih.</summary>
    public static readonly DateOnly MinDate = new(2000, 1, 1);

    /// <summary>Kabul edilen en geç tarih.</summary>
    public static readonly DateOnly MaxDate = new(2099, 12, 31);

    /// <summary>
    /// <see cref="TimeZoneId"/> zaman dilimi. Depo <c>InvariantGlobalization</c> kullandığı için Windows'ta IANA kimliği
    /// çözülemez; sırasıyla IANA kimliği, Windows kimliği (<c>Turkey Standard Time</c>) ve son çare olarak sabit UTC+3 denenir
    /// (Türkiye 2016'dan beri yaz saati uygulamaz; tzdata'sız Linux imajında da çalışır).
    /// </summary>
    public static TimeZoneInfo TimeZone { get; } = ResolveTimeZone();

    /// <summary>Özet ve kategori raporlarının aralığı: iki uç da isteğe bağlıdır; ikisi verilirse bitiş başlangıçtan önce olamaz.</summary>
    public static Result<OptionalDateRange> ParseOptionalRange(string? from, string? to)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var fromDate = ParseDate(from, "from", errors);
        var toDate = ParseDate(to, "to", errors);
        if (errors.Count == 0 && fromDate is { } start && toDate is { } end && end < start)
        {
            errors["to"] = ["Bitiş tarihi başlangıç tarihinden önce olamaz."];
        }

        return errors.Count > 0 ? Invalid<OptionalDateRange>(errors) : new OptionalDateRange(fromDate, toDate);
    }

    /// <summary>
    /// Zaman serisi aralığı ve kovası. Bitiş verilmezse <paramref name="today"/> (başlangıç bugünden sonraysa başlangıç),
    /// başlangıç verilmezse bitişle biten <see cref="DefaultTimelineDays"/> gün. Aralık en fazla <see cref="MaxTimelineDays"/>
    /// gün; kova <c>Day</c> (varsayılan) veya <c>Week</c>, büyük/küçük harf duyarsız.
    /// </summary>
    public static Result<TimelineRequest> ParseTimeline(string? from, string? to, string? bucket, DateOnly today)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var fromDate = ParseDate(from, "from", errors);
        var toDate = ParseDate(to, "to", errors);
        var bucketValue = ParseBucket(bucket, errors);
        if (errors.Count > 0)
        {
            return Invalid<TimelineRequest>(errors);
        }

        var end = toDate ?? (fromDate is { } explicitStart && explicitStart > today ? explicitStart : today);
        var start = fromDate ?? Later(end.AddDays(-(DefaultTimelineDays - 1)), MinDate);
        if (end < start)
        {
            errors["to"] = ["Bitiş tarihi başlangıç tarihinden önce olamaz."];
        }
        else if (end.DayNumber - start.DayNumber + 1 > MaxTimelineDays)
        {
            errors["to"] = [$"Aralık en fazla {MaxTimelineDays} gün olabilir."];
        }

        return errors.Count > 0 ? Invalid<TimelineRequest>(errors) : new TimelineRequest(start, end, bucketValue);
    }

    /// <summary>Yerel gün aralığını Complaints'in UTC [From, To) filtresine çevirir.</summary>
    public static ComplaintStatisticsFilter ToFilter(OptionalDateRange range, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(range);
        return new ComplaintStatisticsFilter(
            range.From is { } from ? StartOfLocalDay(from, timeZone) : null,
            range.To is { } to ? StartOfLocalDay(to.AddDays(1), timeZone) : null);
    }

    /// <summary>Yerel günün ilk anı (UTC). Gece yarısı yaz saati boşluğuna düşüyorsa boşluğun bittiği an.</summary>
    public static DateTimeOffset StartOfLocalDay(DateOnly day, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(timeZone);
        var local = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        while (timeZone.IsInvalidTime(local))
        {
            local = local.AddMinutes(30);
        }

        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, timeZone), TimeSpan.Zero);
    }

    /// <summary>Verilen anın yerel günü.</summary>
    public static DateOnly LocalDay(DateTimeOffset instant, TimeZoneInfo timeZone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, timeZone).DateTime);

    /// <summary>Günlük noktaları kovalara toplar. Haftalar pazartesi başlar; ilk ve son hafta aralığa kırpılır.</summary>
    public static IReadOnlyList<BucketCount> Bucket(IReadOnlyList<TimelinePoint> days, TimelineBucket bucket)
    {
        ArgumentNullException.ThrowIfNull(days);
        if (bucket == TimelineBucket.Day)
        {
            return [.. days.OrderBy(d => d.Date).Select(d => new BucketCount(d.Date, d.Date, d.Opened, d.Closed))];
        }

        return [.. days
            .GroupBy(d => d.Date.AddDays(-(((int)d.Date.DayOfWeek + 6) % 7)))
            .OrderBy(g => g.Key)
            .Select(g => new BucketCount(g.Min(d => d.Date), g.Max(d => d.Date), g.Sum(d => d.Opened), g.Sum(d => d.Closed)))];
    }

    private static TimelineBucket ParseBucket(string? text, Dictionary<string, string[]> errors)
    {
        switch (text?.Trim().ToUpperInvariant())
        {
            case null or "DAY":
                return TimelineBucket.Day;
            case "WEEK":
                return TimelineBucket.Week;
            default:
                errors["bucket"] = ["Kova 'Day' veya 'Week' olmalıdır."];
                return TimelineBucket.Day;
        }
    }

    private static DateOnly? ParseDate(string? text, string field, Dictionary<string, string[]> errors)
    {
        if (text is null)
        {
            return null;
        }

        if (!DateOnly.TryParseExact(text.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            errors[field] = ["Tarih yyyy-MM-dd biçiminde olmalıdır (Europe/Istanbul yerel günü)."];
            return null;
        }

        if (date < MinDate || date > MaxDate)
        {
            errors[field] = [string.Create(CultureInfo.InvariantCulture, $"Tarih {MinDate:yyyy-MM-dd} ile {MaxDate:yyyy-MM-dd} arasında olmalıdır.")];
            return null;
        }

        return date;
    }

    private static TimeZoneInfo ResolveTimeZone()
    {
        foreach (var id in new[] { TimeZoneId, "Turkey Standard Time" })
        {
            if (TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone))
            {
                return zone;
            }
        }

        return TimeZoneInfo.CreateCustomTimeZone(TimeZoneId, TimeSpan.FromHours(3), TimeZoneId, TimeZoneId);
    }

    private static DateOnly Later(DateOnly a, DateOnly b) => a > b ? a : b;

    private static Result<T> Invalid<T>(Dictionary<string, string[]> errors) =>
        Result.Failure<T>(Error.Validation(ValidationFailed, "İstek doğrulanamadı.", errors));
}
