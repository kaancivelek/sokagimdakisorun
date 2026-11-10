namespace SokagimdakiSorun.Platform.Outbox;

/// <summary>
/// <c>platform.outbox_messages</c> satırı (DOMAIN_MODEL §3.5). Tip <c>internal</c>'dır: modüller bu satırı
/// göremez, sorgulayamaz veya değiştiremez; yalnızca <see cref="OutboxDbContextExtensions.AddIntegrationEvent"/>
/// ile ekleyebilir. Okuma ve güncelleme yalnızca <see cref="OutboxDispatcher"/>'a aittir.
/// </summary>
internal sealed class OutboxMessage
{
    public const int TypeMaxLength = 300;
    public const int LastErrorMaxLength = 2000;

    /// <summary>Event'in <c>EventId</c>'si; aynı event'in iki kez eklenmesini birincil anahtar engeller.</summary>
    public Guid Id { get; init; }

    /// <summary>Event'in CLR tam adı (ör. <c>SokagimdakiSorun.Complaints.Contracts.ComplaintSubmitted</c>).</summary>
    public required string Type { get; init; }

    /// <summary>Event'in JSON gövdesi (<c>jsonb</c>).</summary>
    public required string Payload { get; init; }

    public DateTimeOffset OccurredAt { get; init; }

    /// <summary>Bu andan önce dağıtılmaz; yeniden denemede backoff ile ileri alınır.</summary>
    public DateTimeOffset AvailableAt { get; set; }

    public int AttemptCount { get; set; }

    public DateTimeOffset? ProcessedAt { get; set; }

    /// <summary>Azami deneme aşıldığında dolar; satır bir daha otomatik dağıtılmaz.</summary>
    public DateTimeOffset? DeadLetteredAt { get; set; }

    /// <summary>Son hatanın tipi ve mesajı (kısaltılmış). Yığın izi saklanmaz.</summary>
    public string? LastError { get; set; }
}
