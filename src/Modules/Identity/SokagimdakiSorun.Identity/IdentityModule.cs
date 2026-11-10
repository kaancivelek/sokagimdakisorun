using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace SokagimdakiSorun.Identity;

/// <summary>
/// Identity modÃ¼lÃ¼nÃ¼n host tarafÄ±ndan Ã§aÄŸrÄ±lan kayÄ±t noktalarÄ± (docs/ARCHITECTURE.md Â§3).
/// </summary>
public static class IdentityModule
{
    /// <summary>Identity modÃ¼lÃ¼nÃ¼n DI kayÄ±tlarÄ±nÄ± ekler.</summary>
    public static IServiceCollection AddIdentityModule(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        return services;
    }

    /// <summary>Identity modÃ¼lÃ¼nÃ¼n uÃ§ nokta gruplarÄ±nÄ± eÅŸler.</summary>
    public static IEndpointRouteBuilder MapIdentityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        return endpoints;
    }
}