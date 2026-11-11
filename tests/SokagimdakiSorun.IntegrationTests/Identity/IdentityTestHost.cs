using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SokagimdakiSorun.IntegrationTests.Database.Support;

namespace SokagimdakiSorun.IntegrationTests.Identity;

/// <summary>
/// Gerçek <c>Program</c> + gerçek PostgreSQL 18: her örnek kendi boş veritabanını alır, açılış migration'ı
/// (platform + identity) ve ilk Admin tohumlaması gerçek yoldan çalışır. Her test ayrı örnek kullanır; hız sınırlayıcı
/// bölümü (TestServer'da istemci IP'si yok → tek bölüm) testler arasında paylaşılmaz. Tüm loglar yakalanır.
/// </summary>
internal sealed class IdentityTestHost : IAsyncDisposable
{
    public const string AdminEmail = "ilk.admin@example.test";
    public const string AdminPassword = "Ilk-Admin-Parola-2026!";

    private readonly WebApplicationFactory<Program> _factory;

    private IdentityTestHost(WebApplicationFactory<Program> factory, CapturingLoggerProvider logs)
    {
        _factory = factory;
        Logs = logs;
    }

    public CapturingLoggerProvider Logs { get; }

    public IServiceProvider Services => _factory.Services;

    public static async Task<IdentityTestHost> StartAsync(PostgresFixture postgres)
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        var logs = new CapturingLoggerProvider();
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:Default", connectionString);
            builder.UseSetting("Database:ApplyMigrationsOnStartup", "true");
            builder.UseSetting("Outbox:Enabled", "false");
            builder.UseSetting("Seed:AdminEmail", AdminEmail);
            builder.UseSetting("Seed:AdminPassword", AdminPassword);
            builder.ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.SetMinimumLevel(LogLevel.Debug);
                logging.AddProvider(logs);
            });
        });

        _ = factory.Server; // açılış: migration + tohumlama
        return new IdentityTestHost(factory, logs);
    }

    public HttpClient CreateClient(string? accessToken = null)
    {
        var client = _factory.CreateClient();
        if (accessToken is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        return client;
    }

    public AsyncServiceScope CreateScope() => _factory.Services.CreateAsyncScope();

    public async Task<HttpResponseMessage> LoginAsync(string email, string password)
    {
        using var client = CreateClient();
        return await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password }, TestContext.Current.CancellationToken);
    }

    public async Task<Tokens> LoginSuccessfullyAsync(string email, string password)
    {
        using var response = await LoginAsync(email, password);
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        return await Tokens.ReadAsync(response);
    }

    /// <summary>Admin olarak moderatör oluşturur ve kimliğini döner.</summary>
    public async Task<Guid> CreateModeratorAsync(string adminToken, string email, string password)
    {
        using var client = CreateClient(adminToken);
        using var response = await client.PostAsJsonAsync(
            "/api/v1/users", new { email, displayName = "Test Moderatör", password }, TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        return body.GetProperty("id").GetGuid();
    }

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    internal sealed record Tokens(string AccessToken, string RefreshToken, string Raw)
    {
        public static async Task<Tokens> ReadAsync(HttpResponseMessage response)
        {
            var raw = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            using var json = JsonDocument.Parse(raw);
            Assert.Equal("Bearer", json.RootElement.GetProperty("tokenType").GetString());
            return new Tokens(
                json.RootElement.GetProperty("accessToken").GetString()!,
                json.RootElement.GetProperty("refreshToken").GetString()!,
                raw);
        }
    }

    /// <summary>Tüm log mesajlarını (biçimlenmiş metin + exception) toplar.</summary>
    internal sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _messages = new();

        public IReadOnlyCollection<string> Messages => _messages;

        public ILogger CreateLogger(string categoryName) => new Logger(categoryName, _messages);

        public void Dispose()
        {
        }

        private sealed class Logger(string category, ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                ArgumentNullException.ThrowIfNull(formatter);
                messages.Enqueue($"{logLevel} {category}: {formatter(state, exception)} {exception}");
            }
        }
    }
}
