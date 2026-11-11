using SokagimdakiSorun.SharedKernel;

namespace SokagimdakiSorun.Identity.Contracts;

/// <summary><see cref="IReporterRegistry.FindOrCreateAsync"/> sonucu.</summary>
/// <param name="UserId">Şikâyetçinin kullanıcı kimliği.</param>
/// <param name="Created">Bu çağrı yeni bir kayıt oluşturduysa <see langword="true"/>.</param>
public sealed record ReporterRegistration(Guid UserId, bool Created);

/// <summary>
/// Dış kimlikten (ör. WhatsApp <c>wa_id</c>) şikâyetçiyi bul veya oluştur (docs/API_CONTRACT_STRATEGY.md §7.1).
/// Tüketen: Messaging.
/// </summary>
public interface IReporterRegistry
{
    /// <summary>Dış kimliğin azami uzunluğu.</summary>
    const int MaxExternalIdLength = 64;

    /// <summary>
    /// Idempotent ve eşzamanlılığa dayanıklıdır: aynı (<paramref name="provider"/>, <paramref name="externalId"/>)
    /// ile yapılan tüm çağrılar — eşzamanlı olanlar dâhil — aynı kullanıcıyı döndürür; veritabanında
    /// UNIQUE(provider, external_id) kısıtı tek kaydı garanti eder. Oluşturulan şikâyetçinin parolası yoktur ve panelde
    /// oturum açamaz. Geçersiz dış kimlik <c>identity.invalid_external_id</c> doğrulama hatası döner.
    /// </summary>
    /// <param name="provider">Dış kimlik sağlayıcısı.</param>
    /// <param name="externalId">Sağlayıcıdaki kimlik. Kişisel veridir; loglanmaz.</param>
    /// <param name="displayName">Yeni kayıt için isteğe bağlı görünen ad (mevcut kaydı değiştirmez).</param>
    /// <param name="cancellationToken">İptal belirteci.</param>
    Task<Result<ReporterRegistration>> FindOrCreateAsync(
        ExternalIdentityProvider provider, string externalId, string? displayName, CancellationToken cancellationToken);
}
