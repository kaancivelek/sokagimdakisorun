using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SokagimdakiSorun.Complaints.Application;
using SokagimdakiSorun.Complaints.Contracts;
using SokagimdakiSorun.Complaints.Endpoints;
using SokagimdakiSorun.Complaints.Infrastructure;
using SokagimdakiSorun.Platform.Persistence;

namespace SokagimdakiSorun.Complaints;

/// <summary>
/// Complaints modülünün host tarafından çağrılan kayıt noktaları (docs/ARCHITECTURE.md §3).
/// </summary>
public static class ComplaintsModule
{
    /// <summary>
    /// Complaints modülünün DI kayıtlarını ekler: <c>complaints</c> bağlamı, süreç içi sözleşmeler
    /// (<see cref="IComplaintIntake"/>, <see cref="IComplaintReporterActions"/>, <see cref="IComplaintCatalog"/>,
    /// <see cref="IComplaintAccessPolicy"/>, <see cref="IComplaintStatistics"/>), panel servisleri ve temsili demo tohumlaması
    /// (yalnızca Development + <c>Complaints__DemoSeed__Enabled=true</c>). Veritabanına kayıt sırasında bağlanılmaz.
    /// </summary>
    public static IServiceCollection AddComplaintsModule(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddModuleDbContext<ComplaintsDbContext>();
        services.TryAddSingleton(TimeProvider.System);

        services.AddScoped<IComplaintIntake, ComplaintIntakeService>();
        services.AddScoped<IComplaintReporterActions, ReporterActionsService>();
        services.AddScoped<IComplaintCatalog, ComplaintCatalog>();
        services.AddScoped<IComplaintAccessPolicy, ComplaintAccessPolicy>();
        services.AddScoped<IComplaintStatistics, ComplaintStatistics>();
        services.AddScoped<ComplaintCommandService>();
        services.AddScoped<ComplaintQueries>();

        // Platform'un migration servisinden ve Identity'nin Admin tohumlamasından sonra kaydedildiği için onlardan sonra başlar.
        services.AddOptions<DemoSeedOptions>().Bind(configuration.GetSection(DemoSeedOptions.SectionName));
        services.AddHostedService<DemoDataSeeder>();

        return services;
    }

    /// <summary>
    /// Complaints modülünün uç nokta gruplarını eşler: <c>/api/v1/complaints</c>, <c>/api/v1/categories</c>,
    /// <c>/api/v1/service-units</c>, <c>/api/v1/routing-rules</c>.
    /// </summary>
    public static IEndpointRouteBuilder MapComplaintsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        ComplaintEndpoints.Map(endpoints);
        CatalogEndpoints.Map(endpoints);

        return endpoints;
    }
}
