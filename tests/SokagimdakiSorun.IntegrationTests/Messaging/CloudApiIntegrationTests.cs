using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SokagimdakiSorun.IntegrationTests.Database.Support;
using SokagimdakiSorun.IntegrationTests.Messaging.Support;
using SokagimdakiSorun.Messaging.Infrastructure.WhatsApp;
using SokagimdakiSorun.Messaging.TestSupport;
using static SokagimdakiSorun.IntegrationTests.Messaging.Support.MessagingTestHost;

namespace SokagimdakiSorun.IntegrationTests.Messaging;

/// <summary>
/// <c>WhatsApp__Provider=CloudApi</c> ile gerçek <c>Program</c> + gerçek PostgreSQL 18: Cloud istemcisi, giden kuyruk, 24 saat penceresi
/// ve şablon yolu, teslim durumu idempotensi, medya indirme ve açılış doğrulaması. HTTP katmanı <see cref="FakeGraphHandler"/>'dır.
/// <b>Güven sınırı:</b> sahte Graph API'ye karşıdır; gerçek Meta davranışı (kabul, teslim, şablon onayı) kanıtlanmaz.
/// </summary>
[Collection(DatabaseCollection.Name)]
[Trait("Category", "Database")]
public sealed class CloudApiIntegrationTests(PostgresFixture postgres)
{
    private const string LastOutboundStatus =
        "SELECT delivery_status FROM messaging.messages WHERE direction = 'Outbound' ORDER BY created_at DESC, id DESC LIMIT 1";

    private static readonly Dictionary<string, string?> Template = new()
    {
        ["WhatsApp:Templates:ComplaintUpdate:Name"] = "complaint_update",
        ["WhatsApp:Templates:ComplaintUpdate:LanguageCode"] = "tr",
    };

    [Fact]
    public async Task Cloud_provider_is_selected_and_reply_is_sent_with_documented_request()
    {
        var graph = new FakeGraphHandler();
        await using var host = await StartCloudAsync(postgres, graph);
        Assert.IsType<CloudWhatsAppClient>(host.Services.GetRequiredService<IWhatsAppClient>());

        await host.SendAsync(WebhookFixtures.Text(Reporter, "merhaba"));
        graph.Enqueue(FakeGraphHandler.Accepted("wamid.cloud.1"));
        Assert.Equal(1, await host.SendOutboundAsync());

        var request = Assert.Single(graph.MessageRequests);
        Assert.Equal($"/v27.0/{WebhookFixtures.PhoneNumberId}/messages", request.Uri.AbsolutePath);
        Assert.Equal($"Bearer {FakeGraphHandler.AccessToken}", request.Authorization);
        Assert.Equal(Reporter, request.Json.GetProperty("to").GetString());
        Assert.Equal("text", request.Json.GetProperty("type").GetString());
        Assert.Equal((await host.OutboundTextsAsync())[0], request.Json.GetProperty("text").GetProperty("body").GetString());
        Assert.Equal("Sent", await host.StringAsync(LastOutboundStatus));
        Assert.Equal("wamid.cloud.1", await host.StringAsync(
            "SELECT provider_message_id FROM messaging.messages WHERE direction = 'Outbound'"));
    }

    [Fact]
    public async Task Transient_graph_error_is_retried_and_permanent_error_fails()
    {
        var graph = new FakeGraphHandler();
        await using var host = await StartCloudAsync(postgres, graph);

        await host.SendAsync(WebhookFixtures.Text(Reporter, "merhaba"));
        graph.Enqueue(FakeGraphHandler.GraphError(HttpStatusCode.ServiceUnavailable, 133004));
        Assert.Equal(1, await host.SendOutboundAsync());
        Assert.Equal("Queued", await host.StringAsync(LastOutboundStatus));
        Assert.Equal("http_503:provider_error:133004", await host.StringAsync(
            "SELECT last_error FROM messaging.messages WHERE direction = 'Outbound'"));
        Assert.Equal(0, await host.SendOutboundAsync()); // backoff
        host.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(1, await host.SendOutboundAsync());
        Assert.Equal("Sent", await host.StringAsync(LastOutboundStatus));
        Assert.Equal(2, graph.MessageRequests.Count());

        await host.SendAsync(WebhookFixtures.Text(Reporter, "1"));
        graph.Enqueue(FakeGraphHandler.GraphError(HttpStatusCode.BadRequest, 131026));
        await host.SendOutboundAsync();
        Assert.Equal("Failed", await host.StringAsync(LastOutboundStatus));
        host.Clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(0, await host.SendOutboundAsync());
        Assert.Equal(3, graph.MessageRequests.Count());
    }

