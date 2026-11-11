using Microsoft.Extensions.Diagnostics.HealthChecks;
using SokagimdakiSorun.Media.Infrastructure.Storage;

namespace SokagimdakiSorun.Media.Infrastructure;

/// <summary>
/// Medya kökü yazılabilir mi (<c>/health/ready</c>, ARCHITECTURE §11): kökte rastgele adlı bir sonda dosyası
/// <c>CreateNew</c> ile oluşturulur, yazılır ve kapanınca silinir. Kök tanımlı değilse veya yazılamıyorsa "Unhealthy".
/// Sonuca exception eklenmez: iç exception mesajları yol içerebilir; açıklama yalnızca temizlenmiş mesajdır.
/// </summary>
internal sealed class MediaStorageHealthCheck(IMediaStorage storage) : IHealthCheck
{
    public const string Name = "media-storage";

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            await storage.EnsureWritableAsync(cancellationToken).ConfigureAwait(false);
            return HealthCheckResult.Healthy();
        }
        catch (MediaStorageException ex)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, ex.Message);
        }
    }
}
