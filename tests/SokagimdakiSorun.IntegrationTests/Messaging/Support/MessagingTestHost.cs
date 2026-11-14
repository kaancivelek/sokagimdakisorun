using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using SokagimdakiSorun.IntegrationTests.Database.Support;
using SokagimdakiSorun.IntegrationTests.Identity;
using SokagimdakiSorun.Messaging.Infrastructure;
using SokagimdakiSorun.Messaging.Infrastructure.WhatsApp;
using SokagimdakiSorun.Messaging.TestSupport;
using SokagimdakiSorun.Platform.Outbox;

namespace SokagimdakiSorun.IntegrationTests.Messaging.Support;

/// <summary>
/// Gerçek <c>Program</c> + gerçek PostgreSQL 18 (test başına boş veritabanı, açılışta tüm migration'lar) + mock WhatsApp sağlayıcısı.
/// Arka plan işleyicileri ve outbox dağıtıcısı kapalıdır; testler turları açıkça çalıştırır (<see cref="ProcessInboxAsync"/>,
/// <see cref="SendOutboundAsync"/>, <see cref="DispatchOutboxAsync"/>). Saat elle ilerletilir (<see cref="Clock"/>).
/// <b>Güven sınırı:</b> mock sağlayıcıya karşıdır; gerçek Meta davranışını kanıtlamaz.
/// </summary>
internal sealed class MessagingTestHost : IAsyncDisposable
{
    public const string Reporter = "905550000001";

    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _sandbox;

    private MessagingTestHost(WebApplicationFactory<Program> factory, string connectionString, string sandbox, ManualTimeProvider clock,
        IdentityTestHost.CapturingLoggerProvider logs)
    {
        _factory = factory;
        ConnectionString = connectionString;
        _sandbox = sandbox;
        Clock = clock;
        Logs = logs;
    }

    public string ConnectionString { get; }

    public ManualTimeProvider Clock { get; }

    public IdentityTestHost.CapturingLoggerProvider Logs { get; }

    public IServiceProvider Services => _factory.Services;

