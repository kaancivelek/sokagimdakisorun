using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using SokagimdakiSorun.Media.Application;
using SokagimdakiSorun.Media.Contracts;
using SokagimdakiSorun.Media.Infrastructure;
using SokagimdakiSorun.Media.Infrastructure.Storage;
using SokagimdakiSorun.Platform.Persistence;

namespace SokagimdakiSorun.Media;

/// <summary>
/// Media modülünün host tarafından çağrılan kayıt noktaları (docs/ARCHITECTURE.md §3).
/// </summary>
public static class MediaModule
{
    /// <summary>Hazırlık kontrolünün azami süresi; aşılırsa kontrol "Unhealthy" sayılır.</summary>
    public static readonly TimeSpan StorageHealthCheckTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Media modülünün DI kayıtlarını ekler: <c>MediaDbContext</c> (<c>media</c> schema), yerel dosya sistemi saklaması,
    /// <see cref="IMediaService"/> ve <c>OrphanMediaCleaner</c>. Kayıt sırasında veritabanına veya dosya sistemine erişilmez.
    /// </summary>
    public static IServiceCollection AddMediaModule(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<MediaOptions>()
            .Bind(configuration.GetSection(MediaOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.TryAddSingleton(TimeProvider.System);

        services.AddModuleDbContext<MediaDbContext>();
        services.AddSingleton<IMediaStorage, LocalFileSystemMediaStorage>();
        services.AddScoped<IMediaService, MediaService>();
        services.AddSingleton<OrphanMediaCleaner>();
        services.AddHostedService(provider => provider.GetRequiredService<OrphanMediaCleaner>());

        return services;
    }

    /// <summary>Medya kökü yazılabilirlik kontrolünü verilen etiketlerle (ör. <c>ready</c>) ekler.</summary>
    public static IHealthChecksBuilder AddMediaStorageCheck(this IHealthChecksBuilder builder, params string[] tags)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.AddCheck<MediaStorageHealthCheck>(
            MediaStorageHealthCheck.Name, HealthStatus.Unhealthy, tags, StorageHealthCheckTimeout);
    }

    /// <summary>
    /// Media modülü HTTP uç noktası sunmaz: dosya erişimi Complaints üzerinden yetkilendirilir (ADR-0004 §5).
    /// Yöntem, modül kayıt sözleşmesinin bütünlüğü için korunur.
    /// </summary>
    public static IEndpointRouteBuilder MapMediaEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        return endpoints;
    }
}
