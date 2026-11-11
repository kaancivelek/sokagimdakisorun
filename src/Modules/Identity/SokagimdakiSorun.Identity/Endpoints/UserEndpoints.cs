using System.Net.Mail;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SokagimdakiSorun.Identity.Contracts;
using SokagimdakiSorun.Identity.Domain;
using SokagimdakiSorun.Identity.Infrastructure;
using SokagimdakiSorun.SharedKernel;

namespace SokagimdakiSorun.Identity.Endpoints;

/// <summary>Panel rolü (istek gövdelerinde).</summary>
internal enum StaffRole
{
    Admin,
    Moderator,
}

/// <summary>Panel kullanıcısı. Parola hash'i, güvenlik damgası veya token içermez.</summary>
internal sealed record UserResponse(
    Guid Id, string Email, string DisplayName, UserStatus Status, IReadOnlyList<string> Roles, bool IsLockedOut, DateTimeOffset CreatedAt);

/// <summary>Sayfalı liste zarfı (API_CONTRACT_STRATEGY §3).</summary>
internal sealed record UserPage(IReadOnlyList<UserResponse> Items, int Page, int PageSize, int TotalCount);

/// <summary>Panel kullanıcısı oluşturma. Rol verilmezse Moderator.</summary>
internal sealed record CreateUserRequest(string? Email, string? DisplayName, string? Password, StaffRole? Role);

/// <summary>Kısmi güncelleme: yalnızca verilen alanlar değişir.</summary>
internal sealed record UpdateUserRequest(StaffRole? Role, UserStatus? Status, string? DisplayName);

/// <summary>
/// Admin'e özel kullanıcı yönetimi (ADR-0006 §7: kendi kendine kayıt yok, moderatörü Admin oluşturur).
/// Yalnızca panel kullanıcılarını (Staff) yönetir; şikâyetçi kayıtları burada görünmez.
/// </summary>
internal static class UserEndpoints
{
    private const int DefaultPageSize = 25;
    private const int MaxPageSize = 100;

    private static readonly Error UserNotFound = Error.NotFound("user.not_found", "Kullanıcı bulunamadı.");
    private static readonly Error EmailTaken = Error.Conflict("user.email_taken", "Bu e-posta adresiyle bir kullanıcı zaten var.");
    private static readonly Error CannotModifySelf = Error.BusinessRule(
        "user.cannot_modify_self", "Kendi rolünüzü veya durumunuzu değiştiremezsiniz.");

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/users")
            .WithTags("Users")
            .RequireAuthorization(IdentityPolicies.Admin)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapGet("/", ListAsync)
            .WithName("ListUsers")
            .WithSummary("Panel kullanıcılarını listeler.")
            .Produces<UserPage>()
            .ProducesProblem(StatusCodes.Status400BadRequest);

