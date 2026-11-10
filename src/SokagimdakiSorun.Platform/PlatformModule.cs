using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using SokagimdakiSorun.Platform.Outbox;
using SokagimdakiSorun.Platform.Persistence;

namespace SokagimdakiSorun.Platform;

/// <summary>Host'un çağırdığı Platform kayıt noktaları (ARCHITECTURE §3).</summary>
public static class PlatformModule
{
    /// <summary>Hazırlık kontrolünün azami süresi; aşılırsa kontrol "Unhealthy" sayılır.</summary>
    public static readonly TimeSpan DatabaseHealthCheckTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// <c>PlatformDbContext</c>, açılış migration'ı ve <c>OutboxDispatcher</c>'ı kaydeder. Modül kayıtlarından
    /// <b>önce</b> çağrılmalıdır; böylece <c>platform</c> migration'ı diğer modüllerden önce uygulanır.
    /// Kayıt sırasında veritabanına bağlanılmaz.
    /// </summary>
    public static IServiceCollection AddPlatform(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<DatabaseOptions>().Bind(configuration.GetSection(DatabaseOptions.SectionName));
        services.AddOptions<OutboxOptions>()
            .Bind(configuration.GetSection(OutboxOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.TryAddSingleton(TimeProvider.System);

        services.AddModuleDbContext<PlatformDbContext>();
        services.TryAddSingleton<IntegrationEventTypeRegistry>();

        // Sıra önemli: hosted service'ler kayıt sırasıyla başlar; migration'lar dağıtıcıdan önce uygulanır.
        services.AddHostedService<DatabaseMigrator>();
        services.AddHostedService<OutboxDispatcher>();

        return services;
    }

    /// <summary>Veritabanı bağlantı kontrolünü verilen etiketlerle (ör. <c>ready</c>) ekler.</summary>
    public static IHealthChecksBuilder AddPlatformDatabaseCheck(this IHealthChecksBuilder builder, params string[] tags)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.AddCheck<DatabaseHealthCheck>(
            DatabaseHealthCheck.Name, HealthStatus.Unhealthy, tags, DatabaseHealthCheckTimeout);
    }
}
