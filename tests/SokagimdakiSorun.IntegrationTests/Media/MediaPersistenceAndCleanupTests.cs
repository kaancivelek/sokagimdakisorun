using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SokagimdakiSorun.IntegrationTests.Database;
using SokagimdakiSorun.IntegrationTests.Database.Support;
using SokagimdakiSorun.IntegrationTests.Media.Support;
using SokagimdakiSorun.Media.Contracts;
using SokagimdakiSorun.Media.Infrastructure;
using SokagimdakiSorun.Media.Infrastructure.Storage;
using SokagimdakiSorun.Media.TestSupport;
using static SokagimdakiSorun.IntegrationTests.Database.MigrationAndConventionTests;

namespace SokagimdakiSorun.IntegrationTests.Media;

/// <summary>
/// WS-05: <c>media.media_files</c> kısıtları, yetim temizliği ve <c>/health/ready</c> medya kontrolü — gerçek PostgreSQL 18.
/// </summary>
[Collection(DatabaseCollection.Name)]
[Trait("Category", "Database")]
public sealed class MediaPersistenceAndCleanupTests(PostgresFixture postgres)
{
    private const string ValidSha = "0000000000000000000000000000000000000000000000000000000000000000";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Migration_creates_only_media_schema_objects_and_model_is_in_sync()
    {
        await using var media = await MediaTestContext.CreateAsync(postgres);
        await using var scope = media.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MediaDbContext>();

        Assert.False(db.Database.HasPendingModelChanges(), "MediaDbContext modeli son migration ile uyumlu değil.");
        Assert.Equal(["media.__ef_migrations_history", "media.media_files"], await QueryStringsAsync(media.ConnectionString,
            "SELECT table_schema || '.' || table_name FROM information_schema.tables WHERE table_schema NOT IN ('pg_catalog','information_schema') ORDER BY 1"));
    }

    public static TheoryData<string, string> ConstraintViolations => new()
    {
        // (değerler), beklenen kısıt
        { $"'{Key('1')}', 'image/jpeg', 0, '{ValidSha}', 'Temporary', NULL, NULL", "ck_media_files_size_bytes" },
        { $"'{Key('2')}', 'image/jpeg', -5, '{ValidSha}', 'Temporary', NULL, NULL", "ck_media_files_size_bytes" },
        { $"'{Key('3')}', 'image/gif', 10, '{ValidSha}', 'Temporary', NULL, NULL", "ck_media_files_content_type" },
        // Bilinmeyen durum hem ck_media_files_lifecycle hem ck_media_files_status'u ihlal eder; PostgreSQL CHECK'leri ada göre sırayla dener.
        { $"'{Key('4')}', 'image/jpeg', 10, '{ValidSha}', 'Archived', NULL, NULL", "ck_media_files_lifecycle" },
        { $"'{Key('5')}', 'image/jpeg', 10, '{ValidSha}', 'Committed', NULL, NULL", "ck_media_files_lifecycle" },
        { $"'{Key('6')}', 'image/jpeg', 10, '{ValidSha}', 'Deleted', now(), NULL", "ck_media_files_lifecycle" },
        { $"'{Key('7')}', 'image/jpeg', 10, '{ValidSha}', 'Temporary', now(), NULL", "ck_media_files_lifecycle" },
        { $"'../../etc/passwd', 'image/jpeg', 10, '{ValidSha}', 'Temporary', NULL, NULL", "ck_media_files_storage_key" },
        { $"'{Key('8')}', 'image/jpeg', 10, 'not-a-hash', 'Temporary', NULL, NULL", "ck_media_files_sha256" },
    };

    [Theory]
    [MemberData(nameof(ConstraintViolations))]
    public async Task Database_rejects_rows_that_break_metadata_invariants(string values, string constraint)
    {
        await using var media = await MediaTestContext.CreateAsync(postgres);

        var error = await Assert.ThrowsAsync<PostgresException>(() => InsertAsync(media.ConnectionString, values));

        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        Assert.Equal(constraint, error.ConstraintName);
    }

