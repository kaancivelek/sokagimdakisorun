using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SokagimdakiSorun.Complaints.Contracts;
using SokagimdakiSorun.Identity.Contracts;
using SokagimdakiSorun.Media.Contracts;
using SokagimdakiSorun.Messaging.Domain;
using SokagimdakiSorun.Messaging.Infrastructure;
using SokagimdakiSorun.Messaging.Infrastructure.WhatsApp;

namespace SokagimdakiSorun.Messaging.Application;

/// <summary>Yeniden denenmesi gereken geçici işleme hatası (ör. medya indirilemedi).</summary>
internal sealed class TransientProcessingException(string message) : Exception(message);

/// <summary>
/// Tek bir inbox olayını işler (DOMAIN_MODEL §5 "gelen mesajın yönlendirme önceliği"):
/// <list type="number">
///   <item>Teslim durumu olayı → giden mesajın <c>delivery_status</c>'ü.</item>
///   <item>Aktif intake → intake ilerler; onayda <see cref="IComplaintIntake.SubmitFromIntakeAsync"/> (idempotent).</item>
///   <item>Konuşma bir şikâyet için cevap bekliyor → <see cref="IComplaintReporterActions"/> (idempotency anahtarı = olay kimliği).</item>
///   <item>Hiçbiri → yeni intake.</item>
/// </list>
/// Medya indirme transaction dışında yapılır. Olay başına tek transaction: konuşma <c>FOR UPDATE</c> → gelen mesaj + intake +
/// giden cevaplar + inbox "işlendi". Gelen mesajın kimliği olay kimliğidir; yeniden deneme aynı kimlikleri üretir.
/// Exception = yeniden dene (çağıran <see cref="Infrastructure.InboxProcessor"/> backoff uygular). Loglar yalnızca kimlik ve tür içerir.
/// </summary>
internal sealed partial class InboundEventProcessor(
    MessagingDbContext db,
    IReporterRegistry reporters,
    IMediaService media,
    IWhatsAppClient whatsApp,
    IComplaintCatalog catalog,
    IComplaintIntake complaintIntake,
    IComplaintReporterActions reporterActions,
    IOptions<MessagingOptions> options,
    TimeProvider timeProvider,
    ILogger<InboundEventProcessor> logger)
{
    private readonly IntakeRules _rules = options.Value.ToIntakeRules();

    public async Task ProcessAsync(Guid eventId, CancellationToken cancellationToken)
    {
        var evt = await db.InboxEvents.AsNoTracking().SingleOrDefaultAsync(e => e.Id == eventId, cancellationToken);
        if (evt is null || evt.ProcessingState is InboxProcessingState.Processed or InboxProcessingState.DeadLetter)
        {
            return;
        }

        if (evt.EventType == InboxEventType.Status)
        {
            await ProcessStatusAsync(evt, cancellationToken);
        }
        else
        {
            await ProcessMessageAsync(evt, cancellationToken);
        }
    }

    private async Task ProcessStatusAsync(WebhookInboxEvent evt, CancellationToken cancellationToken)
    {
        StatusUpdate? status;
        try
        {
            status = WhatsAppWebhookParser.ParseStatus(evt.Payload);
        }
        catch (FormatException)
        {
            await CompleteIgnoredAsync(evt.Id, "unparseable_status", cancellationToken);
            return;
        }

        if (status is null)
        {
            await CompleteIgnoredAsync(evt.Id, "unknown_status", cancellationToken);
            return;
        }

        var now = timeProvider.GetUtcNow();
        var message = await db.Messages.FirstOrDefaultAsync(
            m => m.ProviderMessageId == status.ProviderMessageId && m.Direction == MessageDirection.Outbound, cancellationToken);
        var tracked = await db.InboxEvents.SingleAsync(e => e.Id == evt.Id, cancellationToken);
        if (message is null)
        {
            tracked.MarkProcessed(now, "unknown_message");
        }
        else
        {
            message.ApplyProviderStatus(status.Status, status.Timestamp, status.Error, now);
            tracked.MarkProcessed(now);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task ProcessMessageAsync(WebhookInboxEvent evt, CancellationToken cancellationToken)
    {
        InboundMessage message;
        try
        {
            message = WhatsAppWebhookParser.ParseMessage(evt.Payload);
        }
        catch (FormatException)
        {
            await CompleteIgnoredAsync(evt.Id, "unparseable_message", cancellationToken);
            return;
        }

        // İkinci tekrar katmanı (I-2): aynı sağlayıcı mesajı başka bir olayla zaten kaydedildiyse hiçbir şey yapılmaz.
        if (await db.Messages.AnyAsync(m => m.Id == evt.Id || m.ProviderMessageId == message.ProviderMessageId, cancellationToken))
        {
            LogDuplicateMessage(logger, evt.Id);
            await CompleteIgnoredAsync(evt.Id, "duplicate_message", cancellationToken);
            return;
        }

        var (mediaFileId, mediaRejected) = message.Kind == MessageType.Image
            ? await StoreMediaAsync(evt.Id, message.MediaId!, cancellationToken)
            : (null, false);

        var existingConversation = await db.Conversations.AsNoTracking()
            .Where(c => c.BusinessPhoneNumberId == message.BusinessPhoneNumberId && c.ExternalContactId == message.ContactId)
            .Select(c => (Guid?)c.Id)
            .FirstOrDefaultAsync(cancellationToken);

        Guid? reporterUserId = null;
        if (existingConversation is null)
        {
            var registration = await reporters.FindOrCreateAsync(
                ExternalIdentityProvider.WhatsApp, message.ContactId, message.ProfileName, cancellationToken);
            if (registration.IsFailure)
            {
                await CompleteIgnoredAsync(evt.Id, $"reporter_rejected:{registration.Error.Code}", cancellationToken);
                return;
            }

            reporterUserId = registration.Value.UserId;
        }

        var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await using (transaction.ConfigureAwait(false))
        {
            var now = timeProvider.GetUtcNow();
            var conversation = existingConversation is { } id
                ? await db.LockConversationAsync(id, cancellationToken) ?? throw new InvalidOperationException("Konuşma bulunamadı.")
                : await CreateAndLockConversationAsync(reporterUserId!.Value, message, now, cancellationToken);

            var inbound = Message.Inbound(
                evt.Id, conversation.Id, message.ProviderMessageId, message.Kind, message.ProviderType, message.Text,
                message.Latitude, message.Longitude, mediaFileId, message.Timestamp, now);
            db.Messages.Add(inbound);
            conversation.RecordInbound(message.Timestamp is { } ts && ts <= now ? ts : now, now);

            var input = new IntakeInput(message.Kind, message.Text, message.Latitude, message.Longitude, mediaFileId, mediaRejected);
            var routed = await RouteAsync(conversation, inbound, input, evt.Id, now, cancellationToken);

            for (var i = 0; i < routed.Replies.Count; i++)
            {
                db.Messages.Add(Message.Outbound(
                    conversation.Id, routed.Replies[i], string.Create(CultureInfo.InvariantCulture, $"reply:{evt.Id}:{i}"),
                    routed.IntakeSessionId, routed.ComplaintId, now));
            }

            var tracked = await db.InboxEvents.SingleAsync(e => e.Id == evt.Id, cancellationToken);
            tracked.MarkProcessed(now);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            LogMessageProcessed(logger, evt.Id, message.Kind, routed.Route);
        }
    }

    private async Task<RoutedMessage> RouteAsync(
        Conversation conversation, Message inbound, IntakeInput input, Guid eventId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // 2. Aktif intake
        var active = await db.IntakeSessions
            .Where(s => s.ConversationId == conversation.Id && IntakeStates.Active.Contains(s.State))
            .SingleOrDefaultAsync(cancellationToken);
        if (active is not null)
        {
            inbound.LinkTo(active.Id, null);
            var categories = await catalog.GetActiveCategoriesAsync(cancellationToken);
            var outcome = active.Advance(input, categories, now, _rules);
            if (outcome.SubmitRequested)
            {
                outcome = await SubmitAsync(conversation, active, categories, now, cancellationToken);
            }

            inbound.LinkTo(null, active.SubmittedComplaintId);
            return new RoutedMessage(outcome.Replies, active.Id, active.SubmittedComplaintId, "intake");
        }

        // 3. Bilgi bekleyen şikâyet
        if (conversation.AwaitingReplyComplaintId is { } complaintId)
        {
            var forwarded = await ForwardToComplaintAsync(conversation, inbound, input, complaintId, eventId, now, cancellationToken);
            if (forwarded is not null)
            {
                return forwarded;
            }
        }

        // 4. Yeni intake
        var menu = await catalog.GetActiveCategoriesAsync(cancellationToken);
        var (session, started) = IntakeSession.Start(
            conversation.Id, menu, input.Kind == MessageType.Unsupported ? BotMessages.Unsupported : null, now, _rules);
        db.IntakeSessions.Add(session);
        inbound.LinkTo(session.Id, null);
        return new RoutedMessage(started.Replies, session.Id, null, "new_intake");
    }

    private async Task<IntakeOutcome> SubmitAsync(
        Conversation conversation, IntakeSession session, IReadOnlyList<ComplaintCategorySummary> categories, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var request = new SubmitFromIntakeRequest(
            session.Id,
            conversation.ReporterUserId,
            session.CategoryId!.Value,
            session.DescriptionDraft!,
            session.Latitude,
            session.Longitude,
            session.AddressText,
            session.Latitude is null ? LocationSource.TextAddress : LocationSource.WhatsAppPin,
            [.. session.MediaFileIds]);

        var result = await complaintIntake.SubmitFromIntakeAsync(request, cancellationToken);
        if (result.IsSuccess)
        {
            // "Başvurunuz alındı" mesajı ComplaintSubmitted handler'ından gelir (tek kaynak).
            session.MarkSubmitted(result.Value.ComplaintId, now);
            LogIntakeSubmitted(logger, session.Id, result.Value.ComplaintId, result.Value.Created);
            return new IntakeOutcome([], false);
        }

        LogSubmitRejected(logger, session.Id, result.Error.Code);
        return result.Error.Code switch
        {
            ComplaintErrorCodes.CategoryInvalid => session.ReturnToCategoryAfterRejectedSubmit(categories, now),
            ComplaintErrorCodes.AttachmentUnavailable => session.DropMediaAfterRejectedSubmit(categories, now),
            _ => IntakeOutcome.Reply(BotMessages.SubmitFailed),
        };
    }

    private async Task<RoutedMessage?> ForwardToComplaintAsync(
        Conversation conversation, Message inbound, IntakeInput input, Guid complaintId, Guid eventId, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        inbound.LinkTo(null, complaintId);

        if (input.Kind == MessageType.Text && IntakeText.Normalize(input.Text) is "geri cek" or "geri cekiyorum")
        {
            var withdrawn = await reporterActions.WithdrawAsync(
                new WithdrawRequest(complaintId, conversation.ReporterUserId, eventId, null), cancellationToken);
            if (withdrawn.IsSuccess)
            {
                // Bildirim ComplaintStatusChanged(Withdrawn) handler'ından gelir.
                conversation.StopAwaitingReplyFor(complaintId, now, now);
                return new RoutedMessage([], null, complaintId, "withdraw");
            }

            return StaleOrFailed(withdrawn.Error.Code);
        }

        if (input.Kind == MessageType.Unsupported)
        {
            return new RoutedMessage([BotMessages.Unsupported], null, complaintId, "information_unsupported");
        }

        if (input.Kind == MessageType.Image && input.MediaFileId is null)
        {
            return new RoutedMessage([BotMessages.InformationMediaRejected], null, complaintId, "information_media_rejected");
        }

        var text = input.Kind switch
        {
            MessageType.Location => string.Create(CultureInfo.InvariantCulture, $"Konum: {input.Latitude:F5}, {input.Longitude:F5}")
                + (string.IsNullOrWhiteSpace(input.Text) ? string.Empty : $" ({input.Text.Trim()})"),
            _ => input.Text?.Trim(),
        };
        if (text is { Length: > IComplaintIntake.MaxDescriptionLength })
        {
            text = text[..IComplaintIntake.MaxDescriptionLength];
        }

        List<Guid> mediaIds = input.MediaFileId is { } m ? [m] : [];
        if (string.IsNullOrWhiteSpace(text) && mediaIds.Count == 0)
        {
            return new RoutedMessage([BotMessages.InformationNeedsContent], null, complaintId, "information_empty");
        }

        var result = await reporterActions.ProvideInformationAsync(
            new ProvideInformationRequest(complaintId, conversation.ReporterUserId, eventId, string.IsNullOrWhiteSpace(text) ? null : text, mediaIds),
            cancellationToken);
        if (result.IsSuccess)
        {
            conversation.StopAwaitingReplyFor(complaintId, now, now);
            return new RoutedMessage([BotMessages.InformationReceived(result.Value.PublicReference)], null, complaintId, "information");
        }

        return StaleOrFailed(result.Error.Code);

        RoutedMessage? StaleOrFailed(string code)
        {
            LogReporterActionRejected(logger, eventId, code);
            if (code is ComplaintErrorCodes.InvalidTransition or ComplaintErrorCodes.NotFound)
            {
                // Şikâyet artık bilgi beklemiyor: bağlam temizlenir, mesaj yeni intake başlatır (öncelik 4).
                conversation.StopAwaitingReplyFor(complaintId, now, now);
                inbound.ClearComplaint();
                return null;
            }

            return new RoutedMessage([BotMessages.InformationFailed], null, complaintId, "information_failed");
        }
    }

    private async Task<(Guid? MediaFileId, bool Rejected)> StoreMediaAsync(Guid eventId, string providerMediaId, CancellationToken cancellationToken)
    {
        var download = await whatsApp.DownloadMediaAsync(providerMediaId, cancellationToken);
        switch (download.Outcome)
        {
            case MediaDownloadOutcome.TransientFailure:
                throw new TransientProcessingException($"media_download_transient:{download.Error}");
            case MediaDownloadOutcome.PermanentFailure:
                LogMediaRejected(logger, eventId, download.Error ?? "download_failed");
                return (null, true);
        }

        await using var content = download.Content!;
        var stored = await media.StoreAsync(content, download.ContentType ?? "application/octet-stream", cancellationToken);
        if (stored.IsFailure)
        {
            LogMediaRejected(logger, eventId, stored.Error.Code);
            return (null, true);
        }

        return (stored.Value.Value, false);
    }

    private async Task<Conversation> CreateAndLockConversationAsync(
        Guid reporterUserId, InboundMessage message, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var candidate = Conversation.Create(reporterUserId, message.BusinessPhoneNumberId, message.ContactId, now);
        var channel = nameof(ConversationChannel.WhatsApp);
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO messaging.conversations
                (id, reporter_user_id, channel, business_phone_number_id, external_contact_id, created_at, updated_at)
            VALUES ({candidate.Id}, {reporterUserId}, {channel}, {message.BusinessPhoneNumberId}, {message.ContactId}, {now}, {now})
            ON CONFLICT (business_phone_number_id, external_contact_id) DO NOTHING
            """, cancellationToken);

        return (await db.Conversations.FromSql($"""
                SELECT * FROM messaging.conversations
                WHERE business_phone_number_id = {message.BusinessPhoneNumberId} AND external_contact_id = {message.ContactId}
                FOR UPDATE
                """).ToListAsync(cancellationToken)).Single();
    }

    private async Task CompleteIgnoredAsync(Guid eventId, string note, CancellationToken cancellationToken)
    {
        var tracked = await db.InboxEvents.SingleAsync(e => e.Id == eventId, cancellationToken);
        tracked.MarkProcessed(timeProvider.GetUtcNow(), note);
        await db.SaveChangesAsync(cancellationToken);
        LogIgnored(logger, eventId, note);
    }

    private sealed record RoutedMessage(IReadOnlyList<string> Replies, Guid? IntakeSessionId, Guid? ComplaintId, string Route);

    [LoggerMessage(Level = LogLevel.Information, Message = "Inbox olayı {EventId} işlendi: tür {Kind}, yönlendirme {Route}.")]
    private static partial void LogMessageProcessed(ILogger logger, Guid eventId, MessageType kind, string route);

    [LoggerMessage(Level = LogLevel.Information, Message = "Inbox olayı {EventId}: aynı sağlayıcı mesajı zaten kayıtlı (tekrar).")]
    private static partial void LogDuplicateMessage(ILogger logger, Guid eventId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Inbox olayı {EventId} yok sayıldı: {Reason}.")]
    private static partial void LogIgnored(ILogger logger, Guid eventId, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Intake {IntakeSessionId} şikâyete dönüştü: {ComplaintId} (yeni: {Created}).")]
    private static partial void LogIntakeSubmitted(ILogger logger, Guid intakeSessionId, Guid complaintId, bool created);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Intake {IntakeSessionId} gönderimi reddedildi: {Code}.")]
    private static partial void LogSubmitRejected(ILogger logger, Guid intakeSessionId, string code);

    [LoggerMessage(Level = LogLevel.Information, Message = "Inbox olayı {EventId}: şikâyetçi eylemi reddedildi: {Code}.")]
    private static partial void LogReporterActionRejected(ILogger logger, Guid eventId, string code);

    [LoggerMessage(Level = LogLevel.Information, Message = "Inbox olayı {EventId}: fotoğraf kabul edilmedi ({Reason}).")]
    private static partial void LogMediaRejected(ILogger logger, Guid eventId, string reason);
}
