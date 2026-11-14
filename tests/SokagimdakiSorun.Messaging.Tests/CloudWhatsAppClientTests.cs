using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SokagimdakiSorun.Messaging.Infrastructure.WhatsApp;
using SokagimdakiSorun.Messaging.TestSupport;

namespace SokagimdakiSorun.Messaging.Tests;

/// <summary>
/// <see cref="CloudWhatsAppClient"/>: istek yolu/başlık/gövde, hata eşlemesi, taşıma hataları, medya indirme ve log gizliliği.
/// <b>Güven sınırı:</b> sahte <see cref="FakeGraphHandler"/>'a karşıdır; gerçek Meta davranışını kanıtlamaz.
/// </summary>
public sealed class CloudWhatsAppClientTests
{
    private const string PhoneId = "100000000000001";
    private const string Recipient = "905550000001";
    private const string SecretText = "GIZLI-METIN-icerik";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Text_message_uses_versioned_path_bearer_token_and_documented_body()
    {
        var (client, handler, _) = Create();
        var messageId = Guid.CreateVersion7();
        handler.Enqueue(FakeGraphHandler.Accepted("wamid.ABC"));

        var result = await client.SendTextAsync(new OutboundText(PhoneId, Recipient, SecretText, messageId), Ct);

        Assert.Equal(SendResult.Success("wamid.ABC"), result);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal($"https://graph.facebook.com/v27.0/{PhoneId}/messages", request.Uri.ToString());
        Assert.Equal($"Bearer {FakeGraphHandler.AccessToken}", request.Authorization);
        Assert.Equal("application/json", request.ContentType);
        var json = request.Json;
        Assert.Equal("whatsapp", json.GetProperty("messaging_product").GetString());
        Assert.Equal("individual", json.GetProperty("recipient_type").GetString());
        Assert.Equal(Recipient, json.GetProperty("to").GetString());
        Assert.Equal("text", json.GetProperty("type").GetString());
        Assert.Equal(SecretText, json.GetProperty("text").GetProperty("body").GetString());
        Assert.False(json.GetProperty("text").GetProperty("preview_url").GetBoolean());
    }