    public MockWhatsAppClient WhatsApp => Services.GetRequiredService<MockWhatsAppClient>();

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task<MessagingTestHost> StartAsync(
        PostgresFixture postgres, IReadOnlyDictionary<string, string?>? settings = null, bool withCategory = true,
        FakeGraphHandler? graph = null)
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        var sandbox = Path.Combine(Path.GetTempPath(), "sokagimdakisorun-tests", Guid.CreateVersion7().ToString("N"));
        var logs = new IdentityTestHost.CapturingLoggerProvider();
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:Default", connectionString);
            builder.UseSetting("Database:ApplyMigrationsOnStartup", "true");
            builder.UseSetting("Outbox:Enabled", "false");
            builder.UseSetting("Media:RootPath", Path.Combine(sandbox, "media"));
            builder.UseSetting("Media:CleanupEnabled", "false");
            builder.UseSetting("Seed:AdminEmail", IdentityTestHost.AdminEmail);
            builder.UseSetting("Seed:AdminPassword", IdentityTestHost.AdminPassword);
            builder.UseSetting("WhatsApp:Provider", "Mock");
            builder.UseSetting("WhatsApp:AppSecret", WebhookFixtures.AppSecret);
            builder.UseSetting("WhatsApp:VerifyToken", WebhookFixtures.VerifyToken);
            builder.UseSetting("Messaging:InboxProcessorEnabled", "false");
            builder.UseSetting("Messaging:OutboundSenderEnabled", "false");
            builder.UseSetting("Messaging:ExpirySweeperEnabled", "false");
            foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
            {
                builder.UseSetting(key, value);
            }

            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<TimeProvider>(clock);
                if (graph is not null)
                {
                    // Cloud API istemcisinin HTTP katmanı sahte handler'a bağlanır: gerçek Graph API'ye istek atılmaz.
                    services.AddHttpClient(CloudWhatsAppClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => graph);
                }
            });
            builder.ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.SetMinimumLevel(LogLevel.Debug);
                logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Information);
                logging.AddProvider(logs);
            });
        });

        try
        {
            _ = factory.Server; // açılış: migration + Admin tohumlama
        }
        catch
        {
            // Açılış doğrulaması (ör. CloudApi'de eksik secret) testleri: başarısız host'un kaynakları bırakılır.
            await factory.DisposeAsync();
            throw;
        }

        var host = new MessagingTestHost(factory, connectionString, sandbox, clock, logs);
        if (withCategory)
        {
            await host.CreateCategoryAsync("yol-kaldirim", "Yol ve Kaldırım");
        }

        return host;
    }

    public HttpClient CreateClient(string? token = null)
    {
        var client = _factory.CreateClient();
        if (token is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return client;
    }

    public AsyncServiceScope CreateScope() => _factory.Services.CreateAsyncScope();

    public async Task<string> LoginAsync(string email = IdentityTestHost.AdminEmail, string password = IdentityTestHost.AdminPassword)
    {
        using var client = CreateClient();
        using var response = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password }, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("accessToken").GetString()!;
    }

    public async Task<Guid> CreateCategoryAsync(string code, string name)
    {
        using var client = CreateClient(await LoginAsync());
        using var response = await client.PostAsJsonAsync("/api/v1/categories", new { code, name }, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
    }

    /// <summary>Gövdeyi doğru imzayla (veya verilen başlıkla) webhook'a gönderir.</summary>
    public async Task<HttpResponseMessage> PostWebhookAsync(string body, string? signature = null, bool sign = true)
    {
        using var client = CreateClient();
        var bytes = Encoding.UTF8.GetBytes(body);
        using var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/integrations/whatsapp/webhook") { Content = content };
        if (sign || signature is not null)
        {
            request.Headers.TryAddWithoutValidation("X-Hub-Signature-256", signature ?? WebhookSignatureVerifier.Sign(bytes, WebhookFixtures.AppSecret));
        }

        return await client.SendAsync(request, Ct);
    }

    /// <summary>Webhook'u gönderir (200 bekler) ve inbox'ı boşaltır.</summary>
    public async Task SendAsync(string body)
    {
        using var response = await PostWebhookAsync(body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await ProcessInboxAsync();
    }

    public async Task<int> ProcessInboxAsync()
    {
        var total = 0;
        int processed;
        while ((processed = await Services.GetRequiredService<InboxProcessor>().RunOnceAsync(Ct)) > 0)
        {
            total += processed;
        }

        return total;
    }

    /// <summary>
    /// <c>WhatsApp__Provider=CloudApi</c> + sahte secret'lar + <paramref name="graph"/> (sahte Graph API). Değerler gerçek değildir.
    /// </summary>
    public static Task<MessagingTestHost> StartCloudAsync(
        PostgresFixture postgres, FakeGraphHandler graph, IReadOnlyDictionary<string, string?>? settings = null)
    {
        var all = new Dictionary<string, string?>
        {
            ["WhatsApp:Provider"] = "CloudApi",
            ["WhatsApp:AccessToken"] = FakeGraphHandler.AccessToken,
            ["WhatsApp:PhoneNumberId"] = WebhookFixtures.PhoneNumberId,
            ["WhatsApp:GraphApiVersion"] = FakeGraphHandler.GraphVersion,
        };
        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
        {
            all[key] = value;
        }

        return StartAsync(postgres, all, graph: graph);
    }

    public Task<int> SendOutboundAsync() => Services.GetRequiredService<OutboundSender>().RunOnceAsync(Ct);

    public Task<int> SweepExpiredAsync() => Services.GetRequiredService<IntakeExpirySweeper>().RunOnceAsync(Ct);

    /// <summary>Bekleyen outbox event'lerini gerçek handler'larla dağıtır (Platform'un dağıtıcısı).</summary>
    public async Task DispatchOutboxAsync()
    {
        var dispatcher = ActivatorUtilities.CreateInstance<OutboxDispatcher>(Services);
        while (await dispatcher.DispatchBatchAsync(Ct) > 0)
        {
        }
    }

    /// <summary>Konuşmanın son giden mesaj metni (en yeni önce).</summary>
    public async Task<IReadOnlyList<string>> OutboundTextsAsync(string contact = Reporter)
    {
        var texts = new List<string>();
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT m.text_body FROM messaging.messages m JOIN messaging.conversations c ON c.id = m.conversation_id
            WHERE c.external_contact_id = @contact AND m.direction = 'Outbound' ORDER BY m.created_at DESC, m.id DESC
            """, connection);
        command.Parameters.AddWithValue("contact", contact);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            texts.Add(reader.GetString(0));
        }

        return texts;
    }

    public async Task<long> ScalarAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return Convert.ToInt64(await command.ExecuteScalarAsync(Ct), System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task<string?> StringAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return (await command.ExecuteScalarAsync(Ct)) as string;
    }

    /// <summary>Kategori → açıklama → konum → atla → onay; şikâyet kimliğini döner.</summary>
    public async Task<Guid> CompleteIntakeAsync(string contact = Reporter, string description = "Kaldırım kırık (temsili).")
    {
        await SendAsync(WebhookFixtures.Text(contact, "merhaba"));
        await SendAsync(WebhookFixtures.Text(contact, "1"));
        await SendAsync(WebhookFixtures.Text(contact, description));
        await SendAsync(WebhookFixtures.Location(contact, 40.4105, 26.6712));
        await SendAsync(WebhookFixtures.Text(contact, "atla"));
        await SendAsync(WebhookFixtures.Text(contact, "1"));
        var id = await StringAsync("""
            SELECT s.submitted_complaint_id::text FROM messaging.intake_sessions s
            JOIN messaging.conversations c ON c.id = s.conversation_id
            WHERE c.external_contact_id = @contact AND s.state = 'Submitted' ORDER BY s.created_at DESC LIMIT 1
            """, ("contact", contact));
        Assert.NotNull(id);
        return Guid.Parse(id);
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();

        // Her test kendi veritabanına bağlanır; boşta kalan havuz bağlantıları kapanmazsa paylaşılan konteynerin
        // max_connections (100) sınırı aşılır (53300). Bu host'un havuzu bırakılır.
        await using (var connection = new NpgsqlConnection(ConnectionString))
        {
            NpgsqlConnection.ClearPool(connection);
        }

        if (Directory.Exists(_sandbox))
        {
            Directory.Delete(_sandbox, recursive: true);
        }
    }

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(Ct);
        return connection;
    }
}
