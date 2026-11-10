using Microsoft.Extensions.DependencyInjection;
using SokagimdakiSorun.SharedKernel;

namespace SokagimdakiSorun.Platform.Outbox;

/// <summary>Integration event handler kaydı.</summary>
public static class OutboxServiceCollectionExtensions
{
    /// <summary>
    /// <typeparamref name="THandler"/>'ı <typeparamref name="TEvent"/> için scoped olarak kaydeder ve dağıtıcıya
    /// event tipini tanıtır. Handler'ı hiç kayıtlı olmayan bir event tipi dağıtıcıda "işlendi" sayılır
    /// (dinleyeni yoktur). Handler sözleşmesi: <see cref="IIntegrationEventHandler{TEvent}"/>.
    /// </summary>
    public static IServiceCollection AddIntegrationEventHandler<TEvent, THandler>(this IServiceCollection services)
        where TEvent : IIntegrationEvent
        where THandler : class, IIntegrationEventHandler<TEvent>
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<IIntegrationEventHandler<TEvent>, THandler>();
        services.AddSingleton(IntegrationEventRegistration.For<TEvent>());

        return services;
    }
}