    [Fact]
    public async Task Location_and_template_bodies_follow_documented_shape()
    {
        var (client, handler, _) = Create();

        await client.SendLocationAsync(new OutboundLocation(PhoneId, Recipient, 40.4105, 26.6712, "Yer (temsili)", null, Guid.CreateVersion7()), Ct);
        await client.SendTemplateAsync(
            new OutboundTemplate(PhoneId, Recipient, "complaint_update", "tr", ["SS-ABCD-EFGH"], Guid.CreateVersion7()), Ct);
        await client.SendTemplateAsync(new OutboundTemplate(PhoneId, Recipient, "hello_world", "en_US", [], Guid.CreateVersion7()), Ct);

        var requests = handler.Requests.ToList();
        var location = requests[0].Json;
        Assert.Equal("location", location.GetProperty("type").GetString());
        Assert.Equal(40.4105, location.GetProperty("location").GetProperty("latitude").GetDouble());
        Assert.Equal(26.6712, location.GetProperty("location").GetProperty("longitude").GetDouble());
        Assert.Equal("Yer (temsili)", location.GetProperty("location").GetProperty("name").GetString());
        Assert.False(location.GetProperty("location").TryGetProperty("address", out _));

        var template = requests[1].Json;
        Assert.Equal("template", template.GetProperty("type").GetString());
        var t = template.GetProperty("template");
        Assert.Equal("complaint_update", t.GetProperty("name").GetString());
        Assert.Equal("tr", t.GetProperty("language").GetProperty("code").GetString());
        var component = Assert.Single(t.GetProperty("components").EnumerateArray());
        Assert.Equal("body", component.GetProperty("type").GetString());
        var parameter = Assert.Single(component.GetProperty("parameters").EnumerateArray());
        Assert.Equal("text", parameter.GetProperty("type").GetString());
        Assert.Equal("SS-ABCD-EFGH", parameter.GetProperty("text").GetString());

        Assert.False(requests[2].Json.GetProperty("template").TryGetProperty("components", out _)); // parametresiz şablon
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, 131047, false, "http_400:provider_error:131047:window_closed")]
    [InlineData(HttpStatusCode.BadRequest, 131026, false, "http_400:provider_error:131026")]
    [InlineData(HttpStatusCode.BadRequest, 132001, false, "http_400:provider_error:132001")]
    [InlineData(HttpStatusCode.BadRequest, 131049, false, "http_400:provider_error:131049")]
    [InlineData(HttpStatusCode.Unauthorized, 190, false, "http_401:provider_error:190")]
    [InlineData(HttpStatusCode.BadRequest, 130429, true, "http_400:provider_error:130429")]
    [InlineData(HttpStatusCode.BadRequest, 131056, true, "http_400:provider_error:131056")]
    [InlineData(HttpStatusCode.BadRequest, 4, true, "http_400:provider_error:4")]
    [InlineData(HttpStatusCode.InternalServerError, 131000, true, "http_500:provider_error:131000")]
    [InlineData(HttpStatusCode.ServiceUnavailable, 133004, true, "http_503:provider_error:133004")]
    public async Task Graph_error_codes_map_to_transient_or_permanent(HttpStatusCode status, int code, bool transient, string error)
    {
        var (client, handler, _) = Create();
        handler.Enqueue(FakeGraphHandler.GraphError(status, code));

        var result = await client.SendTextAsync(new OutboundText(PhoneId, Recipient, "x", Guid.CreateVersion7()), Ct);

        Assert.Equal(transient ? SendOutcome.TransientFailure : SendOutcome.PermanentFailure, result.Outcome);
        Assert.Equal(error, result.Error);
        Assert.Null(result.ProviderMessageId);
    }

    [Fact]
    public async Task Bodyless_and_transport_failures_are_classified_without_leaking_details()
    {
        var (client, handler, _) = Create(timeout: TimeSpan.FromSeconds(1));
        handler.Enqueue(
            FakeGraphHandler.Status(HttpStatusCode.BadGateway),
            FakeGraphHandler.Status(HttpStatusCode.TooManyRequests, "<html>"),
            FakeGraphHandler.Status(HttpStatusCode.Forbidden),
            FakeGraphHandler.Status(HttpStatusCode.OK, """{"messaging_product":"whatsapp","messages":[]}"""),
            FakeGraphHandler.NetworkFailure(),
            FakeGraphHandler.Hang());

        var results = new List<SendResult>();
        for (var i = 0; i < 6; i++)
        {
            results.Add(await client.SendTextAsync(new OutboundText(PhoneId, Recipient, "x", Guid.CreateVersion7()), Ct));
        }

        Assert.Equal(SendResult.Transient("http_502"), results[0]);
        Assert.Equal(SendResult.Transient("http_429"), results[1]);
        Assert.Equal(SendResult.Permanent("http_403"), results[2]);
        Assert.Equal(SendResult.Permanent("invalid_provider_response"), results[3]); // kabul edilmiş olabilir: tekrar yok
        Assert.Equal(SendResult.Transient("network_error"), results[4]);
        Assert.Equal(SendResult.Transient("timeout"), results[5]);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_swallowed()
    {
        var (client, handler, _) = Create();
        handler.Enqueue(FakeGraphHandler.Hang());
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.SendTextAsync(new OutboundText(PhoneId, Recipient, "x", Guid.CreateVersion7()), cts.Token));
    }

    [Fact]
    public async Task Non_numeric_business_number_falls_back_to_configured_phone_number_id()
    {
        var (client, handler, _) = Create();
        await client.SendTextAsync(new OutboundText("not-a-number", Recipient, "x", Guid.CreateVersion7()), Ct);
        Assert.Equal($"/v27.0/{PhoneId}/messages", Assert.Single(handler.Requests).Uri.AbsolutePath);
    }

    [Fact]
    public async Task Media_download_resolves_metadata_then_downloads_with_token_and_verifies_hash()
    {
        var (client, handler, logs) = Create();
        handler.RegisterMedia("1234567890", MockWhatsAppClient.SamplePng, "image/png");

        var result = await client.DownloadMediaAsync("1234567890", Ct);

        Assert.Equal(MediaDownloadOutcome.Downloaded, result.Outcome);
        Assert.Equal("image/png", result.ContentType);
        await using (var content = result.Content!)
        {
            using var copy = new MemoryStream();
            await content.CopyToAsync(copy, Ct);
            Assert.Equal(MockWhatsAppClient.SamplePng, copy.ToArray());
        }

        var requests = handler.Requests.ToList();
        Assert.Equal(2, requests.Count);
        Assert.Equal("https://graph.facebook.com/v27.0/1234567890", requests[0].Uri.ToString());
        Assert.Equal(FakeGraphHandler.MediaUrl("1234567890"), requests[1].Uri);
        Assert.All(requests, r => Assert.Equal($"Bearer {FakeGraphHandler.AccessToken}", r.Authorization));
        AssertNoSecrets(logs);
    }

    [Fact]
    public async Task Media_type_size_hash_and_id_are_checked()
    {
        var (client, handler, logs) = Create(maxMediaBytes: 2048);
        handler.RegisterMedia("111", [1, 2, 3], "application/pdf");
        handler.RegisterMedia("222", new byte[4096], "image/jpeg");                       // meta veri boyutu sınır üstü
        handler.RegisterMedia("333", new byte[4096], "image/jpeg", declaredSize: 100);    // beyan küçük, içerik büyük
        handler.RegisterMedia("444", MockWhatsAppClient.SamplePng, "image/png; charset=binary");

        Assert.Equal(MediaDownloadResult.Permanent("media_type_not_allowed"), await client.DownloadMediaAsync("111", Ct));
        Assert.Equal(MediaDownloadResult.Permanent("media_too_large"), await client.DownloadMediaAsync("222", Ct));
        Assert.Equal(MediaDownloadResult.Permanent("media_too_large"), await client.DownloadMediaAsync("333", Ct));
        Assert.Equal(MediaDownloadResult.Permanent("invalid_media_id"), await client.DownloadMediaAsync("../me/x", Ct));
        var ok = await client.DownloadMediaAsync("444", Ct); // MIME parametresi atılır
        Assert.Equal(MediaDownloadOutcome.Downloaded, ok.Outcome);
        Assert.Equal("image/png", ok.ContentType);
        await ok.Content!.DisposeAsync();

        // Tür/boyut reddinde indirme isteği atılmaz (yalnızca meta veri).
        Assert.DoesNotContain(handler.Requests, r => r.Uri.Host == "media.example.test"
            && (r.Uri.AbsolutePath.EndsWith("/111", StringComparison.Ordinal) || r.Uri.AbsolutePath.EndsWith("/222", StringComparison.Ordinal)));
        AssertNoSecrets(logs);
    }

    [Fact]
    public async Task Media_failures_map_expired_url_to_transient_and_hash_mismatch_to_permanent()
    {
        var (client, handler, logs) = Create();
        handler.RegisterMedia("555", MockWhatsAppClient.SamplePng, "image/png");

        // İndirme 404 (URL süresi dolmuş) → geçici: yeniden denemede yeni URL alınır.
        handler.Enqueue(
            (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $$"""{"url":"{{FakeGraphHandler.MediaUrl("555")}}","mime_type":"image/png","file_size":10,"id":"555"}""",
                    System.Text.Encoding.UTF8, "application/json"),
            }),
            FakeGraphHandler.Status(HttpStatusCode.NotFound));
        Assert.Equal(MediaDownloadResult.Transient("media_download:http_404"), await client.DownloadMediaAsync("555", Ct));

        // Meta veri hataları
        handler.Enqueue(FakeGraphHandler.GraphError(HttpStatusCode.BadRequest, 100));
        Assert.Equal(MediaDownloadResult.Permanent("media_metadata:http_400:provider_error:100"), await client.DownloadMediaAsync("555", Ct));
        handler.Enqueue(FakeGraphHandler.NetworkFailure());
        Assert.Equal(MediaDownloadResult.Transient("network_error"), await client.DownloadMediaAsync("555", Ct));
        handler.Enqueue(FakeGraphHandler.Status(HttpStatusCode.OK, """{"url":"http://insecure.example.test/x","mime_type":"image/png"}"""));
        Assert.Equal(MediaDownloadResult.Permanent("media_metadata_invalid"), await client.DownloadMediaAsync("555", Ct));

        // Özet uyuşmazlığı (hex ve base64)
        var wrongHex = Convert.ToHexStringLower(SHA256.HashData([9, 9, 9]));
        Assert.False(CloudWhatsAppClient.HashMatches(new MemoryStream(MockWhatsAppClient.SamplePng), wrongHex));
        Assert.True(CloudWhatsAppClient.HashMatches(new MemoryStream(MockWhatsAppClient.SamplePng),
            Convert.ToBase64String(SHA256.HashData(MockWhatsAppClient.SamplePng))));
        Assert.True(CloudWhatsAppClient.HashMatches(new MemoryStream(MockWhatsAppClient.SamplePng), null));

        AssertNoSecrets(logs);
    }

    [Fact]
    public async Task Logs_hold_no_token_recipient_text_or_media_url()
    {
        var (client, handler, logs) = Create();
        handler.Enqueue(FakeGraphHandler.GraphError(HttpStatusCode.BadRequest, 131026));
        await client.SendTextAsync(new OutboundText(PhoneId, Recipient, SecretText, Guid.CreateVersion7()), Ct);
        await client.SendTemplateAsync(new OutboundTemplate(PhoneId, Recipient, "complaint_update", "tr", [SecretText], Guid.CreateVersion7()), Ct);

        Assert.NotEmpty(logs);
        AssertNoSecrets(logs);
        Assert.DoesNotContain(logs, l => l.Contains(SecretText, StringComparison.Ordinal) || l.Contains(Recipient, StringComparison.Ordinal));
    }

    [Fact]
    public void Cloud_options_require_every_secret_and_a_valid_version_without_echoing_values()
    {
        Assert.Empty(Validate(new WhatsAppOptions())); // Mock: secret zorunlu değil

        var missing = Validate(new WhatsAppOptions { Provider = WhatsAppProvider.CloudApi });
        foreach (var key in new[] { "AccessToken", "AppSecret", "VerifyToken", "PhoneNumberId", "GraphApiVersion" })
        {
            Assert.Contains(missing, m => m.Contains($"WhatsApp__{key}", StringComparison.Ordinal));
        }

        var complete = CloudOptions();
        Assert.Empty(Validate(complete));

        foreach (var version in new[] { "v2", "27.0", "v27", "V27.0", "v27.0.1", "latest" })
        {
            complete.GraphApiVersion = version;
            Assert.Contains(Validate(complete), e => e.Contains("GraphApiVersion", StringComparison.Ordinal));
        }

        var secretLeak = CloudOptions();
        secretLeak.PhoneNumberId = "abc";
        Assert.DoesNotContain(Validate(secretLeak), e => e.Contains(FakeGraphHandler.AccessToken, StringComparison.Ordinal));

        var halfTemplate = CloudOptions();
        halfTemplate.Templates.ComplaintUpdate.Name = "complaint_update";
        Assert.Contains(Validate(halfTemplate), e => e.Contains("Templates__ComplaintUpdate", StringComparison.Ordinal));
        halfTemplate.Templates.ComplaintUpdate.LanguageCode = "tr";
        Assert.Empty(Validate(halfTemplate));
        halfTemplate.Templates.ComplaintUpdate.Name = "Bad Name";
        Assert.NotEmpty(Validate(halfTemplate));
    }

    private static List<string> Validate(WhatsAppOptions options)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true);
        return [.. results.Select(r => r.ErrorMessage ?? string.Empty)];
    }

    private static WhatsAppOptions CloudOptions(int maxMediaBytes = 10 * 1024 * 1024, TimeSpan? timeout = null) => new()
    {
        Provider = WhatsAppProvider.CloudApi,
        AccessToken = FakeGraphHandler.AccessToken,
        AppSecret = WebhookFixtures.AppSecret,
        VerifyToken = WebhookFixtures.VerifyToken,
        PhoneNumberId = PhoneId,
        GraphApiVersion = FakeGraphHandler.GraphVersion,
        MaxMediaBytes = maxMediaBytes,
        HttpTimeout = timeout ?? TimeSpan.FromSeconds(30),
    };

    private static (CloudWhatsAppClient Client, FakeGraphHandler Handler, ConcurrentQueue<string> Logs) Create(
        int maxMediaBytes = 10 * 1024 * 1024, TimeSpan? timeout = null)
    {
        var handler = new FakeGraphHandler();
        var logs = new ConcurrentQueue<string>();
        var client = new CloudWhatsAppClient(
            new SingleHandlerFactory(handler), Options.Create(CloudOptions(maxMediaBytes, timeout)), new ListLogger(logs));
        return (client, handler, logs);
    }

    private static void AssertNoSecrets(IEnumerable<string> logs) => Assert.DoesNotContain(logs, l =>
        l.Contains(FakeGraphHandler.AccessToken, StringComparison.Ordinal)
        || l.Contains(FakeGraphHandler.MediaHost, StringComparison.Ordinal)
        || l.Contains("token-like", StringComparison.Ordinal)
        || l.Contains(PhoneId, StringComparison.Ordinal));

    private sealed class SingleHandlerFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class ListLogger(ConcurrentQueue<string> sink) : ILogger<CloudWhatsAppClient>
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            sink.Enqueue($"{logLevel}: {formatter(state, exception)} {exception}");
    }
}
