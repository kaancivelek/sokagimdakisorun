using System.Runtime.CompilerServices;
using Microsoft.Extensions.Options;
using SokagimdakiSorun.Media.Application;

namespace SokagimdakiSorun.Media.Infrastructure.Storage;

/// <summary>
/// Yerel dosya sistemi uygulaması (ADR-0004 §1). Yerelde <c>./.local/media</c>, Railway'de Volume yolu; fark yalnızca
/// <c>Media__RootPath</c> değeridir. Yerleşim: <c>&lt;kök&gt;/&lt;anahtarın ilk 2 hanesi&gt;/&lt;anahtar&gt;</c>
/// (uzantısız). Güvenlik:
/// <list type="bullet">
///   <item>Yol yalnızca <see cref="StorageKey.IsValid"/>'den geçen anahtardan kurulur; ayrıca sonucun kökün içinde
///         kaldığı doğrulanır (yol geçişi imkânsız).</item>
///   <item>Yazma önce aynı dizinde <c>FileMode.CreateNew</c> ile geçici dosyaya yapılır, sonra
///         <c>File.Move(overwrite: false)</c> ile yerine taşınır: mevcut dosyanın üzerine yazılamaz ve yarım dosya
///         asıl adla görünmez.</item>
///   <item>Hata mesajları yol veya anahtar içermez; iç exception (mesajında yol olabilir) dışarı taşınmaz.</item>
/// </list>
/// </summary>
internal sealed class LocalFileSystemMediaStorage(IOptions<MediaOptions> options) : IMediaStorage
{
    public const string Name = "local";

    private const string PartialSuffix = ".partial";
    private const int BufferSize = 81920;

    private readonly Lazy<string> _root = new(() => ResolveRoot(options.Value.RootPath));

    public string ProviderName => Name;

    public async Task SaveAsync(string storageKey, Stream content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);

        var finalPath = PathFor(storageKey);
        var directory = Path.GetDirectoryName(finalPath)!;
        var partialPath = Path.Combine(directory, $"{storageKey}.{StorageKey.New()}{PartialSuffix}");
        try
        {
            Directory.CreateDirectory(directory);
            var file = new FileStream(partialPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous);
            await using (file.ConfigureAwait(false))
            {
                await content.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
                await file.FlushAsync(cancellationToken).ConfigureAwait(false);
                file.Flush(flushToDisk: true);
            }

            if (File.Exists(finalPath))
            {
                throw new MediaStorageConflictException();
            }

            // overwrite: false → hedef varsa IOException (Windows: MoveFileEx bayraksız; Unix: link + unlink).
            File.Move(partialPath, finalPath, overwrite: false);
        }
        catch (IOException) when (File.Exists(finalPath))
        {
            throw new MediaStorageConflictException();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw Sanitized("Medya dosyası yazılamadı.", ex);
        }
        finally
        {
            TryDelete(partialPath);
        }
    }

    public Task<Stream?> OpenReadAsync(string storageKey, CancellationToken cancellationToken)
    {
        var path = PathFor(storageKey);
        try
        {
            Stream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, BufferSize, FileOptions.Asynchronous);
            return Task.FromResult<Stream?>(stream);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return Task.FromResult<Stream?>(null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw Sanitized("Medya dosyası okunamadı.", ex);
        }
    }

    public Task DeleteAsync(string storageKey, CancellationToken cancellationToken)
    {
        DeleteFile(PathFor(storageKey));
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<StoredMediaObject> ListAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var root = _root.Value;
        if (!Directory.Exists(root))
        {
            yield break;
        }

        var enumeration = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint, // sembolik bağlantıların ardına geçilmez
            ReturnSpecialDirectories = false,
        };

        foreach (var path in Directory.EnumerateFiles(root, "*", enumeration))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var name = Path.GetFileName(path);
            var shard = Path.GetFileName(Path.GetDirectoryName(path));
            var isKey = StorageKey.IsValid(name) && string.Equals(shard, name[..2], StringComparison.Ordinal)
                && string.Equals(Path.GetDirectoryName(Path.GetDirectoryName(path)), root, PathComparison);
            DateTimeOffset lastModified;
            try
            {
                lastModified = new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            yield return new StoredMediaObject(Path.GetRelativePath(root, path), isKey ? name : null, lastModified);
            await Task.Yield();
        }
    }

    public Task DeleteObjectAsync(StoredMediaObject storedObject, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(storedObject);

        var root = _root.Value;
        var path = Path.GetFullPath(Path.Combine(root, storedObject.Locator));
        EnsureInsideRoot(root, path);
        DeleteFile(path);
        return Task.CompletedTask;
    }

    public async Task EnsureWritableAsync(CancellationToken cancellationToken)
    {
        var root = _root.Value;
        var probePath = Path.Combine(root, $".write-probe-{StorageKey.New()}");
        try
        {
            Directory.CreateDirectory(root);
            var probe = new FileStream(probePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
            await using (probe.ConfigureAwait(false))
            {
                await probe.WriteAsync(new byte[] { 0 }, cancellationToken).ConfigureAwait(false);
                await probe.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw Sanitized("Medya kökü yazılabilir değil.", ex);
        }
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private string PathFor(string storageKey)
    {
        if (!StorageKey.IsValid(storageKey))
        {
            throw new ArgumentException("Geçersiz storage key biçimi.", nameof(storageKey));
        }

        var root = _root.Value;
        var path = Path.GetFullPath(Path.Combine(root, storageKey[..2], storageKey));
        EnsureInsideRoot(root, path);
        return path;
    }

    private static void EnsureInsideRoot(string root, string fullPath)
    {
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(prefix, PathComparison))
        {
            throw new MediaStorageException("Medya yolu kök dizinin dışına çıkıyor; işlem reddedildi.");
        }
    }

    private static string ResolveRoot(string? rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            throw new MediaStorageException("Medya kökü tanımlı değil: Media__RootPath ortam değişkenini ayarlayın.");
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
    }

    private static void DeleteFile(string path)
    {
        try
        {
            File.Delete(path); // yoksa exception fırlatmaz
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw Sanitized("Medya dosyası silinemedi.", ex);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Kalıntı .partial dosyası OrphanMediaCleaner tarafından toplanır.
        }
    }

    // İç exception'ın mesajı tam yolu içerebilir; yalnızca türü ve HResult taşınır.
    private static MediaStorageException Sanitized(string message, Exception inner) =>
        new($"{message} ({inner.GetType().Name}, HResult 0x{inner.HResult:X8})");
}
