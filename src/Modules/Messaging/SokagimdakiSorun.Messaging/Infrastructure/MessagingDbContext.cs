using Microsoft.EntityFrameworkCore;
using Npgsql;
using SokagimdakiSorun.Messaging.Domain;
using SokagimdakiSorun.Platform.Persistence;

namespace SokagimdakiSorun.Messaging.Infrastructure;

/// <summary>
/// <c>messaging</c> schema'sı (DOMAIN_MODEL §3.3). Tekrar koruması veritabanı kısıtlarıyla: inbox <c>dedup_key</c> UNIQUE (I-3),
/// gelen mesaj <c>provider_message_id</c> kısmi UNIQUE (I-2), giden mesaj <c>dedup_key</c> kısmi UNIQUE, konuşma başına tek aktif
/// intake (I-4), intake başına tek şikâyet. Identity/Complaints/Media kimlikleri skalerdir (FK yok); modül içi FK'ler RESTRICT.
/// </summary>
internal sealed class MessagingDbContext(DbContextOptions<MessagingDbContext> options) : ModuleDbContext(options)
{
    public const string SchemaName = "messaging";
    public const string ConversationContactUniqueIndex = "ux_conversations_business_contact";
    public const string ProviderMessageUniqueIndex = "ux_messages_provider_message_id";
    public const string OutboundDedupUniqueIndex = "ux_messages_dedup_key";
    public const string ActiveIntakeUniqueIndex = "ux_intake_sessions_active_conversation";
    public const string SubmittedComplaintUniqueIndex = "ux_intake_sessions_submitted_complaint_id";
    public const string InboxDedupUniqueIndex = "ux_webhook_inbox_events_dedup_key";

    public override string Schema => SchemaName;

    public DbSet<Conversation> Conversations => Set<Conversation>();

    public DbSet<Message> Messages => Set<Message>();

    public DbSet<IntakeSession> IntakeSessions => Set<IntakeSession>();

    public DbSet<WebhookInboxEvent> InboxEvents => Set<WebhookInboxEvent>();

    /// <summary>Konuşma satırını transaction sonuna kadar kilitler ve izlenen hâlini döner (yazıcılar arası sıralama).</summary>
    public async Task<Conversation?> LockConversationAsync(Guid id, CancellationToken cancellationToken) =>
        (await Conversations.FromSql($"SELECT * FROM messaging.conversations WHERE id = {id} FOR UPDATE")
            .ToListAsync(cancellationToken)).SingleOrDefault();

