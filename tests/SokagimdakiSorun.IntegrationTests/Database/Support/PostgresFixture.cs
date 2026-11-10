using Microsoft.EntityFrameworkCore;
using Npgsql;
using SokagimdakiSorun.Platform.Persistence;
using Testcontainers.PostgreSql;

namespace SokagimdakiSorun.IntegrationTests.Database.Support;

/// <summary>
/// Gerçek PostgreSQL 18 konteyneri (Testcontainers). Koleksiyon başına bir kez başlar; her test
/// <see cref="CreateDatabaseAsync"/> ile kendi <b>boş</b> veritabanını alır, böylece testler birbirini etkilemez.
/// Docker gerekir; Docker yoksa bu koleksiyondaki testler başarısız olur (atlanmaz). Docker olmadan
/// geri kalan testleri çalıştırmak için: <c>dotnet test --solution SokagimdakiSorun.slnx --filter-not-trait "Category=Database"</c>.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    /// <summary>docker-compose.yml ile aynı ana sürüm (ADR-0005).</summary>
    public const string Image = "postgres:18";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder(Image).Build();

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    /// <summary>Konteynerde yeni, boş bir veritabanı oluşturur ve bağlantı dizesini döner.</summary>
    public async Task<string> CreateDatabaseAsync()
    {
        var name = $"test_{Guid.CreateVersion7():N}";
        await using (var connection = new NpgsqlConnection(_container.GetConnectionString()))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection);
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        return new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = name }.ConnectionString;
    }

    /// <summary>Boş veritabanı + uygulanmış <c>platform</c> migration'ı.</summary>
    public async Task<string> CreateMigratedDatabaseAsync()
    {
        var connectionString = await CreateDatabaseAsync();
        await using var context = CreatePlatformContext(connectionString);
        await context.Database.MigrateAsync(TestContext.Current.CancellationToken);
        return connectionString;
    }

    internal static PlatformDbContext CreatePlatformContext(string connectionString) =>
        new(new DbContextOptionsBuilder<PlatformDbContext>().UseNpgsql(connectionString).Options);
}

/// <summary>Gerçek PostgreSQL gerektiren testlerin koleksiyonu: konteyner bir kez başlatılır.</summary>
[CollectionDefinition(Name)]
public sealed class DatabaseCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "Database";
}
