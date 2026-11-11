namespace SokagimdakiSorun.Messaging.Domain;

/// <summary>
/// Bir şikâyetçi ile işletme numarası arasındaki WhatsApp iletişim bağlamı (DOMAIN_MODEL §1). Kalıcıdır;
/// (<see cref="BusinessPhoneNumberId"/>, <see cref="ExternalContactId"/>) çifti UNIQUE'tir. Bu satırı değiştiren her yazıcı
/// (inbox işleyicisi, event handler'ları, süre dolum süpürücüsü) önce satırı <c>FOR UPDATE</c> ile kilitler.
/// </summary>
internal sealed class Conversation
{
    public const int ExternalIdMaxLength = 64;

    private Conversation()
    {
    }

    public Guid Id { get; private set; }

    /// <summary>Identity'deki şikâyetçi (skaler; FK yok).</summary>
    public Guid ReporterUserId { get; private set; }

    public ConversationChannel Channel { get; private set; }

    /// <summary>İşletme numarasının sağlayıcı kimliği (Meta: <c>phone_number_id</c>).</summary>
    public string BusinessPhoneNumberId { get; private set; } = string.Empty;

    /// <summary>Kişinin sağlayıcı kimliği (Meta: <c>wa_id</c>, telefon numarası). Kişisel veridir; loglanmaz, panelde maskelenir.</summary>
    public string ExternalContactId { get; private set; } = string.Empty;

    /// <summary>Son gelen mesaj anı; 24 saatlik hizmet penceresinin başlangıcı.</summary>
    public DateTimeOffset? LastInboundAt { get; private set; }

    /// <summary><c>AwaitingInformation</c> durumundaki ve şikâyetçinin cevabını bekleyen şikâyet (skaler).</summary>
    public Guid? AwaitingReplyComplaintId { get; private set; }

    /// <summary>
    /// <see cref="AwaitingReplyComplaintId"/>'yi en son değiştiren olayın zamanı. Eski bir olayın (yeniden teslim) yeni durumu
    /// ezmesini önler.
    /// </summary>
    public DateTimeOffset? AwaitingReplyChangedAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static Conversation Create(Guid reporterUserId, string businessPhoneNumberId, string externalContactId, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        ReporterUserId = reporterUserId,
        Channel = ConversationChannel.WhatsApp,
        BusinessPhoneNumberId = businessPhoneNumberId,
        ExternalContactId = externalContactId,
        CreatedAt = now,
        UpdatedAt = now,
    };

    /// <summary>Gelen mesajı kaydeder; pencere başlangıcı yalnızca ileri gider.</summary>
    public void RecordInbound(DateTimeOffset at, DateTimeOffset now)
    {
        if (LastInboundAt is null || at > LastInboundAt)
        {
            LastInboundAt = at;
        }

        UpdatedAt = now;
    }

    /// <summary>24 saatlik hizmet penceresi <paramref name="now"/> anında açık mı.</summary>
    public bool IsServiceWindowOpen(DateTimeOffset now, TimeSpan window) =>
        LastInboundAt is { } last && now - last < window;

    /// <summary>Şikâyet bilgi bekliyor: gelen mesajlar ona yönlendirilir (yönlendirme önceliği 3). Eski olay yok sayılır.</summary>
    public void AwaitReplyFor(Guid complaintId, DateTimeOffset occurredAt, DateTimeOffset now)
    {
        if (AwaitingReplyChangedAt is { } changed && occurredAt < changed)
        {
            return;
        }

        AwaitingReplyComplaintId = complaintId;
        AwaitingReplyChangedAt = occurredAt;
        UpdatedAt = now;
    }

    /// <summary>Şikâyet artık bilgi beklemiyor. Başka bir şikâyetin bekleyişini veya daha yeni bir değişikliği bozmaz.</summary>
    public void StopAwaitingReplyFor(Guid complaintId, DateTimeOffset occurredAt, DateTimeOffset now)
    {
        if (AwaitingReplyComplaintId != complaintId || (AwaitingReplyChangedAt is { } changed && occurredAt < changed))
        {
            return;
        }

        AwaitingReplyComplaintId = null;
        AwaitingReplyChangedAt = occurredAt;
        UpdatedAt = now;
    }
}
