using System.Net;
using Microsoft.Extensions.DependencyInjection;
using SokagimdakiSorun.Complaints.Contracts;
using SokagimdakiSorun.IntegrationTests.Database.Support;
using SokagimdakiSorun.IntegrationTests.Messaging.Support;
using SokagimdakiSorun.Messaging.Domain;
using SokagimdakiSorun.Messaging.TestSupport;
using static SokagimdakiSorun.IntegrationTests.Messaging.Support.MessagingTestHost;

namespace SokagimdakiSorun.IntegrationTests.Messaging;

/// <summary>
/// Webhook güvenliği, tekrar koruması ve intake akışı (HTTP + gerçek PostgreSQL 18 + mock sağlayıcı).
/// </summary>
[Collection(DatabaseCollection.Name)]
[Trait("Category", "Database")]
public sealed class WebhookAndIntakeTests(PostgresFixture postgres)
{
    private const string Inbox = "SELECT count(*) FROM messaging.webhook_inbox_events";
    private const string Complaints = "SELECT count(*) FROM complaints.complaints";
    private const string InboundMessages = "SELECT count(*) FROM messaging.messages WHERE direction = 'Inbound'";
    private const string Intakes = "SELECT count(*) FROM messaging.intake_sessions";

    [Fact]
    public async Task Get_verification_echoes_challenge_only_for_correct_token()
    {
        await using var host = await StartAsync(postgres, withCategory: false);
        using var client = host.CreateClient();

        var ok = await client.GetAsync(
            $"/api/integrations/whatsapp/webhook?hub.mode=subscribe&hub.verify_token={WebhookFixtures.VerifyToken}&hub.challenge=1158201444", Ct);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("1158201444", await ok.Content.ReadAsStringAsync(Ct));

        var wrong = await client.GetAsync("/api/integrations/whatsapp/webhook?hub.mode=subscribe&hub.verify_token=yanlis&hub.challenge=1", Ct);
        Assert.Equal(HttpStatusCode.Forbidden, wrong.StatusCode);
        var mode = await client.GetAsync(
            $"/api/integrations/whatsapp/webhook?hub.mode=unsubscribe&hub.verify_token={WebhookFixtures.VerifyToken}&hub.challenge=1", Ct);
        Assert.Equal(HttpStatusCode.Forbidden, mode.StatusCode);
    }

    [Fact]
    public async Task Missing_or_wrong_signature_is_rejected_and_nothing_is_stored()
    {
        await using var host = await StartAsync(postgres, withCategory: false);
        var body = WebhookFixtures.Text(Reporter, "merhaba");

        using var missing = await host.PostWebhookAsync(body, sign: false);
        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        using var wrong = await host.PostWebhookAsync(body, signature: "sha256=" + new string('0', 64));
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        // Başka bir gövdenin geçerli imzası (imza ham gövdeye bağlı)
        using var otherBody = await host.PostWebhookAsync(body,
            signature: SokagimdakiSorun.Messaging.Infrastructure.WhatsApp.WebhookSignatureVerifier.Sign(
                System.Text.Encoding.UTF8.GetBytes(body + " "), WebhookFixtures.AppSecret));
        Assert.Equal(HttpStatusCode.Unauthorized, otherBody.StatusCode);
        Assert.Equal(0, await host.ScalarAsync(Inbox));

        using var ok = await host.PostWebhookAsync(body);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(1, await host.ScalarAsync(Inbox));
    }

    [Fact]
    public async Task Same_webhook_twice_creates_one_message_intake_and_complaint()
    {
        await using var host = await StartAsync(postgres);
        var hello = WebhookFixtures.Text(Reporter, "merhaba");
        await host.SendAsync(hello);
        await host.SendAsync(hello);
        Assert.Equal(1, await host.ScalarAsync(Inbox));
        Assert.Equal(1, await host.ScalarAsync(InboundMessages));
        Assert.Equal(1, await host.ScalarAsync(Intakes));

        // Aynı sağlayıcı mesajı, farklı gövdeyle (ör. zaman damgası değişmiş yeniden teslim) → yine tek kayıt.
        var id = WebhookFixtures.NewMessageId();
        await host.SendAsync(WebhookFixtures.Build("text", Reporter, id, text: "1", timestamp: 1_700_000_000));
        await host.SendAsync(WebhookFixtures.Build("text", Reporter, id, text: "1", timestamp: 1_700_000_100));
        Assert.Equal(2, await host.ScalarAsync(InboundMessages));

        await host.SendAsync(WebhookFixtures.Text(Reporter, "Çukur (temsili)."));
        await host.SendAsync(WebhookFixtures.Location(Reporter, 40.41, 26.67));
        await host.SendAsync(WebhookFixtures.Text(Reporter, "atla"));
        var confirm = WebhookFixtures.Text(Reporter, "1");
        await host.SendAsync(confirm);
        await host.SendAsync(confirm);
        Assert.Equal(1, await host.ScalarAsync(Complaints));
        Assert.Equal(1, await host.ScalarAsync(Intakes));
    }

