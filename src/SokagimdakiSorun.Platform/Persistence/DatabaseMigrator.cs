using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SokagimdakiSorun.Platform.Hosting;

namespace SokagimdakiSorun.Platform.Persistence;

/// <summary>
/// <see cref="DatabaseOptions.ApplyMigrationsOnStartup"/> açıksa kayıtlı tüm modül bağlamlarının migration'larını
/// host açılırken, kayıt sırasıyla uygular (önce <c>platform</c>). Schema'lar arası FK olmadığı için sıra bağımlılığı
/// yoktur (ADR-0002 §4). Hata açılışı durdurur. Build-zamanı araçlarında (OpenAPI üretimi, <c>dotnet ef</c>) çalışmaz.
/// Yalnızca yerel geliştirme içindir; üretim yöntemi WS-11'de belirlenir.
/// </summary>
internal sealed partial class DatabaseMigrator(
    IServiceScopeFactory scopeFactory,
    IEnumerable<ModuleDbContextRegistration> registrations,
    IOptions<DatabaseOptions> options,
    ILogger<DatabaseMigrator> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.ApplyMigrationsOnStartup || BuildTimeTooling.IsRunning)
        {
            return;
        }

        foreach (var contextType in registrations.Select(r => r.ContextType).Distinct())
        {
            var scope = scopeFactory.CreateAsyncScope();
            await using (scope.ConfigureAwait(false))
            {
                var context = (DbContext)scope.ServiceProvider.GetRequiredService(contextType);
                var pending = (await context.Database.GetPendingMigrationsAsync(cancellationToken).ConfigureAwait(false)).Count();
                await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
                LogMigrated(logger, contextType.Name, pending);
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Information, Message = "{Context} için {Count} bekleyen migration uygulandı.")]
    private static partial void LogMigrated(ILogger logger, string context, int count);
}
