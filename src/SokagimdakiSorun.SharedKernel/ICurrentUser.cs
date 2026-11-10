namespace SokagimdakiSorun.SharedKernel;

/// <summary>
/// Geçerli HTTP isteğinin kullanıcısı. Yalnızca HTTP isteği bağlamında geçerlidir;
/// arka plan işleyicileri aktörü açıkça parametre olarak geçirir
/// (docs/API_CONTRACT_STRATEGY.md §7.1).
/// </summary>
public interface ICurrentUser
{
    /// <summary>İstek kimliği doğrulanmış bir kullanıcıya mı ait.</summary>
    bool IsAuthenticated { get; }

    /// <summary>Kullanıcı kimliği; kimlik doğrulanmamışsa veya kimlik okunamıyorsa <see langword="null"/>.</summary>
    Guid? UserId { get; }

    /// <summary>Kullanıcı verilen role sahip mi.</summary>
    bool IsInRole(string role);
}
