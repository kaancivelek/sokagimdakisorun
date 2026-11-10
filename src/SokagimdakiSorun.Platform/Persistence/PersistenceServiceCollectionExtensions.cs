using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace SokagimdakiSorun.Platform.Persistence;

/// <summary>Modül <c>DbContext</c>'lerinin kaydı (rehber: docs/guides/ADDING_A_MODULE_DBCONTEXT.md).</summary>
public static class PersistenceServiceCollectionExtensions
{
    /// <summary>
    /// <typeparamref name="TContext"/>'i Npgsql ile kaydeder. Bağlantı dizesi bağlam ilk oluşturulduğunda
    /// <c>ConnectionStrings:Default</c>'tan okunur; böylece kayıt sırasında (ör. build-zamanı OpenAPI üretimi)
    /// veritabanına bağlanılmaz. Bağlam, açılış migration'ı listesine de eklenir.
    /// </summary>
    public static IServiceCollection AddModuleDbContext<TContext>(this IServiceCollection services)
        where TContext : ModuleDbContext
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddDbContext<TContext>((provider, options) =>
            options.UseNpgsql(GetRequiredConnectionString(provider.GetRequiredService<IConfiguration>())));
        services.AddSingleton(new ModuleDbContextRegistration(typeof(TContext)));

        return services;
    }

    internal static string GetRequiredConnectionString(IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(DatabaseOptions.ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"Veritabanı bağlantı dizesi tanımlı değil: ConnectionStrings__{DatabaseOptions.ConnectionStringName} ortam değişkenini ayarlayın.");
        }

        return connectionString;
    }
}
