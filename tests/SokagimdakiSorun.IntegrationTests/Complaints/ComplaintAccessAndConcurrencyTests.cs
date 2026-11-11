using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SokagimdakiSorun.Complaints.Contracts;
using SokagimdakiSorun.IntegrationTests.Database.Support;
using static SokagimdakiSorun.IntegrationTests.Complaints.ComplaintsTestHost;

namespace SokagimdakiSorun.IntegrationTests.Complaints;

/// <summary>Kapsam (OD-4), iyimser eşzamanlılık (V-6) ve idempotency (I-1) — gerçek PostgreSQL 18.</summary>
[Collection(DatabaseCollection.Name)]
[Trait("Category", "Database")]
public sealed class ComplaintAccessAndConcurrencyTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Out_of_scope_moderator_cannot_list_or_read_and_closed_reopen_is_admin_only()
    {
        await using var host = await StartAsync(postgres);
        var (memberId, member) = await host.CreateModeratorAsync("uye");
        var (_, outsider) = await host.CreateModeratorAsync("disarida");
        var category = await host.CreateCategoryAsync();
        var unit = await host.CreateUnitAsync("temizlik", memberId);
        var reporter = await host.CreateReporterAsync();
        var pooled = await host.SubmitAsync(category, reporter);
        var assigned = await host.SubmitAsync(category, reporter);

        await host.CommandOkAsync(host.AdminToken, assigned.ComplaintId, "start-review");
        await host.CommandOkAsync(host.AdminToken, assigned.ComplaintId, "accept");
        await host.CommandOkAsync(host.AdminToken, assigned.ComplaintId, "assign", new { serviceUnitId = unit });

        // Liste: atanmamış herkese açık (OD-4); atanmış yalnızca birim üyesine.
        Assert.Equal([pooled.ComplaintId], await ListIdsAsync(host, outsider));
        Assert.Equal(new[] { assigned.ComplaintId, pooled.ComplaintId }.Order(), (await ListIdsAsync(host, member)).Order());
        Assert.Equal(2, (await ListIdsAsync(host, host.AdminToken)).Length);

        // Tekil kayıt ve alt kaynaklar: 404 (403 değil — varlık sızdırılmaz).
        using var client = host.CreateClient(outsider);
        foreach (var path in new[] { "", "/history", "/comments", "/attachments" })
        {
            using var response = await client.GetAsync($"/api/v1/complaints/{assigned.ComplaintId}{path}", Ct);
            await ReadProblemAsync(response, HttpStatusCode.NotFound, ComplaintErrorCodes.NotFound);
        }

        using (var command = await host.CommandWithVersionAsync(outsider, assigned.ComplaintId, "start-work", "1"))
        {
            await ReadProblemAsync(command, HttpStatusCode.NotFound, ComplaintErrorCodes.NotFound);
        }

        using (var comment = await client.PostAsJsonAsync($"/api/v1/complaints/{assigned.ComplaintId}/comments", new { body = "merhaba" }, Ct))
        {
            await ReadProblemAsync(comment, HttpStatusCode.NotFound, ComplaintErrorCodes.NotFound);
        }

        // Erişim politikası sözleşmesi aynı kararı verir.
        await using (var scope = host.CreateScope())
        {
            var policy = scope.ServiceProvider.GetRequiredService<IComplaintAccessPolicy>();
            Assert.True(await policy.CanViewAsync(new StaffActor(memberId, false), assigned.ComplaintId, Ct));
            Assert.False(await policy.CanViewAsync(new StaffActor(Guid.CreateVersion7(), false), assigned.ComplaintId, Ct));
            Assert.Equal([unit], (await policy.GetScopeAsync(new StaffActor(memberId, false), Ct)).MemberServiceUnitIds);

            var stats = scope.ServiceProvider.GetRequiredService<IComplaintStatistics>();
            var outsiderCounts = await stats.CountByStatusAsync(new StaffActor(Guid.CreateVersion7(), false), new ComplaintStatisticsFilter(null, null), Ct);
            Assert.Equal(1, outsiderCounts.Sum(c => c.Count));
            Assert.Equal(12, outsiderCounts.Count);
            var adminCounts = await stats.CountByStatusAsync(new StaffActor(host.AdminId, true), new ComplaintStatisticsFilter(null, null), Ct);
            Assert.Equal(1, adminCounts.Single(c => c.Status == ComplaintStatus.Assigned).Count);
        }

        // Closed → reopen yalnızca Admin (kapsamdaki moderatör de olsa).
        await host.CommandOkAsync(member, assigned.ComplaintId, "start-work");
        await host.CommandOkAsync(member, assigned.ComplaintId, "resolve", new { reason = "temizlendi" });
        await host.CommandOkAsync(member, assigned.ComplaintId, "close");
        using (var reopen = await host.CommandAsync(member, assigned.ComplaintId, "reopen", new { reason = "tekrar" }))
        {
            await ReadProblemAsync(reopen, HttpStatusCode.Forbidden, ComplaintErrorCodes.ActionForbidden);
        }

        Assert.Equal("UnderReview", (await host.CommandOkAsync(host.AdminToken, assigned.ComplaintId, "reopen", new { reason = "tekrar" }))
            .GetProperty("status").GetString());
    }

    [Fact]
    public async Task Stale_or_concurrent_update_gets_409_concurrency_conflict()
    {
        await using var host = await StartAsync(postgres);
        var (_, moderator) = await host.CreateModeratorAsync("es");
        var complaint = await host.SubmitAsync(await host.CreateCategoryAsync(), await host.CreateReporterAsync());
        var version = (await host.GetDetailAsync(moderator, complaint.ComplaintId)).GetProperty("version").GetString();

        using (var first = await host.CommandWithVersionAsync(moderator, complaint.ComplaintId, "start-review", version))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            var newVersion = (await first.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("version").GetString();
            Assert.NotEqual(version, newVersion);
        }

        using (var stale = await host.CommandWithVersionAsync(moderator, complaint.ComplaintId, "accept", version))
        {
            await ReadProblemAsync(stale, HttpStatusCode.Conflict, ComplaintErrorCodes.ConcurrencyConflict);
        }

        // Aynı sürümle eşzamanlı iki istek: biri kazanır, diğeri 409 (okuma ile yazma arasındaki yarış xmin koşulunda yakalanır).
        version = (await host.GetDetailAsync(moderator, complaint.ComplaintId)).GetProperty("version").GetString();
        for (var round = 0; round < 3; round++)
        {
            var responses = await Task.WhenAll(
                host.CommandWithVersionAsync(moderator, complaint.ComplaintId, "accept", version),
                host.CommandWithVersionAsync(host.AdminToken, complaint.ComplaintId, "mark-spam", version, new { reason = "spam" }));
            var codes = responses.Select(r => r.StatusCode).ToList();
            foreach (var response in responses)
            {
                response.Dispose();
            }

            if (round == 0)
            {
                Assert.Single(codes, HttpStatusCode.OK);
                Assert.Single(codes, HttpStatusCode.Conflict);
            }
            else
            {
                Assert.All(codes, c => Assert.Equal(HttpStatusCode.Conflict, c)); // sürüm artık eski
            }
        }

        Assert.Equal(3, await host.ScalarAsync(
            "SELECT count(*) FROM complaints.complaint_status_history WHERE complaint_id = @id", ("id", complaint.ComplaintId)));
    }

    [Fact]
    public async Task Same_intake_session_submitted_concurrently_creates_one_complaint()
    {
        await using var host = await StartAsync(postgres);
        var category = await host.CreateCategoryAsync();
        var reporter = await host.CreateReporterAsync();
        var intakeId = Guid.CreateVersion7();

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => host.SubmitAsync(category, reporter, intakeId)));
        var again = await host.SubmitAsync(category, reporter, intakeId);

        Assert.Single(results, r => r.Created);
        Assert.Single(results.Select(r => r.ComplaintId).Append(again.ComplaintId).Distinct());
        Assert.Single(results.Select(r => r.PublicReference).Append(again.PublicReference).Distinct());
        Assert.False(again.Created);
        Assert.Equal(1, await host.ScalarAsync("SELECT count(*) FROM complaints.complaints"));
        Assert.Equal(1, await host.ScalarAsync("SELECT count(*) FROM complaints.complaint_status_history"));
        Assert.Equal(1, await host.ScalarAsync("SELECT count(*) FROM platform.outbox_messages WHERE type = @t", ("t", typeof(ComplaintSubmitted).FullName!)));
    }

    [Fact]
    public async Task Reporter_information_reply_is_idempotent_and_returns_to_work_with_assignment()
    {
        await using var host = await StartAsync(postgres);
        var category = await host.CreateCategoryAsync();
        var unit = await host.CreateUnitAsync("park");
        var reporter = await host.CreateReporterAsync();
        var complaint = await host.SubmitAsync(category, reporter);
        foreach (var (action, body) in new (string, object?)[]
        {
            ("start-review", null), ("accept", null), ("assign", new { serviceUnitId = unit }), ("start-work", null),
            ("request-information", new { reason = "konum", reporterNote = "Konumu gönderir misiniz?" }),
        })
        {
            await host.CommandOkAsync(host.AdminToken, complaint.ComplaintId, action, body);
        }

        var messageId = Guid.CreateVersion7();
        await using var scope = host.CreateScope();
        var actions = scope.ServiceProvider.GetRequiredService<IComplaintReporterActions>();

        var stranger = await actions.ProvideInformationAsync(new ProvideInformationRequest(
            complaint.ComplaintId, Guid.CreateVersion7(), Guid.CreateVersion7(), "ben", []), Ct);
        Assert.Equal(ComplaintErrorCodes.NotFound, stranger.Error.Code);

        var first = await actions.ProvideInformationAsync(new ProvideInformationRequest(complaint.ComplaintId, reporter, messageId, "Parkın kuzey girişi.", []), Ct);
        var second = await actions.ProvideInformationAsync(new ProvideInformationRequest(complaint.ComplaintId, reporter, messageId, "Parkın kuzey girişi.", []), Ct);

        Assert.Equal(ComplaintStatus.InProgress, first.Value.Status);
        Assert.False(first.Value.AlreadyApplied);
        Assert.True(second.Value.AlreadyApplied);
        Assert.Equal(1, await host.ScalarAsync("SELECT count(*) FROM complaints.complaint_status_history WHERE source_message_id = @m", ("m", messageId)));
        Assert.Equal(1, await host.ScalarAsync("SELECT count(*) FROM complaints.complaint_comments WHERE complaint_id = @id", ("id", complaint.ComplaintId)));
        Assert.Equal(1, await host.ScalarAsync(
            "SELECT count(*) FROM complaints.complaint_assignments WHERE complaint_id = @id AND unassigned_at IS NULL", ("id", complaint.ComplaintId)));

        // Bilgi beklenmiyorken: yasak geçiş.
        var notAwaiting = await actions.WithdrawAsync(new WithdrawRequest(complaint.ComplaintId, reporter, Guid.CreateVersion7(), null), Ct);
        Assert.Equal(ComplaintErrorCodes.InvalidTransition, notAwaiting.Error.Code);
    }

    [Fact]
    public async Task List_rejects_unknown_filters_and_filters_by_status_and_reference()
    {
        await using var host = await StartAsync(postgres);
        var category = await host.CreateCategoryAsync();
        var reporter = await host.CreateReporterAsync();
        var a = await host.SubmitAsync(category, reporter);
        var b = await host.SubmitAsync(category, reporter);
        await host.CommandOkAsync(host.AdminToken, b.ComplaintId, "start-review");

        using var client = host.CreateClient(host.AdminToken);
        using (var unknown = await client.GetAsync("/api/v1/complaints?colour=red", Ct))
        {
            await ReadProblemAsync(unknown, HttpStatusCode.BadRequest, ComplaintErrorCodes.ValidationFailed);
        }

        using (var tooBig = await client.GetAsync("/api/v1/complaints?pageSize=101", Ct))
        {
            await ReadProblemAsync(tooBig, HttpStatusCode.BadRequest, ComplaintErrorCodes.ValidationFailed);
        }

        var underReview = await client.GetFromJsonAsync<JsonElement>("/api/v1/complaints?status=UnderReview", Ct);
        Assert.Equal(b.ComplaintId, Assert.Single(underReview.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());

        var byReference = await client.GetFromJsonAsync<JsonElement>($"/api/v1/complaints?reference={a.PublicReference.ToLowerInvariant()}", Ct);
        Assert.Equal(a.ComplaintId, Assert.Single(byReference.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());

        var page = await client.GetFromJsonAsync<JsonElement>("/api/v1/complaints?page=2&pageSize=1&sort=createdAt", Ct);
        Assert.Equal(2, page.GetProperty("totalCount").GetInt32());
        Assert.Equal(b.ComplaintId, Assert.Single(page.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
    }

    private static async Task<Guid[]> ListIdsAsync(ComplaintsTestHost host, string token)
    {
        using var client = host.CreateClient(token);
        var page = await client.GetFromJsonAsync<JsonElement>("/api/v1/complaints?sort=createdAt", Ct);
        return [.. page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid())];
    }
}
