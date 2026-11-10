using Microsoft.EntityFrameworkCore;
using SokagimdakiSorun.Platform.Persistence;
using SokagimdakiSorun.SharedKernel;

namespace SokagimdakiSorun.IntegrationTests.Database.SampleModule;

/// <summary>
/// Yalnızca testte var olan örnek bir modül bağlamı: gerçek bir modülün <see cref="ModuleDbContext"/>'ten
/// nasıl türeyeceğini taklit eder (kendi schema'sı, kendi tablosu, paylaşılan outbox'a yalnızca ekleme).
/// Üretim kodunda hiçbir modülün entity'si yazılmaz (WS-03 kapsam dışı).
/// </summary>
internal sealed class SampleModuleDbContext(DbContextOptions<SampleModuleDbContext> options) : ModuleDbContext(options)
{
    public const string SchemaName = "sample_module";

    public override string Schema => SchemaName;

    public DbSet<Widget> Widgets => Set<Widget>();

    public static SampleModuleDbContext Create(string connectionString) =>
        new(new DbContextOptionsBuilder<SampleModuleDbContext>().UseNpgsql(connectionString).Options);

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Widget>(widget =>
        {
            widget.HasKey(w => w.Id);
            widget.Property(w => w.DisplayName).HasMaxLength(100);
        });
    }
}

internal sealed class Widget
{
    public Guid Id { get; set; }

    public required string DisplayName { get; set; }

    public WidgetColor Color { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

internal enum WidgetColor
{
    Red,
    Green,
}

public sealed record WidgetCreated(Guid EventId, DateTimeOffset OccurredAt, Guid WidgetId, string Note) : IIntegrationEvent
{
    public static WidgetCreated New(DateTimeOffset occurredAt, string note = "örnek") =>
        new(Guid.CreateVersion7(), occurredAt, Guid.CreateVersion7(), note);
}
