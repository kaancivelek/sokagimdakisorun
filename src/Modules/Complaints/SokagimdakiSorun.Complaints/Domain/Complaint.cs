using SokagimdakiSorun.Complaints.Contracts;
using SokagimdakiSorun.SharedKernel;

namespace SokagimdakiSorun.Complaints.Domain;

/// <summary>
/// Şikâyet aggregate'i (DOMAIN_MODEL §3.2.1, §4). Durum yalnızca komut metotlarıyla değişir; her geçiş
/// <see cref="ComplaintTransitionPolicy"/>'den geçer, aynı nesnede bir geçmiş satırı ve bir <see cref="StatusChange"/>
/// üretir. Uygulama katmanı bunları aynı <c>SaveChanges</c>'ta (tek transaction) outbox event'iyle birlikte yazar.
/// </summary>
internal sealed class Complaint
{
    private readonly List<ComplaintStatusHistoryEntry> _history = [];
    private readonly List<ComplaintAssignment> _assignments = [];
    private readonly List<ComplaintComment> _comments = [];
    private readonly List<ComplaintAttachment> _attachments = [];
    private readonly List<StatusChange> _pendingChanges = [];

    private Complaint()
    {
    }

    public Guid Id { get; private set; }

    public string PublicReference { get; private set; } = string.Empty;

    public Guid ReporterUserId { get; private set; }

    public Guid CategoryId { get; private set; }

    public string Description { get; private set; } = string.Empty;

    public ComplaintStatus Status { get; private set; }

    /// <summary>
    /// Yalnızca <see cref="ComplaintStatus.AwaitingInformation"/> iken dolu: kaydın bu duruma girdiği durum (#6 → UnderReview,
    /// #18 → InProgress). <c>ProvideInformation</c>/<c>Resume</c> hedefi buradan seçilir (OD-3); istemci seçemez.
    /// </summary>
    public ComplaintStatus? AwaitingInformationEntryStatus { get; private set; }

    public SourceChannel SourceChannel { get; private set; }

    public Guid? SourceIntakeSessionId { get; private set; }

    public Guid? DuplicateOfComplaintId { get; private set; }

    public double? Latitude { get; private set; }

    public double? Longitude { get; private set; }

    public string? AddressText { get; private set; }

    public LocationSource LocationSource { get; private set; }

    public DateTimeOffset SubmittedAt { get; private set; }

    public DateTimeOffset? ResolvedAt { get; private set; }

    public DateTimeOffset? ClosedAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>İyimser eşzamanlılık belirteci: PostgreSQL <c>xmin</c> sistem kolonu (V-6).</summary>
    public uint Version { get; private set; }

    public IReadOnlyCollection<ComplaintStatusHistoryEntry> History => _history;

    /// <summary>Yükleme sırasında yalnızca aktif atama yüklenir; yeni açılan atamalar da buraya eklenir.</summary>
    public IReadOnlyCollection<ComplaintAssignment> Assignments => _assignments;

    public IReadOnlyCollection<ComplaintComment> Comments => _comments;

    public IReadOnlyCollection<ComplaintAttachment> Attachments => _attachments;

    public ComplaintAssignment? ActiveAssignment => _assignments.SingleOrDefault(a => a.IsActive);

    /// <summary>Henüz outbox'a yazılmamış geçişler.</summary>
    public IReadOnlyList<StatusChange> PendingChanges => _pendingChanges;

    public void ClearPendingChanges() => _pendingChanges.Clear();

    /// <summary>Geçiş #1: onaylı intake'ten şikâyet. Alan doğrulaması çağıranda yapılmıştır.</summary>
    public static Complaint SubmitFromIntake(
        Guid intakeSessionId, string publicReference, Guid reporterUserId, Guid categoryId, string description,
        double? latitude, double? longitude, string? addressText, LocationSource locationSource, DateTimeOffset now)
    {
        var rule = ComplaintTransitionPolicy.Evaluate(ComplaintCommand.SubmitFromIntake, null, null, ActorRole.System, null, null);
        if (rule.IsFailure)
        {
            throw new InvalidOperationException(rule.Error.Message);
        }

        var complaint = new Complaint
        {
            Id = Guid.CreateVersion7(),
            PublicReference = publicReference,
            ReporterUserId = reporterUserId,
            CategoryId = categoryId,
            Description = description,
            Status = rule.Value.To,
            SourceChannel = SourceChannel.WhatsApp,
            SourceIntakeSessionId = intakeSessionId,
            Latitude = latitude,
            Longitude = longitude,
            AddressText = addressText,
            LocationSource = locationSource,
            SubmittedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };

        // "Sistem (şikâyetçi adına)": aktör System, kanal WhatsApp.
        complaint._history.Add(ComplaintStatusHistoryEntry.Create(
            complaint.Id, null, rule.Value.To, ActorKind.System, null, ChangeSource.WhatsApp, null, null, null, now));
        complaint._pendingChanges.Add(new StatusChange(null, rule.Value.To, null, now));
        return complaint;
    }

