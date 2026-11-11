using System.Buffers;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SokagimdakiSorun.Media.Contracts;
using SokagimdakiSorun.Media.Domain;
using SokagimdakiSorun.Media.Infrastructure;
using SokagimdakiSorun.Media.Infrastructure.Storage;
using SokagimdakiSorun.SharedKernel;

namespace SokagimdakiSorun.Media.Application;

/// <summary>
/// <see cref="IMediaService"/> uygulaması. Saklama sırası (ADR-0004 §4): doğrula → metadata'yı ayıkla → dosyayı yaz →
/// metadata satırını kaydet. Satır kaydedilemezse dosya hemen silinmeye çalışılır; silinemeyen dosya yetim taramasıyla
/// toplanır. Loglar yalnızca <see cref="MediaFileId"/>, içerik tipi, boyut ve hata kodu içerir; storage key, yol veya
/// içerik loglanmaz (ARCHITECTURE §11).
/// </summary>
internal sealed partial class MediaService(
    MediaDbContext db,
    IMediaStorage storage,
    IOptions<MediaOptions> options,
    TimeProvider timeProvider,
    ILogger<MediaService> logger) : IMediaService
{
    private const int ReadChunkSize = 81920;

    public async Task<Result<MediaFileId>> StoreAsync(Stream content, string contentType, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);

        if (!ImageFormats.TryParseContentType(contentType, out var format))
        {
            return Reject(MediaErrorCodes.UnsupportedType, "Yalnızca JPEG, PNG ve WebP görüntüleri kabul edilir.");
        }

        var maxBytes = options.Value.MaxFileSizeBytes;
        using var original = new MemoryStream();
        if (!await TryReadWithLimitAsync(content, original, maxBytes, cancellationToken).ConfigureAwait(false))
        {
            return Reject(MediaErrorCodes.TooLarge, $"Dosya boyutu {maxBytes} baytı aşamaz.");
        }

        if (original.Length == 0)
        {
            return Reject(MediaErrorCodes.Empty, "Dosya boş.");
        }

        var input = original.GetBuffer().AsSpan(0, (int)original.Length);
        if (ImageFormats.Detect(input) != format)
        {
            return Reject(MediaErrorCodes.SignatureMismatch, "Dosya içeriği bildirilen içerik tipiyle uyuşmuyor.");
        }

        using var sanitized = new MemoryStream((int)original.Length);
        if (!ImageMetadataStripper.TryStrip(format, input, sanitized))
        {
            return Reject(MediaErrorCodes.InvalidImage, "Görüntü dosyası bozuk veya işlenemiyor.");
        }

        var sha256 = Convert.ToHexStringLower(SHA256.HashData(sanitized.GetBuffer().AsSpan(0, (int)sanitized.Length)));
        var storageKey = StorageKey.New();
        sanitized.Position = 0;
        await storage.SaveAsync(storageKey, sanitized, cancellationToken).ConfigureAwait(false);

        var file = MediaFile.CreateTemporary(
            storage.ProviderName, storageKey, ImageFormats.ToContentType(format), sanitized.Length, sha256, timeProvider.GetUtcNow());
        db.MediaFiles.Add(file);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            db.Entry(file).State = EntityState.Detached;
            await TryDeleteAsync(storageKey).ConfigureAwait(false);
            throw;
        }

        LogStored(logger, file.Id, file.ContentType, file.SizeBytes, original.Length - file.SizeBytes);
        return new MediaFileId(file.Id);
    }

    public async Task<Result> CommitAsync(MediaFileId id, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        // Koşullu UPDATE: temizleyiciyle yarışta yalnızca biri kazanır (ikisi de "status = Temporary" şartına bağlı).
        var updated = await db.MediaFiles
            .Where(f => f.Id == id.Value && f.Status == MediaFileStatus.Temporary)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(f => f.Status, MediaFileStatus.Committed)
                    .SetProperty(f => f.CommittedAt, now),
                cancellationToken)
            .ConfigureAwait(false);
        if (updated == 1)
        {
            LogCommitted(logger, id.Value);
            return Result.Success();
        }

        var status = await db.MediaFiles.AsNoTracking()
            .Where(f => f.Id == id.Value)
            .Select(f => (MediaFileStatus?)f.Status)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return status switch
        {
            MediaFileStatus.Committed => Result.Success(), // idempotent
            MediaFileStatus.Deleted => Error.Conflict(MediaErrorCodes.Deleted, "Dosya silinmiş; kesinleştirilemez."),
            _ => Error.NotFound(MediaErrorCodes.NotFound, "Dosya bulunamadı."),
        };
    }

    public async Task<Result<MediaContent>> OpenReadAsync(MediaFileId id, CancellationToken cancellationToken)
    {
        var file = await db.MediaFiles.AsNoTracking()
            .Where(f => f.Id == id.Value && f.Status != MediaFileStatus.Deleted)
            .Select(f => new { f.StorageKey, f.ContentType, f.SizeBytes })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (file is null)
        {
            return Error.NotFound(MediaErrorCodes.NotFound, "Dosya bulunamadı.");
        }

        var stream = await storage.OpenReadAsync(file.StorageKey, cancellationToken).ConfigureAwait(false);
        if (stream is null)
        {
            LogContentMissing(logger, id.Value);
            return Error.NotFound(MediaErrorCodes.NotFound, "Dosya bulunamadı.");
        }

        return new MediaContent(stream, file.ContentType, file.SizeBytes);
    }

    /// <summary>Akışı sınır aşılana kadar okur; bildirilen uzunluğa güvenilmez. Sınır aşılırsa <see langword="false"/>.</summary>
    private static async Task<bool> TryReadWithLimitAsync(Stream source, MemoryStream destination, long maxBytes, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(ReadChunkSize);
        try
        {
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(0, ReadChunkSize), cancellationToken).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > maxBytes)
                {
                    return false; // kalan içerik okunmaz
                }

                destination.Write(buffer, 0, read);
            }

            return true;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private Error Reject(string code, string message)
    {
        LogRejected(logger, code);
        return Error.Validation(code, message);
    }

    private async Task TryDeleteAsync(string storageKey)
    {
        try
        {
            await storage.DeleteAsync(storageKey, CancellationToken.None).ConfigureAwait(false);
        }
        catch (MediaStorageException ex)
        {
            LogCleanupDeferred(logger, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Medya dosyası {MediaFileId} geçici olarak saklandı ({ContentType}, {SizeBytes} bayt; {StrippedBytes} bayt metadata ayıklandı).")]
    private static partial void LogStored(ILogger logger, Guid mediaFileId, string contentType, long sizeBytes, long strippedBytes);

    [LoggerMessage(Level = LogLevel.Information, Message = "Medya dosyası {MediaFileId} kesinleştirildi.")]
    private static partial void LogCommitted(ILogger logger, Guid mediaFileId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Medya dosyası reddedildi: {ErrorCode}.")]
    private static partial void LogRejected(ILogger logger, string errorCode);

    [LoggerMessage(Level = LogLevel.Error, Message = "Medya dosyası {MediaFileId} için metadata var ama içerik bulunamadı.")]
    private static partial void LogContentMissing(ILogger logger, Guid mediaFileId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Metadata kaydedilemeyen dosya silinemedi; yetim taramasında temizlenecek.")]
    private static partial void LogCleanupDeferred(ILogger logger, Exception exception);
}
