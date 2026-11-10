using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SokagimdakiSorun.Platform.Hosting;
using SokagimdakiSorun.Platform.Persistence;

namespace SokagimdakiSorun.Platform.Outbox;

/// <summary>
/// Outbox satırlarını süreç içi handler'lara dağıtan arka plan işleyicisi (ARCHITECTURE §4.2, §5.3).
/// <list type="bullet">
///   <item>Her turda tek transaction açar; bekleyen satırları <c>FOR UPDATE SKIP LOCKED</c> ile kilitler. Eşzamanlı
///         ikinci bir dağıtıcı kilitli satırları atlar, aynı satırı işlemez.</item>
///   <item>Başarılı satır <c>processed_at</c> ile işaretlenir. Hata: <c>attempt_count</c> artar, <c>available_at</c>
///         üstel backoff + jitter ile ileri alınır; <see cref="OutboxOptions.MaxAttempts"/>'ta <c>dead_lettered_at</c> dolar.</item>
///   <item>Kapanışta <see cref="CancellationToken"/>'a uyar: işlenmiş satırları kaydeder, kalanını bir sonraki açılışa bırakır.</item>
/// </list>
/// Teslim garantisi en az bir kezdir; handler sözleşmesi: <see cref="IIntegrationEventHandler{TEvent}"/>.
/// </summary>
internal sealed partial class OutboxDispatcher(
    IServiceScopeFactory scopeFactory,
    IntegrationEventTypeRegistry registry,
    IOptions<OutboxOptions> options,
    IConfiguration configuration,
    TimeProvider timeProvider,
    ILogger<OutboxDispatcher> logger) : BackgroundService
{
    private readonly OutboxOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled || BuildTimeTooling.IsRunning)
        {
            LogDisabled(logger);
            return;
        }

        if (string.IsNullOrWhiteSpace(configuration.GetConnectionString(DatabaseOptions.ConnectionStringName)))
        {
            // Bağlantı dizesi yokken döngü her turda hata üretmesin; /health/ready zaten "Unhealthy" döner.
            LogNoConnectionString(logger);
            return;
        }

        await Task.Yield();
        while (!stoppingToken.IsCancellationRequested)
        {
            int fetched;
            try
            {
                fetched = await DispatchBatchAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Veritabanı geçici olarak erişilemez olabilir; döngü ölmez, bir sonraki turda yeniden dener.
                LogBatchFailed(logger, ex);
                fetched = 0;
            }

            if (fetched == 0)
            {
                try
                {
                    await Task.Delay(_options.PollingInterval, timeProvider, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    /// <summary>Tek bir tur: en fazla <see cref="OutboxOptions.BatchSize"/> satırı kilitler ve işler. Kilitlenen satır sayısını döner.</summary>
    internal async Task<int> DispatchBatchAsync(CancellationToken cancellationToken)
    {
        var scope = scopeFactory.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                var now = timeProvider.GetUtcNow();
                var batchSize = _options.BatchSize;
                var batch = await db.OutboxMessages
                    .FromSql($"""
                        SELECT * FROM platform.outbox_messages
                        WHERE processed_at IS NULL AND dead_lettered_at IS NULL AND available_at <= {now}
                        ORDER BY available_at, id
                        LIMIT {batchSize}
                        FOR UPDATE SKIP LOCKED
                        """)
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);

                foreach (var message in batch)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    using (logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = message.Id }))
                    {
                        try
                        {
                            await DispatchAsync(message, cancellationToken).ConfigureAwait(false);
                            message.ProcessedAt = timeProvider.GetUtcNow();
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            // Kapanış: iptal deneme sayılmaz; satır kilidi transaction ile bırakılır.
                            break;
                        }
                        catch (Exception ex)
                        {
                            RecordFailure(message, ex);
                        }
                    }
                }

                // Kapanışta bile işlenmiş satırların durumu kaydedilir; aksi halde handler'lar gereksiz yere yeniden çalışır.
                await db.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
                await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
                return batch.Count;
            }
        }
    }

    internal static TimeSpan ComputeRetryDelay(int attempt, OutboxOptions options, double jitterSample)
    {
        var exponential = options.BaseRetryDelay.TotalMilliseconds * Math.Pow(2, Math.Clamp(attempt - 1, 0, 30));
        var capped = Math.Min(exponential, options.MaxRetryDelay.TotalMilliseconds);

        // "Equal jitter": yarısı sabit, yarısı rastgele. Aynı anda hata alan satırlar yayılır, gecikme sıfıra düşmez.
        return TimeSpan.FromMilliseconds((capped / 2) + (capped / 2 * Math.Clamp(jitterSample, 0, 1)));
    }

    private async Task DispatchAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        if (!registry.TryGet(message.Type, out var registration))
        {
            LogNoHandler(logger, message.Id, message.Type);
            return;
        }

        var handlerScope = scopeFactory.CreateAsyncScope();
        await using (handlerScope.ConfigureAwait(false))
        {
            await registration.Dispatch(handlerScope.ServiceProvider, message.Payload, cancellationToken).ConfigureAwait(false);
        }
    }

    private void RecordFailure(OutboxMessage message, Exception exception)
    {
        var now = timeProvider.GetUtcNow();
        message.AttemptCount++;
        message.LastError = Truncate($"{exception.GetType().FullName}: {exception.Message}", OutboxMessage.LastErrorMaxLength);

        if (message.AttemptCount >= _options.MaxAttempts)
        {
            message.DeadLetteredAt = now;
            LogDeadLettered(logger, exception, message.Id, message.Type, message.AttemptCount);
            return;
        }

        var delay = ComputeRetryDelay(message.AttemptCount, _options, Random.Shared.NextDouble());
        message.AvailableAt = now + delay;
        LogRetryScheduled(logger, exception, message.Id, message.Type, message.AttemptCount, delay);
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    [LoggerMessage(Level = LogLevel.Information, Message = "Outbox dağıtıcısı devre dışı; çalışmayacak.")]
    private static partial void LogDisabled(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Veritabanı bağlantı dizesi tanımlı değil; outbox dağıtıcısı çalışmayacak.")]
    private static partial void LogNoConnectionString(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox turu başarısız oldu; bir sonraki turda yeniden denenecek.")]
    private static partial void LogBatchFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Outbox mesajı {MessageId} ({EventType}) için kayıtlı handler yok; işlendi sayıldı.")]
    private static partial void LogNoHandler(ILogger logger, Guid messageId, string eventType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Outbox mesajı {MessageId} ({EventType}) başarısız oldu (deneme {Attempt}); {Delay} sonra yeniden denenecek.")]
    private static partial void LogRetryScheduled(ILogger logger, Exception exception, Guid messageId, string eventType, int attempt, TimeSpan delay);

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox mesajı {MessageId} ({EventType}) {Attempt} denemeden sonra dead-letter oldu.")]
    private static partial void LogDeadLettered(ILogger logger, Exception exception, Guid messageId, string eventType, int attempt);
}