    [Fact]
    public async Task Full_flow_creates_exactly_one_complaint_only_after_confirmation()
    {
        await using var host = await StartAsync(postgres);
        await host.SendAsync(WebhookFixtures.Text(Reporter, "merhaba"));
        Assert.Contains("1. Yol ve Kaldırım", (await host.OutboundTextsAsync())[0], StringComparison.Ordinal);
        await host.SendAsync(WebhookFixtures.Text(Reporter, "1"));
        await host.SendAsync(WebhookFixtures.Text(Reporter, "Kaldırım taşları kırık (temsili)."));
        await host.SendAsync(WebhookFixtures.Location(Reporter, 40.4105, 26.6712));
        await host.SendAsync(WebhookFixtures.Image(Reporter, "media-1"));
        Assert.StartsWith("Başvuru özeti:", (await host.OutboundTextsAsync())[0], StringComparison.Ordinal);
        Assert.Equal(0, await host.ScalarAsync(Complaints)); // onaysız şikâyet yok

        await host.SendAsync(WebhookFixtures.Text(Reporter, "onaylıyorum"));
        Assert.Equal(1, await host.ScalarAsync(Complaints));
        Assert.Equal(1, await host.ScalarAsync("SELECT count(*) FROM complaints.complaint_attachments"));
        Assert.Equal("Submitted", await host.StringAsync("SELECT state FROM messaging.intake_sessions"));
        Assert.Equal(1, await host.ScalarAsync(
            "SELECT count(*) FROM complaints.complaints c JOIN messaging.intake_sessions s ON s.submitted_complaint_id = c.id AND c.source_intake_session_id = s.id"));

        // "Başvurunuz alındı" yalnızca ComplaintSubmitted handler'ından gelir; iki kez dağıtım tek mesaj üretir.
        await host.DispatchOutboxAsync();
        await host.DispatchOutboxAsync();
        var reference = await host.StringAsync("SELECT public_reference FROM complaints.complaints");
        var received = (await host.OutboundTextsAsync()).Where(t => t.StartsWith("Başvurunuz alındı", StringComparison.Ordinal)).ToList();
        Assert.Contains(reference!, Assert.Single(received), StringComparison.Ordinal);

        // Gönderim: mock sağlayıcıya gider, durum Sent olur.
        Assert.True(await host.SendOutboundAsync() > 0);
        Assert.Equal(0, await host.ScalarAsync("SELECT count(*) FROM messaging.messages WHERE direction = 'Outbound' AND delivery_status <> 'Sent'"));
    }

    [Fact]
    public async Task Crash_after_complaint_creation_is_recovered_without_second_complaint()
    {
        await using var host = await StartAsync(postgres);
        await host.SendAsync(WebhookFixtures.Text(Reporter, "merhaba"));
        await host.SendAsync(WebhookFixtures.Text(Reporter, "1"));
        await host.SendAsync(WebhookFixtures.Text(Reporter, "Açıklama (temsili)."));
        await host.SendAsync(WebhookFixtures.Text(Reporter, "Yazıcızade Mah. (temsili)"));
        await host.SendAsync(WebhookFixtures.Text(Reporter, "atla"));

        // Önceki denemede şikâyet oluşmuş ama intake işaretlenmeden süreç çökmüş gibi: aynı intake kimliğiyle doğrudan çağrı.
        var sessionId = Guid.Parse((await host.StringAsync("SELECT id::text FROM messaging.intake_sessions"))!);
        var reporterId = Guid.Parse((await host.StringAsync("SELECT reporter_user_id::text FROM messaging.conversations"))!);
        var categoryId = Guid.Parse((await host.StringAsync("SELECT id::text FROM complaints.complaint_categories"))!);
        await using (var scope = host.CreateScope())
        {
            var first = await scope.ServiceProvider.GetRequiredService<IComplaintIntake>().SubmitFromIntakeAsync(
                new SubmitFromIntakeRequest(sessionId, reporterId, categoryId, "Açıklama (temsili).", null, null, "Yazıcızade Mah. (temsili)",
                    LocationSource.TextAddress, []), Ct);
            Assert.True(first.Value.Created);
        }

        await host.SendAsync(WebhookFixtures.Text(Reporter, "1"));
        Assert.Equal(1, await host.ScalarAsync(Complaints));
        Assert.Equal("Submitted", await host.StringAsync("SELECT state FROM messaging.intake_sessions"));
    }