        group.MapGet("/{id:guid}", GetAsync)
            .WithName("GetUser")
            .WithSummary("Tek panel kullanıcısı.")
            .Produces<UserResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/", CreateAsync)
            .WithName("CreateUser")
            .WithSummary("Panel kullanıcısı (varsayılan: Moderator) oluşturur.")
            .Produces<UserResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPatch("/{id:guid}", UpdateAsync)
            .WithName("UpdateUser")
            .WithSummary("Rol, durum veya görünen adı günceller. Rol/durum değişikliği kullanıcının oturumlarını kapatır.")
            .Produces<UserResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
    }

    private static async Task<IResult> ListAsync(
        IdentityModuleDbContext db, TimeProvider time, CancellationToken ct, int page = 1, int pageSize = DefaultPageSize)
    {
        if (page < 1)
        {
            return ProblemResults.Validation("page", "Sayfa 1 veya daha büyük olmalıdır.");
        }

        if (pageSize is < 1 or > MaxPageSize)
        {
            return ProblemResults.Validation("pageSize", $"Sayfa boyutu 1 ile {MaxPageSize} arasında olmalıdır.");
        }

        var query = db.Users.AsNoTracking().Where(u => u.Kind == UserKind.Staff);
        var total = await query.CountAsync(ct);
        var pageUsers = await query
            .OrderBy(u => u.CreatedAt).ThenBy(u => u.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(ct);

        var roles = await LoadRolesAsync(db, pageUsers.Select(u => u.Id).ToArray(), ct);
        var now = time.GetUtcNow();
        var items = pageUsers.Select(u => ToResponse(u, roles.GetValueOrDefault(u.Id) ?? [], now)).ToList();
        return TypedResults.Ok(new UserPage(items, page, pageSize, total));
    }

    private static async Task<IResult> GetAsync(Guid id, UserManager<User> users, TimeProvider time)
    {
        var user = await FindStaffAsync(users, id);
        return user is null ? ProblemResults.From(UserNotFound) : TypedResults.Ok(await ToResponseAsync(users, user, time));
    }

    private static async Task<IResult> CreateAsync(
        CreateUserRequest request, UserManager<User> users, IdentityModuleDbContext db, TimeProvider time, CancellationToken ct)
    {
        var email = request.Email?.Trim();
        if (string.IsNullOrEmpty(email) || !MailAddress.TryCreate(email, out _) || email.Length > 256)
        {
            return ProblemResults.Validation("email", "Geçerli bir e-posta adresi girin.");
        }

        var displayName = request.DisplayName?.Trim();
        if (string.IsNullOrEmpty(displayName) || displayName.Length > User.DisplayNameMaxLength)
        {
            return ProblemResults.Validation("displayName", $"Görünen ad zorunludur ve en fazla {User.DisplayNameMaxLength} karakter olabilir.");
        }

        if (string.IsNullOrEmpty(request.Password))
        {
            return ProblemResults.Validation("password", "Parola zorunludur.");
        }

        if (await users.FindByEmailAsync(email) is not null)
        {
            return ProblemResults.From(EmailTaken);
        }

        var user = User.CreateStaff(email, displayName, time.GetUtcNow());
        var transaction = await db.Database.BeginTransactionAsync(ct);
        await using (transaction.ConfigureAwait(false))
        {
            IdentityResult result;
            try
            {
                result = await users.CreateAsync(user, request.Password);
                if (result.Succeeded)
                {
                    result = await users.AddToRoleAsync(user, RoleName(request.Role ?? StaffRole.Moderator));
                }
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                return ProblemResults.From(EmailTaken);
            }

            if (!result.Succeeded)
            {
                return ProblemResults.From(ToError(result));
            }

            await transaction.CommitAsync(ct);
        }

        return TypedResults.Created($"/api/v1/users/{user.Id}", await ToResponseAsync(users, user, time));
    }

    private static async Task<IResult> UpdateAsync(
        Guid id, UpdateUserRequest request, HttpContext context, UserManager<User> users, IdentityModuleDbContext db, TimeProvider time, CancellationToken ct)
    {
        if (request.Status == UserStatus.Anonymized)
        {
            return ProblemResults.Validation("status", "Durum yalnızca Active veya Suspended olabilir.");
        }

        var displayName = request.DisplayName?.Trim();
        if (request.DisplayName is not null && (string.IsNullOrEmpty(displayName) || displayName.Length > User.DisplayNameMaxLength))
        {
            return ProblemResults.Validation("displayName", $"Görünen ad boş olamaz ve en fazla {User.DisplayNameMaxLength} karakter olabilir.");
        }

        var user = await FindStaffAsync(users, id);
        if (user is null)
        {
            return ProblemResults.From(UserNotFound);
        }

        var currentRoles = await users.GetRolesAsync(user);
        var targetRole = request.Role is { } role ? RoleName(role) : null;
        var roleChanges = targetRole is not null && !(currentRoles.Count == 1 && currentRoles[0] == targetRole);
        var statusChanges = request.Status is { } status && status != user.Status;

        if ((roleChanges || statusChanges) && users.GetUserId(context.User) == user.Id.ToString())
        {
            return ProblemResults.From(CannotModifySelf);
        }

        var transaction = await db.Database.BeginTransactionAsync(ct);
        await using (transaction.ConfigureAwait(false))
        {
            if (displayName is not null)
            {
                user.DisplayName = displayName;
            }

            if (statusChanges)
            {
                user.SetStatus(request.Status!.Value);
            }

            var result = await users.UpdateAsync(user);
            if (result.Succeeded && roleChanges)
            {
                result = await users.RemoveFromRolesAsync(user, currentRoles);
                if (result.Succeeded)
                {
                    result = await users.AddToRoleAsync(user, targetRole!);
                }
            }

            if (result.Succeeded && (roleChanges || statusChanges))
            {
                // Mevcut token'lardaki roller/durum artık geçersiz: kullanıcının tüm oturumları kapanır.
                result = await users.UpdateSecurityStampAsync(user);
            }

            if (!result.Succeeded)
            {
                return ProblemResults.From(ToError(result));
            }

            await transaction.CommitAsync(ct);
        }

        return TypedResults.Ok(await ToResponseAsync(users, user, time));
    }

    private static async Task<User?> FindStaffAsync(UserManager<User> users, Guid id) =>
        await users.FindByIdAsync(id.ToString()) is { Kind: UserKind.Staff } user ? user : null;

    private static async Task<Dictionary<Guid, List<string>>> LoadRolesAsync(IdentityModuleDbContext db, Guid[] userIds, CancellationToken ct)
    {
        var pairs = await db.UserRoles.AsNoTracking()
            .Where(ur => userIds.Contains(ur.UserId))
            .Join(db.Roles, ur => ur.RoleId, r => r.Id, (ur, r) => new { ur.UserId, r.Name })
            .ToListAsync(ct);
        return pairs.GroupBy(p => p.UserId)
            .ToDictionary(g => g.Key, g => g.Select(p => p.Name!).Order(StringComparer.Ordinal).ToList());
    }

    private static async Task<UserResponse> ToResponseAsync(UserManager<User> users, User user, TimeProvider time) =>
        ToResponse(user, [.. (await users.GetRolesAsync(user)).Order(StringComparer.Ordinal)], time.GetUtcNow());

    private static UserResponse ToResponse(User user, IReadOnlyList<string> roles, DateTimeOffset now) => new(
        user.Id, user.Email ?? string.Empty, user.DisplayName, user.Status, roles,
        IsLockedOut: user.LockoutEnd is { } end && end > now, user.CreatedAt);

    private static string RoleName(StaffRole role) => role == StaffRole.Admin ? IdentityRoles.Admin : IdentityRoles.Moderator;

    private static Error ToError(IdentityResult result)
    {
        if (result.Errors.Any(e => e.Code is "DuplicateUserName" or "DuplicateEmail"))
        {
            return EmailTaken;
        }

        var errors = result.Errors
            .GroupBy(e => e.Code.StartsWith("Password", StringComparison.Ordinal) ? "password"
                : e.Code is "InvalidEmail" or "InvalidUserName" ? "email" : "user")
            .ToDictionary(g => g.Key, g => g.Select(e => e.Description).ToArray());
        return Error.Validation("validation.failed", "İstek doğrulanamadı.", errors);
    }
}
