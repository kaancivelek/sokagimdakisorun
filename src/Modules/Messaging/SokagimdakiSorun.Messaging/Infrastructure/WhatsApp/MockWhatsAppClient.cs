using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;

namespace SokagimdakiSorun.Messaging.Infrastructure.WhatsApp;

/// <summary>
/// Sahte sağlayıcı (<c>WhatsApp__Provider=Mock</c>). Hiçbir dış çağrı yapmaz. Gönderilen mesajları bellekte tutar (testler okur),
/// isteğe bağlı olarak sıradaki gönderimlerin sonucunu senaryolaştırır (geçici/kalıcı hata). Medya indirmede kayıtlı içerik
/// yoksa geçerli küçük bir PNG döner; <c>mock-missing</c> kimliği kalıcı, <c>mock-transient</c> geçici hata üretir.
/// <b>Güven sınırı:</b> mock'un davranışı gerçek Meta davranışını kanıtlamaz (WS-10, V-8).
/// </summary>
internal sealed partial class MockWhatsAppClient(ILogger<MockWhatsAppClient> logger) : IWhatsAppClient
{
    public const string MissingMediaId = "mock-missing";
    public const string TransientMediaId = "mock-transient";

    private readonly ConcurrentQueue<SendResult> _scriptedSends = new();
    private readonly ConcurrentDictionary<string, (byte[] Content, string ContentType)> _media = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<OutboundText> _sent = new();

    /// <summary>Başarıyla "gönderilen" mesajlar (yalnızca testler ve yerel tanı için).</summary>
    public IReadOnlyCollection<OutboundText> Sent => _sent;

    /// <summary>Sonraki gönderimlerin sonuçlarını sırayla belirler; kuyruk boşken gönderim başarılıdır.</summary>
    public void ScriptSends(params SendResult[] results)
    {
        foreach (var result in results)
        {
            _scriptedSends.Enqueue(result);
        }
    }

    /// <summary>Bir medya kimliği için indirilecek içeriği kaydeder.</summary>
    public void RegisterMedia(string mediaId, byte[] content, string contentType) => _media[mediaId] = (content, contentType);

    public Task<SendResult> SendTextAsync(OutboundText message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();

        var result = _scriptedSends.TryDequeue(out var scripted)
            ? scripted
            : SendResult.Success($"wamid.mock.{Guid.CreateVersion7():N}");

        if (result.Outcome == SendOutcome.Sent)
        {
            _sent.Enqueue(message);
        }

        // Yalnızca kendi mesaj kimliğimiz ve sonuç; alıcı ve metin loglanmaz.
        LogSend(logger, message.MessageId, result.Outcome);
        return Task.FromResult(result);
    }

    public Task<MediaDownloadResult> DownloadMediaAsync(string providerMediaId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(providerMediaId switch
        {
            MissingMediaId => MediaDownloadResult.Permanent("mock_media_not_found"),
            TransientMediaId => MediaDownloadResult.Transient("mock_media_unavailable"),
            _ when _media.TryGetValue(providerMediaId, out var registered) =>
                MediaDownloadResult.Success(new MemoryStream(registered.Content, writable: false), registered.ContentType),
            _ => MediaDownloadResult.Success(new MemoryStream(SamplePng, writable: false), "image/png"),
        });
    }

    /// <summary>Yapısal olarak geçerli 1×1 PNG (Media doğrulamasından geçer).</summary>
    internal static readonly byte[] SamplePng = BuildSamplePng();

    private static byte[] BuildSamplePng()
    {
        var b = new List<byte> { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        Chunk(b, "IHDR", [0, 0, 0, 1, 0, 0, 0, 1, 8, 0, 0, 0, 0]);
        Chunk(b, "IDAT", [0x78, 0x9C, 0x63, 0x60, 0x00, 0x00, 0x00, 0x02, 0x00, 0x01]);
        Chunk(b, "IEND", []);
        return [.. b];

        static void Chunk(List<byte> target, string type, byte[] data)
        {
            var length = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
            target.AddRange(length);
            var typeBytes = Encoding.ASCII.GetBytes(type);
            target.AddRange(typeBytes);
            target.AddRange(data);
            var crc = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32([.. typeBytes, .. data]));
            target.AddRange(crc);
        }

        static uint Crc32(byte[] data)
        {
            var crc = 0xFFFFFFFFu;
            foreach (var value in data)
            {
                crc ^= value;
                for (var k = 0; k < 8; k++)
                {
                    crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
                }
            }

            return ~crc;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Mock WhatsApp gönderimi: mesaj {MessageId}, sonuç {Outcome}.")]
    private static partial void LogSend(ILogger logger, Guid messageId, SendOutcome outcome);
}
