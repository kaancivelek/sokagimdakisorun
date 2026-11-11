using Microsoft.Extensions.Options;
using SokagimdakiSorun.Media.Application;
using SokagimdakiSorun.Media.Infrastructure.Storage;

namespace SokagimdakiSorun.Media.Tests;

/// <summary>
/// Yerel dosya sistemi saklaması: üzerine yazma yok, yol geçişi yok, hata mesajında yol yok.
/// Yalnızca test başına oluşturulan geçici dizini kullanır.
/// </summary>
public sealed class LocalFileSystemMediaStorageTests : IDisposable
{
    private readonly string _sandbox = Path.Combine(Path.GetTempPath(), "sokagimdakisorun-tests", Guid.CreateVersion7().ToString("N"));
    private readonly string _root;
    private readonly LocalFileSystemMediaStorage _storage;

    public LocalFileSystemMediaStorageTests()
    {
        _root = Path.Combine(_sandbox, "media");
        _storage = Create(_root);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(_sandbox))
        {
            Directory.Delete(_sandbox, recursive: true);
        }
    }

    [Fact]
    public async Task Saved_content_round_trips_under_a_sharded_key_path()
    {
        var key = StorageKey.New();
        await _storage.SaveAsync(key, new MemoryStream([1, 2, 3]), Ct);

        Assert.True(File.Exists(Path.Combine(_root, key[..2], key)));
        await using var read = await _storage.OpenReadAsync(key, Ct);
        Assert.NotNull(read);
        using var copy = new MemoryStream();
        await read.CopyToAsync(copy, Ct);
        Assert.Equal([1, 2, 3], copy.ToArray());
        Assert.Empty(Directory.GetFiles(_root, "*.partial", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Second_write_with_same_key_is_refused_and_original_is_kept()
    {
        var key = StorageKey.New();
        await _storage.SaveAsync(key, new MemoryStream([1, 2, 3]), Ct);

        await Assert.ThrowsAsync<MediaStorageConflictException>(() => _storage.SaveAsync(key, new MemoryStream([9, 9]), Ct));

        Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(Path.Combine(_root, key[..2], key), Ct));
        Assert.Empty(Directory.GetFiles(_root, "*.partial", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("../../../../outside")]
    [InlineData("..\\..\\..\\outside")]
    [InlineData("/etc/passwd")]
    [InlineData("C:\\Windows\\win.ini")]
    [InlineData("aa/../../outside0000000000000000")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF")] // büyük harf
    [InlineData("0123456789abcdef0123456789abcde")] // 31 hane
    [InlineData("0123456789abcdef0123456789abcdef.jpg")]
    [InlineData("")]
    public async Task Keys_that_are_not_server_generated_hex_are_rejected_before_touching_disk(string key)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _storage.SaveAsync(key, new MemoryStream([1]), Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => _storage.OpenReadAsync(key, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => _storage.DeleteAsync(key, Ct));

        Assert.Equal([], Directory.Exists(_sandbox) ? Directory.GetFileSystemEntries(_sandbox, "*", SearchOption.AllDirectories) : []);
    }

    [Fact]
    public async Task Listed_object_locator_cannot_escape_root_on_delete()
    {
        var outside = Path.Combine(_sandbox, "outside.txt");
        Directory.CreateDirectory(_sandbox);
        await File.WriteAllTextAsync(outside, "keep", Ct);

        await Assert.ThrowsAsync<MediaStorageException>(() =>
            _storage.DeleteObjectAsync(new StoredMediaObject(Path.Combine("..", "outside.txt"), null, DateTimeOffset.UnixEpoch), Ct));

        Assert.True(File.Exists(outside));
    }

    [Fact]
    public async Task Listing_reports_keys_and_marks_leftovers_without_key()
    {
        var key = StorageKey.New();
        await _storage.SaveAsync(key, new MemoryStream([1]), Ct);
        await File.WriteAllBytesAsync(Path.Combine(_root, key[..2], $"{key}.abc.partial"), [1], Ct);
        await File.WriteAllBytesAsync(Path.Combine(_root, "stray.bin"), [1], Ct);

        var listed = new List<StoredMediaObject>();
        await foreach (var item in _storage.ListAsync(Ct))
        {
            listed.Add(item);
        }

        Assert.Equal(3, listed.Count);
        Assert.Equal([key], listed.Where(o => o.StorageKey is not null).Select(o => o.StorageKey));
    }

    [Fact]
    public async Task Unconfigured_or_unwritable_root_fails_without_revealing_path()
    {
        var unconfigured = Create(null);
        var error = await Assert.ThrowsAsync<MediaStorageException>(() => unconfigured.EnsureWritableAsync(Ct));
        Assert.Contains("Media__RootPath", error.Message, StringComparison.Ordinal);

        // Kök bir dosyanın altında: dizin oluşturulamaz.
        Directory.CreateDirectory(_sandbox);
        var blocker = Path.Combine(_sandbox, "blocker");
        await File.WriteAllTextAsync(blocker, "x", Ct);
        var unwritable = Create(Path.Combine(blocker, "media"));
        error = await Assert.ThrowsAsync<MediaStorageException>(() => unwritable.EnsureWritableAsync(Ct));
        Assert.DoesNotContain(_sandbox, error.Message, StringComparison.OrdinalIgnoreCase);

        await _storage.EnsureWritableAsync(Ct);
        Assert.Empty(Directory.GetFiles(_root)); // sonda dosyası kapanınca silinir
    }

    private static LocalFileSystemMediaStorage Create(string? root) =>
        new(Options.Create(new MediaOptions { RootPath = root }));
}