    /// <summary>Yeni bir yayımlanma referansıyla (UNIQUE çakışmasında yeniden deneme).</summary>
    public void ReplacePublicReference(string publicReference) => PublicReference = publicReference;

    public Result StartReview(ComplaintActor actor, string? reason, DateTimeOffset now) =>
        Apply(ComplaintCommand.StartReview, actor, reason, null, null, now);

    public Result Accept(ComplaintActor actor, string? reason, DateTimeOffset now) =>
        Apply(ComplaintCommand.Accept, actor, reason, null, null, now);

    public Result Reject(ComplaintActor actor, string? reason, string? reporterNote, DateTimeOffset now) =>
        Apply(ComplaintCommand.Reject, actor, reason, reporterNote, null, now);

    public Result MarkSpam(ComplaintActor actor, string? reason, DateTimeOffset now) =>
        Apply(ComplaintCommand.MarkSpam, actor, reason, null, null, now);

    /// <summary>#4/#9. Hedef: kendisi olamaz, <c>Duplicate</c> durumunda olamaz (I-9; zincir engeli).</summary>
    public Result MarkDuplicate(ComplaintActor actor, Complaint? target, string? reason, string? reporterNote, DateTimeOffset now)
    {
        var rule = Evaluate(ComplaintCommand.MarkDuplicate, actor, reason, reporterNote);
        if (rule.IsFailure)
        {
            return rule;
        }

        if (target is null || target.Id == Id || target.Status == ComplaintStatus.Duplicate)
        {
            return Error.BusinessRule(
                ComplaintErrorCodes.DuplicateTargetInvalid,
                "Tekrar hedefi geçersiz: şikâyet kendisinin veya başka bir tekrar kaydının tekrarı olamaz.");
        }

        DuplicateOfComplaintId = target.Id;
        Commit(rule.Value, actor, reason, reporterNote, null, now);
        return Result.Success();
    }

    /// <summary>#6/#18. Giriş durumu saklanır; <c>InProgress</c>'ten girilirken aktif atama korunur.</summary>
    public Result RequestInformation(ComplaintActor actor, string? reason, string? question, DateTimeOffset now)
    {
        var entry = Status;
        var rule = Evaluate(ComplaintCommand.RequestInformation, actor, reason, question);
        if (rule.IsFailure)
        {
            return rule;
        }

        AwaitingInformationEntryStatus = entry;
        Commit(rule.Value, actor, reason, question, null, now);
        return Result.Success();
    }

    /// <summary>#11/#23 (moderatör): giriş durumuna döner.</summary>
    public Result Resume(ComplaintActor actor, string? reason, DateTimeOffset now) =>
        LeaveAwaitingInformation(ComplaintCommand.Resume, actor, reason, null, now);

    /// <summary>#11/#23 (şikâyetçi): ek bilgi geldi; giriş durumuna döner.</summary>
    public Result ProvideInformation(ComplaintActor actor, Guid sourceMessageId, DateTimeOffset now) =>
        LeaveAwaitingInformation(ComplaintCommand.ProvideInformation, actor, null, sourceMessageId, now);

    /// <summary>#12.</summary>
    public Result Withdraw(ComplaintActor actor, string? reason, Guid sourceMessageId, DateTimeOffset now)
    {
        var rule = Evaluate(ComplaintCommand.Withdraw, actor, reason, null);
        if (rule.IsFailure)
        {
            return rule;
        }

        AwaitingInformationEntryStatus = null;
        Commit(rule.Value, actor, reason, null, sourceMessageId, now);
        return Result.Success();
    }

    /// <summary>
    /// <c>assign</c> komutu: <c>Accepted</c> → #13; <c>InProgress</c> → #19 (Reassign, gerekçe zorunlu); <c>Assigned</c> →
    /// durum değiştirmeyen atama değişikliği (geçmiş satırı yok). Birim/kişi doğrulaması çağıranda yapılmıştır.
    /// </summary>
    public Result Assign(ComplaintActor actor, Guid serviceUnitId, Guid? assigneeUserId, string? reason, DateTimeOffset now)
    {
        if (ComplaintTransitionPolicy.AllowsAssignmentChangeWithoutTransition(Status))
        {
            if (actor.Role is not (ActorRole.Moderator or ActorRole.Admin))
            {
                return Error.Forbidden(ComplaintErrorCodes.ActionForbidden, "Bu işlem için yetkiniz yok.");
            }

            ReplaceActiveAssignment(actor, serviceUnitId, assigneeUserId, reason, now);
            UpdatedAt = now;
            return Result.Success();
        }

        var command = Status == ComplaintStatus.InProgress ? ComplaintCommand.Reassign : ComplaintCommand.Assign;
        var rule = Evaluate(command, actor, reason, null);
        if (rule.IsFailure)
        {
            return rule;
        }

        ReplaceActiveAssignment(actor, serviceUnitId, assigneeUserId, reason, now);
        Commit(rule.Value, actor, reason, null, null, now);
        return Result.Success();
    }

