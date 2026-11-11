using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SokagimdakiSorun.Identity.Contracts;
using SokagimdakiSorun.Identity.Domain;
using SokagimdakiSorun.Platform.Persistence;

namespace SokagimdakiSorun.Identity.Infrastructure;

/// <summary>
/// <c>identity</c> schema'sı. <c>IdentityDbContext</c> yerine <see cref="ModuleDbContext"/>'ten türer; yalnızca
/// kullanılan Identity tabloları eşlenir: <c>users</c>, <c>roles</c>, <c>user_roles</c>. Claims, logins ve tokens
/// tabloları elenmiştir (özel claims fabrikası bunları okumaz; harici giriş ve 2FA kullanılmaz).
/// </summary>
internal sealed class IdentityModuleDbContext(DbContextOptions<IdentityModuleDbContext> options) : ModuleDbContext(options)
{
    public const string ExternalIdentityUniqueIndex = "ux_external_identities_provider_external_id";

    public override string Schema => "identity";

    public DbSet<User> Users => Set<User>();

    public DbSet<Role> Roles => Set<Role>();

    public DbSet<IdentityUserRole<Guid>> UserRoles => Set<IdentityUserRole<Guid>>();

    public DbSet<ExternalIdentity> ExternalIdentities => Set<ExternalIdentity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<User>(b =>
        {
            b.ToTable("users", t => t.HasCheckConstraint(
                "ck_users_reporter_has_no_password", "user_kind <> 'Reporter' OR password_hash IS NULL"));
            b.HasKey(u => u.Id);
            b.Property(u => u.Kind).HasColumnName("user_kind").HasMaxLength(16);
            b.Property(u => u.Status).HasMaxLength(16);
            b.Property(u => u.DisplayName).HasMaxLength(User.DisplayNameMaxLength).IsRequired();
            b.Property(u => u.UserName).HasMaxLength(256).IsRequired();
            b.Property(u => u.NormalizedUserName).HasMaxLength(256).IsRequired();
            b.Property(u => u.Email).HasMaxLength(256);
            b.Property(u => u.NormalizedEmail).HasMaxLength(256);
            b.Property(u => u.ConcurrencyStamp).IsConcurrencyToken();
            b.HasIndex(u => u.NormalizedUserName).IsUnique().HasDatabaseName("ux_users_normalized_user_name");
            // E-posta benzersizliği veritabanında; şikâyetçilerin e-postası NULL'dır (PostgreSQL'de NULL'lar çakışmaz).
            b.HasIndex(u => u.NormalizedEmail).IsUnique().HasDatabaseName("ux_users_normalized_email");

            // Kullanılmayan Identity kolonları: telefon kimliği external_identities'tedir, 2FA yoktur.
            b.Ignore(u => u.PhoneNumber);
            b.Ignore(u => u.PhoneNumberConfirmed);
            b.Ignore(u => u.TwoFactorEnabled);
        });

        modelBuilder.Entity<Role>(b =>
        {
            b.ToTable("roles");
            b.HasKey(r => r.Id);
            b.Property(r => r.Name).HasMaxLength(256);
            b.Property(r => r.NormalizedName).HasMaxLength(256);
            b.Property(r => r.ConcurrencyStamp).IsConcurrencyToken();
            b.HasIndex(r => r.NormalizedName).IsUnique().HasDatabaseName("ux_roles_normalized_name");
            b.HasData(Role.Seed);
        });

        modelBuilder.Entity<IdentityUserRole<Guid>>(b =>
        {
            b.ToTable("user_roles");
            b.HasKey(ur => new { ur.UserId, ur.RoleId });
            b.HasOne<User>().WithMany().HasForeignKey(ur => ur.UserId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<Role>().WithMany().HasForeignKey(ur => ur.RoleId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ExternalIdentity>(b =>
        {
            b.ToTable("external_identities");
            b.HasKey(e => e.Id);
            b.Property(e => e.Provider).HasMaxLength(32);
            b.Property(e => e.ExternalId).HasMaxLength(IReporterRegistry.MaxExternalIdLength).IsRequired();
            b.HasIndex(e => new { e.Provider, e.ExternalId }).IsUnique().HasDatabaseName(ExternalIdentityUniqueIndex);
            b.HasIndex(e => e.UserId);
            b.HasOne<User>().WithMany().HasForeignKey(e => e.UserId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}

internal sealed class IdentityDesignTimeDbContextFactory : ModuleDesignTimeDbContextFactory<IdentityModuleDbContext>
{
    protected override IdentityModuleDbContext Create(DbContextOptions<IdentityModuleDbContext> options) => new(options);
}
