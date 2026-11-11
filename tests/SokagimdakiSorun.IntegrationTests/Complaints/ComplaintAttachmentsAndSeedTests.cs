using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SokagimdakiSorun.Complaints.Contracts;
using SokagimdakiSorun.Complaints.Infrastructure;
using SokagimdakiSorun.IntegrationTests.Database.Support;
using SokagimdakiSorun.Media.Contracts;
using SokagimdakiSorun.Media.TestSupport;
using static SokagimdakiSorun.IntegrationTests.Complaints.ComplaintsTestHost;

namespace SokagimdakiSorun.IntegrationTests.Complaints;

/// <summary>Ekler (yetki sonrası Media akışı), DTO sızıntısı, katalog yetkisi ve temsili tohum verisi.</summary>
[Collection(DatabaseCollection.Name)]
[Trait("Category", "Database")]
public sealed class ComplaintAttachmentsAndSeedTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Attachments_are_committed_streamed_after_scope_check_and_never_expose_storage_details()
    {
        await using var host = await StartAsync(postgres);
        var (_, outsider) = await host.CreateModeratorAsync("dis");
        var category = await host.CreateCategoryAsync();
        var unit = await host.CreateUnitAsync("aydinlatma");
        const string externalId = "demo-ozel-kimlik-905550000000";
        var reporter = await host.CreateReporterAsync(externalId);

        var image = TestImages.Jpeg(withMetadata: false);
        Guid mediaId;
        await using (var scope = host.CreateScope())
        {
            var media = scope.ServiceProvider.GetRequiredService<IMediaService>();
            mediaId = (await media.StoreAsync(new MemoryStream(image), "image/jpeg", Ct)).Value.Value;
        }

        var complaint = await host.SubmitAsync(category, reporter, media: [mediaId]);
        Assert.Equal(1, await host.ScalarAsync("SELECT count(*) FROM media.media_files WHERE id = @id AND status = 'Committed'", ("id", mediaId)));

        await using var connection = new Npgsql.NpgsqlConnection(host.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var keyCommand = new Npgsql.NpgsqlCommand("SELECT storage_key FROM media.media_files WHERE id = @id", connection);
        keyCommand.Parameters.AddWithValue("id", mediaId);
        var storageKey = (string)(await keyCommand.ExecuteScalarAsync(Ct))!;

        using var admin = host.CreateClient(host.AdminToken);
        var detailText = await admin.GetStringAsync($"/api/v1/complaints/{complaint.ComplaintId}", Ct);
        var listText = await admin.GetStringAsync($"/api/v1/complaints/{complaint.ComplaintId}/attachments", Ct);
        foreach (var text in new[] { detailText, listText })
        {
            Assert.DoesNotContain(storageKey, text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("storage", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("path", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(externalId, text, StringComparison.Ordinal); // telefon/kanal kimliği yok
            Assert.DoesNotContain("phone", text, StringComparison.OrdinalIgnoreCase);
        }

        var attachment = Assert.Single(JsonDocument.Parse(listText).RootElement.EnumerateArray());
        Assert.Equal("Intake", attachment.GetProperty("source").GetString());
        var contentUrl = attachment.GetProperty("contentUrl").GetString()!;

        using (var content = await admin.GetAsync(contentUrl, Ct))
        {
            Assert.Equal(HttpStatusCode.OK, content.StatusCode);
            Assert.Equal("image/jpeg", content.Content.Headers.ContentType?.MediaType);
            Assert.Equal(image, await content.Content.ReadAsByteArrayAsync(Ct));
            Assert.Contains("no-store", content.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
        }

        // Kapsam dışına alınınca dosya da 404.
        await host.CommandOkAsync(host.AdminToken, complaint.ComplaintId, "start-review");
        await host.CommandOkAsync(host.AdminToken, complaint.ComplaintId, "accept");
        await host.CommandOkAsync(host.AdminToken, complaint.ComplaintId, "assign", new { serviceUnitId = unit });
        using var outsiderClient = host.CreateClient(outsider);
        using (var denied = await outsiderClient.GetAsync(contentUrl, Ct))
        {
            await ReadProblemAsync(denied, HttpStatusCode.NotFound, ComplaintErrorCodes.NotFound);
        }

        // Aynı dosya ikinci bir şikâyete bağlanamaz (UNIQUE media_file_id).
        await using (var scope = host.CreateScope())
        {
            var intake = scope.ServiceProvider.GetRequiredService<IComplaintIntake>();
            var reused = await intake.SubmitFromIntakeAsync(new SubmitFromIntakeRequest(
                Guid.CreateVersion7(), reporter, category, "ikinci", 40.41, 26.67, null, LocationSource.WhatsAppPin, [mediaId]), Ct);
            Assert.Equal(ComplaintErrorCodes.AttachmentUnavailable, reused.Error.Code);
            var missing = await intake.SubmitFromIntakeAsync(new SubmitFromIntakeRequest(
                Guid.CreateVersion7(), reporter, category, "üçüncü", 40.41, 26.67, null, LocationSource.WhatsAppPin, [Guid.CreateVersion7()]), Ct);
            Assert.Equal(ComplaintErrorCodes.AttachmentUnavailable, missing.Error.Code);
        }

        Assert.Equal(1, await host.ScalarAsync("SELECT count(*) FROM complaints.complaints"));
    }

    [Fact]
    public async Task Catalog_writes_are_admin_only_and_validated()
    {
        await using var host = await StartAsync(postgres);
        var (moderatorId, moderator) = await host.CreateModeratorAsync("katalog");
        var category = await host.CreateCategoryAsync("cop-atik");
        var unit = await host.CreateUnitAsync("temizlik");

        using var modClient = host.CreateClient(moderator);
        using (var forbidden = await modClient.PostAsJsonAsync("/api/v1/categories", new { code = "yeni", name = "Yeni" }, Ct))
        {
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        }

        using (var forbiddenRules = await modClient.GetAsync("/api/v1/routing-rules", Ct))
        {
            Assert.Equal(HttpStatusCode.Forbidden, forbiddenRules.StatusCode);
        }

        Assert.Single((await modClient.GetFromJsonAsync<JsonElement>("/api/v1/categories", Ct)).EnumerateArray());

        using var admin = host.CreateClient(host.AdminToken);
        using (var duplicate = await admin.PostAsJsonAsync("/api/v1/categories", new { code = "cop-atik", name = "Tekrar" }, Ct))
        {
            await ReadProblemAsync(duplicate, HttpStatusCode.Conflict, "category.code_taken");
        }

        using (var badCode = await admin.PostAsJsonAsync("/api/v1/categories", new { code = "Büyük Harf", name = "x" }, Ct))
        {
            await ReadProblemAsync(badCode, HttpStatusCode.BadRequest, ComplaintErrorCodes.ValidationFailed);
        }

        using (var reporterMember = await admin.PostAsJsonAsync($"/api/v1/service-units/{unit}/members", new { userId = await host.CreateReporterAsync() }, Ct))
        {
            await ReadProblemAsync(reporterMember, HttpStatusCode.UnprocessableEntity, "service_unit.member_invalid");
        }

        using (var put = await admin.PutAsJsonAsync("/api/v1/routing-rules", new { rules = new[] { new { categoryId = category, serviceUnitId = unit, priority = 0, isActive = true } } }, Ct))
        {
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        }

        var complaint = await host.SubmitAsync(category, await host.CreateReporterAsync());
        var detail = await host.GetDetailAsync(moderator, complaint.ComplaintId);
        Assert.Equal(unit, detail.GetProperty("suggestedServiceUnit").GetProperty("id").GetGuid());

        // Üye olmayan kişiye atama 422; birim etkin değilse 422.
        await host.CommandOkAsync(host.AdminToken, complaint.ComplaintId, "start-review");
        await host.CommandOkAsync(host.AdminToken, complaint.ComplaintId, "accept");
        using (var notMember = await host.CommandAsync(host.AdminToken, complaint.ComplaintId, "assign", new { serviceUnitId = unit, assigneeUserId = moderatorId }))
        {
            await ReadProblemAsync(notMember, HttpStatusCode.UnprocessableEntity, ComplaintErrorCodes.AssigneeNotMember);
        }

        using (var deactivate = await admin.PatchAsJsonAsync($"/api/v1/service-units/{unit}", new { isActive = false }, Ct))
        {
            Assert.Equal(HttpStatusCode.OK, deactivate.StatusCode);
        }

        using (var inactive = await host.CommandAsync(host.AdminToken, complaint.ComplaintId, "assign", new { serviceUnitId = unit }))
        {
            await ReadProblemAsync(inactive, HttpStatusCode.UnprocessableEntity, ComplaintErrorCodes.ServiceUnitInvalid);
        }
    }

    [Fact]
    public async Task Demo_seed_loads_representative_data_only_when_enabled_and_is_idempotent()
    {
        await using (var disabled = await StartAsync(postgres))
        {
            Assert.Equal(0, await disabled.ScalarAsync("SELECT count(*) FROM complaints.complaint_categories"));
        }

        await using var host = await StartAsync(postgres, new Dictionary<string, string?> { ["Complaints:DemoSeed:Enabled"] = "true" });
        Assert.Equal(6, await host.ScalarAsync("SELECT count(*) FROM complaints.complaint_categories"));
        Assert.Equal(5, await host.ScalarAsync("SELECT count(*) FROM complaints.service_units WHERE name LIKE '%(Temsili)'"));
        Assert.Equal(6, await host.ScalarAsync("SELECT count(*) FROM complaints.routing_rules"));
        Assert.Equal(5, await host.ScalarAsync("SELECT count(*) FROM complaints.complaints WHERE description LIKE '[Örnek veri]%'"));

        var seeder = host.Services.GetServices<IHostedService>().OfType<DemoDataSeeder>().Single();
        await seeder.StartAsync(Ct);
        Assert.Equal(5, await host.ScalarAsync("SELECT count(*) FROM complaints.complaints"));
        Assert.Equal(6, await host.ScalarAsync("SELECT count(*) FROM complaints.complaint_categories"));

        using var client = host.CreateClient(host.AdminToken);
        var page = await client.GetFromJsonAsync<JsonElement>("/api/v1/complaints", Ct);
        Assert.Equal(5, page.GetProperty("totalCount").GetInt32());
    }
}