    [Fact]
    public async Task Storage_key_is_unique()
    {
        await using var media = await MediaTestContext.CreateAsync(postgres);
        var values = $"'{Key('a')}', 'image/png', 10, '{ValidSha}', 'Temporary', NULL, NULL";
        await InsertAsync(media.ConnectionString, values);

        var error = await Assert.ThrowsAsync<PostgresException>(() => InsertAsync(media.ConnectionString, values));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, error.SqlState);
        Assert.Equal("ix_media_files_storage_key", error.ConstraintName);
    }

    [Fact]
    public async Task Cleaner_removes_expired_temporaries_and_old_orphans_but_keeps_committed_fresh_and_in_flight_files()
    {
        await using var media = await MediaTestContext.CreateAsync(postgres);
        await using var scope = media.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IMediaService>();
        var cleaner = media.GetRequiredService<OrphanMediaCleaner>();

        var committed = (await service.StoreAsync(new MemoryStream(TestImages.Jpeg(false)), "image/jpeg", Ct)).Value;
        Assert.True((await service.CommitAsync(committed, Ct)).IsSuccess);
        var expired = (await service.StoreAsync(new MemoryStream(TestImages.Png(false)), "image/png", Ct)).Value;
        media.Clock.Advance(TimeSpan.FromHours(47));
        var fresh = (await service.StoreAsync(new MemoryStream(TestImages.WebP(false)), "image/webp", Ct)).Value;
        media.Clock.Advance(TimeSpan.FromHours(2)); // expired: 49 saat, fresh: 2 saat

        // Metadata'sız dosyalar: biri eski (yetim), biri yeni ("yaz → kaydet" arasında olabilir), biri eski yarım yazma kalıntısı.
        var now = media.Clock.GetUtcNow().UtcDateTime;
        var oldOrphan = await WriteStrayAsync(media, StorageKey.New(), now.AddHours(-3));
        var newOrphan = await WriteStrayAsync(media, StorageKey.New(), now.AddMinutes(-5));
        var oldPartial = await WriteStrayAsync(media, $"{StorageKey.New()}.0123.partial", now.AddHours(-3));
        foreach (var file in Directory.GetFiles(media.Root, "*", SearchOption.AllDirectories))
        {
            if (file != newOrphan)
            {
                File.SetLastWriteTimeUtc(file, now.AddHours(-3)); // gerçek dosyalar da "eski": silinmemeleri metadata sayesinde
            }
        }

        var result = await cleaner.RunOnceAsync(Ct);

        Assert.Equal(new MediaCleanupResult(1, 2), result);
        Assert.False(File.Exists(oldOrphan));
        Assert.False(File.Exists(oldPartial));
        Assert.True(File.Exists(newOrphan));
        Assert.Equal(3, media.Files().Count); // committed + fresh + yeni yetim
        Assert.True((await service.OpenReadAsync(committed, Ct)).IsSuccess);
        Assert.True((await service.OpenReadAsync(fresh, Ct)).IsSuccess);
        Assert.Equal(MediaErrorCodes.NotFound, (await service.OpenReadAsync(expired, Ct)).Error.Code);

        // İkinci tur: yapacak iş yok (idempotent).
        Assert.Equal(new MediaCleanupResult(0, 0), await cleaner.RunOnceAsync(Ct));
    }

    [Fact]
    public async Task Cleaner_deletes_leftover_file_whose_row_is_already_deleted()
    {
        await using var media = await MediaTestContext.CreateAsync(postgres);
        await using var scope = media.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IMediaService>();
        await service.StoreAsync(new MemoryStream(TestImages.Jpeg(false)), "image/jpeg", Ct);

        // Dosya silinemeden süreç çökmüş: satır Deleted, dosya duruyor.
        await using (var connection = new NpgsqlConnection(media.ConnectionString))
        {
            await connection.OpenAsync(Ct);
            await using var command = new NpgsqlCommand("UPDATE media.media_files SET status = 'Deleted', deleted_at = now()", connection);
            await command.ExecuteNonQueryAsync(Ct);
        }

        var file = Path.Combine(media.Root, Assert.Single(media.Files()));
        File.SetLastWriteTimeUtc(file, media.Clock.GetUtcNow().UtcDateTime.AddHours(-2));

        Assert.Equal(new MediaCleanupResult(0, 1), await media.GetRequiredService<OrphanMediaCleaner>().RunOnceAsync(Ct));
        Assert.Empty(media.Files());
    }

    [Fact]
    public async Task Ready_requires_writable_media_root()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        var blocker = Path.Combine(Path.GetTempPath(), "sokagimdakisorun-tests", $"blocker-{Guid.CreateVersion7():N}");
        Directory.CreateDirectory(Path.GetDirectoryName(blocker)!);
        await File.WriteAllTextAsync(blocker, "x", Ct);
        try
        {
            foreach (var (root, expected) in new (string?, HttpStatusCode)[]
            {
                (null, HttpStatusCode.ServiceUnavailable), // tanımsız
                (Path.Combine(blocker, "media"), HttpStatusCode.ServiceUnavailable), // dosyanın altında: yazılamaz
                (DatabaseHostFactory.DefaultMediaRoot, HttpStatusCode.OK),
            })
            {
                await using var factory = DatabaseHostFactory.Create(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Default"] = connectionString,
                    ["Outbox:Enabled"] = "false",
                    ["Media:CleanupEnabled"] = "false",
                    ["Media:RootPath"] = root,
                });
                using var client = factory.CreateClient();

                using var ready = await client.GetAsync("/health/ready", Ct);

                Assert.Equal(expected, ready.StatusCode);
                var body = await ready.Content.ReadAsStringAsync(Ct);
                Assert.True(body is "Healthy" or "Unhealthy", body);
            }
        }
        finally
        {
            File.Delete(blocker);
        }
    }

    private static string Key(char c) => new(c, 32);

    private static async Task InsertAsync(string connectionString, string values)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            "INSERT INTO media.media_files (id, storage_provider, created_at, storage_key, content_type, size_bytes, sha256, status, committed_at, deleted_at) "
            + $"VALUES (gen_random_uuid(), 'local', now(), {values})",
            connection);
        await command.ExecuteNonQueryAsync(Ct);
    }

    private static async Task<string> WriteStrayAsync(MediaTestContext media, string name, DateTime lastWriteUtc)
    {
        var directory = Path.Combine(media.Root, name[..2]);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        await File.WriteAllBytesAsync(path, [1, 2, 3], Ct);
        File.SetLastWriteTimeUtc(path, lastWriteUtc);
        return path;
    }
}
