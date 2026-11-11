namespace SokagimdakiSorun.Media.Contracts;

/// <summary>
/// <see cref="IMediaService.OpenReadAsync"/> sonucu: okunabilir akış ve içerik tipi. Akışın sahibi çağırandır;
/// kullanım sonunda dispose edilmelidir. Fiziksel yol veya storage key taşımaz.
/// </summary>
public sealed class MediaContent : IAsyncDisposable, IDisposable
{
    public MediaContent(Stream content, string contentType, long length)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);

        Content = content;
        ContentType = contentType;
        Length = length;
    }

    /// <summary>Dosya içeriği (salt okunur akış).</summary>
    public Stream Content { get; }

    /// <summary>Doğrulanmış içerik tipi: <c>image/jpeg</c>, <c>image/png</c> veya <c>image/webp</c>.</summary>
    public string ContentType { get; }

    /// <summary>Bayt cinsinden boyut.</summary>
    public long Length { get; }

    public ValueTask DisposeAsync() => Content.DisposeAsync();

    public void Dispose() => Content.Dispose();
}
