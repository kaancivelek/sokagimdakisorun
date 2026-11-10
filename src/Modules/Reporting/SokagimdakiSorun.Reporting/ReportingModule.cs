using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace SokagimdakiSorun.Reporting;

/// <summary>
/// Reporting modÃ¼lÃ¼nÃ¼n host tarafÄ±ndan Ã§aÄŸrÄ±lan kayÄ±t noktalarÄ± (docs/ARCHITECTURE.md Â§3).
/// </summary>
public static class ReportingModule
{
    /// <summary>Reporting modÃ¼lÃ¼nÃ¼n DI kayÄ±tlarÄ±nÄ± ekler.</summary>
    public static IServiceCollection AddReportingModule(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        return services;
    }

    /// <summary>Reporting modÃ¼lÃ¼nÃ¼n uÃ§ nokta gruplarÄ±nÄ± eÅŸler.</summary>
    public static IEndpointRouteBuilder MapReportingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        return endpoints;
    }
}