    public static bool IsUniqueViolation(DbUpdateException exception, string indexName) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg && pg.ConstraintName == indexName;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder); // schema + paylaşılan outbox eşlemesi

        modelBuilder.Entity<Conversation>(b =>
        {
            b.ToTable("conversations", t =>
            {
                t.HasCheckConstraint("ck_conversations_ids_not_blank",
                    "length(btrim(business_phone_number_id)) > 0 AND length(btrim(external_contact_id)) > 0");
                t.HasCheckConstraint("ck_conversations_awaiting_reply",
                    "awaiting_reply_complaint_id IS NULL OR awaiting_reply_changed_at IS NOT NULL");
            });
            b.HasKey(c => c.Id);
            b.Property(c => c.Id).ValueGeneratedNever();
            b.Property(c => c.Channel).HasMaxLength(16);
            b.Property(c => c.BusinessPhoneNumberId).HasMaxLength(Conversation.ExternalIdMaxLength).IsRequired();
            b.Property(c => c.ExternalContactId).HasMaxLength(Conversation.ExternalIdMaxLength).IsRequired();
            b.HasIndex(c => new { c.BusinessPhoneNumberId, c.ExternalContactId }).IsUnique().HasDatabaseName(ConversationContactUniqueIndex);
            b.HasIndex(c => c.ReporterUserId).HasDatabaseName("ix_conversations_reporter_user_id");
        });

        modelBuilder.Entity<Message>(b =>
        {
            b.ToTable("messages", t =>
            {
                t.HasCheckConstraint("ck_messages_direction_status",
                    "(direction = 'Inbound') = (delivery_status = 'Received')");
                t.HasCheckConstraint("ck_messages_inbound_provider_id",
                    "direction <> 'Inbound' OR provider_message_id IS NOT NULL");
                t.HasCheckConstraint("ck_messages_outbound_fields",
                    "direction <> 'Outbound' OR (dedup_key IS NOT NULL AND text_body IS NOT NULL)");
                t.HasCheckConstraint("ck_messages_outbound_pending_schedule",
                    "delivery_status NOT IN ('Queued', 'Sending') OR next_attempt_at IS NOT NULL");
                t.HasCheckConstraint("ck_messages_attempt_count", "attempt_count >= 0");
                t.HasCheckConstraint("ck_messages_coordinates_pair", "(latitude IS NULL) = (longitude IS NULL)");
                t.HasCheckConstraint("ck_messages_latitude", "latitude IS NULL OR (latitude >= -90 AND latitude <= 90)");
                t.HasCheckConstraint("ck_messages_longitude", "longitude IS NULL OR (longitude >= -180 AND longitude <= 180)");
            });
            b.HasKey(m => m.Id);
            b.Property(m => m.Id).ValueGeneratedNever();
            b.Property(m => m.Direction).HasMaxLength(16);
            b.Property(m => m.MessageType).HasMaxLength(16);
            b.Property(m => m.DeliveryStatus).HasMaxLength(16);
            b.Property(m => m.ProviderMessageId).HasMaxLength(Message.ProviderIdMaxLength);
            b.Property(m => m.ProviderMessageType).HasMaxLength(Message.ProviderTypeMaxLength);
            b.Property(m => m.TextBody).HasMaxLength(Message.TextMaxLength);
            b.Property(m => m.DedupKey).HasMaxLength(Message.DedupKeyMaxLength);
            b.Property(m => m.LastError).HasMaxLength(Message.LastErrorMaxLength);
            b.Property(m => m.ComplaintReference).HasMaxLength(Message.ComplaintReferenceMaxLength);

            b.HasIndex(m => m.ProviderMessageId).IsUnique()
                .HasFilter("provider_message_id IS NOT NULL").HasDatabaseName(ProviderMessageUniqueIndex);
            b.HasIndex(m => m.DedupKey).IsUnique()
                .HasFilter("dedup_key IS NOT NULL").HasDatabaseName(OutboundDedupUniqueIndex);
            b.HasIndex(m => m.NextAttemptAt)
                .HasFilter("direction = 'Outbound' AND delivery_status IN ('Queued', 'Sending')")
                .HasDatabaseName("ix_messages_outbound_pending");
            b.HasIndex(m => new { m.ConversationId, m.CreatedAt }).HasDatabaseName("ix_messages_conversation_created_at");
            b.HasIndex(m => m.ComplaintId).HasFilter("complaint_id IS NOT NULL").HasDatabaseName("ix_messages_complaint_id");
            b.HasIndex(m => m.IntakeSessionId).HasDatabaseName("ix_messages_intake_session_id");

            b.HasOne<Conversation>().WithMany().HasForeignKey(m => m.ConversationId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<IntakeSession>().WithMany().HasForeignKey(m => m.IntakeSessionId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<IntakeSession>(b =>
        {
            b.ToTable("intake_sessions", t =>
            {
                t.HasCheckConstraint("ck_intake_sessions_submitted",
                    "(state = 'Submitted') = (submitted_complaint_id IS NOT NULL)");
                t.HasCheckConstraint("ck_intake_sessions_completed_at",
                    $"(state IN ({IntakeStates.ActiveSqlList})) = (completed_at IS NULL)");
                t.HasCheckConstraint("ck_intake_sessions_coordinates_pair", "(latitude IS NULL) = (longitude IS NULL)");
                t.HasCheckConstraint("ck_intake_sessions_latitude", "latitude IS NULL OR (latitude >= -90 AND latitude <= 90)");
                t.HasCheckConstraint("ck_intake_sessions_longitude", "longitude IS NULL OR (longitude >= -180 AND longitude <= 180)");
                t.HasCheckConstraint("ck_intake_sessions_invalid_input_count", "invalid_input_count >= 0");
            });
            b.HasKey(s => s.Id);
            b.Property(s => s.Id).ValueGeneratedNever();
            b.Property(s => s.State).HasMaxLength(32);
            b.Property(s => s.DescriptionDraft).HasMaxLength(Complaints.Contracts.IComplaintIntake.MaxDescriptionLength);
            b.Property(s => s.AddressText).HasMaxLength(Complaints.Contracts.IComplaintIntake.MaxAddressLength);
            b.Property(s => s.CategoryMenu).HasField("_categoryMenu").UsePropertyAccessMode(PropertyAccessMode.Field)
                .HasColumnType("uuid[]").IsRequired();
            b.Property(s => s.MediaFileIds).HasField("_mediaFileIds").UsePropertyAccessMode(PropertyAccessMode.Field)
                .HasColumnType("uuid[]").IsRequired();
            b.Property(s => s.Version).IsRowVersion(); // Npgsql: uint + IsRowVersion → xmin
            b.Ignore(s => s.IsActive);

            b.HasIndex(s => s.ConversationId).IsUnique()
                .HasFilter($"state IN ({IntakeStates.ActiveSqlList})").HasDatabaseName(ActiveIntakeUniqueIndex);
            b.HasIndex(s => s.SubmittedComplaintId).IsUnique()
                .HasFilter("submitted_complaint_id IS NOT NULL").HasDatabaseName(SubmittedComplaintUniqueIndex);
            b.HasIndex(s => s.ExpiresAt)
                .HasFilter($"state IN ({IntakeStates.ActiveSqlList})").HasDatabaseName("ix_intake_sessions_active_expires_at");

            b.HasOne<Conversation>().WithMany().HasForeignKey(s => s.ConversationId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<WebhookInboxEvent>(b =>
        {
            b.ToTable("webhook_inbox_events", t =>
            {
                t.HasCheckConstraint("ck_webhook_inbox_events_processed_at",
                    "(processing_state = 'Processed') = (processed_at IS NOT NULL)");
                t.HasCheckConstraint("ck_webhook_inbox_events_attempt_count", "attempt_count >= 0");
                t.HasCheckConstraint("ck_webhook_inbox_events_dedup_key", "dedup_key ~ '^(msg|status):'");
            });
            b.HasKey(e => e.Id);
            b.Property(e => e.Id).ValueGeneratedNever();
            b.Property(e => e.Provider).HasMaxLength(16);
            b.Property(e => e.EventType).HasMaxLength(16);
            b.Property(e => e.ProcessingState).HasMaxLength(16);
            b.Property(e => e.DedupKey).HasMaxLength(WebhookInboxEvent.DedupKeyMaxLength).IsRequired();
            b.Property(e => e.Payload).HasColumnType("jsonb").IsRequired();
            b.Property(e => e.LastError).HasMaxLength(WebhookInboxEvent.LastErrorMaxLength);
            b.HasIndex(e => e.DedupKey).IsUnique().HasDatabaseName(InboxDedupUniqueIndex);
            b.HasIndex(e => e.NextAttemptAt)
                .HasFilter("processing_state IN ('Pending', 'Processing')").HasDatabaseName("ix_webhook_inbox_events_pending");
        });
    }
}

/// <summary><c>dotnet ef migrations</c> için tasarım-zamanı fabrikası.</summary>
internal sealed class MessagingDesignTimeDbContextFactory : ModuleDesignTimeDbContextFactory<MessagingDbContext>
{
    protected override MessagingDbContext Create(DbContextOptions<MessagingDbContext> options) => new(options);
}
