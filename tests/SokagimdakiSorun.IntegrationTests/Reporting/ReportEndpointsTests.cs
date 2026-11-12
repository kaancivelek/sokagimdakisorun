using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SokagimdakiSorun.Complaints.Contracts;
using SokagimdakiSorun.IntegrationTests.Complaints;
using SokagimdakiSorun.IntegrationTests.Database.Support;
using static SokagimdakiSorun.IntegrationTests.Complaints.ComplaintsTestHost;

namespace SokagimdakiSorun.IntegrationTests.Reporting;

/// <summary>
/// <c>/api/v1/reports/*</c> — gerçek <c>Program</c> + gerçek PostgreSQL 18. Sayılar Complaints'in kapsam kuralından (OD-4)
/// gelir; zaman serisi Europe/Istanbul günlerine göre kovalanır.
/// </summary>
[Collection(DatabaseCollection.Name)]
[Trait("Category", "Database")]
public sealed class ReportEndpointsTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Counts_are_scope_sensitive_for_moderators_and_complete_for_admin()
    {
        await using var host = await StartAsync(postgres);
        var (memberId, member) = await host.CreateModeratorAsync("uye");
        var (_, outsider) = await host.CreateModeratorAsync("disarida");
        var roads = await host.CreateCategoryAsync("yol-kaldirim");
        var lights = await host.CreateCategoryAsync("aydinlatma");
        var unit = await host.CreateUnitAsync("fen-isleri", memberId);
        var reporter = await host.CreateReporterAsync();

        await host.SubmitAsync(roads, reporter);                          // atanmamış: tüm moderatörler görür
        var assigned = await host.SubmitAsync(lights, reporter);          // birime atanmış: yalnızca üye görür
        await host.CommandOkAsync(host.AdminToken, assigned.ComplaintId, "start-review");
        await host.CommandOkAsync(host.AdminToken, assigned.ComplaintId, "accept");
        await host.CommandOkAsync(host.AdminToken, assigned.ComplaintId, "assign", new { serviceUnitId = unit });

        // Özet: kapsam dışı birime atanmış şikâyet dışarıdaki moderatörün sayılarına girmez.
        var outsiderSummary = await GetOkAsync(host, outsider, "/api/v1/reports/summary");
        Assert.Equal(1, outsiderSummary.GetProperty("total").GetInt32());
        Assert.Equal(1, StatusCount(outsiderSummary, ComplaintStatus.Submitted));
        Assert.Equal(0, StatusCount(outsiderSummary, ComplaintStatus.Assigned));
        Assert.Equal(12, outsiderSummary.GetProperty("byStatus").GetArrayLength());
        Assert.Equal("Europe/Istanbul", outsiderSummary.GetProperty("timeZone").GetString());

        var memberSummary = await GetOkAsync(host, member, "/api/v1/reports/summary");
        Assert.Equal(2, memberSummary.GetProperty("total").GetInt32());
        Assert.Equal(1, StatusCount(memberSummary, ComplaintStatus.Assigned));

        var adminSummary = await GetOkAsync(host, host.AdminToken, "/api/v1/reports/summary");
        Assert.Equal(2, adminSummary.GetProperty("total").GetInt32());

        // Kategori dağılımı: aynı kapsam.
        var outsiderCategories = await GetOkAsync(host, outsider, "/api/v1/reports/by-category");
        var only = Assert.Single(outsiderCategories.GetProperty("items").EnumerateArray());
        Assert.Equal("yol-kaldirim", only.GetProperty("categoryCode").GetString());
        Assert.Equal(2, (await GetOkAsync(host, member, "/api/v1/reports/by-category")).GetProperty("items").GetArrayLength());

        // Zaman serisi: bugün açılan şikâyetler kapsam kadar sayılır.
        Assert.Equal(1, (await GetOkAsync(host, outsider, "/api/v1/reports/timeline")).GetProperty("totalOpened").GetInt32());
        Assert.Equal(2, (await GetOkAsync(host, member, "/api/v1/reports/timeline")).GetProperty("totalOpened").GetInt32());
    }

    [Fact]
    public async Task Timeline_buckets_by_istanbul_day_and_range_filters_use_local_days()
    {
        await using var host = await StartAsync(postgres);
        var category = await host.CreateCategoryAsync();
        var reporter = await host.CreateReporterAsync();
        var lateEvening = await host.SubmitAsync(category, reporter);
        var closed = await host.SubmitAsync(category, reporter);

        // 10 Mart 22:30Z = 11 Mart 01:30 İstanbul; UTC gününe göre kovalansaydı 10 Mart'a düşerdi.
        await host.ExecuteAsync($"""
            UPDATE complaints.complaints SET submitted_at = '2026-03-10T22:30:00Z' WHERE id = '{lateEvening.ComplaintId}';
            UPDATE complaints.complaints SET submitted_at = '2026-03-09T09:00:00Z', status = 'Closed',
                closed_at = '2026-03-12T20:59:59Z' WHERE id = '{closed.ComplaintId}';
            """);

        var daily = await GetOkAsync(host, host.AdminToken, "/api/v1/reports/timeline?from=2026-03-09&to=2026-03-15");
        var points = daily.GetProperty("points").EnumerateArray().ToList();
        Assert.Equal(7, points.Count);
        Assert.Equal("Day", daily.GetProperty("bucket").GetString());
        Assert.Equal("2026-03-09", points[0].GetProperty("start").GetString());
        Assert.Equal(1, points[0].GetProperty("opened").GetInt32());                 // 9 Mart
        Assert.Equal(0, points[1].GetProperty("opened").GetInt32());                 // 10 Mart (UTC'de olurdu)
        Assert.Equal(1, points[2].GetProperty("opened").GetInt32());                 // 11 Mart (İstanbul)
        Assert.Equal(1, points[3].GetProperty("closed").GetInt32());                 // 12 Mart 23:59:59 İstanbul
        Assert.Equal(2, daily.GetProperty("totalOpened").GetInt32());
        Assert.Equal(1, daily.GetProperty("totalClosed").GetInt32());

        // Haftalık: 9 Mart 2026 pazartesi → tek kova.
        var weekly = await GetOkAsync(host, host.AdminToken, "/api/v1/reports/timeline?from=2026-03-09&to=2026-03-15&bucket=week");
        var week = Assert.Single(weekly.GetProperty("points").EnumerateArray());
        Assert.Equal("2026-03-15", week.GetProperty("end").GetString());
        Assert.Equal(2, week.GetProperty("opened").GetInt32());

        // Özet ve kategori aralığı da İstanbul günü: yalnızca 11 Mart.
        var summary = await GetOkAsync(host, host.AdminToken, "/api/v1/reports/summary?from=2026-03-11&to=2026-03-11");
        Assert.Equal(1, summary.GetProperty("total").GetInt32());
        Assert.Equal("2026-03-11", summary.GetProperty("from").GetString());
        var byCategory = await GetOkAsync(host, host.AdminToken, "/api/v1/reports/by-category?from=2026-03-10&to=2026-03-10");
        Assert.Equal(0, byCategory.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Empty_data_returns_zeros_and_empty_lists()
    {
        await using var host = await StartAsync(postgres);
        var (_, moderator) = await host.CreateModeratorAsync("bos");

        var summary = await GetOkAsync(host, moderator, "/api/v1/reports/summary");
        Assert.Equal(0, summary.GetProperty("total").GetInt32());
        Assert.Equal(12, summary.GetProperty("byStatus").GetArrayLength());
        Assert.All(summary.GetProperty("byStatus").EnumerateArray(), s => Assert.Equal(0, s.GetProperty("count").GetInt32()));
        Assert.Equal(JsonValueKind.Null, summary.GetProperty("from").ValueKind);

        var categories = await GetOkAsync(host, moderator, "/api/v1/reports/by-category");
        Assert.Equal(0, categories.GetProperty("total").GetInt32());
        Assert.Equal(0, categories.GetProperty("items").GetArrayLength());

        var timeline = await GetOkAsync(host, moderator, "/api/v1/reports/timeline?bucket=Week&from=2026-01-01&to=2026-01-31");
        Assert.Equal(0, timeline.GetProperty("totalOpened").GetInt32());
        Assert.All(timeline.GetProperty("points").EnumerateArray(), p => Assert.Equal(0, p.GetProperty("opened").GetInt32()));
        Assert.Equal(5, timeline.GetProperty("points").GetArrayLength()); // 1–4, 5–11, 12–18, 19–25, 26–31 Ocak

        Assert.Equal(30, (await GetOkAsync(host, moderator, "/api/v1/reports/timeline")).GetProperty("points").GetArrayLength());
    }

    [Theory]
    [InlineData("/api/v1/reports/timeline?from=2026-03-10&to=2026-03-01", "to")]
    [InlineData("/api/v1/reports/timeline?from=2024-01-01&to=2026-01-01", "to")]
    [InlineData("/api/v1/reports/timeline?bucket=month", "bucket")]
    [InlineData("/api/v1/reports/timeline?from=10.03.2026", "from")]
    [InlineData("/api/v1/reports/timeline?granularity=day", "granularity")]
    [InlineData("/api/v1/reports/summary?from=2026-03-10&to=2026-03-01", "to")]
    [InlineData("/api/v1/reports/summary?status=Closed", "status")]
    [InlineData("/api/v1/reports/by-category?to=yarin", "to")]
    public async Task Invalid_parameters_return_400_problem_details(string path, string field)
    {
        await using var host = await StartAsync(postgres);
        using var client = host.CreateClient(host.AdminToken);
        using var response = await client.GetAsync(path, Ct);

        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await ReadProblemAsync(response, HttpStatusCode.BadRequest, "validation.failed");
        Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _), problem.ToString());
        Assert.True(problem.TryGetProperty("traceId", out _));
    }

    [Theory]
    [InlineData("/api/v1/reports/summary")]
    [InlineData("/api/v1/reports/by-category")]
    [InlineData("/api/v1/reports/timeline")]
    public async Task Anonymous_request_gets_401(string path)
    {
        await using var host = await StartAsync(postgres);
        using var client = host.CreateClient();
        using var response = await client.GetAsync(path, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static int StatusCount(JsonElement summary, ComplaintStatus status) =>
        summary.GetProperty("byStatus").EnumerateArray()
            .Single(s => s.GetProperty("status").GetString() == status.ToString())
            .GetProperty("count").GetInt32();

    private static async Task<JsonElement> GetOkAsync(ComplaintsTestHost host, string token, string path)
    {
        using var client = host.CreateClient(token);
        using var response = await client.GetAsync(path, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{path}: {(int)response.StatusCode} {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }
}
