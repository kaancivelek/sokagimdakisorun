using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using SokagimdakiSorun.Complaints.Contracts;
using SokagimdakiSorun.Identity.Contracts;
using SokagimdakiSorun.IntegrationTests.Database.Support;
using SokagimdakiSorun.IntegrationTests.Identity;

namespace SokagimdakiSorun.IntegrationTests.Complaints;

/// <summary>
/// Gerçek <c>Program</c> + gerçek PostgreSQL 18 (test başına boş veritabanı; açılışta tüm migration'lar ve ilk Admin) + geçici
/// medya kökü. Outbox dağıtıcısı kapalıdır: yazılan outbox satırları doğrudan sorgulanabilir. Panel çağrıları gerçek bearer
/// token'larla yapılır.
/// </summary>
internal sealed class ComplaintsTestHost : IAsyncDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _mediaSandbox;

    private ComplaintsTestHost(
        WebApplicationFactory<Program> factory, string connectionString, string mediaSandbox, string adminToken, Guid adminId,
        IdentityTestHost.CapturingLoggerProvider logs)
    {
        Logs = logs;
        _factory = factory;
        ConnectionString = connectionString;
        _mediaSandbox = mediaSandbox;
        AdminToken = adminToken;
        AdminId = adminId;
    }

    public string ConnectionString { get; }

    /// <summary>Tüm loglar (hata ayıklama ve sızıntı kontrolü için).</summary>
    public IdentityTestHost.CapturingLoggerProvider Logs { get; }

    public string AdminToken { get; }

    public Guid AdminId { get; }

    public IServiceProvider Services => _factory.Services;

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task<ComplaintsTestHost> StartAsync(PostgresFixture postgres, IReadOnlyDictionary<string, string?>? settings = null)
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        var sandbox = Path.Combine(Path.GetTempPath(), "sokagimdakisorun-tests", Guid.CreateVersion7().ToString("N"));
        var logs = new IdentityTestHost.CapturingLoggerProvider();
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
            foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
            {
                builder.UseSetting(key, value);
            }

            builder.ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.SetMinimumLevel(LogLevel.Warning);
                logging.AddProvider(logs);
            });
        });

        _ = factory.Server; // açılış: migration + tohumlama
        var token = await LoginAsync(factory, IdentityTestHost.AdminEmail, IdentityTestHost.AdminPassword);
        using var client = CreateClient(factory, token);
        var me = await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/me", Ct);
        return new ComplaintsTestHost(factory, connectionString, sandbox, token, me.GetProperty("id").GetGuid(), logs);
    }

    public HttpClient CreateClient(string? token = null) => CreateClient(_factory, token);

    public AsyncServiceScope CreateScope() => _factory.Services.CreateAsyncScope();

    /// <summary>Admin olarak moderatör oluşturur, oturum açar.</summary>
    public async Task<(Guid Id, string Token)> CreateModeratorAsync(string name)
    {
        var email = $"{name}@example.test";
        const string password = "Moderator-Parola-2026!";
        using var client = CreateClient(AdminToken);
        using var response = await client.PostAsJsonAsync("/api/v1/users", new { email, displayName = $"Moderatör {name}", password }, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var id = (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        return (id, await LoginAsync(_factory, email, password));
    }

    public async Task<Guid> CreateCategoryAsync(string code = "yol-kaldirim")
    {
        using var client = CreateClient(AdminToken);
        using var response = await client.PostAsJsonAsync("/api/v1/categories", new { code, name = $"Kategori {code}" }, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
    }

    public async Task<Guid> CreateUnitAsync(string code, params Guid[] members)
    {
        using var client = CreateClient(AdminToken);
        using var response = await client.PostAsJsonAsync("/api/v1/service-units", new { code, name = $"Birim {code} (Temsili)" }, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var id = (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        foreach (var member in members)
        {
            using var added = await client.PostAsJsonAsync($"/api/v1/service-units/{id}/members", new { userId = member }, Ct);
            Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        }

        return id;
    }

    public async Task<Guid> CreateReporterAsync(string externalId = "demo-test-reporter")
    {
        await using var scope = CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<IReporterRegistry>();
        return (await registry.FindOrCreateAsync(ExternalIdentityProvider.WhatsApp, externalId, "Test Vatandaş", Ct)).Value.UserId;
    }

    /// <summary>Sözleşme üzerinden şikâyet oluşturur (Messaging'in yapacağı çağrı).</summary>
    public async Task<SubmittedComplaint> SubmitAsync(Guid categoryId, Guid reporterId, Guid? intakeSessionId = null, IReadOnlyList<Guid>? media = null)
    {
        await using var scope = CreateScope();
        var intake = scope.ServiceProvider.GetRequiredService<IComplaintIntake>();
        var result = await intake.SubmitFromIntakeAsync(new SubmitFromIntakeRequest(
            intakeSessionId ?? Guid.CreateVersion7(), reporterId, categoryId, "Yazıcızade'de çukur (temsili).",
            40.4105, 26.6712, null, LocationSource.WhatsAppPin, media ?? []), Ct);
        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Code : null);
        return result.Value;
    }

    public async Task<JsonElement> GetDetailAsync(string token, Guid id)
    {
        using var client = CreateClient(token);
        using var response = await client.GetAsync($"/api/v1/complaints/{id}", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    /// <summary>Komutu güncel <c>version</c> ile gönderir.</summary>
    public async Task<HttpResponseMessage> CommandAsync(string token, Guid id, string action, object? body = null)
    {
        var version = (await GetDetailAsync(token, id)).GetProperty("version").GetString();
        return await CommandWithVersionAsync(token, id, action, version, body);
    }

    public async Task<HttpResponseMessage> CommandWithVersionAsync(string token, Guid id, string action, string? version, object? body = null)
    {
        var payload = JsonSerializer.SerializeToElement(body ?? new { });
        var merged = new Dictionary<string, object?> { ["version"] = version };
        foreach (var property in payload.EnumerateObject())
        {
            merged[property.Name] = property.Value;
        }

        using var client = CreateClient(token);
        return await client.PostAsJsonAsync($"/api/v1/complaints/{id}/{action}", merged, Ct);
    }

    /// <summary>Komutu gönderir ve 200 bekler.</summary>
    public async Task<JsonElement> CommandOkAsync(string token, Guid id, string action, object? body = null)
    {
        using var response = await CommandAsync(token, id, action, body);
        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK,
            $"{action}: {(int)response.StatusCode} {text} {Logs.Messages.LastOrDefault(m => m.StartsWith("Error", StringComparison.Ordinal))}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    public async Task<long> ScalarAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return Convert.ToInt64(await command.ExecuteScalarAsync(Ct), System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(Ct);
    }

    public static async Task<JsonElement> ReadProblemAsync(HttpResponseMessage response, HttpStatusCode expected, string code)
    {
        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == expected, $"Beklenen {(int)expected}, gelen {(int)response.StatusCode}: {text}");
        var problem = JsonDocument.Parse(text).RootElement.Clone();
        Assert.Equal(code, problem.GetProperty("code").GetString());
        return problem;
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        if (Directory.Exists(_mediaSandbox))
        {
            Directory.Delete(_mediaSandbox, recursive: true);
        }
    }

    private static HttpClient CreateClient(WebApplicationFactory<Program> factory, string? token)
    {
        var client = factory.CreateClient();
        if (token is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return client;
    }

    private static async Task<string> LoginAsync(WebApplicationFactory<Program> factory, string email, string password)
    {
        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password }, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("accessToken").GetString()!;
    }
}
