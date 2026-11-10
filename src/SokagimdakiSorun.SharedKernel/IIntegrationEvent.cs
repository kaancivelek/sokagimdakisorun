namespace SokagimdakiSorun.SharedKernel;

/// <summary>
/// Modüller arası asenkron bildirim için işaretleyici (docs/ARCHITECTURE.md §4.2).
/// Somut event tipleri sahibi modülün <c>Contracts</c> projesinde tanımlanır.
/// </summary>
public interface IIntegrationEvent
{
    /// <summary>Benzersiz event kimliği; handler'lar bunu idempotency anahtarı olarak kullanır.</summary>
    Guid EventId { get; }

    /// <summary>Olayın gerçekleştiği an (UTC).</summary>
    DateTimeOffset OccurredAt { get; }
}
