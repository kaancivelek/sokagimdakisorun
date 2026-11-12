using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SokagimdakiSorun.Reporting.Endpoints;

namespace SokagimdakiSorun.Reporting;

/// <summary>
/// Reporting modülünün host tarafından çağrılan kayıt noktaları (docs/ARCHITECTURE.md §3). Modülün tablosu, schema'sı ve
/// DbContext'i yoktur (ADR-0002); tüm sayılar Complaints'in kapsam duyarlı <c>IComplaintStatistics</c> sözleşmesinden gelir.
/// </summary>
public static class ReportingModule
{
    /// <summary>Reporting modülünün DI kayıtlarını ekler.</summary>
    public static IServiceCollection AddReportingModule(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.TryAddSingleton(TimeProvider.System);
        return services;
    }

    /// <summary>Reporting modülünün uç nokta gruplarını eşler.</summary>
    public static IEndpointRouteBuilder MapReportingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        ReportEndpoints.Map(endpoints);
        return endpoints;
    }
}
