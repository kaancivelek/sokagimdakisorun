using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace SokagimdakiSorun.Platform.Persistence;

/// <summary>
/// <c>dotnet ef migrations add</c> için tasarım-zamanı fabrikası tabanı. Modül projesinde tek satırlık
/// bir alt sınıf yeterlidir; migration üretimi veritabanına bağlanmaz (bağlantı dizesi yer tutucudur).
/// Host (<c>Program</c>) çalıştırılmadığı için API yapılandırmasına veya secret'a ihtiyaç duyulmaz.
/// </summary>
public abstract class ModuleDesignTimeDbContextFactory<TContext> : IDesignTimeDbContextFactory<TContext>
    where TContext : ModuleDbContext
{
    private const string DesignTimeConnectionString = "Host=design-time-only;Database=design-time-only";

    public TContext CreateDbContext(string[] args) =>
        Create(new DbContextOptionsBuilder<TContext>().UseNpgsql(DesignTimeConnectionString).Options);

    /// <summary>Bağlamı verilen seçeneklerle oluşturur (genellikle <c>new TContext(options)</c>).</summary>
    protected abstract TContext Create(DbContextOptions<TContext> options);
}

internal sealed class PlatformDesignTimeDbContextFactory : ModuleDesignTimeDbContextFactory<PlatformDbContext>
{
    protected override PlatformDbContext Create(DbContextOptions<PlatformDbContext> options) => new(options);
}
