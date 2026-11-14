using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SokagimdakiSorun.Complaints.Contracts;
using SokagimdakiSorun.Messaging.Domain;
using SokagimdakiSorun.Messaging.Infrastructure;
using SokagimdakiSorun.Platform.Outbox;

namespace SokagimdakiSorun.Messaging.Application;

/// <summary>
/// <see cref="ComplaintSubmitted"/> → "Başvurunuz alındı" (referansla). İdempotent: giden mesaj <c>dedup_key = event:{EventId}</c>
/// kısmi UNIQUE'tir; aynı event iki kez gelse de tek mesaj oluşur. Dış HTTP çağrısı yapmaz; yalnızca kuyruğa yazar.
/// </summary>
internal sealed class ComplaintSubmittedHandler(ComplaintNotifier notifier) : IIntegrationEventHandler<ComplaintSubmitted>
{
    public Task HandleAsync(ComplaintSubmitted integrationEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        return notifier.NotifyAsync(
            integrationEvent.EventId, integrationEvent.ReporterUserId, integrationEvent.ComplaintId, integrationEvent.PublicReference,
            NotificationPolicy.ForSubmitted(integrationEvent.PublicReference), linkIntake: true, conversation: null, cancellationToken);
    }
}

/// <summary>
/// <see cref="ComplaintStatusChanged"/> → bildirim politikasına göre mesaj (<see cref="NotificationPolicy"/>, tek tablo) ve cevap bağlamı:
/// <c>AwaitingInformation</c>'a girişte konuşma o şikâyet için cevap bekler; çıkışta bekleyiş kalkar (eski olay yeniyi ezmez).
/// İdempotent (<c>event:{EventId}</c>).
/// </summary>
internal sealed class ComplaintStatusChangedHandler(ComplaintNotifier notifier) : IIntegrationEventHandler<ComplaintStatusChanged>
{
    public Task HandleAsync(ComplaintStatusChanged integrationEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        var text = NotificationPolicy.ForStatusChange(
            integrationEvent.FromStatus, integrationEvent.ToStatus, integrationEvent.PublicReference, integrationEvent.ReporterNote);

        return notifier.NotifyAsync(
            integrationEvent.EventId, integrationEvent.ReporterUserId, integrationEvent.ComplaintId, integrationEvent.PublicReference, text,
            linkIntake: false,
            conversation: (c, now) =>
            {
                if (integrationEvent.ToStatus == ComplaintStatus.AwaitingInformation)
                {
                    c.AwaitReplyFor(integrationEvent.ComplaintId, integrationEvent.OccurredAt, now);
                }
                else if (integrationEvent.FromStatus == ComplaintStatus.AwaitingInformation)
                {
                    c.StopAwaitingReplyFor(integrationEvent.ComplaintId, integrationEvent.OccurredAt, now);
                }
            },
            cancellationToken);
    }
}

/// <summary>
/// Şikâyetçinin konuşmasını bulur, kilitler, isteğe bağlı bağlamı günceller ve bildirimi kuyruğa yazar. Referans mesajda saklanır:
/// gönderim anında pencere kapalıysa onaylı şablonun parametresi olur (WS-10).
/// </summary>
internal sealed partial class ComplaintNotifier(MessagingDbContext db, TimeProvider timeProvider, ILogger<ComplaintNotifier> logger)
{
    public async Task NotifyAsync(
        Guid eventId, Guid reporterUserId, Guid complaintId, string publicReference, string? text, bool linkIntake,
        Action<Conversation, DateTimeOffset>? conversation, CancellationToken cancellationToken)
    {
        var dedupKey = $"event:{eventId}";
        if (text is not null && conversation is null && await db.Messages.AnyAsync(m => m.DedupKey == dedupKey, cancellationToken))
        {
            return;
        }

        // Şikâyetçi birden çok işletme numarasıyla konuştuysa en son yazdığı konuşma.
        var conversationId = await db.Conversations.AsNoTracking()
            .Where(c => c.ReporterUserId == reporterUserId)
            .OrderByDescending(c => c.LastInboundAt)
            .Select(c => (Guid?)c.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (conversationId is null)
        {
            // Ör. demo tohum verisi: WhatsApp konuşması yok; bildirilecek kanal yok.
            LogNoConversation(logger, eventId);
            return;
        }

        var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await using (transaction.ConfigureAwait(false))
        {
            var now = timeProvider.GetUtcNow();
            var locked = await db.LockConversationAsync(conversationId.Value, cancellationToken)
                ?? throw new InvalidOperationException("Konuşma bulunamadı.");
            conversation?.Invoke(locked, now);

            if (text is not null && !await db.Messages.AnyAsync(m => m.DedupKey == dedupKey, cancellationToken))
            {
                Guid? intakeSessionId = linkIntake
                    ? await db.IntakeSessions.Where(s => s.SubmittedComplaintId == complaintId).Select(s => (Guid?)s.Id)
                        .FirstOrDefaultAsync(cancellationToken)
                    : null;
                db.Messages.Add(Message.Outbound(locked.Id, text, dedupKey, intakeSessionId, complaintId, now, publicReference));
            }

            try
            {
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            catch (DbUpdateException ex) when (MessagingDbContext.IsUniqueViolation(ex, MessagingDbContext.OutboundDedupUniqueIndex))
            {
                // Eşzamanlı ikinci teslim aynı mesajı yazdı; bu çağrı etkisizdir. Bağlam değişikliği diğer teslimde de uygulanmıştır.
                LogDuplicateEvent(logger, eventId);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Event {EventId}: şikâyetçinin WhatsApp konuşması yok; bildirim atlandı.")]
    private static partial void LogNoConversation(ILogger logger, Guid eventId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Event {EventId} için bildirim zaten kuyrukta (tekrar teslim).")]
    private static partial void LogDuplicateEvent(ILogger logger, Guid eventId);
}
