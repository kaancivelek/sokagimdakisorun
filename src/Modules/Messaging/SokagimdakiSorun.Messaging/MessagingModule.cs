using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace SokagimdakiSorun.Messaging;

/// <summary>
/// Messaging modÃ¼lÃ¼nÃ¼n host tarafÄ±ndan Ã§aÄŸrÄ±lan kayÄ±t noktalarÄ± (docs/ARCHITECTURE.md Â§3).
/// </summary>
public static class MessagingModule
{
    /// <summary>Messaging modÃ¼lÃ¼nÃ¼n DI kayÄ±tlarÄ±nÄ± ekler.</summary>
    public static IServiceCollection AddMessagingModule(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        return services;
    }

    /// <summary>Messaging modÃ¼lÃ¼nÃ¼n uÃ§ nokta gruplarÄ±nÄ± eÅŸler.</summary>
    public static IEndpointRouteBuilder MapMessagingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        return endpoints;
    }
}