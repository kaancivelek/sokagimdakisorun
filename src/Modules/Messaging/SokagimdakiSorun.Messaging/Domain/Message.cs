namespace SokagimdakiSorun.Messaging.Domain;

/// <summary>
/// Tek bir gelen veya giden mesaj (DOMAIN_MODEL §3.3). Giden kuyruk ayrı tablo değildir: <see cref="MessageDirection.Outbound"/> +
/// <see cref="DeliveryStatus.Queued"/>/<see cref="DeliveryStatus.Sending"/> satırlar kuyruğun kendisidir.
/// Tekrar koruması: gelen mesajda <see cref="ProviderMessageId"/> (kısmi UNIQUE), giden mesajda <see cref="DedupKey"/> (kısmi UNIQUE).
/// </summary>
internal sealed class Message
{
    public const int TextMaxLength = 4096;
    public const int ProviderIdMaxLength = 128;
    public const int ProviderTypeMaxLength = 32;
    public const int DedupKeyMaxLength = 200;
    public const int LastErrorMaxLength = 500;

    private Message()
    {
    }

    public Guid Id { get; private set; }

    public Guid ConversationId { get; private set; }

    public MessageDirection Direction { get; private set; }

    /// <summary>Sağlayıcının mesaj kimliği (Meta: <c>wamid</c>). Gelen mesajda zorunlu; giden mesajda gönderimden sonra dolar.</summary>
    public string? ProviderMessageId { get; private set; }

    public MessageType MessageType { get; private set; }

    /// <summary>Sağlayıcının özgün tür adı (ör. <c>audio</c>); desteklenmeyen türleri ayırt etmek için.</summary>
    public string? ProviderMessageType { get; private set; }

    /// <summary>Metin (gelen metin, resim açıklaması, adres veya giden mesaj). Kişisel veri olabilir; loglanmaz.</summary>
    public string? TextBody { get; private set; }

    public double? Latitude { get; private set; }

    public double? Longitude { get; private set; }

    /// <summary>Media modülündeki dosya (skaler; FK yok).</summary>
    public Guid? MediaFileId { get; private set; }

    public Guid? IntakeSessionId { get; private set; }

    /// <summary>İlişkili şikâyet (skaler; FK yok).</summary>
    public Guid? ComplaintId { get; private set; }

    public DeliveryStatus DeliveryStatus { get; private set; }

    /// <summary>Giden mesajın idempotency anahtarı (ör. <c>event:{eventId}</c>).</summary>
    public string? DedupKey { get; private set; }

    public int AttemptCount { get; private set; }

    /// <summary>Giden kuyrukta: bir sonraki deneme veya (<see cref="DeliveryStatus.Sending"/> iken) kiranın bitişi.</summary>
    public DateTimeOffset? NextAttemptAt { get; private set; }

    /// <summary>Son hata özeti; kişisel veri içermez.</summary>
    public string? LastError { get; private set; }

    /// <summary>Sağlayıcının bildirdiği zaman (gelen mesaj veya son teslim durumu).</summary>
    public DateTimeOffset? ProviderTimestamp { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Gelen mesaj. Kimlik, inbox olayının kimliğidir (yeniden denemede deterministik).</summary>
    public static Message Inbound(
        Guid id, Guid conversationId, string providerMessageId, MessageType type, string providerType, string? text,
        double? latitude, double? longitude, Guid? mediaFileId, DateTimeOffset? providerTimestamp, DateTimeOffset now) => new()
    {
        Id = id,
        ConversationId = conversationId,
        Direction = MessageDirection.Inbound,
        ProviderMessageId = providerMessageId,
        MessageType = type,
        ProviderMessageType = Truncate(providerType, ProviderTypeMaxLength),
        TextBody = text is null ? null : Truncate(text, TextMaxLength),
        Latitude = latitude,
        Longitude = longitude,
        MediaFileId = mediaFileId,
        DeliveryStatus = DeliveryStatus.Received,
        ProviderTimestamp = providerTimestamp,
        CreatedAt = now,
        UpdatedAt = now,
    };

    /// <summary>Kuyruğa giden metin mesajı.</summary>
    public static Message Outbound(
        Guid conversationId, string text, string dedupKey, Guid? intakeSessionId, Guid? complaintId, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        ConversationId = conversationId,
        Direction = MessageDirection.Outbound,
        MessageType = MessageType.Text,
        ProviderMessageType = "text",
        TextBody = Truncate(text, TextMaxLength),
        IntakeSessionId = intakeSessionId,
        ComplaintId = complaintId,
        DeliveryStatus = DeliveryStatus.Queued,
        DedupKey = dedupKey,
        NextAttemptAt = now,
        CreatedAt = now,
        UpdatedAt = now,
    };

    public void LinkTo(Guid? intakeSessionId, Guid? complaintId)
    {
        IntakeSessionId = intakeSessionId ?? IntakeSessionId;
        ComplaintId = complaintId ?? ComplaintId;
    }

    /// <summary>Gönderici mesajı aldı: deneme sayılır ve kira başlar.</summary>
    public void BeginSending(DateTimeOffset leaseUntil, DateTimeOffset now)
    {
        DeliveryStatus = DeliveryStatus.Sending;
        AttemptCount++;
        NextAttemptAt = leaseUntil;
        UpdatedAt = now;
    }

    public void MarkSent(string providerMessageId, DateTimeOffset now)
    {
        DeliveryStatus = DeliveryStatus.Sent;
        ProviderMessageId = providerMessageId;
        NextAttemptAt = null;
        LastError = null;
        UpdatedAt = now;
    }

    public void ScheduleRetry(DateTimeOffset at, string error, DateTimeOffset now)
    {
        DeliveryStatus = DeliveryStatus.Queued;
        NextAttemptAt = at;
        LastError = Truncate(error, LastErrorMaxLength);
        UpdatedAt = now;
    }

    public void MarkFailed(string reason, DateTimeOffset now)
    {
        DeliveryStatus = DeliveryStatus.Failed;
        NextAttemptAt = null;
        LastError = Truncate(reason, LastErrorMaxLength);
        UpdatedAt = now;
    }

    /// <summary>
    /// Sağlayıcının teslim durumu olayı (yönlendirme önceliği 1). Durum yalnızca ileri gider (Sent → Delivered → Read);
    /// tekrar gelen veya sırası bozuk olay geri almaz. Değişiklik olduysa <see langword="true"/>.
    /// </summary>
    public bool ApplyProviderStatus(DeliveryStatus status, DateTimeOffset? timestamp, string? error, DateTimeOffset now)
    {
        if (Direction != MessageDirection.Outbound || status is DeliveryStatus.Received or DeliveryStatus.Queued or DeliveryStatus.Sending)
        {
            return false;
        }

        var changed = status switch
        {
            DeliveryStatus.Failed => DeliveryStatus is not (DeliveryStatus.Read or DeliveryStatus.Failed),
            _ => Rank(status) > Rank(DeliveryStatus),
        };

        if (!changed)
        {
            return false;
        }

        DeliveryStatus = status;
        ProviderTimestamp = timestamp ?? ProviderTimestamp;
        if (status == DeliveryStatus.Failed)
        {
            LastError = Truncate(error ?? "provider_failed", LastErrorMaxLength);
        }

        UpdatedAt = now;
        return true;
    }

    private static int Rank(DeliveryStatus status) => status switch
    {
        DeliveryStatus.Sent => 1,
        DeliveryStatus.Delivered => 2,
        DeliveryStatus.Read => 3,
        _ => 0,
    };

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
