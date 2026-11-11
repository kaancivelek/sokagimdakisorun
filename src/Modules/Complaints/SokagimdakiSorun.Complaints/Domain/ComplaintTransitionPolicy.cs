using SokagimdakiSorun.Complaints.Contracts;
using SokagimdakiSorun.SharedKernel;

namespace SokagimdakiSorun.Complaints.Domain;

/// <summary>Durum değiştiren komutlar (docs/DOMAIN_MODEL.md §4.3 "Komut" sütunu).</summary>
internal enum ComplaintCommand
{
    SubmitFromIntake,
    StartReview,
    Reject,
    MarkDuplicate,
    MarkSpam,
    RequestInformation,
    Accept,
    ProvideInformation,
    Resume,
    Withdraw,
    Assign,
    Close,
    StartWork,
    Unassign,
    Resolve,
    Reassign,
    Reopen,
}

/// <summary>Komutu veren aktörün rolü.</summary>
internal enum ActorRole
{
    System,
    Reporter,
    Moderator,
    Admin,
}

/// <summary>Bir geçişi kimin yapabileceği ("Kim" sütunu). "Moderatör" = yetki kapsamındaki moderatör veya Admin.</summary>
internal enum AllowedActor
{
    System,
    Reporter,
    Moderator,
    AdminOnly,
}

/// <summary>Geçiş tablosunun tek satırı (bir komut için). #11 ve #23 iki komutla (şikâyetçi / moderatör) iki kez yer alır.</summary>
/// <param name="Number">DOMAIN_MODEL §4.3'teki satır numarası.</param>
/// <param name="Command">Komut.</param>
/// <param name="From">Kaynak durum; yalnızca #1'de <see langword="null"/>.</param>
/// <param name="To">Hedef durum.</param>
/// <param name="Actor">Kim yapabilir.</param>
/// <param name="ReasonRequired">İç gerekçe (<c>reason</c>) zorunlu mu.</param>
/// <param name="ReporterNoteRequired">Şikâyetçiye gidecek metin zorunlu mu (bilgi isteğinde soru metni).</param>
/// <param name="RequiredEntryStatus">
/// Yalnızca <c>AwaitingInformation</c> çıkışları için: kaydın bu duruma hangi durumdan girmiş olması gerektiği (#11, #23).
/// </param>
internal sealed record TransitionRule(
    int Number,
    ComplaintCommand Command,
    ComplaintStatus? From,
    ComplaintStatus To,
    AllowedActor Actor,
    bool ReasonRequired,
    bool ReporterNoteRequired = false,
    ComplaintStatus? RequiredEntryStatus = null);

