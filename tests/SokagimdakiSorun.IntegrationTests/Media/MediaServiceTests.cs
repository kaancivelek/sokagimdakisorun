using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SokagimdakiSorun.IntegrationTests.Database.Support;
using SokagimdakiSorun.IntegrationTests.Media.Support;
using SokagimdakiSorun.Media.Contracts;
using SokagimdakiSorun.Media.Domain;
using SokagimdakiSorun.Media.Infrastructure;
using SokagimdakiSorun.Media.TestSupport;
using SokagimdakiSorun.SharedKernel;

namespace SokagimdakiSorun.IntegrationTests.Media;

/// <summary>
/// WS-05: <see cref="IMediaService"/> uçtan uca — doğrulama, saklama, metadata ayıklama, yaşam döngüsü ve log
/// sızıntısı — gerçek PostgreSQL 18 ve geçici medya kökü ile.
/// </summary>
[Collection(DatabaseCollection.Name)]
[Trait("Category", "Database")]
public sealed class MediaServiceTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Valid_image_is_stored_as_temporary_with_metadata_stripped_and_read_back()
    {
        await using var media = await MediaTestContext.CreateAsync(postgres);
        await using var scope = media.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IMediaService>();

        var stored = await service.StoreAsync(new MemoryStream(TestImages.Jpeg(withMetadata: true)), "image/jpeg", Ct);

        Assert.True(stored.IsSuccess);
        var expected = TestImages.Jpeg(withMetadata: false);
        var row = await scope.ServiceProvider.GetRequiredService<MediaDbContext>().MediaFiles.AsNoTracking().SingleAsync(Ct);
        Assert.Equal(stored.Value.Value, row.Id);
        Assert.Equal(MediaFileStatus.Temporary, row.Status);
        Assert.Equal("image/jpeg", row.ContentType);
        Assert.Equal("local", row.StorageProvider);
        Assert.Equal(expected.Length, row.SizeBytes);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(expected)), row.Sha256);
        Assert.Null(row.CommittedAt);

        // Dosya adı kullanıcı girdisinden değil, rastgele anahtardan: <kök>/<2 hane>/<32 hane>.
        var file = Assert.Single(media.Files());
        Assert.Equal(Path.Combine(row.StorageKey[..2], row.StorageKey), file);
        Assert.False(TestImages.ContainsMarker(await File.ReadAllBytesAsync(Path.Combine(media.Root, file), Ct)));

        var opened = await service.OpenReadAsync(stored.Value, Ct);
        Assert.True(opened.IsSuccess);
        await using var content = opened.Value;
        using var copy = new MemoryStream();
        await content.Content.CopyToAsync(copy, Ct);
        Assert.Equal(expected, copy.ToArray());
        Assert.Equal("image/jpeg", content.ContentType);
        Assert.Equal(expected.Length, content.Length);
    }

    public static TheoryData<string, string, string, long?> RejectedUploads => new()
    {
        // içerik tipi, içerik (ad), beklenen kod, boyut sınırı
        { "image/gif", "jpeg", MediaErrorCodes.UnsupportedType, null },
        { "application/octet-stream", "jpeg", MediaErrorCodes.UnsupportedType, null },
        { "image/jpeg", "png", MediaErrorCodes.SignatureMismatch, null },
        { "image/png", "html", MediaErrorCodes.SignatureMismatch, null },
        { "image/webp", "empty", MediaErrorCodes.Empty, null },
        { "image/jpeg", "jpeg", MediaErrorCodes.TooLarge, 64L },
        { "image/jpeg", "truncated-jpeg", MediaErrorCodes.InvalidImage, null },
    };

    [Theory]
    [MemberData(nameof(RejectedUploads))]
    public async Task Invalid_upload_is_rejected_and_leaves_no_file_or_row(string contentType, string content, string expectedCode, long? maxBytes)
    {
        await using var media = await MediaTestContext.CreateAsync(postgres, maxBytes is null
            ? null
            : new Dictionary<string, string?> { ["Media:MaxFileSizeBytes"] = maxBytes.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        await using var scope = media.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IMediaService>();
        byte[] bytes = content switch
        {
            "jpeg" => TestImages.Jpeg(withMetadata: true),
            "png" => TestImages.Png(withMetadata: false),
            "html" => "<html><script>alert(1)</script></html>"u8.ToArray(),
            "empty" => [],
            "truncated-jpeg" => TestImages.Jpeg(withMetadata: false)[..^4],
            _ => throw new ArgumentOutOfRangeException(nameof(content)),
        };

        var result = await service.StoreAsync(new MemoryStream(bytes), contentType, Ct);

        Assert.True(result.IsFailure);
        Assert.Equal(expectedCode, result.Error.Code);
        Assert.Equal(ErrorType.Validation, result.Error.Type);
        Assert.Empty(media.Files());
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<MediaDbContext>().MediaFiles.CountAsync(Ct));
    }

    [Fact]
    public async Task Size_limit_is_counted_while_reading_and_does_not_trust_declared_length()
    {
        await using var media = await MediaTestContext.CreateAsync(postgres, new Dictionary<string, string?> { ["Media:MaxFileSizeBytes"] = "1024" });
        await using var scope = media.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IMediaService>();
        var source = new LyingStream(TestImages.Jpeg(withMetadata: false), totalLength: 10L * 1024 * 1024 * 1024);

        var result = await service.StoreAsync(source, "image/jpeg", Ct);

        Assert.Equal(MediaErrorCodes.TooLarge, result.Error.Code);
        Assert.True(source.BytesRead <= 1024 + 81920, $"Sınır aşıldıktan sonra okumaya devam edildi: {source.BytesRead} bayt.");
    }

    [Fact]
    public async Task Commit_is_idempotent_and_unknown_or_deleted_files_are_reported()
    {
        await using var media = await MediaTestContext.CreateAsync(postgres);
        await using var scope = media.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IMediaService>();
        var id = (await service.StoreAsync(new MemoryStream(TestImages.Png(withMetadata: true)), "image/png", Ct)).Value;

        Assert.True((await service.CommitAsync(id, Ct)).IsSuccess);
        Assert.True((await service.CommitAsync(id, Ct)).IsSuccess);

        var row = await scope.ServiceProvider.GetRequiredService<MediaDbContext>().MediaFiles.AsNoTracking().SingleAsync(Ct);
        Assert.Equal(MediaFileStatus.Committed, row.Status);
        Assert.Equal(media.Clock.GetUtcNow(), row.CommittedAt);

        var unknown = await service.CommitAsync(new MediaFileId(Guid.CreateVersion7()), Ct);
        Assert.Equal(MediaErrorCodes.NotFound, unknown.Error.Code);
        Assert.Equal(ErrorType.NotFound, unknown.Error.Type);
        Assert.Equal(MediaErrorCodes.NotFound, (await service.OpenReadAsync(new MediaFileId(Guid.CreateVersion7()), Ct)).Error.Code);
    }

    [Fact]
    public async Task Expired_temporary_file_is_cleaned_and_can_no_longer_be_committed_or_read()
    {
        await using var media = await MediaTestContext.CreateAsync(postgres);
        await using var scope = media.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IMediaService>();
        var id = (await service.StoreAsync(new MemoryStream(TestImages.WebP(withMetadata: true)), "image/webp", Ct)).Value;

        media.Clock.Advance(TimeSpan.FromHours(49));
        var result = await media.GetRequiredService<OrphanMediaCleaner>().RunOnceAsync(Ct);

        Assert.Equal(new MediaCleanupResult(1, 0), result);
        Assert.Empty(media.Files());
        var commit = await service.CommitAsync(id, Ct);
        Assert.Equal(MediaErrorCodes.Deleted, commit.Error.Code);
        Assert.Equal(ErrorType.Conflict, commit.Error.Type);
        Assert.Equal(MediaErrorCodes.NotFound, (await service.OpenReadAsync(id, Ct)).Error.Code);

        var row = await scope.ServiceProvider.GetRequiredService<MediaDbContext>().MediaFiles.AsNoTracking().SingleAsync(Ct);
        Assert.Equal(MediaFileStatus.Deleted, row.Status);
        Assert.Equal(media.Clock.GetUtcNow(), row.DeletedAt);
    }

    [Fact]
    public async Task Logs_never_contain_storage_key_root_path_or_file_content()
    {
        await using var media = await MediaTestContext.CreateAsync(postgres);
        await using var scope = media.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IMediaService>();

        var id = (await service.StoreAsync(new MemoryStream(TestImages.Jpeg(withMetadata: true)), "image/jpeg", Ct)).Value;
        await service.StoreAsync(new MemoryStream(TestImages.Png(withMetadata: true)), "image/jpeg", Ct); // red
        await service.CommitAsync(id, Ct);
        var key = (await scope.ServiceProvider.GetRequiredService<MediaDbContext>().MediaFiles.AsNoTracking().SingleAsync(Ct)).StorageKey;
        await File.WriteAllBytesAsync(Path.Combine(media.Root, "stray.bin"), [1], Ct);
        File.SetLastWriteTimeUtc(Path.Combine(media.Root, "stray.bin"), DateTime.UtcNow.AddDays(-1));
        await media.GetRequiredService<OrphanMediaCleaner>().RunOnceAsync(Ct);

        Assert.NotEmpty(media.Logs.Entries);
        Assert.Contains(media.Logs.Entries, e => e.Contains(id.Value.ToString(), StringComparison.Ordinal));
        foreach (var entry in media.Logs.Entries)
        {
            Assert.DoesNotContain(key, entry, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(media.Root, entry, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(TestImages.Marker, entry, StringComparison.Ordinal);
        }
    }

    /// <summary>Uzunluğunu yanlış (çok büyük) bildiren ve sonsuz veri üreten akış; okunan baytları sayar.</summary>
    private sealed class LyingStream(byte[] prefix, long totalLength) : Stream
    {
        public long BytesRead { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => totalLength;

        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = Math.Min(count, 4096);
            for (var i = 0; i < n; i++)
            {
                buffer[offset + i] = BytesRead + i < prefix.Length ? prefix[BytesRead + i] : (byte)0xAB;
            }

            BytesRead += n;
            return n;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
