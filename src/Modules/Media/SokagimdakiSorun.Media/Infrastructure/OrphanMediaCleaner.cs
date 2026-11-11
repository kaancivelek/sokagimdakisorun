using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SokagimdakiSorun.Media.Application;
using SokagimdakiSorun.Media.Domain;
using SokagimdakiSorun.Media.Infrastructure.Storage;
using SokagimdakiSorun.Platform.Persistence;

namespace SokagimdakiSorun.Media.Infrastructure;

/// <summary>
/// Yetim medya temizleyicisi (ARCHITECTURE §5.3, ADR-0004 §4). Her turda:
/// <list type="number">
///   <item><see cref="MediaOptions.TemporaryRetention"/>'dan uzun <c>Temporary</c> kalan kayıtları koşullu
///         <c>UPDATE</c> ile <c>Deleted</c> yapar, ardından dosyasını siler. Aynı anda <c>CommitAsync</c> çağrılırsa
///         yalnızca biri kazanır; kesinleşmiş dosya silinmez.</item>
///   <item>Kökte olup etkin (<c>Temporary</c>/<c>Committed</c>) metadata'sı olmayan dosyaları ve yarım kalmış yazma
///         kalıntılarını siler; yalnızca <see cref="MediaOptions.OrphanFileGracePeriod"/>'dan eskilerse ("dosyayı yaz →
///         metadata'yı kaydet" arasındaki dosyalar korunur).</item>
/// </list>
/// Loglar yalnızca sayı içerir. Tek örnek çalışır (ARCHITECTURE §13); iki örnekte de koşullu UPDATE sayesinde
/// kesinleşmiş dosya silinmez.
/// </summary>
internal sealed partial class OrphanMediaCleaner(
    IServiceScopeFactory scopeFactory,
    IOptions<MediaOptions> options,
    IConfiguration configuration,
    TimeProvider timeProvider,
    ILogger<OrphanMediaCleaner> logger) : BackgroundService
{
    // Platform.Hosting.BuildTimeTooling ile aynı ölçüt (o tip Platform'a internal): build-zamanı OpenAPI üretimi, dotnet ef.
    private static readonly bool BuildTimeToolingIsRunning =
        Assembly.GetEntryAssembly()?.GetName().Name is "GetDocument.Insider" or "ef";

    private readonly MediaOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.CleanupEnabled || BuildTimeToolingIsRunning)
        {
            LogDisabled(logger);
            return;
        }

        if (string.IsNullOrWhiteSpace(_options.RootPath) || string.IsNullOrWhiteSpace(configuration.GetConnectionString(DatabaseOptions.ConnectionStringName)))
        {
            // /health/ready bu durumda zaten "Unhealthy" döner; döngü her turda hata üretmesin.
            LogNotConfigured(logger);
            return;
        }

        await Task.Yield();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = await RunOnceAsync(stoppingToken).ConfigureAwait(false);
                if (result.ExpiredTemporary > 0 || result.OrphanObjects > 0)
                {
                    LogCleaned(logger, result.ExpiredTemporary, result.OrphanObjects);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                LogRunFailed(logger, ex);
            }

            try
            {
                await Task.Delay(_options.CleanupInterval, timeProvider, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>Tek bir temizlik turu.</summary>
    internal async Task<MediaCleanupResult> RunOnceAsync(CancellationToken cancellationToken)
    {
        var scope = scopeFactory.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var db = scope.ServiceProvider.GetRequiredService<MediaDbContext>();
            var storage = scope.ServiceProvider.GetRequiredService<IMediaStorage>();

            var expired = await DeleteExpiredTemporaryAsync(db, storage, cancellationToken).ConfigureAwait(false);
            var orphans = await DeleteOrphanObjectsAsync(db, storage, cancellationToken).ConfigureAwait(false);
            return new MediaCleanupResult(expired, orphans);
        }
    }

    private async Task<int> DeleteExpiredTemporaryAsync(MediaDbContext db, IMediaStorage storage, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var cutoff = now - _options.TemporaryRetention;
        var deleted = 0;
        List<(Guid Id, string StorageKey)> candidates;
        do
        {
            candidates = (await db.MediaFiles.AsNoTracking()
                    .Where(f => f.Status == MediaFileStatus.Temporary && f.CreatedAt < cutoff)
                    .OrderBy(f => f.CreatedAt)
                    .Take(_options.CleanupBatchSize)
                    .Select(f => new { f.Id, f.StorageKey })
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false))
                .Select(f => (f.Id, f.StorageKey))
                .ToList();

            foreach (var (id, storageKey) in candidates)
            {
                var updated = await db.MediaFiles
                    .Where(f => f.Id == id && f.Status == MediaFileStatus.Temporary)
                    .ExecuteUpdateAsync(
                        setters => setters
                            .SetProperty(f => f.Status, MediaFileStatus.Deleted)
                            .SetProperty(f => f.DeletedAt, now),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (updated != 1)
                {
                    continue; // arada kesinleşti
                }

                // Dosya silinemezse kayıt yine Deleted kalır; dosya bir sonraki yetim taramasında toplanır.
                try
                {
                    await storage.DeleteAsync(storageKey, cancellationToken).ConfigureAwait(false);
                }
                catch (MediaStorageException ex)
                {
                    LogDeleteFailed(logger, ex, id);
                }

                deleted++;
            }
        }
        while (candidates.Count == _options.CleanupBatchSize);

        return deleted;
    }

    private async Task<int> DeleteOrphanObjectsAsync(MediaDbContext db, IMediaStorage storage, CancellationToken cancellationToken)
    {
        var graceCutoff = timeProvider.GetUtcNow() - _options.OrphanFileGracePeriod;
        var deleted = 0;
        var batch = new List<StoredMediaObject>(_options.CleanupBatchSize);

        await foreach (var storedObject in storage.ListAsync(cancellationToken).ConfigureAwait(false))
        {
            if (storedObject.LastModifiedUtc >= graceCutoff)
            {
                continue;
            }

            batch.Add(storedObject);
            if (batch.Count == _options.CleanupBatchSize)
            {
                deleted += await DeleteOrphansInBatchAsync(db, storage, batch, cancellationToken).ConfigureAwait(false);
                batch.Clear();
            }
        }

        if (batch.Count > 0)
        {
            deleted += await DeleteOrphansInBatchAsync(db, storage, batch, cancellationToken).ConfigureAwait(false);
        }

        return deleted;
    }

    private static async Task<int> DeleteOrphansInBatchAsync(
        MediaDbContext db, IMediaStorage storage, List<StoredMediaObject> batch, CancellationToken cancellationToken)
    {
        var keys = batch.Where(o => o.StorageKey is not null).Select(o => o.StorageKey!).ToList();
        var active = keys.Count == 0
            ? []
            : (await db.MediaFiles.AsNoTracking()
                .Where(f => keys.Contains(f.StorageKey) && f.Status != MediaFileStatus.Deleted)
                .Select(f => f.StorageKey)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false)).ToHashSet(StringComparer.Ordinal);

        var deleted = 0;
        foreach (var storedObject in batch)
        {
            if (storedObject.StorageKey is not null && active.Contains(storedObject.StorageKey))
            {
                continue;
            }

            await storage.DeleteObjectAsync(storedObject, cancellationToken).ConfigureAwait(false);
            deleted++;
        }

        return deleted;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Yetim medya temizleyicisi devre dışı; çalışmayacak.")]
    private static partial void LogDisabled(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Medya kökü veya veritabanı bağlantı dizesi tanımlı değil; yetim medya temizleyicisi çalışmayacak.")]
    private static partial void LogNotConfigured(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Yetim medya temizliği: {ExpiredTemporary} süresi dolmuş geçici dosya, {OrphanObjects} metadata'sız dosya silindi.")]
    private static partial void LogCleaned(ILogger logger, int expiredTemporary, int orphanObjects);

    [LoggerMessage(Level = LogLevel.Error, Message = "Yetim medya temizliği turu başarısız oldu; bir sonraki turda yeniden denenecek.")]
    private static partial void LogRunFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Medya dosyası {MediaFileId} silindi olarak işaretlendi ama içeriği silinemedi; yetim taramasında yeniden denenecek.")]
    private static partial void LogDeleteFailed(ILogger logger, Exception exception, Guid mediaFileId);
}

/// <summary>Bir temizlik turunun sonucu.</summary>
internal readonly record struct MediaCleanupResult(int ExpiredTemporary, int OrphanObjects);
