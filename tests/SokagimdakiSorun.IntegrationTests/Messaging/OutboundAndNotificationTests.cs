using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SokagimdakiSorun.Complaints.Contracts;
using SokagimdakiSorun.IntegrationTests.Database.Support;
using SokagimdakiSorun.IntegrationTests.Messaging.Support;
using SokagimdakiSorun.Messaging.Infrastructure;
using SokagimdakiSorun.Messaging.Infrastructure.WhatsApp;
using SokagimdakiSorun.Messaging.TestSupport;
using SokagimdakiSorun.Platform.Outbox;
using static SokagimdakiSorun.IntegrationTests.Messaging.Support.MessagingTestHost;

namespace SokagimdakiSorun.IntegrationTests.Messaging;

/// <summary>Giden kuyruk, bildirim handler'ları, ek bilgi cevabı, konuşma uç noktası ve log gizliliği.</summary>
[Collection(DatabaseCollection.Name)]
[Trait("Category", "Database")]
public sealed class OutboundAndNotificationTests(PostgresFixture postgres)
{
    private const string LastOutbound =
        "SELECT delivery_status FROM messaging.messages WHERE direction = 'Outbound' ORDER BY created_at DESC, id DESC LIMIT 1";

    [Fact]
    public async Task Transient_failure_is_retried_permanent_and_exhausted_become_failed()
    {
        await using var host = await StartAsync(postgres, new Dictionary<string, string?> { ["Messaging:MaxAttempts"] = "2" });

        // Geçici hata → yeniden deneme → gönderildi
        await host.SendAsync(WebhookFixtures.Text(Reporter, "merhaba"));
        host.WhatsApp.ScriptSends(SendResult.Transient("http_503"));
        Assert.Equal(1, await host.SendOutboundAsync());
        Assert.Equal("Queued", await host.StringAsync(LastOutbound));
        Assert.Equal(0, await host.SendOutboundAsync()); // backoff
        host.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(1, await host.SendOutboundAsync());
        Assert.Equal("Sent", await host.StringAsync(LastOutbound));

        // Kalıcı hata → doğrudan Failed
        await host.SendAsync(WebhookFixtures.Text(Reporter, "1"));
        host.WhatsApp.ScriptSends(SendResult.Permanent("provider_error:131026"));
        await host.SendOutboundAsync();
        Assert.Equal("Failed", await host.StringAsync(LastOutbound));

        // Geçici hata azami denemede Failed; sonsuz döngü yok
        await host.SendAsync(WebhookFixtures.Text(Reporter, "Açıklama (temsili)."));
        host.WhatsApp.ScriptSends(SendResult.Transient("timeout"), SendResult.Transient("timeout"));
        await host.SendOutboundAsync();
        host.Clock.Advance(TimeSpan.FromMinutes(1));
        await host.SendOutboundAsync();
        Assert.Equal("Failed", await host.StringAsync(LastOutbound));
        host.Clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(0, await host.SendOutboundAsync());
        Assert.Equal(2, await host.ScalarAsync(
            "SELECT attempt_count FROM messaging.messages WHERE direction = 'Outbound' ORDER BY created_at DESC, id DESC LIMIT 1"));
    }

    [Fact]
    public async Task Free_text_is_not_sent_when_service_window_is_closed()
    {
        await using var host = await StartAsync(postgres);
        await host.SendAsync(WebhookFixtures.Text(Reporter, "merhaba"));
        var sentBefore = host.WhatsApp.Sent.Count;

        host.Clock.Advance(TimeSpan.FromHours(25));
        Assert.Equal(1, await host.SendOutboundAsync());
        Assert.Equal("Failed", await host.StringAsync(LastOutbound));
        Assert.StartsWith("window_closed", await host.StringAsync(
            "SELECT last_error FROM messaging.messages WHERE direction = 'Outbound'"), StringComparison.Ordinal);
        Assert.Equal(sentBefore, host.WhatsApp.Sent.Count);
    }

