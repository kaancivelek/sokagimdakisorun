namespace SokagimdakiSorun.Identity.Contracts;

/// <summary>Panel rolleri (docs/DOMAIN_MODEL.md §3.1). Şikâyetçinin rolü yoktur.</summary>
public static class IdentityRoles
{
    /// <summary>Yönetici: kullanıcı yönetimi ve tüm moderatör yetkileri.</summary>
    public const string Admin = "Admin";

    /// <summary>Moderatör: şikâyetleri kapsamı dâhilinde yönetir.</summary>
    public const string Moderator = "Moderator";
}

/// <summary>
/// Identity modülünün kaydettiği yetkilendirme politikalarının adları. Diğer modüller uç noktalarında
/// <c>.RequireAuthorization(IdentityPolicies.Staff)</c> biçiminde kullanır.
/// </summary>
public static class IdentityPolicies
{
    /// <summary>Yalnızca <see cref="IdentityRoles.Admin"/>.</summary>
    public const string Admin = "identity.admin";

    /// <summary>Panel kullanıcısı: <see cref="IdentityRoles.Admin"/> veya <see cref="IdentityRoles.Moderator"/>.</summary>
    public const string Staff = "identity.staff";
}
