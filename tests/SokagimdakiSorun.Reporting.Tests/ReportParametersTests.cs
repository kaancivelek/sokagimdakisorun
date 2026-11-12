using System.Reflection;
using SokagimdakiSorun.Complaints.Contracts;
using SokagimdakiSorun.Reporting;
using SokagimdakiSorun.Reporting.Application;

namespace SokagimdakiSorun.Reporting.Tests;

/// <summary>Parametre doğrulama, Europe/Istanbul gün sınırı ve kovalama (saf mantık).</summary>
public sealed class ReportParametersTests
{
    private static readonly DateOnly Today = new(2026, 11, 12);

    [Fact]
    public void Timeline_defaults_to_30_days_ending_today_with_day_bucket()
    {
        var request = ReportParameters.ParseTimeline(null, null, null, Today);

        Assert.True(request.IsSuccess);
        Assert.Equal(new TimelineRequest(new DateOnly(2026, 10, 14), Today, TimelineBucket.Day), request.Value);
    }

    [Theory]
    [InlineData("week", TimelineBucket.Week)]
    [InlineData("Week", TimelineBucket.Week)]
    [InlineData("DAY", TimelineBucket.Day)]
    public void Bucket_is_case_insensitive(string bucket, TimelineBucket expected) =>
        Assert.Equal(expected, ReportParameters.ParseTimeline("2026-01-01", "2026-01-31", bucket, Today).Value.Bucket);

    [Theory]
    [InlineData("2026-02-01", "2026-01-31", null, "to")]       // ters aralık
    [InlineData("2025-01-01", "2026-01-02", null, "to")]       // 367 gün
    [InlineData("2026-01-01", "2026-01-31", "month", "bucket")] // bilinmeyen kova
    [InlineData("2026-01-01", "2026-01-31", "1", "bucket")]     // sayısal enum değeri kabul edilmez
    [InlineData("01.02.2026", null, null, "from")]              // biçim
    [InlineData("2026-02-30", null, null, "from")]              // olmayan gün
    [InlineData("1999-12-31", null, null, "from")]              // alt sınır
    [InlineData(null, "2100-01-01", null, "to")]                // üst sınır
    public void Invalid_timeline_parameters_fail_validation_on_the_field(string? from, string? to, string? bucket, string field)
    {
        var request = ReportParameters.ParseTimeline(from, to, bucket, Today);

        Assert.True(request.IsFailure);
        Assert.Equal("validation.failed", request.Error.Code);
        Assert.Contains(field, request.Error.ValidationErrors!.Keys);
    }

    [Fact]
    public void Timeline_accepts_exactly_366_days()
    {
        var request = ReportParameters.ParseTimeline("2024-01-01", "2024-12-31", null, Today); // artık yıl: 366 gün
        Assert.True(request.IsSuccess);
    }

    [Fact]
    public void Timeline_with_only_from_in_the_future_ends_on_from()
    {
        var request = ReportParameters.ParseTimeline("2026-12-01", null, null, Today);
        Assert.Equal(new DateOnly(2026, 12, 1), request.Value.To);
    }

    [Fact]
    public void Optional_range_allows_open_ends_and_rejects_reversed()
    {
        Assert.Equal(new OptionalDateRange(null, null), ReportParameters.ParseOptionalRange(null, null).Value);
        Assert.Equal(new OptionalDateRange(new DateOnly(2026, 1, 1), null), ReportParameters.ParseOptionalRange("2026-01-01", null).Value);
        Assert.True(ReportParameters.ParseOptionalRange("2026-01-01", "2026-01-01").IsSuccess);

        var reversed = ReportParameters.ParseOptionalRange("2026-01-02", "2026-01-01");
        Assert.True(reversed.IsFailure);
        Assert.Contains("to", reversed.Error.ValidationErrors!.Keys);
    }

    [Fact]
    public void Filter_uses_istanbul_day_boundaries()
    {
        var filter = ReportParameters.ToFilter(new OptionalDateRange(new DateOnly(2026, 3, 11), new DateOnly(2026, 3, 11)), ReportParameters.TimeZone);

        // Europe/Istanbul UTC+3: 11 Mart yerel günü = [10 Mart 21:00Z, 11 Mart 21:00Z).
        Assert.Equal(new DateTimeOffset(2026, 3, 10, 21, 0, 0, TimeSpan.Zero), filter.From);
        Assert.Equal(new DateTimeOffset(2026, 3, 11, 21, 0, 0, TimeSpan.Zero), filter.To);
        Assert.Equal(new DateOnly(2026, 3, 11), ReportParameters.LocalDay(new DateTimeOffset(2026, 3, 10, 22, 30, 0, TimeSpan.Zero), ReportParameters.TimeZone));
    }

    [Fact]
    public void Week_buckets_start_on_monday_and_are_clipped_to_the_range()
    {
        // 2026-11-05 perşembe … 2026-11-17 salı.
        var days = Enumerable.Range(0, 13).Select(i => new TimelinePoint(new DateOnly(2026, 11, 5).AddDays(i), 1, i % 2)).ToList();

        var weeks = ReportParameters.Bucket(days, TimelineBucket.Week);

        Assert.Equal(
            [
                new BucketCount(new DateOnly(2026, 11, 5), new DateOnly(2026, 11, 8), 4, 2),
                new BucketCount(new DateOnly(2026, 11, 9), new DateOnly(2026, 11, 15), 7, 3),
                new BucketCount(new DateOnly(2026, 11, 16), new DateOnly(2026, 11, 17), 2, 1),
            ],
            weeks);
        Assert.Equal(13, ReportParameters.Bucket(days, TimelineBucket.Day).Count);
        Assert.Empty(ReportParameters.Bucket([], TimelineBucket.Week));
    }

    [Fact]
    public void Reporting_does_not_reference_another_modules_implementation_or_ef_core()
    {
        // ADR-0002 / ARCHITECTURE §4: Reporting'in DbContext'i yoktur, başka modülün tablosuna erişmez; yalnızca *.Contracts tüketir.
        var assembly = typeof(ReportingModule).Assembly;
        var referenced = assembly.GetReferencedAssemblies().Select(a => a.Name!).ToList();
        string[] forbidden =
        [
            "SokagimdakiSorun.Complaints", "SokagimdakiSorun.Identity", "SokagimdakiSorun.Media", "SokagimdakiSorun.Messaging",
            "Microsoft.EntityFrameworkCore", "Npgsql",
        ];

        Assert.Empty(referenced.Intersect(forbidden, StringComparer.Ordinal));
        Assert.DoesNotContain(assembly.GetTypes(), t => t.BaseType?.FullName == "Microsoft.EntityFrameworkCore.DbContext"
            || t.GetCustomAttribute<System.ComponentModel.DataAnnotations.Schema.TableAttribute>() is not null);
    }
}
