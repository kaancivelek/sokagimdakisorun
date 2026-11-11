using Microsoft.AspNetCore.Identity;
using SokagimdakiSorun.Identity.Contracts;

namespace SokagimdakiSorun.Identity.Domain;

/// <summary>Panel rolü. Roller sabittir ve migration ile tohumlanır (RoleManager kullanılmaz).</summary>
internal sealed class Role : IdentityRole<Guid>
{
    public static readonly Guid AdminId = new("0195f0a0-0000-7000-8000-000000000001");
    public static readonly Guid ModeratorId = new("0195f0a0-0000-7000-8000-000000000002");

    public static IReadOnlyList<Role> Seed { get; } =
    [
        new() { Id = AdminId, Name = IdentityRoles.Admin, NormalizedName = "ADMIN", ConcurrencyStamp = "8d0a3b4e-6f2c-4f7a-9a51-3a0f4f6a2c01" },
        new() { Id = ModeratorId, Name = IdentityRoles.Moderator, NormalizedName = "MODERATOR", ConcurrencyStamp = "8d0a3b4e-6f2c-4f7a-9a51-3a0f4f6a2c02" },
    ];
}
