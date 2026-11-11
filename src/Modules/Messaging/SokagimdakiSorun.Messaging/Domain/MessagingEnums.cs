namespace SokagimdakiSorun.Messaging.Domain;

/// <summary>Konuşma kanalı (DOMAIN_MODEL §3.3).</summary>
internal enum ConversationChannel
{
    WhatsApp,
}

/// <summary>Mesajın yönü.</summary>
internal enum MessageDirection
{
    Inbound,
    Outbound,
}

/// <summary>
/// Normalize edilmiş mesaj türü. Sağlayıcının özgün tür adı ayrıca <c>provider_message_type</c>'ta saklanır
/// (desteklenmeyen türler için).
/// </summary>
internal enum MessageType
{
    Text,
    Location,
    Image,
    Unsupported,
}

/// <summary>
/// Mesaj teslim durumu (DOMAIN_MODEL §3.3). Şikâyet ve intake durumlarından tamamen bağımsızdır (ADR-0003 §2).
/// Gelen: <see cref="Received"/>. Giden: <see cref="Queued"/> → <see cref="Sending"/> → <see cref="Sent"/> →
/// <see cref="Delivered"/> → <see cref="Read"/>, veya <see cref="Failed"/>.
/// </summary>
internal enum DeliveryStatus
{
    Received,
    Queued,
    Sending,
    Sent,
    Delivered,
    Read,
    Failed,
}

/// <summary>Intake durumu (DOMAIN_MODEL §5). Şikâyet durumuyla aynı küme değildir.</summary>
internal enum IntakeState
{
    Started,
    AwaitingCategory,
    AwaitingDescription,
    AwaitingLocation,
    AwaitingMedia,
    AwaitingConfirmation,
    Submitted,
    Cancelled,
    Expired,
    NeedsHumanHelp,
}

/// <summary>Webhook olayının sağlayıcısı.</summary>
internal enum WebhookProvider
{
    WhatsApp,
}

/// <summary>Inbox olayının türü.</summary>
internal enum InboxEventType
{
    /// <summary>Gelen mesaj.</summary>
    Message,

    /// <summary>Giden mesajın teslim durumu.</summary>
    Status,
}

/// <summary>Inbox satırının işlenme durumu.</summary>
internal enum InboxProcessingState
{
    /// <summary>İşlenmeyi bekliyor (ilk kez veya backoff sonrası).</summary>
    Pending,

    /// <summary>Bir işleyici tarafından alındı; <c>next_attempt_at</c> kira (lease) bitişidir.</summary>
    Processing,

    /// <summary>İşlendi (veya bilerek yok sayıldı).</summary>
    Processed,

    /// <summary>Azami denemeden sonra bırakıldı; otomatik denenmez.</summary>
    DeadLetter,
}

/// <summary>Intake durumlarıyla ilgili yardımcılar.</summary>
internal static class IntakeStates
{
    /// <summary>Aktif durumlar: konuşma başına en fazla bir tane (kısmi UNIQUE, DOMAIN_MODEL I-4).</summary>
    public static readonly IReadOnlyList<IntakeState> Active =
    [
        IntakeState.Started,
        IntakeState.AwaitingCategory,
        IntakeState.AwaitingDescription,
        IntakeState.AwaitingLocation,
        IntakeState.AwaitingMedia,
        IntakeState.AwaitingConfirmation,
    ];

    /// <summary>SQL filtresi (kısmi index ve sorgular için).</summary>
    public const string ActiveSqlList =
        "'Started', 'AwaitingCategory', 'AwaitingDescription', 'AwaitingLocation', 'AwaitingMedia', 'AwaitingConfirmation'";

    public static bool IsActive(this IntakeState state) => state <= IntakeState.AwaitingConfirmation;
}