    [Fact]
    public async Task Same_event_twice_produces_one_outbound_message()
    {
        await using var host = await StartAsync(postgres);
        var complaintId = await host.CompleteIntakeAsync();
        var reporterId = Guid.Parse((await host.StringAsync("SELECT reporter_user_id::text FROM messaging.conversations"))!);
        var evt = new ComplaintStatusChanged(Guid.CreateVersion7(), host.Clock.GetUtcNow(), complaintId, "SS-TEST-TEST", reporterId,
            ComplaintStatus.Assigned, ComplaintStatus.InProgress, null);

        for (var i = 0; i < 2; i++)
        {
            await using var scope = host.CreateScope();
            foreach (var handler in scope.ServiceProvider.GetServices<IIntegrationEventHandler<ComplaintStatusChanged>>())
            {
                await handler.HandleAsync(evt, Ct);
            }
        }

        Assert.Equal(1, await host.ScalarAsync("SELECT count(*) FROM messaging.messages WHERE dedup_key = @key", ("key", $"event:{evt.EventId}")));

        // Bildirim gerektirmeyen durum → mesaj yok
        var silent = evt with { EventId = Guid.CreateVersion7(), FromStatus = ComplaintStatus.UnderReview, ToStatus = ComplaintStatus.Accepted };
        await using (var scope = host.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IIntegrationEventHandler<ComplaintStatusChanged>>().HandleAsync(silent, Ct);
        }

        Assert.Equal(0, await host.ScalarAsync("SELECT count(*) FROM messaging.messages WHERE dedup_key = @key", ("key", $"event:{silent.EventId}")));
    }

    [Fact]
    public async Task Reply_while_awaiting_information_goes_to_complaint_and_status_updates_apply()
    {
        await using var host = await StartAsync(postgres);
        var complaintId = await host.CompleteIntakeAsync();
        var admin = await host.LoginAsync();
        await CommandAsync(host, admin, complaintId, "start-review");
        await CommandAsync(host, admin, complaintId, "request-information",
            new { reason = "GIZLI-IC-GEREKCE", reporterNote = "Tam adresi yazar mısınız?" });
        await host.DispatchOutboxAsync();

        var question = (await host.OutboundTextsAsync())[0];
        Assert.Contains("Tam adresi yazar mısınız?", question, StringComparison.Ordinal);
        Assert.DoesNotContain("GIZLI-IC-GEREKCE", question, StringComparison.Ordinal);
        Assert.Equal(complaintId.ToString(), await host.StringAsync("SELECT awaiting_reply_complaint_id::text FROM messaging.conversations"));

        // Cevap (iki kez teslim) → tek ProvideInformation; şikâyet UnderReview'a döner, yorum eklenir, bekleyiş kalkar.
        var reply = WebhookFixtures.Text(Reporter, "Yazıcızade Mah. 12. Sokak (temsili).");
        await host.SendAsync(reply);
        await host.SendAsync(reply);
        Assert.Equal("UnderReview", await host.StringAsync("SELECT status FROM complaints.complaints"));
        Assert.Equal(1, await host.ScalarAsync("SELECT count(*) FROM complaints.complaint_comments"));
        Assert.Null(await host.StringAsync("SELECT awaiting_reply_complaint_id::text FROM messaging.conversations"));
        Assert.StartsWith("Ek bilginiz", (await host.OutboundTextsAsync())[0], StringComparison.Ordinal);
        Assert.Equal(1, await host.ScalarAsync("SELECT count(*) FROM messaging.intake_sessions")); // yeni intake başlamadı

        // Yeniden bilgi isteği → "geri çek" → Withdrawn + bildirim
        await CommandAsync(host, admin, complaintId, "request-information", new { reason = "Ek soru", reporterNote = "Fotoğraf ekler misiniz?" });
        await host.DispatchOutboxAsync();
        await host.SendAsync(WebhookFixtures.Text(Reporter, "Geri çek"));
        Assert.Equal("Withdrawn", await host.StringAsync("SELECT status FROM complaints.complaints"));
        await host.DispatchOutboxAsync();
        Assert.Contains("geri çekildi", (await host.OutboundTextsAsync())[0], StringComparison.Ordinal);

        // Teslim durumu webhook'u: Sent → Delivered → (sırası bozuk) sent geri almaz
        await host.SendOutboundAsync();
        var providerId = (await host.StringAsync(
            "SELECT provider_message_id FROM messaging.messages WHERE direction = 'Outbound' AND provider_message_id IS NOT NULL ORDER BY created_at DESC LIMIT 1"))!;
        await host.SendAsync(WebhookFixtures.Status(providerId, "delivered"));
        await host.SendAsync(WebhookFixtures.Status(providerId, "sent"));
        Assert.Equal("Delivered", await host.StringAsync(
            "SELECT delivery_status FROM messaging.messages WHERE provider_message_id = @id", ("id", providerId)));
    }

