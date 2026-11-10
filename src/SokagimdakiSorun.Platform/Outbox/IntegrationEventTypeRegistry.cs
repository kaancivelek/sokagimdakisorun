using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SokagimdakiSorun.SharedKernel;

namespace SokagimdakiSorun.Platform.Outbox;

/// <summary>Bir event tipinin, outbox <c>type</c> adı ve handler'larını çağıran temsilciyle kaydı.</summary>
internal sealed record IntegrationEventRegistration(
    string TypeName,
    Func<IServiceProvider, string, CancellationToken, Task> Dispatch)
{
    public static IntegrationEventRegistration For<TEvent>()
        where TEvent : IIntegrationEvent =>
        new(IntegrationEventTypeName.Of(typeof(TEvent)), DispatchAsync<TEvent>);

    // Payload'ı somut tipe çözer ve kayıtlı tüm handler'ları sırayla çağırır. Biri hata verirse exception yukarı
    // çıkar ve mesaj (tüm handler'larıyla) yeniden denenir — handler'ların idempotent olması bu yüzden zorunludur.
    private static async Task DispatchAsync<TEvent>(IServiceProvider services, string payload, CancellationToken cancellationToken)
        where TEvent : IIntegrationEvent
    {
        var integrationEvent = JsonSerializer.Deserialize<TEvent>(payload, OutboxDbContextExtensions.SerializerOptions)
            ?? throw new InvalidOperationException($"Outbox payload'ı {typeof(TEvent).Name} olarak çözülemedi.");

        foreach (var handler in services.GetServices<IIntegrationEventHandler<TEvent>>())
        {
            await handler.HandleAsync(integrationEvent, cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>Outbox <c>type</c> adından dağıtım temsilcisine eşleme. Handler'ı olmayan tipler burada yoktur.</summary>
internal sealed class IntegrationEventTypeRegistry
{
    private readonly Dictionary<string, IntegrationEventRegistration> _registrations;

    public IntegrationEventTypeRegistry(IEnumerable<IntegrationEventRegistration> registrations)
    {
        _registrations = new Dictionary<string, IntegrationEventRegistration>(StringComparer.Ordinal);
        foreach (var registration in registrations)
        {
            _registrations.TryAdd(registration.TypeName, registration);
        }
    }

    public bool TryGet(string typeName, out IntegrationEventRegistration registration) =>
        _registrations.TryGetValue(typeName, out registration!);
}
