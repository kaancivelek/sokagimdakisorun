using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SokagimdakiSorun.Identity.Contracts;
using SokagimdakiSorun.Identity.Domain;
using SokagimdakiSorun.Identity.Infrastructure;
using SokagimdakiSorun.SharedKernel;

namespace SokagimdakiSorun.Identity.Application;

/// <summary>
/// Idempotent "bul veya oluştur". Eşzamanlılık güvencesi veritabanındadır: iki çağrı aynı anda eklemeye çalışırsa
/// UNIQUE(provider, external_id) ikinciyi 23505 ile reddeder; ikinci çağrı kendi transaction'ını (kullanıcı satırı dâhil)
/// geri alır ve kazanan kaydı okuyup döndürür.
/// </summary>
internal sealed class ReporterRegistry(
    IdentityModuleDbContext db,
    UserManager<User> userManager,
    TimeProvider time) : IReporterRegistry
{
    public static readonly Error InvalidExternalId = Error.Validation(
        "identity.invalid_external_id", "Dış kimlik boş olamaz ve en fazla 64 karakter olabilir.");

    public async Task<Result<ReporterRegistration>> FindOrCreateAsync(
        ExternalIdentityProvider provider, string externalId, string? displayName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(externalId) || externalId.Length > IReporterRegistry.MaxExternalIdLength
            || !Enum.IsDefined(provider))
        {
            return InvalidExternalId;
        }

        if (await FindUserIdAsync(provider, externalId, cancellationToken) is { } existing)
        {
            return new ReporterRegistration(existing, Created: false);
        }

        var now = time.GetUtcNow();
        var user = User.CreateReporter(displayName, now);

        var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await using (transaction.ConfigureAwait(false))
        {
            try
            {
                var created = await userManager.CreateAsync(user); // parolasız: şikâyetçi panelde oturum açamaz
                if (!created.Succeeded)
                {
                    throw new InvalidOperationException(
                        "Şikâyetçi kaydı oluşturulamadı: " + string.Join(", ", created.Errors.Select(e => e.Code)));
                }

                db.ExternalIdentities.Add(ExternalIdentity.Create(user.Id, provider, externalId, now));
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return new ReporterRegistration(user.Id, Created: true);
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException
            {
                SqlState: PostgresErrorCodes.UniqueViolation,
                ConstraintName: IdentityModuleDbContext.ExternalIdentityUniqueIndex,
            })
            {
                await transaction.RollbackAsync(cancellationToken);
                db.ChangeTracker.Clear();
            }
        }

        var winner = await FindUserIdAsync(provider, externalId, cancellationToken)
            ?? throw new InvalidOperationException("Benzersizlik ihlalinden sonra mevcut dış kimlik bulunamadı.");
        return new ReporterRegistration(winner, Created: false);
    }

    private Task<Guid?> FindUserIdAsync(ExternalIdentityProvider provider, string externalId, CancellationToken cancellationToken) =>
        db.ExternalIdentities.AsNoTracking()
            .Where(e => e.Provider == provider && e.ExternalId == externalId)
            .Select(e => (Guid?)e.UserId)
            .SingleOrDefaultAsync(cancellationToken);
}