    [Fact]
    public async Task Cancel_correction_and_unsupported_type()
    {
        await using var host = await StartAsync(postgres);

        // Desteklenmeyen tür: kaydedilir, açıklanır, intake başlar; sistem hata vermez.
        await host.SendAsync(WebhookFixtures.Audio(Reporter));
        Assert.Equal("Unsupported", await host.StringAsync("SELECT message_type FROM messaging.messages WHERE direction = 'Inbound'"));
        var replies = await host.OutboundTextsAsync();
        Assert.Contains(BotMessages.Unsupported, replies);

        // İptal
        await host.SendAsync(WebhookFixtures.Text(Reporter, "1"));
        await host.SendAsync(WebhookFixtures.Text(Reporter, "iptal"));
        Assert.Equal("Cancelled", await host.StringAsync("SELECT state FROM messaging.intake_sessions"));
        Assert.Equal(BotMessages.Cancelled, (await host.OutboundTextsAsync())[0]);

        // Yeni intake + düzeltme: açıklama özetten düzeltilir, şikâyet düzeltilmiş açıklamayla oluşur.
        await host.SendAsync(WebhookFixtures.Text(Reporter, "yeniden merhaba"));
        await host.SendAsync(WebhookFixtures.Text(Reporter, "1"));
        await host.SendAsync(WebhookFixtures.Text(Reporter, "İlk açıklama."));
        await host.SendAsync(WebhookFixtures.Location(Reporter, 40.41, 26.67));
        await host.SendAsync(WebhookFixtures.Text(Reporter, "atla"));
        await host.SendAsync(WebhookFixtures.Text(Reporter, "3"));
        await host.SendAsync(WebhookFixtures.Text(Reporter, "Düzeltilmiş açıklama (temsili)."));
        Assert.StartsWith("Başvuru özeti:", (await host.OutboundTextsAsync())[0], StringComparison.Ordinal);
        await host.SendAsync(WebhookFixtures.Text(Reporter, "1"));
        Assert.Equal("Düzeltilmiş açıklama (temsili).", await host.StringAsync("SELECT description FROM complaints.complaints"));
        Assert.Equal(2, await host.ScalarAsync(Intakes));
    }

    [Fact]
    public async Task Inactive_intake_expires_and_next_message_starts_a_new_one()
    {
        await using var host = await StartAsync(postgres);
        await host.SendAsync(WebhookFixtures.Text(Reporter, "merhaba"));
        await host.SendAsync(WebhookFixtures.Text(Reporter, "1"));

        host.Clock.Advance(TimeSpan.FromMinutes(90));
        Assert.Equal(0, await host.SweepExpiredAsync());
        host.Clock.Advance(TimeSpan.FromMinutes(31));
        Assert.Equal(1, await host.SweepExpiredAsync());
        Assert.Equal(0, await host.SweepExpiredAsync());
        Assert.Equal("Expired", await host.StringAsync("SELECT state FROM messaging.intake_sessions"));
        Assert.Equal(BotMessages.Expired, (await host.OutboundTextsAsync())[0]);
        Assert.Equal(0, await host.ScalarAsync(Complaints));

        await host.SendAsync(WebhookFixtures.Text(Reporter, "tekrar merhaba"));
        Assert.Equal(1, await host.ScalarAsync($"SELECT count(*) FROM messaging.intake_sessions WHERE state = 'AwaitingCategory'"));
    }

    [Fact]
    public async Task Transient_media_failure_is_retried_then_dead_lettered()
    {
        await using var host = await StartAsync(postgres, new Dictionary<string, string?> { ["Messaging:MaxAttempts"] = "2" });
        await host.SendAsync(WebhookFixtures.Text(Reporter, "merhaba"));
        using var response = await host.PostWebhookAsync(WebhookFixtures.Image(Reporter, "mock-transient"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Equal(1, await host.ProcessInboxAsync());
        const string State = "SELECT processing_state FROM messaging.webhook_inbox_events WHERE payload->'message'->'image'->>'id' = 'mock-transient'";
        Assert.Equal("Pending", await host.StringAsync(State));
        Assert.Equal(0, await host.ProcessInboxAsync()); // backoff süresi dolmadı

        host.Clock.Advance(TimeSpan.FromMinutes(20));
        Assert.Equal(1, await host.ProcessInboxAsync());
        Assert.Equal("DeadLetter", await host.StringAsync(State));
        host.Clock.Advance(TimeSpan.FromHours(2));
        Assert.Equal(0, await host.ProcessInboxAsync()); // sonsuz döngü yok
        Assert.Equal(1, await host.ScalarAsync(InboundMessages));
    }
}
