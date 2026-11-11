using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SokagimdakiSorun.Messaging.Application;
using SokagimdakiSorun.Messaging.Domain;

namespace SokagimdakiSorun.Messaging.Infrastructure;

/// <summary>
/// Webhook inbox işleyicisi (ARCHITECTURE §5.1, §5.3).
/// <list type="number">
///   <item><b>Alma (claim):</b> kısa bir transaction'da bekleyen veya kirası dolmuş satırlar <c>FOR UPDATE SKIP LOCKED</c> ile kilitlenir;
///         <c>Processing</c> + kira + <c>attempt_count++</c> yazılıp commit edilir. Eşzamanlı ikinci işleyici aynı satırı almaz.
///         Azami denemeye ulaşmış satır (ör. işleyiciyi çökerten olay) alınmadan dead-letter olur → sonsuz döngü yok.</item>
///   <item><b>İşleme:</b> her olay kendi DI scope'unda <see cref="InboundEventProcessor"/> ile (transaction dışında medya indirme dâhil).</item>
///   <item><b>Hata:</b> üstel backoff + jitter ile <c>Pending</c>; azami denemede <c>DeadLetter</c>.</item>
/// </list>
/// </summary>
internal sealed partial class InboxProcessor(
    IServiceScopeFactory scopeFactory,
    IOptions<MessagingOptions> options,
    IConfiguration configuration,
    TimeProvider timeProvider,
    ILogger<InboxProcessor> logger) : PollingWorker(configuration, timeProvider, logger)
{
    private readonly MessagingOptions _options = options.Value;

    protected override bool Enabled => _options.InboxProcessorEnabled;

    protected override TimeSpan IdleDelay => _options.PollingInterval;

    internal override async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        var claimed = await ClaimAsync(cancellationToken);
        foreach (var eventId in claimed)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break; // kalanlar kira dolunca yeniden alınır
            }

            using (Logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = eventId }))
            {
                try
                {
                    var scope = scopeFactory.CreateAsyncScope();
                    await using (scope.ConfigureAwait(false))
                    {
                        await scope.ServiceProvider.GetRequiredService<InboundEventProcessor>().ProcessAsync(eventId, cancellationToken);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    await RecordFailureAsync(eventId, ex);
                }
            }
        }

        return claimed.Count;
    }

    private async Task<List<Guid>> ClaimAsync(CancellationToken cancellationToken)
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
                var rows = await db.InboxEvents.FromSql($"""
                        SELECT * FROM messaging.webhook_inbox_events
                        WHERE processing_state IN ('Pending', 'Processing') AND next_attempt_at <= {now}
                        ORDER BY received_at, id
                        LIMIT {batchSize}
                        FOR UPDATE SKIP LOCKED
                        """)
                    .ToListAsync(cancellationToken);

                var claimed = new List<Guid>();
                foreach (var row in rows)
                {
                    if (row.AttemptCount >= _options.MaxAttempts)
                    {
                        // Önceki deneme kira süresi içinde bitmedi (ör. süreç çöktü) ve hak kalmadı.
                        row.DeadLetter($"max_attempts_exceeded ({row.AttemptCount}); son hata: {row.LastError ?? "lease_expired"}");
                        LogDeadLettered(Logger, row.Id, row.AttemptCount);
                        continue;
                    }

                    row.Claim(now + _options.LeaseDuration);
                    claimed.Add(row.Id);
                }

                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return claimed;
            }
        }
    }

    private async Task RecordFailureAsync(Guid eventId, Exception exception)
    {
        var scope = scopeFactory.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var db = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();
            var row = await db.InboxEvents.SingleOrDefaultAsync(e => e.Id == eventId, CancellationToken.None);
            if (row is null || row.ProcessingState != InboxProcessingState.Processing)
            {
                return;
            }

            // Exception mesajı kişisel veri taşımaz: kendi exception'larımız sabit kodlu, EF/Npgsql mesajları değer içermez.
            var error = $"{exception.GetType().Name}: {exception.Message}";
            if (row.AttemptCount >= _options.MaxAttempts)
            {
                row.DeadLetter(error);
                LogDeadLettered(Logger, eventId, row.AttemptCount);
            }
            else
            {
                var delay = RetryDelay.Compute(row.AttemptCount, _options.BaseRetryDelay, _options.MaxRetryDelay, Random.Shared.NextDouble());
                row.ScheduleRetry(Time.GetUtcNow() + delay, error);
                LogRetryScheduled(Logger, exception, eventId, row.AttemptCount, delay);
            }

            await db.SaveChangesAsync(CancellationToken.None);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Inbox olayı {EventId} başarısız (deneme {Attempt}); {Delay} sonra yeniden denenecek.")]
    private static partial void LogRetryScheduled(ILogger logger, Exception exception, Guid eventId, int attempt, TimeSpan delay);

    [LoggerMessage(Level = LogLevel.Error, Message = "Inbox olayı {EventId} {Attempt} denemeden sonra dead-letter oldu.")]
    private static partial void LogDeadLettered(ILogger logger, Guid eventId, int attempt);
}
