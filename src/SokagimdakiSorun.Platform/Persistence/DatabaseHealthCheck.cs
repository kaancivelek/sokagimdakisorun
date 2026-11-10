using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace SokagimdakiSorun.Platform.Persistence;

/// <summary>
/// Veritabanına bağlanılabiliyor mu (<c>/health/ready</c>, ARCHITECTURE §11). Bağlantı dizesi yoksa veya sunucuya
/// ulaşılamıyorsa "Unhealthy". Sonuçtaki ayrıntı yalnızca sunucu loglarına gider; health yanıtı yalnızca toplam
/// durumu döner (bağlantı dizesi, host adı veya exception yanıta yazılmaz).
/// </summary>
internal sealed class DatabaseHealthCheck(IServiceScopeFactory scopeFactory) : IHealthCheck
{
    public const string Name = "database";

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            var scope = scopeFactory.CreateAsyncScope();
            await using (scope.ConfigureAwait(false))
            {
                var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
                return await db.Database.CanConnectAsync(cancellationToken).ConfigureAwait(false)
                    ? HealthCheckResult.Healthy()
                    : new HealthCheckResult(context.Registration.FailureStatus, "Veritabanına bağlanılamadı.");
            }
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, "Veritabanına bağlanılamadı.", ex);
        }
    }
}
