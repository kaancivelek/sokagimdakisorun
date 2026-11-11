using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SokagimdakiSorun.Identity.Contracts;
using SokagimdakiSorun.Identity.Domain;

namespace SokagimdakiSorun.Identity.Application;

/// <summary>
/// Panelde oturum açabilecek tek hesap türü: etkin panel kullanıcısı. <c>SignIn.RequireConfirmedAccount</c> açık
/// olduğundan <see cref="SignInManager{TUser}.CanSignInAsync"/> bu kuralı parola denetiminden önce uygular; şikâyetçi
/// (parolası olsa bile) ve askıya alınmış kullanıcı <c>NotAllowed</c> alır.
/// </summary>
internal sealed class StaffOnlyUserConfirmation : IUserConfirmation<User>
{
    public Task<bool> IsConfirmedAsync(UserManager<User> manager, User user) =>
        Task.FromResult(user.Kind == UserKind.Staff && user.Status == UserStatus.Active);
}

/// <summary>
/// Token'a yazılan claim'ler: kullanıcı kimliği, kullanıcı adı, güvenlik damgası, roller. Varsayılan fabrika yerine
/// kullanılır; böylece elenen <c>user_claims</c>/<c>role_claims</c> tabloları hiç sorgulanmaz. E-posta claim'i yazılmaz.
/// </summary>
internal sealed class StaffClaimsPrincipalFactory(UserManager<User> userManager, IOptions<IdentityOptions> options)
    : IUserClaimsPrincipalFactory<User>
{
    /// <summary>Panel oturumu kimliğinin kimlik doğrulama türü.</summary>
    public const string AuthenticationType = "SokagimdakiSorun.Staff";

    public async Task<ClaimsPrincipal> CreateAsync(User user)
    {
        ArgumentNullException.ThrowIfNull(user);
        var claimTypes = options.Value.ClaimsIdentity;

        var identity = new ClaimsIdentity(AuthenticationType, claimTypes.UserNameClaimType, claimTypes.RoleClaimType);
        identity.AddClaim(new Claim(claimTypes.UserIdClaimType, user.Id.ToString()));
        identity.AddClaim(new Claim(claimTypes.UserNameClaimType, user.UserName ?? string.Empty));
        identity.AddClaim(new Claim(claimTypes.SecurityStampClaimType, await userManager.GetSecurityStampAsync(user)));
        foreach (var role in await userManager.GetRolesAsync(user))
        {
            identity.AddClaim(new Claim(claimTypes.RoleClaimType, role));
        }

        return new ClaimsPrincipal(identity);
    }
}

/// <summary>
/// Varsayılan kimlik şeması. Yerleşik bearer token'ları opaktır ve sunucuda saklanmaz; Microsoft dokümanına göre çıkış
/// token'ı kendiliğinden geçersiz kılmaz (yol haritası V-5). Bu işleyici kimlik doğrulamayı yerleşik bearer şemasına
/// devreder ve token geçerliyse güvenlik damgasını ve hesabın oturum açabilirliğini veritabanından doğrular: çıkış,
/// askıya alma veya rol değişikliği damgayı yenilediğinde eski erişim token'ı hemen reddedilir (401). Token yoksa
/// veritabanına gidilmez.
/// </summary>
internal sealed class StaffSessionAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "StaffSession";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var bearer = await Context.AuthenticateAsync(IdentityConstants.BearerScheme);
        if (!bearer.Succeeded)
        {
            return bearer;
        }

        var signInManager = Context.RequestServices.GetRequiredService<SignInManager<User>>();
        var user = await signInManager.ValidateSecurityStampAsync(bearer.Principal);
        if (user is null || !await signInManager.CanSignInAsync(user) || await signInManager.UserManager.IsLockedOutAsync(user))
        {
            return AuthenticateResult.Fail("Oturum geçersiz kılınmış.");
        }

        return AuthenticateResult.Success(new AuthenticationTicket(bearer.Principal, bearer.Properties, SchemeName));
    }
}
