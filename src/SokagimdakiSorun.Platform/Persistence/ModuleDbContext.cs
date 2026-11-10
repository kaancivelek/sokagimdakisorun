using Microsoft.EntityFrameworkCore;
using SokagimdakiSorun.Platform.Outbox;

namespace SokagimdakiSorun.Platform.Persistence;

/// <summary>
/// Her modül <c>DbContext</c>'inin türediği taban (ADR-0002). Ortak konvansiyonları tek yerde uygular:
/// <list type="bullet">
///   <item>Varsayılan schema = <see cref="Schema"/>; migration geçmiş tablosu da bu schema'da (<c>__ef_migrations_history</c>).</item>
///   <item>Tablo, kolon, index ve kısıt adları <c>snake_case</c>.</item>
///   <item>Enum'lar metin olarak saklanır ve her enum kolonu için otomatik <c>CHECK</c> kısıtı eklenir.</item>
///   <item>Zaman: <see cref="DateTimeOffset"/> → <c>timestamptz</c> (Npgsql yalnızca UTC kabul eder).</item>
///   <item>Paylaşılan <c>platform.outbox_messages</c> tablosu modele migration dışı eklenir; modül yalnızca
///         <see cref="OutboxDbContextExtensions.AddIntegrationEvent"/> ile satır ekleyebilir.</item>
/// </list>
/// </summary>
public abstract class ModuleDbContext : DbContext
{
    /// <summary>Modül adına karşılık gelen migration geçmiş tablosu adı.</summary>
    public const string MigrationsHistoryTable = "__ef_migrations_history";

    protected ModuleDbContext(DbContextOptions options)
        : base(options)
    {
    }

    /// <summary>Modülün schema'sı (ör. <c>complaints</c>). Sabit bir değer döndürmelidir.</summary>
    public abstract string Schema { get; }

    /// <summary>
    /// Outbox tablosunun migration'ına sahip olan bağlam yalnızca <see cref="PlatformDbContext"/>'tir.
    /// Diğer tüm modül bağlamlarında tablo migration dışıdır ve yalnızca ekleme yapılabilir.
    /// </summary>
    internal virtual bool OwnsOutbox => false;

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);

        // Bağlantı dizesi kayıt sırasında (AddModuleDbContext / tasarım-zamanı fabrikası) verilir; burada yalnızca
        // her bağlam için aynı olması gereken ayarlar eklenir.
        optionsBuilder
            .UseNpgsql(npgsql => npgsql.MigrationsHistoryTable(MigrationsHistoryTable, Schema))
            .UseSnakeCaseNamingConvention();
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);

        configurationBuilder.Properties<Enum>().HaveConversion<string>();
        configurationBuilder.Conventions.Add(_ => new EnumCheckConstraintConvention());
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfiguration(new OutboxMessageConfiguration(excludeFromMigrations: !OwnsOutbox));
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        EnsureOutboxIsAppendOnly();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        EnsureOutboxIsAppendOnly();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    // ADR-0002 §3: modüllerin outbox tablosundaki tek yetkisi satır eklemektir.
    private void EnsureOutboxIsAppendOnly()
    {
        if (OwnsOutbox)
        {
            return;
        }

        foreach (var entry in ChangeTracker.Entries<OutboxMessage>())
        {
            if (entry.State is EntityState.Modified or EntityState.Deleted)
            {
                throw new InvalidOperationException(
                    $"{GetType().Name} outbox satırını değiştiremez veya silemez; modüller yalnızca satır ekleyebilir (ADR-0002 §3).");
            }
        }
    }
}
