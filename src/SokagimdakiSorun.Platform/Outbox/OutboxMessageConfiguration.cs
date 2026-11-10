using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SokagimdakiSorun.Platform.Persistence;

namespace SokagimdakiSorun.Platform.Outbox;

/// <summary>
/// Outbox tablosunun tek eşlemesi. <see cref="PlatformDbContext"/> migration'ıyla birlikte, diğer modül
/// bağlamları <c>ExcludeFromMigrations</c> ile kullanır (ADR-0002 §3, yol haritası V-3). Kolon adları
/// bağlamın <c>snake_case</c> konvansiyonundan gelir; her iki kullanımda da aynıdır.
/// </summary>
internal sealed class OutboxMessageConfiguration(bool excludeFromMigrations) : IEntityTypeConfiguration<OutboxMessage>
{
    public const string TableName = "outbox_messages";
    public const string PendingIndexName = "ix_outbox_messages_pending";

    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable(TableName, PlatformDbContext.SchemaName, table =>
        {
            if (excludeFromMigrations)
            {
                table.ExcludeFromMigrations();
            }

            table.HasCheckConstraint("ck_outbox_messages_attempt_count", "attempt_count >= 0");
            table.HasCheckConstraint(
                "ck_outbox_messages_single_outcome", "processed_at IS NULL OR dead_lettered_at IS NULL");
        });

        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).ValueGeneratedNever();
        builder.Property(m => m.Type).HasMaxLength(OutboxMessage.TypeMaxLength).IsRequired();
        builder.Property(m => m.Payload).HasColumnType("jsonb").IsRequired();
        builder.Property(m => m.LastError).HasMaxLength(OutboxMessage.LastErrorMaxLength);

        // Dağıtıcının taradığı küme: işlenmemiş ve dead-letter olmamış satırlar, available_at sırasıyla.
        builder.HasIndex(m => m.AvailableAt)
            .HasDatabaseName(PendingIndexName)
            .HasFilter("processed_at IS NULL AND dead_lettered_at IS NULL");
    }
}
