namespace SokagimdakiSorun.Messaging.Domain;

/// <summary>
/// Webhook'tan kabul edilmiş tek olay (DOMAIN_MODEL §3.3). <see cref="DedupKey"/> UNIQUE'tir (I-3): mesaj için
/// <c>msg:{provider_message_id}</c>, durum için <c>status:{provider_message_id}:{status}</c>. <see cref="Payload"/> yalnızca
/// sağlayıcı verisidir (jsonb); ham gövde, imza veya token loglanmaz ve API yanıtına girmez.
/// </summary>
internal sealed class WebhookInboxEvent
{
    public const int DedupKeyMaxLength = 200;
    public const int LastErrorMaxLength = 500;

    private WebhookInboxEvent()
    {
    }

    public Guid Id { get; private set; }

    public WebhookProvider Provider { get; private set; }

    public string DedupKey { get; private set; } = string.Empty;

    public InboxEventType EventType { get; private set; }

    public string Payload { get; private set; } = "{}";

    public DateTimeOffset ReceivedAt { get; private set; }

    public InboxProcessingState ProcessingState { get; private set; }

    /// <summary>Alınma (claim) sayısı; azami değere ulaşınca satır dead-letter olur (sonsuz döngü yok).</summary>
    public int AttemptCount { get; private set; }

    /// <summary><see cref="InboxProcessingState.Pending"/>: en erken deneme; <see cref="InboxProcessingState.Processing"/>: kiranın bitişi.</summary>
    public DateTimeOffset NextAttemptAt { get; private set; }

    public DateTimeOffset? ProcessedAt { get; private set; }

    public string? LastError { get; private set; }

    public void Claim(DateTimeOffset leaseUntil)
    {
        ProcessingState = InboxProcessingState.Processing;
        AttemptCount++;
        NextAttemptAt = leaseUntil;
    }

    /// <summary>İşlendi. <paramref name="note"/>: bilerek yok sayılan olaylar için kısa, kişisel veri içermeyen açıklama.</summary>
    public void MarkProcessed(DateTimeOffset now, string? note = null)
    {
        ProcessingState = InboxProcessingState.Processed;
        ProcessedAt = now;
        LastError = note is null ? null : Truncate(note);
    }

    public void ScheduleRetry(DateTimeOffset at, string error)
    {
        ProcessingState = InboxProcessingState.Pending;
        NextAttemptAt = at;
        LastError = Truncate(error);
    }

    public void DeadLetter(string error)
    {
        ProcessingState = InboxProcessingState.DeadLetter;
        LastError = Truncate(error);
    }

    private static string Truncate(string value) => value.Length <= LastErrorMaxLength ? value : value[..LastErrorMaxLength];
}
