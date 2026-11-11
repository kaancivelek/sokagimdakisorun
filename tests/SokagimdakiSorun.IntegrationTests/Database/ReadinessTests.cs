using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;
using SokagimdakiSorun.IntegrationTests.Database.Support;
using static SokagimdakiSorun.IntegrationTests.Database.MigrationAndConventionTests;

namespace SokagimdakiSorun.IntegrationTests.Database;

/// <summary>Gerçek <c>Program</c> pipeline'ını belirli veritabanı ayarlarıyla in-memory çalıştırır.</summary>
internal static class DatabaseHostFactory
{
    /// <summary>
    /// WS-05: <c>/health/ready</c> medya kökünün yazılabilirliğini de kontrol eder. Ayarlarda <c>Media:RootPath</c>
    /// verilmemişse geçici bir dizin kullanılır (testler kalıcı bir yere yazmaz); <see langword="null"/> verilirse kök tanımsız kalır.
    /// </summary>
    public static readonly string DefaultMediaRoot = Path.Combine(Path.GetTempPath(), "sokagimdakisorun-tests", "readiness-media");

    public static WebApplicationFactory<Program> Create(IReadOnlyDictionary<string, string?> settings) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.ConfigureLogging(logging => logging.ClearProviders());
            if (!settings.ContainsKey("Media:RootPath"))
            {
                builder.UseSetting("Media:RootPath", DefaultMediaRoot);
            }

            foreach (var (key, value) in settings)
            {
                builder.UseSetting(key, value);
            }
        });
}

/// <summary><c>/health/ready</c> veritabanı yokken hazır değildir ve ayrıntı sızdırmaz. Docker gerektirmez.</summary>
public sealed class ReadinessWithoutDatabaseTests
{
    private const string SecretPassword = "GIZLI-PAROLA-123";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string?> UnusableConnectionStrings => new()
    {
        // Bağlantı dizesi hiç yok.
        (string?)null,
        // Dinleyeni olmayan port: sunucuya ulaşılamıyor.
        $"Host=127.0.0.1;Port=1;Database=yok;Username=yok;Password={SecretPassword};Timeout=3",
    };

    [Theory]
    [MemberData(nameof(UnusableConnectionStrings))]
    public async Task Ready_is_unhealthy_without_database_and_leaks_nothing(string? connectionString)
    {
        await using var factory = DatabaseHostFactory.Create(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = connectionString,
        });
        using var client = factory.CreateClient();

        using var ready = await client.GetAsync("/health/ready", Ct);
        using var live = await client.GetAsync("/health/live", Ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        var body = await ready.Content.ReadAsStringAsync(Ct);
        Assert.Equal("Unhealthy", body);
        Assert.DoesNotContain(SecretPassword, body, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
    }
}

/// <summary>Açılışta migration uygulanır ve <c>/health/ready</c> veritabanı varken hazır döner (gerçek PostgreSQL 18).</summary>
[Collection(DatabaseCollection.Name)]
[Trait("Category", "Database")]
public sealed class ReadinessWithDatabaseTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Host_applies_migrations_on_startup_and_reports_ready()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        await using var factory = DatabaseHostFactory.Create(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = connectionString,
            ["Database:ApplyMigrationsOnStartup"] = "true",
            ["Outbox:Enabled"] = "false",
        });
        using var client = factory.CreateClient();

        using var ready = await client.GetAsync("/health/ready", Ct);

        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal("Healthy", await ready.Content.ReadAsStringAsync(Ct));
        Assert.Equal(["20261110221402_InitialPlatform"],
            await QueryStringsAsync(connectionString, "SELECT migration_id FROM platform.__ef_migrations_history"));
    }

    [Fact]
    public async Task Host_does_not_touch_schema_when_startup_migrations_are_off()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        await using var factory = DatabaseHostFactory.Create(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = connectionString,
            ["Outbox:Enabled"] = "false",
        });
        using var client = factory.CreateClient();

        using var ready = await client.GetAsync("/health/ready", Ct);

        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal(0, await CountAsync(connectionString, "SELECT count(*) FROM information_schema.schemata WHERE schema_name = 'platform'"));
    }
}