    [Fact]
    public async Task Messages_endpoint_is_scoped_masks_phone_and_logs_hold_no_personal_data()
    {
        await using var host = await StartAsync(postgres);
        const string secretText = "GIZLI-METIN-Kaldirim";
        var complaintId = await host.CompleteIntakeAsync(description: secretText);
        await host.DispatchOutboxAsync();
        await host.SendOutboundAsync();

        var admin = await host.LoginAsync();
        using (var client = host.CreateClient(admin))
        {
            var raw = await client.GetStringAsync($"/api/v1/complaints/{complaintId}/messages", Ct);
            var json = JsonDocument.Parse(raw).RootElement;
            Assert.Equal("90********01", json.GetProperty("maskedContact").GetString());
            Assert.DoesNotContain(Reporter, raw, StringComparison.Ordinal);
            Assert.DoesNotContain("wamid", raw, StringComparison.Ordinal);
            var messages = json.GetProperty("messages").EnumerateArray().ToList();
            Assert.True(messages.Count >= 12); // 6 gelen + 6 giden (menü … "Başvurunuz alındı")
            Assert.Contains(messages, m => m.GetProperty("text").GetString() == secretText);
            Assert.Contains(messages, m => m.GetProperty("direction").GetString() == "Outbound" && m.GetProperty("deliveryStatus").GetString() == "Sent");

            var missing = await client.GetAsync($"/api/v1/complaints/{Guid.CreateVersion7()}/messages", Ct);
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }

        using (var anonymous = host.CreateClient())
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/v1/complaints/{complaintId}/messages", Ct)).StatusCode);
        }

        // Kapsam dışı moderatör: şikâyet onun üyesi olmadığı birime atanmış → 404
        var unit = await CreateUnitAsync(host, admin, "fen-isleri");
        await CommandAsync(host, admin, complaintId, "start-review");
        await CommandAsync(host, admin, complaintId, "accept");
        await CommandAsync(host, admin, complaintId, "assign", new { serviceUnitId = unit });
        var moderator = await CreateModeratorAsync(host, admin, "mod-disarida");
        using (var client = host.CreateClient(moderator))
        {
            var response = await client.GetAsync($"/api/v1/complaints/{complaintId}/messages", Ct);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal(ComplaintErrorCodes.NotFound,
                (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("code").GetString());
        }

        // Loglarda telefon, mesaj metni, secret, imza, ham payload yok.
        var logs = string.Join('\n', host.Logs.Messages);
        Assert.Contains("Inbox olayı", logs, StringComparison.Ordinal); // log gerçekten toplandı
        Assert.DoesNotContain(Reporter, logs, StringComparison.Ordinal);
        Assert.DoesNotContain(secretText, logs, StringComparison.Ordinal);
        Assert.DoesNotContain("Kaldırım kırık", logs, StringComparison.Ordinal);
        Assert.DoesNotContain(WebhookFixtures.AppSecret, logs, StringComparison.Ordinal);
        Assert.DoesNotContain("sha256=", logs, StringComparison.Ordinal);
        Assert.DoesNotContain("whatsapp_business_account", logs, StringComparison.Ordinal);
    }

    private static async Task CommandAsync(MessagingTestHost host, string token, Guid id, string action, object? body = null)
    {
        using var client = host.CreateClient(token);
        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/v1/complaints/{id}", Ct);
        var payload = new Dictionary<string, object?> { ["version"] = detail.GetProperty("version").GetString() };
        foreach (var property in JsonSerializer.SerializeToElement(body ?? new { }).EnumerateObject())
        {
            payload[property.Name] = property.Value;
        }

        using var response = await client.PostAsJsonAsync($"/api/v1/complaints/{id}/{action}", payload, Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{action}: {await response.Content.ReadAsStringAsync(Ct)}");
    }

    private static async Task<Guid> CreateUnitAsync(MessagingTestHost host, string token, string code)
    {
        using var client = host.CreateClient(token);
        using var response = await client.PostAsJsonAsync("/api/v1/service-units", new { code, name = $"Birim {code} (Temsili)" }, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
    }

    private static async Task<string> CreateModeratorAsync(MessagingTestHost host, string token, string name)
    {
        var email = $"{name}@example.test";
        const string password = "Moderator-Parola-2026!";
        using var client = host.CreateClient(token);
        using var response = await client.PostAsJsonAsync("/api/v1/users", new { email, displayName = $"Moderatör {name}", password }, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await host.LoginAsync(email, password);
    }
}
