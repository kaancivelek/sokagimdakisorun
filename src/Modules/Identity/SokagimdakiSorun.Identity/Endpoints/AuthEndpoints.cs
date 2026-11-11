using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SokagimdakiSorun.Identity.Contracts;
using SokagimdakiSorun.Identity.Domain;
using SokagimdakiSorun.SharedKernel;

namespace SokagimdakiSorun.Identity.Endpoints;

/// <summary>Giriş isteği.</summary>
internal sealed record LoginRequest(string? Email, string? Password);

/// <summary>Token yenileme isteği.</summary>
internal sealed record RefreshRequest(string? RefreshToken);

/// <summary>Geçerli panel kullanıcısı.</summary>
internal sealed record CurrentUserResponse(Guid Id, string Email, string DisplayName, IReadOnlyList<string> Roles);

/// <summary>
/// Panel ↔ API oturum uç noktaları (ADR-0006). Token'lar ASP.NET Core Identity'nin yerleşik, opak bearer token'larıdır;
/// <c>MapIdentityApi</c> kullanılmaz çünkü kendi kendine kayıt (<c>/register</c>) ve kullanılmayan uç noktalar açar.
/// Yanıt biçimi yerleşik <see cref="AccessTokenResponse"/>'tur.
/// </summary>
internal static partial class AuthEndpoints
{
    /// <summary>Host'ta tanımlı hız sınırlayıcı politikası (RateLimitingSetup.AuthLoginPolicy).</summary>
    public const string RateLimitPolicy = "auth-login";

    private static readonly Error InvalidCredentials = Error.Unauthorized(
        "auth.invalid_credentials", "E-posta veya parola hatalı ya da hesap geçici olarak kilitli.");

    private static readonly Error InvalidRefreshToken = Error.Unauthorized(
        "auth.invalid_refresh_token", "Oturum yenilenemedi; yeniden giriş yapın.");

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/auth").WithTags("Auth");

        group.MapPost("/login", LoginAsync)
            .WithName("Login")
            .WithSummary("E-posta + parola ile giriş; erişim ve yenileme token'ı döner.")
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicy)
            .Produces<AccessTokenResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        group.MapPost("/refresh", RefreshAsync)
            .WithName("RefreshToken")
            .WithSummary("Yenileme token'ı ile yeni erişim ve yenileme token'ı alır.")
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicy)
            .Produces<AccessTokenResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        group.MapPost("/logout", LogoutAsync)
            .WithName("Logout")
            .WithSummary("Kullanıcının tüm erişim ve yenileme token'larını geçersiz kılar (güvenlik damgası yenilenir).")
            .RequireAuthorization(IdentityPolicies.Staff)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        group.MapGet("/me", GetCurrentUserAsync)
            .WithName("GetCurrentUser")
            .WithSummary("Geçerli panel kullanıcısı ve rolleri.")
            .RequireAuthorization(IdentityPolicies.Staff)
            .Produces<CurrentUserResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized);
    }

    private static async Task<IResult> LoginAsync(LoginRequest request, SignInManager<User> signInManager, ILoggerFactory loggers)
    {
        if (string.IsNullOrWhiteSpace(request.Email))
        {
            return ProblemResults.Validation("email", "E-posta zorunludur.");
        }

        if (string.IsNullOrEmpty(request.Password))
        {
            return ProblemResults.Validation("password", "Parola zorunludur.");
        }

        var user = await signInManager.UserManager.FindByEmailAsync(request.Email.Trim());
        if (user is null)
        {
            LogLoginFailed(loggers.CreateLogger(typeof(AuthEndpoints)), null, "unknown_account");
            return ProblemResults.From(InvalidCredentials);
        }

        // Bearer şemasıyla oturum açma: başarılıysa yerleşik işleyici AccessTokenResponse gövdesini kendisi yazar.
        signInManager.AuthenticationScheme = IdentityConstants.BearerScheme;
        var result = await signInManager.PasswordSignInAsync(user, request.Password, isPersistent: false, lockoutOnFailure: true);
        if (!result.Succeeded)
        {
            // Tüm başarısızlıklar (yanlış parola, kilit, panel erişimi olmayan hesap) aynı yanıtı alır: hesap sayımı yapılamaz.
            LogLoginFailed(loggers.CreateLogger(typeof(AuthEndpoints)), user.Id,
                result.IsLockedOut ? "locked_out" : result.IsNotAllowed ? "not_allowed" : "invalid_password");
            return ProblemResults.From(InvalidCredentials);
        }

        return TypedResults.Empty;
    }

    private static async Task<IResult> RefreshAsync(
        RefreshRequest request,
        SignInManager<User> signInManager,
        IOptionsMonitor<BearerTokenOptions> bearerOptions,
        TimeProvider time)
    {
        if (string.IsNullOrWhiteSpace(request.RefreshToken))
        {
            return ProblemResults.Validation("refreshToken", "Yenileme token'ı zorunludur.");
        }

        var protector = bearerOptions.Get(IdentityConstants.BearerScheme).RefreshTokenProtector;
        var ticket = protector.Unprotect(request.RefreshToken);

        // MapIdentityApi'deki /refresh ile aynı denetimler + hesabın hâlâ oturum açabilir olması.
        if (ticket?.Properties.ExpiresUtc is not { } expiresUtc
            || time.GetUtcNow() >= expiresUtc
            || await signInManager.ValidateSecurityStampAsync(ticket.Principal) is not { } user
            || !await signInManager.CanSignInAsync(user)
            || await signInManager.UserManager.IsLockedOutAsync(user))
        {
            return ProblemResults.From(InvalidRefreshToken);
        }

        var principal = await signInManager.CreateUserPrincipalAsync(user);
        return TypedResults.SignIn(principal, authenticationScheme: IdentityConstants.BearerScheme);
    }

    private static async Task<IResult> LogoutAsync(HttpContext context, UserManager<User> users)
    {
        if (await users.GetUserAsync(context.User) is not { } user)
        {
            return TypedResults.Unauthorized();
        }

        // Yerleşik bearer token'lar sunucuda saklanmaz; damga yenilenince bu kullanıcının tüm token'ları reddedilir.
        await users.UpdateSecurityStampAsync(user);
        return TypedResults.NoContent();
    }

    private static async Task<IResult> GetCurrentUserAsync(HttpContext context, UserManager<User> users)
    {
        if (await users.GetUserAsync(context.User) is not { } user)
        {
            return TypedResults.Unauthorized();
        }

        var roles = await users.GetRolesAsync(user);
        return TypedResults.Ok(new CurrentUserResponse(user.Id, user.Email ?? string.Empty, user.DisplayName, [.. roles.Order(StringComparer.Ordinal)]));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Başarısız giriş denemesi: kullanıcı {UserId}, neden {Reason}.")]
    private static partial void LogLoginFailed(ILogger logger, Guid? userId, string reason);
}
