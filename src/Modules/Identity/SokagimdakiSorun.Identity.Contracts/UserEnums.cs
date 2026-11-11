namespace SokagimdakiSorun.Identity.Contracts;

/// <summary>Kullanıcı türü (docs/DOMAIN_MODEL.md §3.1).</summary>
public enum UserKind
{
    /// <summary>Panel kullanıcısı (Admin, Moderator). E-posta + parola ile giriş yapar.</summary>
    Staff,

    /// <summary>Şikâyetçi. Parolası ve panel erişimi yoktur; dış kimlik (WhatsApp) ile bağlanır.</summary>
    Reporter,
}

/// <summary>Hesap durumu.</summary>
public enum UserStatus
{
    /// <summary>Etkin.</summary>
    Active,

    /// <summary>Askıya alınmış: oturum açamaz, mevcut oturumları geçersizdir.</summary>
    Suspended,

    /// <summary>Kişisel verisi silinmiş.</summary>
    Anonymized,
}

/// <summary>Dış kimlik sağlayıcısı.</summary>
public enum ExternalIdentityProvider
{
    /// <summary>WhatsApp; dış kimlik <c>wa_id</c>'dir.</summary>
    WhatsApp,
}