    /// <summary>#16: aktif atama kapatılır.</summary>
    public Result Unassign(ComplaintActor actor, string? reason, DateTimeOffset now)
    {
        var rule = Evaluate(ComplaintCommand.Unassign, actor, reason, null);
        if (rule.IsFailure)
        {
            return rule;
        }

        ActiveAssignment?.Close(actor.UserId, reason, now);
        Commit(rule.Value, actor, reason, null, null, now);
        return Result.Success();
    }

    public Result StartWork(ComplaintActor actor, string? reason, string? reporterNote, DateTimeOffset now) =>
        Apply(ComplaintCommand.StartWork, actor, reason, reporterNote, null, now);

    public Result Resolve(ComplaintActor actor, string? reason, string? reporterNote, DateTimeOffset now)
    {
        var rule = Evaluate(ComplaintCommand.Resolve, actor, reason, reporterNote);
        if (rule.IsFailure)
        {
            return rule;
        }

        ResolvedAt = now;
        Commit(rule.Value, actor, reason, reporterNote, null, now);
        return Result.Success();
    }

    /// <summary>#14 (gerekçe zorunlu) / #20.</summary>
    public Result Close(ComplaintActor actor, string? reason, string? reporterNote, DateTimeOffset now)
    {
        var rule = Evaluate(ComplaintCommand.Close, actor, reason, reporterNote);
        if (rule.IsFailure)
        {
            return rule;
        }

        ClosedAt = now;
        Commit(rule.Value, actor, reason, reporterNote, null, now);
        return Result.Success();
    }

    /// <summary>
    /// #21 / #22 (yalnızca Admin). Kayıt <c>UnderReview</c>'a döner; aktif atama kapatılır (şikâyet triage havuzuna döner ve
    /// yeniden kabul/atama yolu tek aktif atama kuralıyla çakışmaz). Çözüm/kapanış zamanları temizlenir.
    /// </summary>
    public Result Reopen(ComplaintActor actor, string? reason, DateTimeOffset now)
    {
        var rule = Evaluate(ComplaintCommand.Reopen, actor, reason, null);
        if (rule.IsFailure)
        {
            return rule;
        }

        ActiveAssignment?.Close(actor.UserId, reason, now);
        ResolvedAt = null;
        ClosedAt = null;
        Commit(rule.Value, actor, reason, null, null, now);
        return Result.Success();
    }

    public void AddComment(Guid authorUserId, CommentAuthorKind authorKind, string body, CommentVisibility visibility, CommentSource source, DateTimeOffset now) =>
        _comments.Add(ComplaintComment.Create(Id, authorUserId, authorKind, body, visibility, source, now));

    public void AddAttachment(Guid mediaFileId, AttachmentSource source, Guid addedBy, DateTimeOffset now) =>
        _attachments.Add(ComplaintAttachment.Create(Id, mediaFileId, source, addedBy, now));

    private Result LeaveAwaitingInformation(ComplaintCommand command, ComplaintActor actor, string? reason, Guid? sourceMessageId, DateTimeOffset now)
    {
        var rule = Evaluate(command, actor, reason, null);
        if (rule.IsFailure)
        {
            return rule;
        }

        AwaitingInformationEntryStatus = null;
        Commit(rule.Value, actor, reason, null, sourceMessageId, now);
        return Result.Success();
    }

    private Result Apply(ComplaintCommand command, ComplaintActor actor, string? reason, string? reporterNote, Guid? sourceMessageId, DateTimeOffset now)
    {
        var rule = Evaluate(command, actor, reason, reporterNote);
        if (rule.IsFailure)
        {
            return rule;
        }

        Commit(rule.Value, actor, reason, reporterNote, sourceMessageId, now);
        return Result.Success();
    }

    private Result<TransitionRule> Evaluate(ComplaintCommand command, ComplaintActor actor, string? reason, string? reporterNote) =>
        ComplaintTransitionPolicy.Evaluate(command, Status, AwaitingInformationEntryStatus, actor.Role, reason, reporterNote);

    private void ReplaceActiveAssignment(ComplaintActor actor, Guid serviceUnitId, Guid? assigneeUserId, string? reason, DateTimeOffset now)
    {
        ActiveAssignment?.Close(actor.UserId, reason, now);
        _assignments.Add(ComplaintAssignment.Open(Id, serviceUnitId, assigneeUserId, actor.UserId!.Value, reason, now));
    }

    private void Commit(TransitionRule rule, ComplaintActor actor, string? reason, string? reporterNote, Guid? sourceMessageId, DateTimeOffset now)
    {
        var from = Status;
        Status = rule.To;
        UpdatedAt = now;
        var note = string.IsNullOrWhiteSpace(reporterNote) ? null : reporterNote.Trim();
        _history.Add(ComplaintStatusHistoryEntry.Create(
            Id, from, rule.To, actor.Kind, actor.UserId, actor.Source,
            string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(), note, sourceMessageId, now));
        _pendingChanges.Add(new StatusChange(from, rule.To, note, now));
    }
}
