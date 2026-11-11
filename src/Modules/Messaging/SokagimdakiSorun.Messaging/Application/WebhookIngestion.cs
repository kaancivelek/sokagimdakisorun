using Microsoft.EntityFrameworkCore;
using SokagimdakiSorun.Messaging.Domain;
using SokagimdakiSorun.Messaging.Infrastructure;
using SokagimdakiSorun.Messaging.Infrastructure.WhatsApp;

namespace SokagimdakiSorun.Messaging.Application;

/// <summary>Kabul sonucu (yalnızca sayılar; loglanabilir).</summary>
internal sealed record IngestionResult(int Accepted, int Duplicates);

/// <summary>
/// Webhook kabulü (ADR-0003 §4): imzası doğrulanmış gövdeyi olay başına inbox satırına yazar. İş mantığı yoktur;
/// <c>INSERT … ON CONFLICT (dedup_key) DO NOTHING</c> tekrar eden teslimi sessizce yutar (I-3).
/// </summary>
internal sealed class WebhookIngestion(MessagingDbContext db, TimeProvider timeProvider)
{
    public async Task<IngestionResult> AcceptAsync(IReadOnlyList<InboxEventDraft> drafts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(drafts);
        var now = timeProvider.GetUtcNow();
        var accepted = 0;
        foreach (var draft in drafts)
        {
            var id = Guid.CreateVersion7();
            var provider = nameof(WebhookProvider.WhatsApp);
            var type = draft.EventType.ToString();
            var state = nameof(InboxProcessingState.Pending);
            accepted += await db.Database.ExecuteSqlAsync($"""
                INSERT INTO messaging.webhook_inbox_events
                    (id, provider, dedup_key, event_type, payload, received_at, processing_state, attempt_count, next_attempt_at)
                VALUES ({id}, {provider}, {draft.DedupKey}, {type}, {draft.Payload}::jsonb, {now}, {state}, 0, {now})
                ON CONFLICT (dedup_key) DO NOTHING
                """, cancellationToken);
        }

        return new IngestionResult(accepted, drafts.Count - accepted);
    }
}