/// <summary>
/// Şikâyet durum geçişlerinin <b>tek</b> politikası (docs/DOMAIN_MODEL.md §4.3, §4.5 kural 1; ADR-0003 §3). Tablo dışındaki her
/// geçiş yasaktır. Endpoint, WhatsApp handler'ı veya panel kendi kuralını yazmaz; <c>allowedActions</c> da buradan türetilir.
/// </summary>
internal static class ComplaintTransitionPolicy
{
    /// <summary>Bağlayıcı geçiş tablosu.</summary>
    public static readonly IReadOnlyList<TransitionRule> Rules =
    [
        new(1, ComplaintCommand.SubmitFromIntake, null, ComplaintStatus.Submitted, AllowedActor.System, ReasonRequired: false),
        new(2, ComplaintCommand.StartReview, ComplaintStatus.Submitted, ComplaintStatus.UnderReview, AllowedActor.Moderator, ReasonRequired: false),
        new(3, ComplaintCommand.Reject, ComplaintStatus.Submitted, ComplaintStatus.Rejected, AllowedActor.Moderator, ReasonRequired: true),
        new(4, ComplaintCommand.MarkDuplicate, ComplaintStatus.Submitted, ComplaintStatus.Duplicate, AllowedActor.Moderator, ReasonRequired: true),
        new(5, ComplaintCommand.MarkSpam, ComplaintStatus.Submitted, ComplaintStatus.Spam, AllowedActor.Moderator, ReasonRequired: true),
        new(6, ComplaintCommand.RequestInformation, ComplaintStatus.UnderReview, ComplaintStatus.AwaitingInformation, AllowedActor.Moderator, ReasonRequired: true, ReporterNoteRequired: true),
        new(7, ComplaintCommand.Accept, ComplaintStatus.UnderReview, ComplaintStatus.Accepted, AllowedActor.Moderator, ReasonRequired: false),
        new(8, ComplaintCommand.Reject, ComplaintStatus.UnderReview, ComplaintStatus.Rejected, AllowedActor.Moderator, ReasonRequired: true),
        new(9, ComplaintCommand.MarkDuplicate, ComplaintStatus.UnderReview, ComplaintStatus.Duplicate, AllowedActor.Moderator, ReasonRequired: true),
        new(10, ComplaintCommand.MarkSpam, ComplaintStatus.UnderReview, ComplaintStatus.Spam, AllowedActor.Moderator, ReasonRequired: true),
        new(11, ComplaintCommand.ProvideInformation, ComplaintStatus.AwaitingInformation, ComplaintStatus.UnderReview, AllowedActor.Reporter, ReasonRequired: false, RequiredEntryStatus: ComplaintStatus.UnderReview),
        new(11, ComplaintCommand.Resume, ComplaintStatus.AwaitingInformation, ComplaintStatus.UnderReview, AllowedActor.Moderator, ReasonRequired: false, RequiredEntryStatus: ComplaintStatus.UnderReview),
        new(12, ComplaintCommand.Withdraw, ComplaintStatus.AwaitingInformation, ComplaintStatus.Withdrawn, AllowedActor.Reporter, ReasonRequired: false),
        new(13, ComplaintCommand.Assign, ComplaintStatus.Accepted, ComplaintStatus.Assigned, AllowedActor.Moderator, ReasonRequired: false),
        new(14, ComplaintCommand.Close, ComplaintStatus.Accepted, ComplaintStatus.Closed, AllowedActor.Moderator, ReasonRequired: true),
        new(15, ComplaintCommand.StartWork, ComplaintStatus.Assigned, ComplaintStatus.InProgress, AllowedActor.Moderator, ReasonRequired: false),
        new(16, ComplaintCommand.Unassign, ComplaintStatus.Assigned, ComplaintStatus.Accepted, AllowedActor.Moderator, ReasonRequired: true),
        new(17, ComplaintCommand.Resolve, ComplaintStatus.InProgress, ComplaintStatus.Resolved, AllowedActor.Moderator, ReasonRequired: true),
        new(18, ComplaintCommand.RequestInformation, ComplaintStatus.InProgress, ComplaintStatus.AwaitingInformation, AllowedActor.Moderator, ReasonRequired: true, ReporterNoteRequired: true),
        new(19, ComplaintCommand.Reassign, ComplaintStatus.InProgress, ComplaintStatus.Assigned, AllowedActor.Moderator, ReasonRequired: true),
        new(20, ComplaintCommand.Close, ComplaintStatus.Resolved, ComplaintStatus.Closed, AllowedActor.Moderator, ReasonRequired: false),
        new(21, ComplaintCommand.Reopen, ComplaintStatus.Resolved, ComplaintStatus.UnderReview, AllowedActor.Moderator, ReasonRequired: true),
        new(22, ComplaintCommand.Reopen, ComplaintStatus.Closed, ComplaintStatus.UnderReview, AllowedActor.AdminOnly, ReasonRequired: true),
        new(23, ComplaintCommand.ProvideInformation, ComplaintStatus.AwaitingInformation, ComplaintStatus.InProgress, AllowedActor.Reporter, ReasonRequired: false, RequiredEntryStatus: ComplaintStatus.InProgress),
        new(23, ComplaintCommand.Resume, ComplaintStatus.AwaitingInformation, ComplaintStatus.InProgress, AllowedActor.Moderator, ReasonRequired: false, RequiredEntryStatus: ComplaintStatus.InProgress),
    ];

    /// <summary>Panelin (moderatör/Admin) kullanabildiği komutlar ve HTTP eylem adları (<c>allowedActions</c>).</summary>
    public static readonly IReadOnlyList<(ComplaintCommand Command, string Action)> StaffActions =
    [
        (ComplaintCommand.StartReview, "start-review"),
        (ComplaintCommand.Accept, "accept"),
        (ComplaintCommand.Reject, "reject"),
        (ComplaintCommand.MarkDuplicate, "mark-duplicate"),
        (ComplaintCommand.MarkSpam, "mark-spam"),
        (ComplaintCommand.RequestInformation, "request-information"),
        (ComplaintCommand.Resume, "resume"),
        (ComplaintCommand.Assign, "assign"),
        (ComplaintCommand.Reassign, "assign"),
        (ComplaintCommand.Unassign, "unassign"),
        (ComplaintCommand.StartWork, "start-work"),
        (ComplaintCommand.Resolve, "resolve"),
        (ComplaintCommand.Close, "close"),
        (ComplaintCommand.Reopen, "reopen"),
    ];

