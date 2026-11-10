using System.Text.Json;
using SokagimdakiSorun.Platform.Persistence;
using SokagimdakiSorun.SharedKernel;

namespace SokagimdakiSorun.Platform.Outbox;

/// <summary>
/// Modüllerin outbox'a yazma yardımcısı (ADR-0002 §3). Event, bağlamın değişiklik izleyicisine eklenir ve
/// modülün kendi <c>SaveChanges</c> çağrısıyla, iş verisiyle <b>aynı transaction'da</b> yazılır.
/// </summary>
public static class OutboxDbContextExtensions
{
    internal static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// <paramref name="integrationEvent"/>'i outbox satırı olarak ekler. Kaydetmez; modül iş verisiyle birlikte
    /// <c>SaveChangesAsync</c> çağırır. Outbox satırının kimliği event'in <see cref="IIntegrationEvent.EventId"/>'sidir.
    /// </summary>
    public static void AddIntegrationEvent(this ModuleDbContext context, IIntegrationEvent integrationEvent)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(integrationEvent);

        if (integrationEvent.EventId == Guid.Empty)
        {
            throw new ArgumentException("Integration event'in EventId'si boş olamaz.", nameof(integrationEvent));
        }

        var eventType = integrationEvent.GetType();
        var occurredAt = integrationEvent.OccurredAt.ToUniversalTime();

        context.Add(new OutboxMessage
        {
            Id = integrationEvent.EventId,
            Type = IntegrationEventTypeName.Of(eventType),
            Payload = JsonSerializer.Serialize(integrationEvent, eventType, SerializerOptions),
            OccurredAt = occurredAt,
            AvailableAt = occurredAt,
        });
    }
}

/// <summary>Outbox <c>type</c> kolonunda saklanan event tipi adı.</summary>
internal static class IntegrationEventTypeName
{
    public static string Of(Type eventType)
    {
        var name = eventType.FullName ?? throw new ArgumentException("Event tipinin tam adı yok.", nameof(eventType));
        if (name.Length > OutboxMessage.TypeMaxLength)
        {
            throw new ArgumentException($"Event tipi adı {OutboxMessage.TypeMaxLength} karakteri aşıyor: {name}", nameof(eventType));
        }

        return name;
    }
}
