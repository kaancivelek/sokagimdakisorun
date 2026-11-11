using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SokagimdakiSorun.Media.Domain;
using SokagimdakiSorun.Platform.Persistence;

namespace SokagimdakiSorun.Media.Infrastructure;

/// <summary>Media modülünün bağlamı: <c>media</c> schema'sı (ADR-0002, DOMAIN_MODEL §3.4).</summary>
internal sealed class MediaDbContext(DbContextOptions<MediaDbContext> options) : ModuleDbContext(options)
{
    public const string SchemaName = "media";

    public override string Schema => SchemaName;

    public DbSet<MediaFile> MediaFiles => Set<MediaFile>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder); // schema + paylaşılan outbox eşlemesi
        modelBuilder.ApplyConfiguration(new MediaFileConfiguration());
    }
}

internal sealed class MediaFileConfiguration : IEntityTypeConfiguration<MediaFile>
{
    public void Configure(EntityTypeBuilder<MediaFile> builder)
    {
        builder.ToTable("media_files", table =>
        {
            table.HasCheckConstraint("ck_media_files_size_bytes", "size_bytes > 0");
            table.HasCheckConstraint("ck_media_files_content_type", "content_type IN ('image/jpeg', 'image/png', 'image/webp')");
            table.HasCheckConstraint("ck_media_files_storage_key", "storage_key ~ '^[0-9a-f]{32}$'");
            table.HasCheckConstraint("ck_media_files_sha256", "sha256 ~ '^[0-9a-f]{64}$'");
            table.HasCheckConstraint(
                "ck_media_files_lifecycle",
                "(status = 'Temporary' AND committed_at IS NULL AND deleted_at IS NULL)"
                + " OR (status = 'Committed' AND committed_at IS NOT NULL AND deleted_at IS NULL)"
                + " OR (status = 'Deleted' AND deleted_at IS NOT NULL)");
        });

        builder.HasKey(f => f.Id);
        builder.Property(f => f.Id).ValueGeneratedNever();
        builder.Property(f => f.StorageProvider).HasMaxLength(32);
        builder.Property(f => f.StorageKey).HasMaxLength(64);
        builder.Property(f => f.ContentType).HasMaxLength(32);
        builder.Property(f => f.Sha256).HasMaxLength(64).IsFixedLength();
        builder.Property(f => f.Status).HasMaxLength(16);

        builder.HasIndex(f => f.StorageKey).IsUnique();

        // OrphanMediaCleaner: süresi dolan Temporary kayıtlarını tarar.
        builder.HasIndex(f => f.CreatedAt)
            .HasFilter("status = 'Temporary'")
            .HasDatabaseName("ix_media_files_temporary_created_at");
    }
}

/// <summary><c>dotnet ef migrations add</c> için (rehber §3).</summary>
internal sealed class MediaDesignTimeDbContextFactory : ModuleDesignTimeDbContextFactory<MediaDbContext>
{
    protected override MediaDbContext Create(DbContextOptions<MediaDbContext> options) => new(options);
}
