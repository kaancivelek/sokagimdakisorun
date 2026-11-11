namespace SokagimdakiSorun.Identity.Contracts;

/// <summary>Bir kullanıcının diğer modüllere açılan özeti. E-posta, telefon veya kimlik bilgisi içermez.</summary>
/// <param name="Id">Kullanıcı kimliği.</param>
/// <param name="DisplayName">Görünen ad.</param>
/// <param name="Kind">Kullanıcı türü.</param>
/// <param name="Status">Hesap durumu.</param>
public sealed record UserSummary(Guid Id, string DisplayName, UserKind Kind, UserStatus Status);

/// <summary>
/// Kimlikten kullanıcı özeti sorgulama (docs/API_CONTRACT_STRATEGY.md §7.1). Tüketenler: Complaints, Messaging, Reporting.
/// </summary>
public interface IUserDirectory
{
    /// <summary>Tek kullanıcı; yoksa <see langword="null"/>.</summary>
    Task<UserSummary?> GetUserAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>
    /// Toplu sorgu (tek veritabanı gidişi). Bulunamayan kimlikler sözlükte yer almaz.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, UserSummary>> GetUsersAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken);
}
