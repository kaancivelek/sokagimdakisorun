using Microsoft.EntityFrameworkCore;
using SokagimdakiSorun.Complaints.Contracts;
using SokagimdakiSorun.Complaints.Domain;
using SokagimdakiSorun.Platform.Persistence;

namespace SokagimdakiSorun.Complaints.Infrastructure;

/// <summary>
/// <c>complaints</c> schema'sı (DOMAIN_MODEL §3.2). Modül içi FK'ler veritabanında tanımlıdır; hiçbiri cascade delete değildir
/// (§4.5 kural 5). Identity/Media/Messaging kimlikleri skalerdir (FK yok). Durum geçmişi yalnızca eklenir.
/// </summary>
internal sealed class ComplaintsDbContext(DbContextOptions<ComplaintsDbContext> options) : ModuleDbContext(options)
{
    public const string SchemaName = "complaints";
    public const string IntakeSessionUniqueIndex = "ux_complaints_source_intake_session_id";
    public const string PublicReferenceUniqueIndex = "ux_complaints_public_reference";
    public const string ActiveAssignmentUniqueIndex = "ux_complaint_assignments_active";
    public const string HistorySourceMessageUniqueIndex = "ux_complaint_status_history_source_message_id";
    public const string AttachmentMediaUniqueIndex = "ux_complaint_attachments_media_file_id";
    public const string CategoryCodeUniqueIndex = "ux_complaint_categories_code";
    public const string ServiceUnitCodeUniqueIndex = "ux_service_units_code";

    public override string Schema => SchemaName;

    public DbSet<Complaint> Complaints => Set<Complaint>();

    public DbSet<ComplaintStatusHistoryEntry> StatusHistory => Set<ComplaintStatusHistoryEntry>();

    public DbSet<ComplaintAssignment> Assignments => Set<ComplaintAssignment>();

    public DbSet<ComplaintComment> Comments => Set<ComplaintComment>();

    public DbSet<ComplaintAttachment> Attachments => Set<ComplaintAttachment>();

    public DbSet<ComplaintCategory> Categories => Set<ComplaintCategory>();

    public DbSet<ServiceUnit> ServiceUnits => Set<ServiceUnit>();

    public DbSet<ServiceUnitMember> ServiceUnitMembers => Set<ServiceUnitMember>();

