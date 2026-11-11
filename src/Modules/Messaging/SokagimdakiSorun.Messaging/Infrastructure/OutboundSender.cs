using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SokagimdakiSorun.Messaging.Application;
using SokagimdakiSorun.Messaging.Domain;
using SokagimdakiSorun.Messaging.Infrastructure.WhatsApp;

namespace SokagimdakiSorun.Messaging.Infrastructure;

/// <summary>
/// Giden mesaj kuyruğunu sağlayıcıya gönderir (ARCHITECTURE §5.2, ADR-0003 §5).
/// <list type="bullet">
///   <item>Alma: <c>Queued</c> (zamanı gelmiş) ve kirası dolmuş <c>Sending</c> satırlar <c>FOR UPDATE SKIP LOCKED</c> ile kısa bir
///         transaction'da <c>Sending</c> + kira + <c>attempt_count++</c> yapılır. HTTP çağrısı açık transaction içinde beklenmez.</item>
///   <item>24 saatlik pencere kapalıysa serbest metin gönderilmez; mesaj açıklayıcı nedenle <c>Failed</c> olur (şablon yolu WS-10).</item>
///   <item>Geçici hata: üstel backoff + jitter ile <c>Queued</c>; azami denemede <c>Failed</c>. Kalıcı hata: doğrudan <c>Failed</c>.</item>
///   <item>Kira dolmuş <c>Sending</c> (gönderim sırasında çökme) yeniden denenir: "gönderildi ama kaydedilemedi" durumunda çift mesaj
///         olasılığı ADR-0003'te kabul edilen risktir.</item>
/// </list>
/// Bir bildirimin gönderilememesi şikâyetin durumunu değiştirmez. Loglar alıcı veya metin içermez.
/// </summary>
internal sealed partial class OutboundSender(
    IServiceScopeFactory scopeFactory,
    IWhatsAppClient whatsApp,
    IOptions<MessagingOptions> options,
    IConfiguration configuration,
    TimeProvider timeProvider,
    ILogger<OutboundSender> logger) : PollingWorker(configuration, timeProvider, logger)
{
    public const string WindowClosedReason =
        "window_closed: 24 saatlik hizmet penceresi kapalı; serbest metin gönderilmedi (onaylı şablon gerekir, WS-10).";

    private readonly MessagingOptions _options = options.Value;

    protected override bool Enabled => _options.OutboundSenderEnabled;

    protected override TimeSpan IdleDelay => _options.PollingInterval;

    internal override async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        var claimed = await ClaimAsync(cancellationToken);
        foreach (var item in claimed)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            SendResult result;
            if (!item.WindowOpen)
            {
                result = SendResult.Permanent(WindowClosedReason);
            }
            else
            {
                try
                {
                    result = await whatsApp.SendTextAsync(
                        new OutboundText(item.BusinessPhoneNumberId, item.RecipientId, item.Text, item.MessageId), cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    result = SendResult.Transient($"{ex.GetType().Name}: {ex.Message}");
                }
            }

            await RecordAsync(item.MessageId, result);
        }

        return claimed.Count;
    }

    private async Task<List<ClaimedMessage>> ClaimAsync(CancellationToken cancellationToken)
    {
        var scope = scopeFactory.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var db = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();
            var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            await using (transaction.ConfigureAwait(false))
            {
                var now = Time.GetUtcNow();
                var batchSize = _options.BatchSize;
                var rows = await db.Messages.FromSql($"""
                        SELECT * FROM messaging.messages
                        WHERE direction = 'Outbound' AND delivery_status IN ('Queued', 'Sending') AND next_attempt_at <= {now}
                        ORDER BY next_attempt_at, id
                        LIMIT {batchSize}
                        FOR UPDATE SKIP LOCKED
                        """)
                    .ToListAsync(cancellationToken);

                var conversationIds = rows.Select(r => r.ConversationId).Distinct().ToList();
                var conversations = await db.Conversations.AsNoTracking()
                    .Where(c => conversationIds.Contains(c.Id))
                    .ToDictionaryAsync(c => c.Id, cancellationToken);

                var claimed = new List<ClaimedMessage>();
                foreach (var row in rows)
                {
                    if (row.AttemptCount >= _options.MaxAttempts)
                    {
                        row.MarkFailed($"max_attempts_exceeded ({row.AttemptCount}); son hata: {row.LastError ?? "lease_expired"}", now);
                        LogFailed(Logger, row.Id, row.AttemptCount);
                        continue;
                    }

                    var conversation = conversations[row.ConversationId];
                    row.BeginSending(now + _options.LeaseDuration, now);
                    claimed.Add(new ClaimedMessage(
                        row.Id, conversation.BusinessPhoneNumberId, conversation.ExternalContactId, row.TextBody!,
                        conversation.IsServiceWindowOpen(now, _options.ServiceWindow)));
                }

                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return claimed;
            }
        }
    }

    private async Task RecordAsync(Guid messageId, SendResult result)
    {
        var scope = scopeFactory.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var db = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();
            var message = await db.Messages.SingleOrDefaultAsync(m => m.Id == messageId, CancellationToken.None);
            if (message is null || message.DeliveryStatus != DeliveryStatus.Sending)
            {
                return;
            }

            var now = Time.GetUtcNow();
            switch (result.Outcome)
            {
                case SendOutcome.Sent:
                    message.MarkSent(result.ProviderMessageId!, now);
                    break;
                case SendOutcome.TransientFailure when message.AttemptCount < _options.MaxAttempts:
                    var delay = RetryDelay.Compute(message.AttemptCount, _options.BaseRetryDelay, _options.MaxRetryDelay, Random.Shared.NextDouble());
                    message.ScheduleRetry(now + delay, result.Error ?? "transient_failure", now);
                    LogRetryScheduled(Logger, messageId, message.AttemptCount, delay);
                    break;
                case SendOutcome.TransientFailure:
                    message.MarkFailed($"max_attempts_exceeded ({message.AttemptCount}); son hata: {result.Error}", now);
                    LogFailed(Logger, messageId, message.AttemptCount);
                    break;
                default:
                    message.MarkFailed(result.Error ?? "permanent_failure", now);
                    LogFailed(Logger, messageId, message.AttemptCount);
                    break;
            }

            await db.SaveChangesAsync(CancellationToken.None);
        }
    }

    private sealed record ClaimedMessage(Guid MessageId, string BusinessPhoneNumberId, string RecipientId, string Text, bool WindowOpen);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Giden mesaj {MessageId} gönderilemedi (deneme {Attempt}); {Delay} sonra yeniden denenecek.")]
    private static partial void LogRetryScheduled(ILogger logger, Guid messageId, int attempt, TimeSpan delay);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Giden mesaj {MessageId} Failed oldu (deneme {Attempt}).")]
    private static partial void LogFailed(ILogger logger, Guid messageId, int attempt);
}