    /// <summary>Terminal durumlar (MVP'de geri dönüş yok).</summary>
    public static bool IsTerminal(ComplaintStatus status) =>
        status is ComplaintStatus.Rejected or ComplaintStatus.Duplicate or ComplaintStatus.Spam or ComplaintStatus.Withdrawn;

    /// <summary>
    /// Durum değiştirmeyen atama değişikliği (§4.3 ek kural): yalnızca <c>Assigned</c> durumunda; eski atama kapanır, yenisi
    /// açılır; durum geçmişine satır eklenmez. Moderatör (kapsamlı) veya Admin yapabilir.
    /// </summary>
    public static bool AllowsAssignmentChangeWithoutTransition(ComplaintStatus status) => status == ComplaintStatus.Assigned;

    /// <summary>
    /// Komutu değerlendirir. Sıra: (1) tabloda <paramref name="current"/> → ? satırı yoksa <c>complaint.invalid_transition</c>
    /// (409); (2) aktör yetkisizse <c>complaint.action_forbidden</c> (403); (3) zorunlu gerekçe veya şikâyetçi notu boşsa
    /// <c>validation.failed</c> (400). <paramref name="entryStatus"/> yalnızca <c>AwaitingInformation</c>'dan çıkışta kullanılır:
    /// hedef istemciden değil bu kalıcı değerden seçilir.
    /// </summary>
    public static Result<TransitionRule> Evaluate(
        ComplaintCommand command,
        ComplaintStatus? current,
        ComplaintStatus? entryStatus,
        ActorRole actor,
        string? reason,
        string? reporterNote)
    {
        var rule = Find(command, current, entryStatus);
        if (rule is null)
        {
            return Error.Conflict(
                ComplaintErrorCodes.InvalidTransition,
                current is { } from
                    ? $"{from} durumundaki şikâyete {command} komutu uygulanamaz."
                    : $"{command} komutu yeni şikâyet oluşturamaz.");
        }

        if (!IsPermitted(rule.Actor, actor))
        {
            return Error.Forbidden(ComplaintErrorCodes.ActionForbidden, "Bu işlem için yetkiniz yok.");
        }

        Dictionary<string, string[]>? errors = null;
        if (rule.ReasonRequired && string.IsNullOrWhiteSpace(reason))
        {
            (errors ??= [])["reason"] = ["Gerekçe zorunludur."];
        }

        if (rule.ReporterNoteRequired && string.IsNullOrWhiteSpace(reporterNote))
        {
            (errors ??= [])["reporterNote"] = ["Şikâyetçiye iletilecek soru metni zorunludur."];
        }

        return errors is null
            ? rule
            : Error.Validation(ComplaintErrorCodes.ValidationFailed, "İstek doğrulanamadı.", errors);
    }

    /// <summary>
    /// Geçerli kullanıcı ve durum için izinli panel eylem adları (sıralı, tekrarsız). Kapsam kontrolü çağıranın işidir:
    /// kapsam dışı kullanıcı şikâyeti zaten göremez.
    /// </summary>
    public static IReadOnlyList<string> AllowedStaffActions(ComplaintStatus current, ComplaintStatus? entryStatus, bool isAdmin)
    {
        var actor = isAdmin ? ActorRole.Admin : ActorRole.Moderator;
        var actions = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (command, action) in StaffActions)
        {
            if (Find(command, current, entryStatus) is { } rule && IsPermitted(rule.Actor, actor))
            {
                actions.Add(action);
            }
        }

        if (AllowsAssignmentChangeWithoutTransition(current))
        {
            actions.Add("assign");
        }

        return [.. actions];
    }

    private static TransitionRule? Find(ComplaintCommand command, ComplaintStatus? current, ComplaintStatus? entryStatus) =>
        Rules.FirstOrDefault(r => r.Command == command
            && r.From == current
            && (r.RequiredEntryStatus is null || r.RequiredEntryStatus == entryStatus));

    private static bool IsPermitted(AllowedActor allowed, ActorRole actor) => allowed switch
    {
        AllowedActor.System => actor == ActorRole.System,
        AllowedActor.Reporter => actor == ActorRole.Reporter,
        AllowedActor.Moderator => actor is ActorRole.Moderator or ActorRole.Admin,
        AllowedActor.AdminOnly => actor == ActorRole.Admin,
        _ => false,
    };
}
