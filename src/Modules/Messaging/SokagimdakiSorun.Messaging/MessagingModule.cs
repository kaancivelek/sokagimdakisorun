using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using SokagimdakiSorun.Complaints.Contracts;
using SokagimdakiSorun.Messaging.Application;
using SokagimdakiSorun.Messaging.Endpoints;
using SokagimdakiSorun.Messaging.Infrastructure;
using SokagimdakiSorun.Messaging.Infrastructure.WhatsApp;
using SokagimdakiSorun.Platform.Outbox;
using SokagimdakiSorun.Platform.Persistence;

namespace SokagimdakiSorun.Messaging;

/// <summary>
/// Messaging modülünün host tarafından çağrılan kayıt noktaları (docs/ARCHITECTURE.md §3).
/// </summary>
public static class MessagingModule
{
    /// <summary>
    /// Messaging modülünün DI kayıtlarını ekler: <c>messaging</c> bağlamı, <c>IWhatsAppClient</c> (<c>Mock</c> veya <c>CloudApi</c>), webhook kabulü,
    /// inbox işleyicisi, giden gönderici, intake süre dolum süpürücüsü ve <see cref="ComplaintSubmitted"/> /
    /// <see cref="ComplaintStatusChanged"/> handler'ları. Kayıt sırasında veritabanına bağlanılmaz; secret zorunlu değildir
    /// (tanımlı değilse webhook istekleri reddedilir).
    /// </summary>
    public static IServiceCollection AddMessagingModule(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<WhatsAppOptions>()
            .Bind(configuration.GetSection(WhatsAppOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddOptions<MessagingOptions>()
            .Bind(configuration.GetSection(MessagingOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.TryAddSingleton(TimeProvider.System);

        services.AddModuleDbContext<MessagingDbContext>();

        // Sağlayıcı (ADR-0003 §6): WhatsApp__Provider. CloudApi'de eksik secret/sürüm açılışta doğrulama hatasıdır (Mock'a düşülmez).
        // Yerleşik HttpClient logları tam URL yazar (medya URL'si, numara kimliği) → kapatılır (ARCHITECTURE §11).
        services.AddSingleton<MockWhatsAppClient>();
        services.AddHttpClient(CloudWhatsAppClient.HttpClientName).RemoveAllLoggers();
        services.AddSingleton<CloudWhatsAppClient>();
        services.AddSingleton<IWhatsAppClient>(provider =>
            provider.GetRequiredService<IOptions<WhatsAppOptions>>().Value.Provider == WhatsAppProvider.CloudApi
                ? provider.GetRequiredService<CloudWhatsAppClient>()
                : provider.GetRequiredService<MockWhatsAppClient>());

        services.AddScoped<WebhookIngestion>();
        services.AddScoped<InboundEventProcessor>();
        services.AddScoped<ComplaintNotifier>();
        services.AddIntegrationEventHandler<ComplaintSubmitted, ComplaintSubmittedHandler>();
        services.AddIntegrationEventHandler<ComplaintStatusChanged, ComplaintStatusChangedHandler>();

        services.AddSingleton<InboxProcessor>();
        services.AddSingleton<OutboundSender>();
        services.AddSingleton<IntakeExpirySweeper>();
        services.AddHostedService(provider => provider.GetRequiredService<InboxProcessor>());
        services.AddHostedService(provider => provider.GetRequiredService<OutboundSender>());
        services.AddHostedService(provider => provider.GetRequiredService<IntakeExpirySweeper>());

        return services;
    }

    /// <summary>
    /// Messaging uç noktalarını eşler: <c>/api/integrations/whatsapp/webhook</c> (GET doğrulama, POST olay alımı) ve
    /// <c>GET /api/v1/complaints/{id}/messages</c>.
    /// </summary>
    public static IEndpointRouteBuilder MapMessagingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        WebhookEndpoints.Map(endpoints);
        ComplaintMessagesEndpoints.Map(endpoints);

        return endpoints;
    }
}