    public DbSet<RoutingRule> RoutingRules => Set<RoutingRule>();

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        EnsureHistoryIsAppendOnly();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        EnsureHistoryIsAppendOnly();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder); // schema + paylaşılan outbox eşlemesi

        modelBuilder.Entity<Complaint>(b =>
        {
            b.ToTable("complaints", t =>
            {
                t.HasCheckConstraint("ck_complaints_public_reference", $"public_reference ~ '{PublicReferenceGenerator.Pattern}'");
                t.HasCheckConstraint("ck_complaints_not_duplicate_of_self", "duplicate_of_complaint_id IS NULL OR duplicate_of_complaint_id <> id");
                t.HasCheckConstraint("ck_complaints_duplicate_target",
                    "(status = 'Duplicate') = (duplicate_of_complaint_id IS NOT NULL)");
                t.HasCheckConstraint("ck_complaints_latitude", "latitude IS NULL OR (latitude >= -90 AND latitude <= 90)");
                t.HasCheckConstraint("ck_complaints_longitude", "longitude IS NULL OR (longitude >= -180 AND longitude <= 180)");
                t.HasCheckConstraint("ck_complaints_coordinates_pair", "(latitude IS NULL) = (longitude IS NULL)");
                t.HasCheckConstraint("ck_complaints_location_present",
                    "latitude IS NOT NULL OR (address_text IS NOT NULL AND length(btrim(address_text)) > 0)");
                t.HasCheckConstraint("ck_complaints_description_not_blank", "length(btrim(description)) > 0");
                t.HasCheckConstraint("ck_complaints_awaiting_entry",
                    "(status = 'AwaitingInformation' AND awaiting_information_entry_status IN ('UnderReview', 'InProgress')) "
                    + "OR (status <> 'AwaitingInformation' AND awaiting_information_entry_status IS NULL)");
                t.HasCheckConstraint("ck_complaints_resolved_at", "status <> 'Resolved' OR resolved_at IS NOT NULL");
                t.HasCheckConstraint("ck_complaints_closed_at", "status <> 'Closed' OR closed_at IS NOT NULL");
            });
            b.HasKey(c => c.Id);
            b.Property(c => c.PublicReference).HasMaxLength(16).IsRequired();
            b.Property(c => c.Description).HasMaxLength(IComplaintIntake.MaxDescriptionLength).IsRequired();
            b.Property(c => c.AddressText).HasMaxLength(IComplaintIntake.MaxAddressLength);
            b.Property(c => c.Status).HasMaxLength(32);
            b.Property(c => c.AwaitingInformationEntryStatus).HasMaxLength(32);
            b.Property(c => c.SourceChannel).HasMaxLength(16);
            b.Property(c => c.LocationSource).HasMaxLength(16);
            b.Property(c => c.Version).IsRowVersion(); // Npgsql: uint + IsRowVersion → xmin (V-6)

            b.HasIndex(c => c.PublicReference).IsUnique().HasDatabaseName(PublicReferenceUniqueIndex);
            b.HasIndex(c => c.SourceIntakeSessionId).IsUnique()
                .HasFilter("source_intake_session_id IS NOT NULL").HasDatabaseName(IntakeSessionUniqueIndex);
            b.HasIndex(c => new { c.Status, c.CreatedAt }).IsDescending(false, true).HasDatabaseName("ix_complaints_status_created_at");
            b.HasIndex(c => new { c.CategoryId, c.Status, c.CreatedAt }).IsDescending(false, false, true)
                .HasDatabaseName("ix_complaints_category_status_created_at");
            b.HasIndex(c => c.ReporterUserId).HasDatabaseName("ix_complaints_reporter_user_id");
            b.HasIndex(c => c.DuplicateOfComplaintId).HasDatabaseName("ix_complaints_duplicate_of_complaint_id");

            b.HasOne<ComplaintCategory>().WithMany().HasForeignKey(c => c.CategoryId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Complaint>().WithMany().HasForeignKey(c => c.DuplicateOfComplaintId).OnDelete(DeleteBehavior.Restrict);

            b.HasMany(c => c.History).WithOne().HasForeignKey(h => h.ComplaintId).OnDelete(DeleteBehavior.Restrict);
            b.HasMany(c => c.Assignments).WithOne().HasForeignKey(a => a.ComplaintId).OnDelete(DeleteBehavior.Restrict);
            b.HasMany(c => c.Comments).WithOne().HasForeignKey(x => x.ComplaintId).OnDelete(DeleteBehavior.Restrict);
            b.HasMany(c => c.Attachments).WithOne().HasForeignKey(x => x.ComplaintId).OnDelete(DeleteBehavior.Restrict);
            b.Navigation(c => c.History).HasField("_history").UsePropertyAccessMode(PropertyAccessMode.Field);
            b.Navigation(c => c.Assignments).HasField("_assignments").UsePropertyAccessMode(PropertyAccessMode.Field);
            b.Navigation(c => c.Comments).HasField("_comments").UsePropertyAccessMode(PropertyAccessMode.Field);
            b.Navigation(c => c.Attachments).HasField("_attachments").UsePropertyAccessMode(PropertyAccessMode.Field);
            b.Ignore(c => c.ActiveAssignment);
            b.Ignore(c => c.PendingChanges);
        });

        modelBuilder.Entity<ComplaintStatusHistoryEntry>(b =>
        {
            b.ToTable("complaint_status_history", t =>
            {
                t.HasCheckConstraint("ck_complaint_status_history_actor",
                    "(actor_kind = 'System') = (actor_user_id IS NULL)");
            });
            b.HasKey(h => h.Id);
            b.Property(h => h.FromStatus).HasMaxLength(32);
            b.Property(h => h.ToStatus).HasMaxLength(32);
            b.Property(h => h.ActorKind).HasMaxLength(16);
            b.Property(h => h.Source).HasMaxLength(16);
            b.Property(h => h.Reason).HasMaxLength(ComplaintStatusHistoryEntry.MaxTextLength);
            b.Property(h => h.ReporterNote).HasMaxLength(ComplaintStatusHistoryEntry.MaxTextLength);
            b.HasIndex(h => new { h.ComplaintId, h.OccurredAt }).HasDatabaseName("ix_complaint_status_history_complaint_id_occurred_at");
            b.HasIndex(h => h.SourceMessageId).IsUnique()
                .HasFilter("source_message_id IS NOT NULL").HasDatabaseName(HistorySourceMessageUniqueIndex);
        });

        modelBuilder.Entity<ComplaintAssignment>(b =>
        {
            b.ToTable("complaint_assignments", t =>
                t.HasCheckConstraint("ck_complaint_assignments_unassigned_after_assigned",
                    "unassigned_at IS NULL OR unassigned_at >= assigned_at"));
            b.HasKey(a => a.Id);
            b.Property(a => a.Reason).HasMaxLength(ComplaintAssignment.MaxReasonLength);
            b.Property(a => a.UnassignReason).HasMaxLength(ComplaintAssignment.MaxReasonLength);
            b.Ignore(a => a.IsActive);
            b.HasIndex(a => a.ComplaintId).IsUnique().HasFilter("unassigned_at IS NULL").HasDatabaseName(ActiveAssignmentUniqueIndex);
            b.HasIndex(a => new { a.ServiceUnitId, a.ComplaintId }).HasFilter("unassigned_at IS NULL")
                .HasDatabaseName("ix_complaint_assignments_active_service_unit");
            b.HasIndex(a => new { a.ComplaintId, a.AssignedAt }).HasDatabaseName("ix_complaint_assignments_complaint_id_assigned_at");
            b.HasOne<ServiceUnit>().WithMany().HasForeignKey(a => a.ServiceUnitId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ComplaintComment>(b =>
        {
            b.ToTable("complaint_comments", t =>
                t.HasCheckConstraint("ck_complaint_comments_body_not_blank", "length(btrim(body)) > 0"));
            b.HasKey(c => c.Id);
            b.Property(c => c.Body).HasMaxLength(ComplaintComment.MaxBodyLength).IsRequired();
            b.Property(c => c.AuthorKind).HasMaxLength(16);
            b.Property(c => c.Visibility).HasMaxLength(16);
            b.Property(c => c.Source).HasMaxLength(16);
            b.HasIndex(c => new { c.ComplaintId, c.CreatedAt }).HasDatabaseName("ix_complaint_comments_complaint_id_created_at");
        });

        modelBuilder.Entity<ComplaintAttachment>(b =>
        {
            b.ToTable("complaint_attachments");
            b.HasKey(a => a.Id);
            b.Property(a => a.Source).HasMaxLength(32);
            b.HasIndex(a => a.MediaFileId).IsUnique().HasDatabaseName(AttachmentMediaUniqueIndex);
            b.HasIndex(a => new { a.ComplaintId, a.CreatedAt }).HasDatabaseName("ix_complaint_attachments_complaint_id_created_at");
        });

        modelBuilder.Entity<ComplaintCategory>(b =>
        {
            b.ToTable("complaint_categories", t =>
            {
                t.HasCheckConstraint("ck_complaint_categories_code", $"code ~ '{ComplaintCategory.CodePattern}'");
                t.HasCheckConstraint("ck_complaint_categories_name_not_blank", "length(btrim(name)) > 0");
            });
            b.HasKey(c => c.Id);
            b.Property(c => c.Code).HasMaxLength(ComplaintCategory.MaxCodeLength).IsRequired();
            b.Property(c => c.Name).HasMaxLength(ComplaintCategory.MaxNameLength).IsRequired();
            b.Property(c => c.Description).HasMaxLength(ComplaintCategory.MaxDescriptionLength);
            b.HasIndex(c => c.Code).IsUnique().HasDatabaseName(CategoryCodeUniqueIndex);
        });

        modelBuilder.Entity<ServiceUnit>(b =>
        {
            b.ToTable("service_units", t =>
            {
                t.HasCheckConstraint("ck_service_units_code", $"code ~ '{ComplaintCategory.CodePattern}'");
                t.HasCheckConstraint("ck_service_units_name_not_blank", "length(btrim(name)) > 0");
            });
            b.HasKey(u => u.Id);
            b.Property(u => u.Code).HasMaxLength(ServiceUnit.MaxCodeLength).IsRequired();
            b.Property(u => u.Name).HasMaxLength(ServiceUnit.MaxNameLength).IsRequired();
            b.HasIndex(u => u.Code).IsUnique().HasDatabaseName(ServiceUnitCodeUniqueIndex);
            b.HasMany(u => u.Members).WithOne().HasForeignKey(m => m.ServiceUnitId).OnDelete(DeleteBehavior.Restrict);
            b.Navigation(u => u.Members).HasField("_members").UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<ServiceUnitMember>(b =>
        {
            b.ToTable("service_unit_members");
            b.HasKey(m => new { m.ServiceUnitId, m.UserId });
            b.HasIndex(m => m.UserId).HasDatabaseName("ix_service_unit_members_user_id");
        });

        modelBuilder.Entity<RoutingRule>(b =>
        {
            b.ToTable("routing_rules");
            b.HasKey(r => r.Id);
            b.Property(r => r.District).HasMaxLength(RoutingRule.MaxDistrictLength);
            b.HasIndex(r => new { r.CategoryId, r.ServiceUnitId }).IsUnique().HasDatabaseName("ux_routing_rules_category_service_unit");
            b.HasIndex(r => r.ServiceUnitId).HasDatabaseName("ix_routing_rules_service_unit_id");
            b.HasOne<ComplaintCategory>().WithMany().HasForeignKey(r => r.CategoryId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<ServiceUnit>().WithMany().HasForeignKey(r => r.ServiceUnitId).OnDelete(DeleteBehavior.Restrict);
        });
    }

    // DOMAIN_MODEL §3.2: geçmiş yalnızca eklenir; güncellenmez, silinmez.
    private void EnsureHistoryIsAppendOnly()
    {
        foreach (var entry in ChangeTracker.Entries<ComplaintStatusHistoryEntry>())
        {
            if (entry.State is EntityState.Modified or EntityState.Deleted)
            {
                throw new InvalidOperationException("Durum geçmişi satırı değiştirilemez veya silinemez (DOMAIN_MODEL §4.4).");
            }
        }
    }
}

internal sealed class ComplaintsDesignTimeDbContextFactory : ModuleDesignTimeDbContextFactory<ComplaintsDbContext>
{
    protected override ComplaintsDbContext Create(DbContextOptions<ComplaintsDbContext> options) => new(options);
}
