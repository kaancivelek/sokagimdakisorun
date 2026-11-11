using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SokagimdakiSorun.Messaging.Application;
using SokagimdakiSorun.Messaging.Domain;

namespace SokagimdakiSorun.Messaging.Infrastructure;

/// <summary>
/// Süresi dolan aktif intake oturumlarını <c>Expired</c> yapar ve şikâyetçiye bilgi mesajı kuyruğa yazar (ARCHITECTURE §5.3).
/// Her oturum kendi transaction'ında, inbox işleyicisiyle aynı sırayla kilitlenir (önce konuşma <c>FOR UPDATE</c>), sonra yeniden
/// okunur: o arada gelen mesaj oturumu ilerlettiyse süre uzamıştır ve dokunulmaz. Giden mesaj <c>expired:{sessionId}</c> ile
/// idempotenttir.
/// </summary>
internal sealed partial class IntakeExpirySweeper(
    IServiceScopeFactory scopeFactory,
    IOptions<MessagingOptions> options,
    IConfiguration configuration,
    TimeProvider timeProvider,
    ILogger<IntakeExpirySweeper> logger) : PollingWorker(configuration, timeProvider, logger)
{
    private readonly MessagingOptions _options = options.Value;

    protected override bool Enabled => _options.ExpirySweeperEnabled;

    protected override TimeSpan IdleDelay => _options.ExpirySweepInterval;

    internal override async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        List<(Guid SessionId, Guid ConversationId)> due;
        var scope = scopeFactory.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var db = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();
            var now = Time.GetUtcNow();
            due = (await db.IntakeSessions.AsNoTracking()
                    .Where(s => IntakeStates.Active.Contains(s.State) && s.ExpiresAt <= now)
                    .OrderBy(s => s.ExpiresAt)
                    .Take(_options.BatchSize)
                    .Select(s => new { s.Id, s.ConversationId })
                    .ToListAsync(cancellationToken))
                .Select(s => (s.Id, s.ConversationId))
                .ToList();
        }

        var expired = 0;
        foreach (var (sessionId, conversationId) in due)
        {
            if (await ExpireAsync(sessionId, conversationId, cancellationToken))
            {
                expired++;
            }
        }

        if (expired > 0)
        {
            LogExpired(Logger, expired);
        }

        return expired;
    }

    private async Task<bool> ExpireAsync(Guid sessionId, Guid conversationId, CancellationToken cancellationToken)
    {
        var scope = scopeFactory.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var db = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();
            var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            await using (transaction.ConfigureAwait(false))
            {
                _ = await db.LockConversationAsync(conversationId, cancellationToken);
                var session = await db.IntakeSessions.SingleAsync(s => s.Id == sessionId, cancellationToken);
                var now = Time.GetUtcNow();
                if (!session.ExpireIfDue(now))
                {
                    return false;
                }

                var dedupKey = $"expired:{sessionId}";
                if (!await db.Messages.AnyAsync(m => m.DedupKey == dedupKey, cancellationToken))
                {
                    db.Messages.Add(Message.Outbound(conversationId, BotMessages.Expired, dedupKey, sessionId, null, now));
                }

                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return true;
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "{Count} intake oturumu zaman aşımıyla kapatıldı.")]
    private static partial void LogExpired(ILogger logger, int count);
}
