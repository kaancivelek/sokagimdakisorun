using SokagimdakiSorun.Complaints.Contracts;

namespace SokagimdakiSorun.Complaints.Domain;

/// <summary>Geçmiş satırındaki aktör türü (DOMAIN_MODEL §4.4).</summary>
internal enum ActorKind
{
    Staff,
    Reporter,
    System,
}

/// <summary>Değişikliğin kaynağı.</summary>
internal enum ChangeSource
{
    Panel,
    WhatsApp,
    System,
}

/// <summary>Kaynak kanal (şikâyetin geldiği yer).</summary>
internal enum SourceChannel
{
    WhatsApp,
}

/// <summary>Yorumun görünürlüğü.</summary>
internal enum CommentVisibility
{
    /// <summary>Yalnızca panel kullanıcıları.</summary>
    Internal,

    /// <summary>Şikâyetçiye de gösterilebilir.</summary>
    ReporterVisible,
}

/// <summary>Yorum yazarının türü.</summary>
internal enum CommentAuthorKind
{
    Staff,
    Reporter,
}

/// <summary>Yorumun kaynağı.</summary>
internal enum CommentSource
{
    Panel,
    WhatsApp,
}

/// <summary>Ekin kaynağı.</summary>
internal enum AttachmentSource
{
    Intake,
    InformationReply,
    Panel,
}

/// <summary>Komutu veren aktör. <see cref="UserId"/> yalnızca <see cref="ActorRole.System"/> için <see langword="null"/>.</summary>
internal sealed record ComplaintActor(ActorRole Role, Guid? UserId)
{
    public static readonly ComplaintActor System = new(ActorRole.System, null);

    public static ComplaintActor Staff(Guid userId, bool isAdmin) => new(isAdmin ? ActorRole.Admin : ActorRole.Moderator, userId);

    public static ComplaintActor Reporter(Guid userId) => new(ActorRole.Reporter, userId);

    public ActorKind Kind => Role switch
    {
        ActorRole.Reporter => ActorKind.Reporter,
        ActorRole.System => ActorKind.System,
        _ => ActorKind.Staff,
    };

    public ChangeSource Source => Role switch
    {
        ActorRole.Reporter => ChangeSource.WhatsApp,
        ActorRole.System => ChangeSource.System,
        _ => ChangeSource.Panel,
    };
}

/// <summary>Durum geçmişi satırı (yalnızca ekleme; DOMAIN_MODEL §4.4).</summary>
internal sealed class ComplaintStatusHistoryEntry
{
    public const int MaxTextLength = 2000;

    private ComplaintStatusHistoryEntry()
    {
    }

    public Guid Id { get; private set; }

    public Guid ComplaintId { get; private set; }

    public ComplaintStatus? FromStatus { get; private set; }

    public ComplaintStatus ToStatus { get; private set; }

    public ActorKind ActorKind { get; private set; }

    public Guid? ActorUserId { get; private set; }

    public ChangeSource Source { get; private set; }

    /// <summary>İç gerekçe; şikâyetçiye gönderilmez.</summary>
    public string? Reason { get; private set; }

    /// <summary>Şikâyetçiye iletilecek metin.</summary>
    public string? ReporterNote { get; private set; }

    /// <summary>Şikâyetçi eylemlerinde idempotency anahtarı (kısmi UNIQUE).</summary>
    public Guid? SourceMessageId { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }

    internal static ComplaintStatusHistoryEntry Create(
        Guid complaintId, ComplaintStatus? from, ComplaintStatus to, ActorKind actorKind, Guid? actorUserId, ChangeSource source,
        string? reason, string? reporterNote, Guid? sourceMessageId, DateTimeOffset now) => new()
        {
            Id = Guid.CreateVersion7(),
            ComplaintId = complaintId,
            FromStatus = from,
            ToStatus = to,
            ActorKind = actorKind,
            ActorUserId = actorUserId,
            Source = source,
            Reason = reason,
            ReporterNote = reporterNote,
            SourceMessageId = sourceMessageId,
            OccurredAt = now,
        };
}

/// <summary>Atama (hizmet birimi + isteğe bağlı kişi). Şikâyet başına en fazla bir aktif atama (kısmi UNIQUE).</summary>
internal sealed class ComplaintAssignment
{
    public const int MaxReasonLength = 2000;

    private ComplaintAssignment()
    {
    }

    public Guid Id { get; private set; }

    public Guid ComplaintId { get; private set; }

    public Guid ServiceUnitId { get; private set; }

    public Guid? AssigneeUserId { get; private set; }

    public Guid AssignedByUserId { get; private set; }

    public DateTimeOffset AssignedAt { get; private set; }

    public string? Reason { get; private set; }

    public DateTimeOffset? UnassignedAt { get; private set; }

    public Guid? UnassignedByUserId { get; private set; }

    public string? UnassignReason { get; private set; }

    public bool IsActive => UnassignedAt is null;

    internal static ComplaintAssignment Open(Guid complaintId, Guid serviceUnitId, Guid? assigneeUserId, Guid assignedBy, string? reason, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        ComplaintId = complaintId,
        ServiceUnitId = serviceUnitId,
        AssigneeUserId = assigneeUserId,
        AssignedByUserId = assignedBy,
        AssignedAt = now,
        Reason = reason,
    };

    internal void Close(Guid? by, string? reason, DateTimeOffset now)
    {
        UnassignedAt = now;
        UnassignedByUserId = by;
        UnassignReason = reason;
    }
}

/// <summary>Şikâyet yorumu.</summary>
internal sealed class ComplaintComment
{
    public const int MaxBodyLength = 4000;

    private ComplaintComment()
    {
    }

    public Guid Id { get; private set; }

    public Guid ComplaintId { get; private set; }

    public Guid AuthorUserId { get; private set; }

    public CommentAuthorKind AuthorKind { get; private set; }

    public string Body { get; private set; } = string.Empty;

    public CommentVisibility Visibility { get; private set; }

    public CommentSource Source { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    internal static ComplaintComment Create(
        Guid complaintId, Guid authorUserId, CommentAuthorKind authorKind, string body, CommentVisibility visibility, CommentSource source, DateTimeOffset now) => new()
        {
            Id = Guid.CreateVersion7(),
            ComplaintId = complaintId,
            AuthorUserId = authorUserId,
            AuthorKind = authorKind,
            Body = body,
            Visibility = visibility,
            Source = source,
            CreatedAt = now,
        };
}

/// <summary>Şikâyet eki: yalnızca opak <c>media_file_id</c>; dosya yolu veya storage key içermez (I-10).</summary>
internal sealed class ComplaintAttachment
{
    private ComplaintAttachment()
    {
    }

    public Guid Id { get; private set; }

    public Guid ComplaintId { get; private set; }

    public Guid MediaFileId { get; private set; }

    public AttachmentSource Source { get; private set; }

    public Guid AddedByUserId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    internal static ComplaintAttachment Create(Guid complaintId, Guid mediaFileId, AttachmentSource source, Guid addedBy, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        ComplaintId = complaintId,
        MediaFileId = mediaFileId,
        Source = source,
        AddedByUserId = addedBy,
        CreatedAt = now,
    };
}

/// <summary>Bir geçişin outbox'a yazılacak özeti (iç gerekçe taşımaz).</summary>
internal sealed record StatusChange(ComplaintStatus? From, ComplaintStatus To, string? ReporterNote, DateTimeOffset OccurredAt);
