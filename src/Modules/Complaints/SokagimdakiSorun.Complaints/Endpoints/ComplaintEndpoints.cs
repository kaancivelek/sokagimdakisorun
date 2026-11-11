using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using SokagimdakiSorun.Complaints.Application;
using SokagimdakiSorun.Complaints.Contracts;
using SokagimdakiSorun.Complaints.Domain;
using SokagimdakiSorun.Identity.Contracts;
using SokagimdakiSorun.Media.Contracts;
using SokagimdakiSorun.SharedKernel;

namespace SokagimdakiSorun.Complaints.Endpoints;

/// <summary>
/// <c>/api/v1/complaints</c> (API_CONTRACT_STRATEGY §8). Tümü panel kullanıcısı (Admin/Moderator) ister ve kaynak kapsamı
/// uygular: kapsam dışı tekil kayıt <c>404 complaint.not_found</c> döner (varlık sızdırılmaz). Durum değiştiren her komut ayrı
/// POST'tur, <c>version</c> ister ve güncel detayı döner.
/// </summary>
internal static class ComplaintEndpoints
{
    public const int DefaultPageSize = 25;
    public const int MaxPageSize = 100;

    private static readonly HashSet<string> ListQueryKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "page", "pageSize", "sort", "status", "categoryId", "serviceUnitId", "assigneeUserId", "submittedFrom", "submittedTo", "reference",
    };

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/complaints")
            .WithTags("Complaints")
            .RequireAuthorization(IdentityPolicies.Staff)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapGet("/", ListAsync)
            .WithName("ListComplaints")
            .WithSummary("Kapsamdaki şikâyetleri listeler (sayfalı). Moderatör: atanmamışlar + üyesi olduğu birimlere atananlar; Admin: hepsi.")
            .Produces<ComplaintPage>()
            .ProducesProblem(StatusCodes.Status400BadRequest);

        group.MapGet("/{id:guid}", GetAsync)
            .WithName("GetComplaint")
            .WithSummary("Şikâyet detayı + version + allowedActions (kapsamlı).")
            .Produces<ComplaintDetail>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/{id:guid}/history", GetHistoryAsync)
            .WithName("GetComplaintHistory")
            .WithSummary("Durum geçmişi (iç gerekçe dâhil) ve atama hareketleri (kapsamlı).")
            .Produces<ComplaintHistory>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/{id:guid}/comments", GetCommentsAsync)
            .WithName("ListComplaintComments")
            .WithSummary("Yorumlar (kapsamlı).")
            .Produces<IReadOnlyList<CommentItem>>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{id:guid}/comments", AddCommentAsync)
            .WithName("AddComplaintComment")
            .WithSummary("Yorum ekler (varsayılan görünürlük Internal). Durum değiştirmez.")
            .Produces<CommentItem>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/{id:guid}/attachments", GetAttachmentsAsync)
            .WithName("ListComplaintAttachments")
            .WithSummary("Ek metadata listesi (kapsamlı). Dosya yolu veya storage key içermez.")
            .Produces<IReadOnlyList<AttachmentItem>>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/{id:guid}/attachments/{attachmentId:guid}/content", GetAttachmentContentAsync)
            .WithName("GetComplaintAttachmentContent")
            .WithSummary("Ek dosyasını akıtır; önce şikâyet kapsamı kontrol edilir, sonra içerik Media'dan alınır.")
            .Produces(StatusCodes.Status200OK, typeof(Stream), "image/jpeg", "image/png", "image/webp")
            .ProducesProblem(StatusCodes.Status404NotFound);

        MapCommand(group, "start-review", "StartComplaintReview", "Submitted → UnderReview (#2).",
            (c, a, now, r) => c.StartReview(a, r.Reason, now));
        MapCommand(group, "accept", "AcceptComplaint", "UnderReview → Accepted (#7).",
            (c, a, now, r) => c.Accept(a, r.Reason, now));
        MapCommand(group, "reject", "RejectComplaint", "Submitted/UnderReview → Rejected (#3, #8). Gerekçe zorunlu; reporterNote şikâyetçiye iletilir.",
            (c, a, now, r) => c.Reject(a, r.Reason, r.ReporterNote, now));
        MapCommand(group, "mark-spam", "MarkComplaintSpam", "Submitted/UnderReview → Spam (#5, #10). Gerekçe zorunlu.",
            (c, a, now, r) => c.MarkSpam(a, r.Reason, now));
        MapCommand(group, "request-information", "RequestComplaintInformation",
            "UnderReview/InProgress → AwaitingInformation (#6, #18). Gerekçe ve soru metni (reporterNote) zorunlu.",
            (c, a, now, r) => c.RequestInformation(a, r.Reason, r.ReporterNote, now));
        MapCommand(group, "resume", "ResumeComplaint",
            "AwaitingInformation → giriş durumu (UnderReview #11 veya InProgress #23). Hedef istemciden alınmaz.",
            (c, a, now, r) => c.Resume(a, r.Reason, now));
        MapCommand(group, "unassign", "UnassignComplaint", "Assigned → Accepted (#16). Gerekçe zorunlu; aktif atama kapanır.",
            (c, a, now, r) => c.Unassign(a, r.Reason, now));
        MapCommand(group, "start-work", "StartComplaintWork", "Assigned → InProgress (#15).",
            (c, a, now, r) => c.StartWork(a, r.Reason, r.ReporterNote, now));
        MapCommand(group, "resolve", "ResolveComplaint", "InProgress → Resolved (#17). Çözüm notu (reason) zorunlu.",
            (c, a, now, r) => c.Resolve(a, r.Reason, r.ReporterNote, now));
        MapCommand(group, "close", "CloseComplaint", "Accepted → Closed (#14, gerekçe zorunlu) / Resolved → Closed (#20).",
            (c, a, now, r) => c.Close(a, r.Reason, r.ReporterNote, now));
        MapCommand(group, "reopen", "ReopenComplaint",
            "Resolved → UnderReview (#21) / Closed → UnderReview (#22, yalnızca Admin). Gerekçe zorunlu; aktif atama kapanır.",
            (c, a, now, r) => c.Reopen(a, r.Reason, now));

        group.MapPost("/{id:guid}/mark-duplicate", MarkDuplicateAsync)
            .WithName("MarkComplaintDuplicate")
            .WithSummary("Submitted/UnderReview → Duplicate (#4, #9). Hedef zorunlu: kendisi veya bir Duplicate olamaz.")
            .WithCommandResponses();

        group.MapPost("/{id:guid}/assign", AssignAsync)
            .WithName("AssignComplaint")
            .WithSummary("Accepted → Assigned (#13); InProgress → Assigned (#19, gerekçe zorunlu); Assigned iken durum değiştirmeden yeniden atama.")
            .WithCommandResponses();
    }

    private static void MapCommand(
        RouteGroupBuilder group, string action, string name, string summary, Func<Complaint, ComplaintActor, DateTimeOffset, CommandRequest, Result> apply) =>
        group.MapPost($"/{{id:guid}}/{action}", (Guid id, CommandRequest request, ICurrentUser user, ComplaintCommandService commands, ComplaintQueries queries, CancellationToken ct) =>
            {
                var invalid = ValidateTexts(request.Reason, request.ReporterNote);
                return invalid is not null
                    ? Task.FromResult<IResult>(invalid)
                    : RunAsync(id, request.Version, user, commands, queries, (c, a, now) => Task.FromResult(apply(c, a, now, request)), ct);
            })
            .WithName(name)
            .WithSummary(summary)
            .WithCommandResponses();

    private static RouteHandlerBuilder WithCommandResponses(this RouteHandlerBuilder builder) => builder
        .Produces<ComplaintDetail>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

    private static async Task<IResult> MarkDuplicateAsync(
        Guid id, MarkDuplicateRequest request, ICurrentUser user, ComplaintCommandService commands, ComplaintQueries queries, CancellationToken ct)
    {
        if (ValidateTexts(request.Reason, request.ReporterNote) is { } invalid)
        {
            return invalid;
        }

        if (request.TargetComplaintId is not { } targetId)
        {
            return ProblemResults.Validation("targetComplaintId", "Tekrar hedefi zorunludur.");
        }

        var actor = Actor(user);
        var target = await commands.FindDuplicateTargetAsync(targetId, actor, ct);
        return await RunAsync(id, request.Version, user, commands, queries,
            (c, a, now) => Task.FromResult(c.MarkDuplicate(a, target, request.Reason, request.ReporterNote, now)), ct);
    }

    private static async Task<IResult> AssignAsync(
        Guid id, AssignRequest request, ICurrentUser user, ComplaintCommandService commands, ComplaintQueries queries, CancellationToken ct)
    {
        if (ValidateTexts(request.Reason, null) is { } invalid)
        {
            return invalid;
        }

        if (request.ServiceUnitId is not { } unitId)
        {
            return ProblemResults.Validation("serviceUnitId", "Hizmet birimi zorunludur.");
        }

        return await RunAsync(id, request.Version, user, commands, queries, async (c, a, now) =>
        {
            // Önce geçiş/yetki (409/403/400), sonra hedef doğrulaması (422): aggregate komutu politikayı uygular.
            var probe = ComplaintTransitionPolicy.AllowsAssignmentChangeWithoutTransition(c.Status);
            if (!probe)
            {
                var command = c.Status == ComplaintStatus.InProgress ? ComplaintCommand.Reassign : ComplaintCommand.Assign;
                var rule = ComplaintTransitionPolicy.Evaluate(command, c.Status, c.AwaitingInformationEntryStatus, a.Role, request.Reason, null);
                if (rule.IsFailure)
                {
                    return rule;
                }
            }

            var target = await commands.ValidateAssignmentTargetAsync(unitId, request.AssigneeUserId, ct);
            return target.IsFailure ? target : c.Assign(a, unitId, request.AssigneeUserId, request.Reason, now);
        }, ct);
    }

    private static async Task<IResult> RunAsync(
        Guid id,
        string? version,
        ICurrentUser user,
        ComplaintCommandService commands,
        ComplaintQueries queries,
        Func<Complaint, ComplaintActor, DateTimeOffset, Task<Result>> command,
        CancellationToken ct)
    {
        var actor = Actor(user);
        var result = await commands.ExecuteAsync(id, actor, version, command, ct);
        if (result.IsFailure)
        {
            return ProblemResults.From(result.Error);
        }

        var detail = await queries.GetDetailAsync(id, actor, ct, ignoreScope: true);
        return detail is null ? ProblemResults.From(ComplaintPersistence.NotFound) : TypedResults.Ok(detail);
    }

    private static async Task<IResult> ListAsync(
        HttpContext context,
        ICurrentUser user,
        ComplaintQueries queries,
        CancellationToken ct,
        [FromQuery] ComplaintStatus[]? status,
        Guid? categoryId,
        Guid? serviceUnitId,
        Guid? assigneeUserId,
        DateTimeOffset? submittedFrom,
        DateTimeOffset? submittedTo,
        string? reference,
        string? sort,
        int page = 1,
        int pageSize = DefaultPageSize)
    {
        if (context.Request.Query.Keys.FirstOrDefault(k => !ListQueryKeys.Contains(k)) is { } unknown)
        {
            return ProblemResults.Validation(unknown, "Bilinmeyen filtre.");
        }

        if (page < 1)
        {
            return ProblemResults.Validation("page", "Sayfa 1 veya daha büyük olmalıdır.");
        }

        if (pageSize is < 1 or > MaxPageSize)
        {
            return ProblemResults.Validation("pageSize", $"Sayfa boyutu 1 ile {MaxPageSize} arasında olmalıdır.");
        }

        sort ??= "-createdAt";
        if (!ComplaintQueries.SortFields.Contains(sort.TrimStart('-')))
        {
            return ProblemResults.Validation("sort", $"Sıralama alanı şunlardan biri olmalıdır: {string.Join(", ", ComplaintQueries.SortFields)} (azalan için '-' öneki).");
        }

        if (submittedFrom is { } from && submittedTo is { } to && to <= from)
        {
            return ProblemResults.Validation("submittedTo", "Bitiş başlangıçtan sonra olmalıdır.");
        }

        var filter = new ComplaintListFilter(
            status ?? [], categoryId, serviceUnitId, assigneeUserId, submittedFrom?.ToUniversalTime(), submittedTo?.ToUniversalTime(),
            reference, sort, page, pageSize);
        return TypedResults.Ok(await queries.ListAsync(Actor(user), filter, ct));
    }

    private static async Task<IResult> GetAsync(Guid id, ICurrentUser user, ComplaintQueries queries, CancellationToken ct) =>
        await queries.GetDetailAsync(id, Actor(user), ct) is { } detail
            ? TypedResults.Ok(detail)
            : ProblemResults.From(ComplaintPersistence.NotFound);

    private static async Task<IResult> GetHistoryAsync(Guid id, ICurrentUser user, ComplaintQueries queries, CancellationToken ct) =>
        await queries.GetHistoryAsync(id, Actor(user), ct) is { } history
            ? TypedResults.Ok(history)
            : ProblemResults.From(ComplaintPersistence.NotFound);

    private static async Task<IResult> GetCommentsAsync(Guid id, ICurrentUser user, ComplaintQueries queries, CancellationToken ct) =>
        await queries.GetCommentsAsync(id, Actor(user), ct) is { } comments
            ? TypedResults.Ok(comments)
            : ProblemResults.From(ComplaintPersistence.NotFound);

    private static async Task<IResult> AddCommentAsync(
        Guid id, AddCommentRequest request, ICurrentUser user, ComplaintCommandService commands, ComplaintQueries queries, CancellationToken ct)
    {
        var body = request.Body?.Trim();
        if (string.IsNullOrEmpty(body) || body.Length > ComplaintComment.MaxBodyLength)
        {
            return ProblemResults.Validation("body", $"Yorum metni zorunludur ve en fazla {ComplaintComment.MaxBodyLength} karakter olabilir.");
        }

        var visibility = request.Visibility ?? CommentVisibility.Internal;
        if (!Enum.IsDefined(visibility))
        {
            return ProblemResults.Validation("visibility", "Geçersiz görünürlük.");
        }

        var actor = Actor(user);
        var added = await commands.AddCommentAsync(id, actor, body, visibility, ct);
        if (added.IsFailure)
        {
            return ProblemResults.From(added.Error);
        }

        var comment = (await queries.GetCommentsAsync(id, actor, ct))?.SingleOrDefault(c => c.Id == added.Value);
        return comment is null
            ? ProblemResults.From(ComplaintPersistence.NotFound)
            : TypedResults.Created($"/api/v1/complaints/{id}/comments", comment);
    }

    private static async Task<IResult> GetAttachmentsAsync(Guid id, ICurrentUser user, ComplaintQueries queries, CancellationToken ct) =>
        await queries.GetAttachmentsAsync(id, Actor(user), ct) is { } attachments
            ? TypedResults.Ok(attachments)
            : ProblemResults.From(ComplaintPersistence.NotFound);

    private static async Task<IResult> GetAttachmentContentAsync(
        Guid id, Guid attachmentId, HttpContext context, ICurrentUser user, ComplaintQueries queries, IMediaService media, CancellationToken ct)
    {
        // Yetki önce: kapsam dışı şikâyetin eki için Media'ya hiç gidilmez.
        if (await queries.FindAttachmentMediaAsync(id, attachmentId, Actor(user), ct) is not { } mediaFileId)
        {
            return ProblemResults.From(ComplaintPersistence.NotFound);
        }

        var opened = await media.OpenReadAsync(new MediaFileId(mediaFileId), ct);
        if (opened.IsFailure)
        {
            return ProblemResults.NotFound("complaint.attachment_not_found", "Ek dosyası bulunamadı.");
        }

        context.Response.Headers.CacheControl = "private, no-store";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        return TypedResults.Stream(opened.Value.Content, opened.Value.ContentType);
    }

    private static IResult? ValidateTexts(string? reason, string? reporterNote)
    {
        if (reason?.Length > ComplaintCommandService.MaxTextLength)
        {
            return ProblemResults.Validation("reason", $"Gerekçe en fazla {ComplaintCommandService.MaxTextLength} karakter olabilir.");
        }

        return reporterNote?.Length > ComplaintCommandService.MaxTextLength
            ? ProblemResults.Validation("reporterNote", $"Şikâyetçi notu en fazla {ComplaintCommandService.MaxTextLength} karakter olabilir.")
            : null;
    }

    internal static StaffActor Actor(ICurrentUser user) =>
        new(user.UserId ?? throw new InvalidOperationException("Kimliği doğrulanmış kullanıcının kimliği okunamadı."),
            user.IsInRole(IdentityRoles.Admin));
}