    [Fact]
    public async Task Closed_window_sends_configured_template_for_notifications_and_never_free_text()
    {
        var graph = new FakeGraphHandler();
        await using var host = await StartCloudAsync(postgres, graph, Template);
        await host.CompleteIntakeAsync();
        await host.SendOutboundAsync(); // intake cevapları pencere açıkken metin olarak gider
        var textsBefore = graph.MessageRequests.Count();
        Assert.All(graph.MessageRequests, r => Assert.Equal("text", r.Json.GetProperty("type").GetString()));

        // "Başvurunuz alındı" bildirimi kuyruğa girer, ama gönderim 25 saat sonra (pencere kapalı) olur.
        await host.DispatchOutboxAsync();
        var reference = await host.StringAsync(
            "SELECT complaint_reference FROM messaging.messages WHERE dedup_key LIKE 'event:%'");
        Assert.NotNull(reference);
        Assert.StartsWith("SS-", reference, StringComparison.Ordinal);

        host.Clock.Advance(TimeSpan.FromHours(25));
        Assert.Equal(1, await host.SendOutboundAsync());

        var sent = graph.MessageRequests.Skip(textsBefore).ToList();
        var template = Assert.Single(sent);
        Assert.Equal("template", template.Json.GetProperty("type").GetString());
        Assert.False(template.Json.TryGetProperty("text", out _)); // serbest metin yok
        var t = template.Json.GetProperty("template");
        Assert.Equal("complaint_update", t.GetProperty("name").GetString());
        Assert.Equal("tr", t.GetProperty("language").GetProperty("code").GetString());
        Assert.Equal(reference, t.GetProperty("components")[0].GetProperty("parameters")[0].GetProperty("text").GetString());
        Assert.Equal("template|Sent", await host.StringAsync(
            "SELECT provider_message_type || '|' || delivery_status FROM messaging.messages WHERE dedup_key LIKE 'event:%'"));
    }

    [Fact]
    public async Task Closed_window_without_template_or_for_bot_replies_fails_without_any_request()
    {
        var graph = new FakeGraphHandler();
        await using var host = await StartCloudAsync(postgres, graph, Template);
        await host.SendAsync(WebhookFixtures.Text(Reporter, "merhaba")); // bot cevabı: referans yok → şablon uygun değil

        host.Clock.Advance(TimeSpan.FromHours(25));
        Assert.Equal(1, await host.SendOutboundAsync());
        Assert.Equal("Failed", await host.StringAsync(LastOutboundStatus));
        Assert.StartsWith("window_closed", await host.StringAsync(
            "SELECT last_error FROM messaging.messages WHERE direction = 'Outbound'"), StringComparison.Ordinal);
        Assert.Empty(graph.MessageRequests);

        // Şablon yapılandırılmamış: bildirim de Failed olur, istek atılmaz.
        var graph2 = new FakeGraphHandler();
        await using var plain = await StartCloudAsync(postgres, graph2);
        await plain.CompleteIntakeAsync();
        await plain.SendOutboundAsync();
        var before = graph2.MessageRequests.Count();
        await plain.DispatchOutboxAsync();
        plain.Clock.Advance(TimeSpan.FromHours(25));
        await plain.SendOutboundAsync();
        Assert.Equal("Failed", await plain.StringAsync(
            "SELECT delivery_status FROM messaging.messages WHERE dedup_key LIKE 'event:%'"));
        Assert.Equal(before, graph2.MessageRequests.Count());
    }

