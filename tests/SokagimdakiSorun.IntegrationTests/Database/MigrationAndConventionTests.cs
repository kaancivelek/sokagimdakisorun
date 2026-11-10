using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using SokagimdakiSorun.IntegrationTests.Database.SampleModule;
using SokagimdakiSorun.IntegrationTests.Database.Support;

namespace SokagimdakiSorun.IntegrationTests.Database;

/// <summary>
/// WS-03: migration'ın boş veritabanına uygulanması, ortak konvansiyonlar (V-2) ve paylaşılan outbox'ın
/// modül bağlamında migration dışı eşlenmesi (V-3) — gerçek PostgreSQL 18 üzerinde.
/// </summary>
[Collection(DatabaseCollection.Name)]
[Trait("Category", "Database")]
public sealed class MigrationAndConventionTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Platform_migration_applies_to_empty_database_and_keeps_history_in_own_schema()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        await using var context = PostgresFixture.CreatePlatformContext(connectionString);

        Assert.False(context.Database.HasPendingModelChanges(), "PlatformDbContext modeli son migration ile uyumlu değil.");
        await context.Database.MigrateAsync(Ct);
        await context.Database.MigrateAsync(Ct); // ikinci çağrı no-op olmalı

        Assert.Equal(["20261110221402_InitialPlatform"], await context.Database.GetAppliedMigrationsAsync(Ct));
        Assert.Empty(await context.Database.GetPendingMigrationsAsync(Ct));

        var tables = await QueryStringsAsync(connectionString,
            "SELECT table_schema || '.' || table_name FROM information_schema.tables WHERE table_schema NOT IN ('pg_catalog','information_schema') ORDER BY 1");
        Assert.Equal(["platform.__ef_migrations_history", "platform.outbox_messages"], tables);

        var indexes = await QueryStringsAsync(connectionString,
            "SELECT indexdef FROM pg_indexes WHERE schemaname = 'platform' AND indexname = 'ix_outbox_messages_pending'");
        Assert.Contains("WHERE ((processed_at IS NULL) AND (dead_lettered_at IS NULL))", Assert.Single(indexes), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Module_context_model_excludes_shared_outbox_from_its_migrations()
    {
        // V-3: modül bağlamı için "dotnet ef migrations add" tam olarak bu farkı üretir.
        await using var context = SampleModuleDbContext.Create("Host=unused");
        var differ = context.GetService<IMigrationsModelDiffer>();
        var model = context.GetService<IDesignTimeModel>().Model.GetRelationalModel();

        var operations = differ.GetDifferences(null, model);

        var created = operations.OfType<CreateTableOperation>().Select(o => $"{o.Schema}.{o.Name}").ToList();
        Assert.Equal(["sample_module.widgets"], created);
        Assert.DoesNotContain(operations.OfType<EnsureSchemaOperation>(), o => o.Name == "platform");

        var historyScript = context.GetService<IHistoryRepository>().GetCreateIfNotExistsScript();
        Assert.Contains("sample_module.__ef_migrations_history", historyScript, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Module_tables_follow_shared_conventions()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        await using var module = SampleModuleDbContext.Create(connectionString);

        // Platform migration'ı outbox'ı zaten oluşturdu; modül tabloları yanında, çakışmadan oluşmalı.
        await module.GetService<IRelationalDatabaseCreator>().CreateTablesAsync(Ct);

        var columns = await QueryStringsAsync(connectionString,
            "SELECT column_name || ':' || data_type FROM information_schema.columns WHERE table_schema = 'sample_module' AND table_name = 'widgets' ORDER BY ordinal_position");
        Assert.Equal(
            ["id:uuid", "display_name:character varying", "color:text", "created_at:timestamp with time zone"],
            columns);

        // Enum metin + otomatik CHECK kısıtı.
        var checks = await QueryStringsAsync(connectionString,
            "SELECT conname || ':' || pg_get_constraintdef(oid) FROM pg_constraint WHERE conrelid = 'sample_module.widgets'::regclass AND contype = 'c'");
        var check = Assert.Single(checks);
        Assert.StartsWith("ck_widgets_color:", check, StringComparison.Ordinal);
        Assert.Contains("'Red'", check, StringComparison.Ordinal);
        Assert.Contains("'Green'", check, StringComparison.Ordinal);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connectionString,
            "INSERT INTO sample_module.widgets (id, display_name, color, created_at) VALUES (gen_random_uuid(), 'x', 'Blue', now())"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);

        // Guid anahtar verilmezse sağlayıcı v7 üretir; enum metin olarak yazılır.
        var widget = new Widget { DisplayName = "lamba", Color = WidgetColor.Green, CreatedAt = DateTimeOffset.UtcNow };
        module.Widgets.Add(widget);
        await module.SaveChangesAsync(Ct);
        Assert.Equal(7, widget.Id.Version);
        Assert.Equal(["Green"], await QueryStringsAsync(connectionString, "SELECT color FROM sample_module.widgets"));
    }

    internal static async Task<List<string>> QueryStringsAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var result = new List<string>();
        while (await reader.ReadAsync(Ct))
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }

    internal static async Task<long> CountAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(Ct);
    }
}
