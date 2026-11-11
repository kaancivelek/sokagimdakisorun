using Microsoft.EntityFrameworkCore;
using SokagimdakiSorun.Identity.Contracts;
using SokagimdakiSorun.Identity.Infrastructure;

namespace SokagimdakiSorun.Identity.Application;

internal sealed class UserDirectory(IdentityModuleDbContext db) : IUserDirectory
{
    public async Task<UserSummary?> GetUserAsync(Guid userId, CancellationToken cancellationToken) =>
        await db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new UserSummary(u.Id, u.DisplayName, u.Kind, u.Status))
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, UserSummary>> GetUsersAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(userIds);
        if (userIds.Count == 0)
        {
            return new Dictionary<Guid, UserSummary>();
        }

        var ids = userIds.Distinct().ToArray();
        return await db.Users.AsNoTracking()
            .Where(u => ids.Contains(u.Id))
            .Select(u => new UserSummary(u.Id, u.DisplayName, u.Kind, u.Status))
            .ToDictionaryAsync(u => u.Id, cancellationToken);
    }
}