    [Fact]
    public async Task Delivery_status_webhooks_are_idempotent_and_never_regress()
    {
        var graph = new FakeGraphHandler();
        await using var host = await StartCloudAsync(postgres, graph);
        await host.SendAsync(WebhookFixtures.Text(Reporter, "merhaba"));
        graph.Enqueue(FakeGraphHandler.Accepted("wamid.status.1"));
        await host.SendOutboundAsync();

        const string Status = "SELECT delivery_status FROM messaging.messages WHERE provider_message_id = 'wamid.status.1'";
        await host.SendAsync(WebhookFixtures.Status("wamid.status.1", "delivered"));
        await host.SendAsync(WebhookFixtures.Status("wamid.status.1", "delivered")); // tekrar teslim
        Assert.Equal("Delivered", await host.StringAsync(Status));
        await host.SendAsync(WebhookFixtures.Status("wamid.status.1", "read"));
        await host.SendAsync(WebhookFixtures.Status("wamid.status.1", "sent"));      // sırası bozuk
        await host.SendAsync(WebhookFixtures.Status("wamid.status.1", "delivered")); // sırası bozuk
        Assert.Equal("Read", await host.StringAsync(Status));
        Assert.Equal(3, await host.ScalarAsync(
            "SELECT count(*) FROM messaging.webhook_inbox_events WHERE dedup_key LIKE 'status:wamid.status.1:%'"));

        // failed: hata kodu saklanır (başlık/ayrıntı saklanmaz); okunduktan sonra gelen failed geri almaz.
        await host.SendAsync(WebhookFixtures.Text(Reporter, "1"));
        graph.Enqueue(FakeGraphHandler.Accepted("wamid.status.2"));
        await host.SendOutboundAsync();
        await host.SendAsync(WebhookFixtures.StatusFailed("wamid.status.2", 131026));
        await host.SendAsync(WebhookFixtures.StatusFailed("wamid.status.2", 131026));
        Assert.Equal("Failed|provider_error:131026", await host.StringAsync(
            "SELECT delivery_status || '|' || last_error FROM messaging.messages WHERE provider_message_id = 'wamid.status.2'"));
        await host.SendAsync(WebhookFixtures.StatusFailed("wamid.status.1", 131026));
        Assert.Equal("Read", await host.StringAsync(Status));
        Assert.DoesNotContain(host.Logs.Messages, l => l.Contains("Temsili hata", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Inbound_image_is_downloaded_through_graph_and_stored_via_media_service()
    {
        var graph = new FakeGraphHandler();
        graph.RegisterMedia("900000000000001", MockWhatsAppClient.SamplePng, "image/png");
        graph.RegisterMedia("900000000000002", [0x25, 0x50, 0x44, 0x46], "application/pdf");
        graph.RegisterMedia("900000000000003", [0x00, 0x01, 0x02, 0x03], "image/jpeg"); // tür beyanı ile içerik uyuşmuyor
        await using var host = await StartCloudAsync(postgres, graph);

        await host.SendAsync(WebhookFixtures.Image(Reporter, "900000000000001", "wamid.img.1"));
        await host.SendAsync(WebhookFixtures.Image(Reporter, "900000000000002", "wamid.img.2"));
        await host.SendAsync(WebhookFixtures.Image(Reporter, "900000000000003", "wamid.img.3"));

        const string MediaOf = "SELECT count(*) FROM messaging.messages WHERE provider_message_id = @id AND media_file_id IS NOT NULL";
        Assert.Equal(1, await host.ScalarAsync(MediaOf, ("id", "wamid.img.1")));
        Assert.Equal(1, await host.ScalarAsync("SELECT count(*) FROM media.media_files WHERE content_type = 'image/png'"));
        Assert.Equal(0, await host.ScalarAsync(MediaOf, ("id", "wamid.img.2"))); // MIME reddi: indirilmedi
        Assert.Equal(0, await host.ScalarAsync(MediaOf, ("id", "wamid.img.3"))); // Media modülü imza doğrulaması reddetti
        Assert.Equal(1, await host.ScalarAsync("SELECT count(*) FROM media.media_files"));
        Assert.DoesNotContain(graph.Requests, r => r.Uri.Host == "media.example.test" && r.Uri.AbsolutePath.EndsWith("/900000000000002", StringComparison.Ordinal));

        var logs = string.Join('\n', host.Logs.Messages);
        Assert.DoesNotContain(FakeGraphHandler.AccessToken, logs, StringComparison.Ordinal);
        Assert.DoesNotContain(FakeGraphHandler.MediaHost, logs, StringComparison.Ordinal);
        Assert.DoesNotContain("token-like", logs, StringComparison.Ordinal);
        Assert.DoesNotContain("graph.facebook.com", logs, StringComparison.Ordinal);
        Assert.DoesNotContain(Reporter, logs, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Transient_media_failure_retries_the_inbox_event()
    {
        var graph = new FakeGraphHandler();
        graph.RegisterMedia("900000000000009", MockWhatsAppClient.SamplePng, "image/png");
        await using var host = await StartCloudAsync(postgres, graph);

        graph.Enqueue(FakeGraphHandler.GraphError(HttpStatusCode.ServiceUnavailable, 131016));
        using (var response = await host.PostWebhookAsync(WebhookFixtures.Image(Reporter, "900000000000009", "wamid.img.9")))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        await host.ProcessInboxAsync();
        Assert.Equal(0, await host.ScalarAsync("SELECT count(*) FROM messaging.messages WHERE direction = 'Inbound'"));
        Assert.Equal("Pending", await host.StringAsync("SELECT processing_state FROM messaging.webhook_inbox_events"));

        host.Clock.Advance(TimeSpan.FromMinutes(5));
        await host.ProcessInboxAsync();
        Assert.Equal(1, await host.ScalarAsync(
            "SELECT count(*) FROM messaging.messages WHERE provider_message_id = 'wamid.img.9' AND media_file_id IS NOT NULL"));
    }

    [Theory]
    [InlineData("WhatsApp:AccessToken", "WhatsApp__AccessToken")]
    [InlineData("WhatsApp:AppSecret", "WhatsApp__AppSecret")]
    [InlineData("WhatsApp:VerifyToken", "WhatsApp__VerifyToken")]
    [InlineData("WhatsApp:PhoneNumberId", "WhatsApp__PhoneNumberId")]
    [InlineData("WhatsApp:GraphApiVersion", "WhatsApp__GraphApiVersion")]
    public async Task Cloud_provider_with_missing_setting_stops_startup_with_clear_error(string key, string expectedName)
    {
        var exception = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await using var host = await StartCloudAsync(postgres, new FakeGraphHandler(), new Dictionary<string, string?> { [key] = "" });
        });

        var validation = FindValidationException(exception);
        Assert.NotNull(validation);
        Assert.Contains(validation.Failures, f => f.Contains(expectedName, StringComparison.Ordinal));
        Assert.DoesNotContain(FakeGraphHandler.AccessToken, validation.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(WebhookFixtures.AppSecret, validation.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invalid_graph_version_stops_startup()
    {
        var exception = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await using var host = await StartCloudAsync(
                postgres, new FakeGraphHandler(), new Dictionary<string, string?> { ["WhatsApp:GraphApiVersion"] = "v2" });
        });

        Assert.Contains(FindValidationException(exception)!.Failures, f => f.Contains("GraphApiVersion", StringComparison.Ordinal));
    }

    private static OptionsValidationException? FindValidationException(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is OptionsValidationException validation)
            {
                return validation;
            }

            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    if (FindValidationException(inner) is { } found)
                    {
                        return found;
                    }
                }
            }
        }

        return null;
    }
}
