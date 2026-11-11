using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SokagimdakiSorun.Complaints.Contracts;
using SokagimdakiSorun.Identity.Contracts;
using SokagimdakiSorun.Messaging.Domain;
using SokagimdakiSorun.Messaging.Infrastructure;
using SokagimdakiSorun.SharedKernel;

namespace SokagimdakiSorun.Messaging.Endpoints;

/// <summary>Şikâyete bağlı WhatsApp konuşması.</summary>
/// <param name="ComplaintId">Şikâyet.</param>
/// <param name="MaskedContact">Şikâyetçinin numarası, maskeli (ör. <c>90*******67</c>); konuşma yoksa <see langword="null"/>.</param>
/// <param name="Messages">Mesajlar, eskiden yeniye.</param>
internal sealed record ComplaintConversation(Guid ComplaintId, string? MaskedContact, IReadOnlyList<ConversationMessageItem> Messages);

/// <summary>Tek mesaj. Ham sağlayıcı payload'ı, sağlayıcı kimlikleri ve dosya kimliği içermez.</summary>
/// <param name="Id">Mesaj kimliği.</param>
/// <param name="Direction"><c>Inbound</c> (şikâyetçiden) veya <c>Outbound</c> (sistemden).</param>
/// <param name="MessageType"><c>Text</c>, <c>Location</c>, <c>Image</c>, <c>Unsupported</c>.</param>
/// <param name="Text">Metin (resimde açıklama, konumda adres); yoksa <see langword="null"/>.</param>
/// <param name="Latitude">Konum mesajında enlem.</param>
/// <param name="Longitude">Konum mesajında boylam.</param>
/// <param name="HasMedia">Fotoğraf saklandı mı (içerik, şikâyet eki olarak <c>/attachments</c> üzerinden erişilir).</param>
/// <param name="DeliveryStatus">Gelen: <c>Received</c>; giden: <c>Queued</c>…<c>Read</c> veya <c>Failed</c>.</param>
/// <param name="FailureReason">Yalnızca <c>Failed</c> iken kısa neden.</param>
/// <param name="CreatedAt">Kayıt anı (UTC).</param>
internal sealed record ConversationMessageItem(
    Guid Id,
    string Direction,
    string MessageType,
    string? Text,
    double? Latitude,
    double? Longitude,
    bool HasMedia,
    string DeliveryStatus,
    string? FailureReason,
    DateTimeOffset CreatedAt);

/// <summary>
/// <c>GET /api/v1/complaints/{id}/messages</c> (API_CONTRACT_STRATEGY §8): şikâyetle ilişkili mesajlar — şikâyeti üreten intake'in
/// konuşması, bildirimler ve ek bilgi cevapları — ve teslim durumları. Panel kullanıcısı ister; kapsam kararı Complaints'in
/// <see cref="IComplaintAccessPolicy"/>'sindedir, kapsam dışı/olmayan şikâyet <c>404 complaint.not_found</c>. Telefon maskelidir.
/// </summary>
internal static class ComplaintMessagesEndpoints
{
    public const int MaxMessages = 500;

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/complaints/{id:guid}/messages", GetAsync)
            .WithTags("Messaging")
            .RequireAuthorization(IdentityPolicies.Staff)
            .WithName("ListComplaintMessages")
            .WithSummary("Şikâyetle ilişkili WhatsApp mesajları ve teslim durumları (kapsamlı; telefon maskeli; en fazla 500, eskiden yeniye).")
            .Produces<ComplaintConversation>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static async Task<IResult> GetAsync(
        Guid id, ICurrentUser user, IComplaintAccessPolicy accessPolicy, MessagingDbContext db, CancellationToken cancellationToken)
    {
        if (user.UserId is not { } userId
            || !await accessPolicy.CanViewAsync(new StaffActor(userId, user.IsInRole(IdentityRoles.Admin)), id, cancellationToken))
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "Şikâyet bulunamadı.",
                extensions: new Dictionary<string, object?> { ["code"] = ComplaintErrorCodes.NotFound });
        }

        var intakeIds = db.IntakeSessions.Where(s => s.SubmittedComplaintId == id).Select(s => (Guid?)s.Id);
        var messages = await db.Messages.AsNoTracking()
            .Where(m => m.ComplaintId == id || intakeIds.Contains(m.IntakeSessionId))
            .OrderBy(m => m.CreatedAt).ThenBy(m => m.Id)
            .Take(MaxMessages)
            .ToListAsync(cancellationToken);

        string? masked = null;
        if (messages.Count > 0)
        {
            var conversationId = messages[0].ConversationId;
            var contact = await db.Conversations.AsNoTracking().Where(c => c.Id == conversationId)
                .Select(c => c.ExternalContactId).SingleAsync(cancellationToken);
            masked = PhoneMasking.Mask(contact);
        }

        return TypedResults.Ok(new ComplaintConversation(id, masked, [.. messages.Select(m => new ConversationMessageItem(
            m.Id,
            m.Direction.ToString(),
            m.MessageType.ToString(),
            m.TextBody,
            m.Latitude,
            m.Longitude,
            m.MediaFileId is not null,
            m.DeliveryStatus.ToString(),
            m.DeliveryStatus == DeliveryStatus.Failed ? m.LastError : null,
            m.CreatedAt))]));
    }
}

/// <summary>Telefon/dış kimlik maskeleme: ilk 2 ve son 2 karakter görünür, gerisi <c>*</c>.</summary>
internal static class PhoneMasking
{
    public static string Mask(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Length <= 4 ? new string('*', value.Length) : string.Concat(value.AsSpan(0, 2), new string('*', value.Length - 4), value.AsSpan(value.Length - 2));
    }
}
