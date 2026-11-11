using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SokagimdakiSorun.Platform.Persistence;

namespace SokagimdakiSorun.Messaging.Infrastructure;

/// <summary>
/// Messaging arka plan işleyicilerinin ortak döngüsü (ARCHITECTURE §5.3): devre dışıysa, build-zamanı aracındaysa veya bağlantı
/// dizesi yoksa çalışmaz; tur hata verirse döngü ölmez; iş yokken bekler; <see cref="CancellationToken"/>'a uyar.
/// </summary>
internal abstract partial class PollingWorker(IConfiguration configuration, TimeProvider timeProvider, ILogger logger) : BackgroundService
{
    // Platform.Hosting.BuildTimeTooling ile aynı ölçüt (o tip Platform'a internal): build-zamanı OpenAPI üretimi, dotnet ef.
    private static readonly bool BuildTimeToolingIsRunning =
        Assembly.GetEntryAssembly()?.GetName().Name is "GetDocument.Insider" or "ef";

    protected TimeProvider Time { get; } = timeProvider;

    protected ILogger Logger { get; } = logger;

    protected abstract bool Enabled { get; }

    protected abstract TimeSpan IdleDelay { get; }

    /// <summary>Tek tur; işlenen öğe sayısını döner (0 → bekle).</summary>
    internal abstract Task<int> RunOnceAsync(CancellationToken cancellationToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var name = GetType().Name;
        if (!Enabled || BuildTimeToolingIsRunning
            || string.IsNullOrWhiteSpace(configuration.GetConnectionString(DatabaseOptions.ConnectionStringName)))
        {
            LogNotRunning(Logger, name);
            return;
        }

        await Task.Yield();
        while (!stoppingToken.IsCancellationRequested)
        {
            int processed;
            try
            {
                processed = await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                LogRunFailed(Logger, ex, name);
                processed = 0;
            }

            if (processed == 0)
            {
                try
                {
                    await Task.Delay(IdleDelay, Time, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "{Worker} devre dışı veya yapılandırılmamış; çalışmayacak.")]
    private static partial void LogNotRunning(ILogger logger, string worker);

    [LoggerMessage(Level = LogLevel.Error, Message = "{Worker} turu başarısız oldu; bir sonraki turda yeniden denenecek.")]
    private static partial void LogRunFailed(ILogger logger, Exception exception, string worker);
}
