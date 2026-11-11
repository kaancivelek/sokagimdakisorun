using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SokagimdakiSorun.Complaints.Contracts;
using SokagimdakiSorun.IntegrationTests.Database.Support;
using static SokagimdakiSorun.IntegrationTests.Complaints.ComplaintsTestHost;

namespace SokagimdakiSorun.IntegrationTests.Complaints;

/// <summary>
/// Yaşam döngüsü uçtan uca (HTTP + gerçek PostgreSQL 18): her geçiş geçmişe ve outbox'a aynı transaction'da yazılır;
/// transaction başarısız olursa ne durum, ne geçmiş, ne outbox satırı kalır.
/// </summary>
[Collection(DatabaseCollection.Name)]
[Trait("Category", "Database")]
public sealed class ComplaintLifecycleTests(PostgresFixture postgres)
{
    private const string OutboxOfType = "SELECT count(*) FROM platform.outbox_messages WHERE type = @type";
    private static readonly string StatusChangedType = typeof(ComplaintStatusChanged).FullName!;
    private static readonly string SubmittedType = typeof(ComplaintSubmitted).FullName!;

    [Fact]
    public async Task Full_lifecycle_writes_history_and_one_event_per_transition()
    {
        await using var host = await StartAsync(postgres);
        var (moderatorId, moderator) = await host.CreateModeratorAsync("mod-a");
        var category = await host.CreateCategoryAsync();
        var unit = await host.CreateUnitAsync("fen-isleri", moderatorId);
        var complaint = await host.SubmitAsync(category, await host.CreateReporterAsync());

        Assert.Matches("^SS-[2-9A-HJKMNP-Z]{4}-[2-9A-HJKMNP-Z]{4}$", complaint.PublicReference);
        Assert.Equal(1, await host.ScalarAsync(OutboxOfType, ("type", SubmittedType)));

        await host.CommandOkAsync(moderator, complaint.ComplaintId, "start-review");
        await host.CommandOkAsync(moderator, complaint.ComplaintId, "accept");
        var assigned = await host.CommandOkAsync(moderator, complaint.ComplaintId, "assign", new { serviceUnitId = unit, assigneeUserId = moderatorId });
        Assert.Equal("Assigned", assigned.GetProperty("status").GetString());
        Assert.Equal(unit, assigned.GetProperty("activeAssignment").GetProperty("serviceUnit").GetProperty("id").GetGuid());
        await host.CommandOkAsync(moderator, complaint.ComplaintId, "start-work");
        var awaiting = await host.CommandOkAsync(moderator, complaint.ComplaintId, "request-information",
            new { reason = "GIZLI-IC-GEREKCE konum belirsiz", reporterNote = "Tam konumu paylaşır mısınız?" });
        Assert.Equal(["resume"], awaiting.GetProperty("allowedActions").EnumerateArray().Select(a => a.GetString()));
        var resumed = await host.CommandOkAsync(moderator, complaint.ComplaintId, "resume");
        Assert.Equal("InProgress", resumed.GetProperty("status").GetString()); // #23: giriş durumuna
        Assert.Equal(unit, resumed.GetProperty("activeAssignment").GetProperty("serviceUnit").GetProperty("id").GetGuid());
        await host.CommandOkAsync(moderator, complaint.ComplaintId, "resolve", new { reason = "Asfalt yamandı." });
        var closed = await host.CommandOkAsync(moderator, complaint.ComplaintId, "close");
        Assert.Equal("Closed", closed.GetProperty("status").GetString());
        Assert.Empty(closed.GetProperty("allowedActions").EnumerateArray()); // reopen yalnızca Admin

        Assert.Equal(9, await host.ScalarAsync(
            "SELECT count(*) FROM complaints.complaint_status_history WHERE complaint_id = @id", ("id", complaint.ComplaintId)));
        Assert.Equal(8, await host.ScalarAsync(OutboxOfType, ("type", StatusChangedType)));

        // Olay iç gerekçeyi taşımaz; şikâyetçi notunu taşır.
        await using var connection = new Npgsql.NpgsqlConnection(host.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new Npgsql.NpgsqlCommand(
            "SELECT payload::text FROM platform.outbox_messages WHERE type = @type AND payload->>'toStatus' = 'AwaitingInformation'", connection);
        command.Parameters.AddWithValue("type", StatusChangedType);
        var payload = (string)(await command.ExecuteScalarAsync(Ct))!;
        Assert.DoesNotContain("GIZLI-IC-GEREKCE", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("\"reason\"", payload, StringComparison.Ordinal);
        using var json = JsonDocument.Parse(payload);
        Assert.Equal("Tam konumu paylaşır mısınız?", json.RootElement.GetProperty("reporterNote").GetString());
        Assert.Equal("InProgress", json.RootElement.GetProperty("fromStatus").GetString());

        // Geçmiş uç noktası iç gerekçeyi panele gösterir (yalnızca panel), atama hareketini de listeler.
        using var client = host.CreateClient(moderator);
        var history = JsonDocument.Parse(await client.GetStringAsync($"/api/v1/complaints/{complaint.ComplaintId}/history", Ct)).RootElement;
        Assert.Equal(9, history.GetProperty("statusChanges").GetArrayLength());
        Assert.Single(history.GetProperty("assignments").EnumerateArray());

        // Admin yeniden açar (#22): atama kapanır, şikâyet triage havuzuna döner.
        var reopened = await host.CommandOkAsync(host.AdminToken, complaint.ComplaintId, "reopen", new { reason = "Sorun tekrarladı." });
        Assert.Equal("UnderReview", reopened.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, reopened.GetProperty("activeAssignment").ValueKind);
    }

    [Fact]
    public async Task Failed_transaction_leaves_no_status_history_or_outbox_change()
    {
        await using var host = await StartAsync(postgres);
        var category = await host.CreateCategoryAsync();
        var reporter = await host.CreateReporterAsync();
        var complaint = await host.SubmitAsync(category, reporter);
        var outboxBefore = await host.ScalarAsync("SELECT count(*) FROM platform.outbox_messages");

        // Geçmiş satırı eklenirken veritabanı hata versin: aynı transaction'daki durum güncellemesi ve outbox satırı geri alınmalı.
        await host.ExecuteAsync("""
            CREATE FUNCTION complaints.fail_history() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'test: geçmiş yazılamadı'; END $$;
            CREATE TRIGGER fail_history BEFORE INSERT ON complaints.complaint_status_history
            FOR EACH ROW EXECUTE FUNCTION complaints.fail_history();
            """);

        using (var response = await host.CommandAsync(host.AdminToken, complaint.ComplaintId, "start-review"))
        {
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        }

        Assert.Equal(1, await host.ScalarAsync(
            "SELECT count(*) FROM complaints.complaints WHERE id = @id AND status = 'Submitted'", ("id", complaint.ComplaintId)));
        Assert.Equal(outboxBefore, await host.ScalarAsync("SELECT count(*) FROM platform.outbox_messages"));

        // Oluşturma da tek transaction: şikâyet, geçmiş ve ComplaintSubmitted birlikte geri alınır.
        await using (var scope = host.CreateScope())
        {
            var intake = scope.ServiceProvider.GetRequiredService<IComplaintIntake>();
            await Assert.ThrowsAsync<DbUpdateException>(() => intake.SubmitFromIntakeAsync(new SubmitFromIntakeRequest(
                Guid.CreateVersion7(), reporter, category, "Geri alınacak", null, null, "Yazıcızade (temsili)", LocationSource.TextAddress, []), Ct));
        }

        Assert.Equal(1, await host.ScalarAsync("SELECT count(*) FROM complaints.complaints"));
        Assert.Equal(outboxBefore, await host.ScalarAsync("SELECT count(*) FROM platform.outbox_messages"));

        await host.ExecuteAsync("DROP TRIGGER fail_history ON complaints.complaint_status_history;");
        Assert.Equal("UnderReview", (await host.CommandOkAsync(host.AdminToken, complaint.ComplaintId, "start-review")).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Invalid_transition_missing_reason_and_missing_version_are_rejected()
    {
        await using var host = await StartAsync(postgres);
        var complaint = await host.SubmitAsync(await host.CreateCategoryAsync(), await host.CreateReporterAsync());

        using (var response = await host.CommandAsync(host.AdminToken, complaint.ComplaintId, "resolve", new { reason = "x" }))
        {
            await ReadProblemAsync(response, HttpStatusCode.Conflict, ComplaintErrorCodes.InvalidTransition);
        }

        using (var response = await host.CommandAsync(host.AdminToken, complaint.ComplaintId, "reject"))
        {
            var problem = await ReadProblemAsync(response, HttpStatusCode.BadRequest, ComplaintErrorCodes.ValidationFailed);
            Assert.True(problem.GetProperty("errors").TryGetProperty("reason", out _));
        }

        using (var response = await host.CommandWithVersionAsync(host.AdminToken, complaint.ComplaintId, "start-review", null))
        {
            var problem = await ReadProblemAsync(response, HttpStatusCode.BadRequest, ComplaintErrorCodes.ValidationFailed);
            Assert.True(problem.GetProperty("errors").TryGetProperty("version", out _));
        }

        await host.CommandOkAsync(host.AdminToken, complaint.ComplaintId, "start-review");
        using (var response = await host.CommandAsync(host.AdminToken, complaint.ComplaintId, "request-information", new { reason = "eksik" }))
        {
            var problem = await ReadProblemAsync(response, HttpStatusCode.BadRequest, ComplaintErrorCodes.ValidationFailed);
            Assert.True(problem.GetProperty("errors").TryGetProperty("reporterNote", out _));
        }

        Assert.Equal(2, await host.ScalarAsync(
            "SELECT count(*) FROM complaints.complaint_status_history WHERE complaint_id = @id", ("id", complaint.ComplaintId)));
    }

    [Fact]
    public async Task Duplicate_target_cannot_be_self_another_duplicate_or_invisible()
    {
        await using var host = await StartAsync(postgres);
        var (moderatorId, moderator) = await host.CreateModeratorAsync("mod-dup");
        var category = await host.CreateCategoryAsync();
        var reporter = await host.CreateReporterAsync();
        var original = await host.SubmitAsync(category, reporter);
        var copy = await host.SubmitAsync(category, reporter);
        var other = await host.SubmitAsync(category, reporter);
        var hidden = await host.SubmitAsync(category, reporter);
        _ = moderatorId;

        using (var self = await host.CommandAsync(moderator, copy.ComplaintId, "mark-duplicate", new { targetComplaintId = copy.ComplaintId, reason = "aynı" }))
        {
            await ReadProblemAsync(self, HttpStatusCode.UnprocessableEntity, ComplaintErrorCodes.DuplicateTargetInvalid);
        }

        var marked = await host.CommandOkAsync(moderator, copy.ComplaintId, "mark-duplicate", new { targetComplaintId = original.ComplaintId, reason = "aynı çukur" });
        Assert.Equal("Duplicate", marked.GetProperty("status").GetString());
        Assert.Equal(original.ComplaintId, marked.GetProperty("duplicateOf").GetProperty("id").GetGuid());

        using (var chain = await host.CommandAsync(moderator, other.ComplaintId, "mark-duplicate", new { targetComplaintId = copy.ComplaintId, reason = "zincir" }))
        {
            await ReadProblemAsync(chain, HttpStatusCode.UnprocessableEntity, ComplaintErrorCodes.DuplicateTargetInvalid);
        }

        // Kapsam dışı hedef: varlığı sızdırılmaz, aynı 422.
        var unit = await host.CreateUnitAsync("gizli-birim");
        await host.CommandOkAsync(host.AdminToken, hidden.ComplaintId, "start-review");
        await host.CommandOkAsync(host.AdminToken, hidden.ComplaintId, "accept");
        await host.CommandOkAsync(host.AdminToken, hidden.ComplaintId, "assign", new { serviceUnitId = unit });
        using (var invisible = await host.CommandAsync(moderator, other.ComplaintId, "mark-duplicate", new { targetComplaintId = hidden.ComplaintId, reason = "x" }))
        {
            await ReadProblemAsync(invisible, HttpStatusCode.UnprocessableEntity, ComplaintErrorCodes.DuplicateTargetInvalid);
        }

        // Veritabanı da kendine tekrarı reddeder (I-9).
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => host.ExecuteAsync(
            $"UPDATE complaints.complaints SET duplicate_of_complaint_id = id, status = 'Duplicate' WHERE id = '{other.ComplaintId}'"));
    }
}
