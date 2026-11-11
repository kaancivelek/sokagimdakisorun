using SokagimdakiSorun.Complaints.Contracts;
using SokagimdakiSorun.Complaints.Domain;

namespace SokagimdakiSorun.Complaints.Endpoints;

// Panel DTO'ları (API_CONTRACT_STRATEGY §2). Entity serileştirilmez. Hiçbirinde dosya yolu, storage key veya telefon
// numarası yoktur (Identity sözleşmesi telefon vermez; şikâyetçi yalnızca görünen adıyla gösterilir).

/// <summary>Kullanıcı başvurusu (yalnızca kimlik ve görünen ad).</summary>
internal sealed record UserRef(Guid Id, string DisplayName);

internal sealed record CategoryRef(Guid Id, string Code, string Name);

internal sealed record ServiceUnitRef(Guid Id, string Code, string Name);

internal sealed record ComplaintRef(Guid Id, string PublicReference);

/// <summary>Aktif atama.</summary>
internal sealed record AssignmentRef(ServiceUnitRef ServiceUnit, UserRef? Assignee, DateTimeOffset AssignedAt);

internal sealed record LocationDto(double? Latitude, double? Longitude, string? AddressText, LocationSource Source);

internal sealed record ComplaintListItem(
    Guid Id,
    string PublicReference,
    ComplaintStatus Status,
    CategoryRef Category,
    string DescriptionExcerpt,
    string? AddressText,
    DateTimeOffset SubmittedAt,
    DateTimeOffset UpdatedAt,
    AssignmentRef? ActiveAssignment);

/// <summary>Sayfalı liste zarfı (API_CONTRACT_STRATEGY §3).</summary>
internal sealed record ComplaintPage(IReadOnlyList<ComplaintListItem> Items, int Page, int PageSize, int TotalCount);

/// <summary>
/// Şikâyet detayı. <see cref="Version"/> opak eşzamanlılık belirtecidir ve durum değiştiren her istekte geri gönderilir;
/// <see cref="AllowedActions"/> geçerli kullanıcı ve durum için izinli komut adlarıdır (panel kural kopyalamaz).
/// </summary>
internal sealed record ComplaintDetail(
    Guid Id,
    string PublicReference,
    ComplaintStatus Status,
    CategoryRef Category,
    string Description,
    LocationDto Location,
    UserRef Reporter,
    SourceChannel SourceChannel,
    ComplaintRef? DuplicateOf,
    AssignmentRef? ActiveAssignment,
    ServiceUnitRef? SuggestedServiceUnit,
    DateTimeOffset SubmittedAt,
    DateTimeOffset? ResolvedAt,
    DateTimeOffset? ClosedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string Version,
    IReadOnlyList<string> AllowedActions);

/// <summary>Durum geçmişi satırı. <see cref="Reason"/> iç gerekçedir (yalnızca panelde).</summary>
internal sealed record StatusHistoryItem(
    Guid Id,
    ComplaintStatus? FromStatus,
    ComplaintStatus ToStatus,
    ActorKind ActorKind,
    UserRef? Actor,
    ChangeSource Source,
    string? Reason,
    string? ReporterNote,
    DateTimeOffset OccurredAt);

internal sealed record AssignmentHistoryItem(
    Guid Id,
    ServiceUnitRef ServiceUnit,
    UserRef? Assignee,
    UserRef AssignedBy,
    DateTimeOffset AssignedAt,
    string? Reason,
    DateTimeOffset? UnassignedAt,
    UserRef? UnassignedBy,
    string? UnassignReason);

internal sealed record ComplaintHistory(IReadOnlyList<StatusHistoryItem> StatusChanges, IReadOnlyList<AssignmentHistoryItem> Assignments);

internal sealed record CommentItem(
    Guid Id, UserRef Author, CommentAuthorKind AuthorKind, string Body, CommentVisibility Visibility, CommentSource Source, DateTimeOffset CreatedAt);

/// <summary>Ek metadata'sı; içerik <see cref="ContentUrl"/>'den yetki kontrolüyle akıtılır.</summary>
internal sealed record AttachmentItem(Guid Id, AttachmentSource Source, UserRef AddedBy, DateTimeOffset CreatedAt, string ContentUrl);

// ---- İstekler ----

/// <summary>Durum değiştiren komutların ortak gövdesi. <c>version</c> zorunludur.</summary>
internal sealed record CommandRequest(string? Version, string? Reason, string? ReporterNote);

internal sealed record MarkDuplicateRequest(string? Version, Guid? TargetComplaintId, string? Reason, string? ReporterNote);

internal sealed record AssignRequest(string? Version, Guid? ServiceUnitId, Guid? AssigneeUserId, string? Reason);

internal sealed record AddCommentRequest(string? Body, CommentVisibility? Visibility);

// ---- Katalog ----

internal sealed record CategoryResponse(Guid Id, string Code, string Name, string? Description, bool IsActive, int SortOrder);

internal sealed record CreateCategoryRequest(string? Code, string? Name, string? Description, int? SortOrder, bool? IsActive);

internal sealed record UpdateCategoryRequest(string? Name, string? Description, int? SortOrder, bool? IsActive);

internal sealed record ServiceUnitResponse(Guid Id, string Code, string Name, bool IsActive, IReadOnlyList<UserRef> Members);

internal sealed record CreateServiceUnitRequest(string? Code, string? Name, bool? IsActive);

internal sealed record UpdateServiceUnitRequest(string? Name, bool? IsActive);

internal sealed record AddMemberRequest(Guid? UserId);

internal sealed record RoutingRuleItem(Guid CategoryId, Guid ServiceUnitId, int Priority, bool IsActive);

internal sealed record RoutingRuleSet(IReadOnlyList<RoutingRuleItem> Rules);
