using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using SokagimdakiSorun.Platform;

namespace SokagimdakiSorun.Api.Observability;

/// <summary>
/// <c>/health/live</c>: süreç ayakta. <c>/health/ready</c>: bağımlılıklar hazır.
/// Hazırlık kontrolleri <see cref="ReadyTag"/> etiketiyle eklenir: veritabanı bağlantısı (WS-03), medya kökü (WS-05).
/// Yanıt yalnızca toplam durumu düz metin olarak döner; kontrol adı, exception veya ayrıntı içermez.
/// </summary>
internal static class HealthEndpoints
{
    public const string LiveTag = "live";
    public const string ReadyTag = "ready";

    public static IServiceCollection AddApiHealthChecks(this IServiceCollection services)
    {
        services.AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy(), tags: [LiveTag])
            .AddPlatformDatabaseCheck(ReadyTag);

        return services;
    }

    public static IEndpointRouteBuilder MapApiHealthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHealthChecks("/health/live", new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains(LiveTag),
        }).AllowAnonymous().DisableHttpMetrics();

        endpoints.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains(ReadyTag),
        }).AllowAnonymous().DisableHttpMetrics();

        return endpoints;
    }
}
