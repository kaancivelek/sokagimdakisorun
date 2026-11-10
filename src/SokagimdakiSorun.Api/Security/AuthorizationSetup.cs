using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using SokagimdakiSorun.SharedKernel;

namespace SokagimdakiSorun.Api.Security;

/// <summary>
/// Varsayılan-reddet yetkilendirme iskeleti (docs/ARCHITECTURE.md §6): yetki meta verisi olmayan
/// her uç nokta kimliği doğrulanmış kullanıcı ister. Anonim uç noktalar <c>AllowAnonymous</c> ile
/// açıkça işaretlenir. Kimlik doğrulama şeması burada bağlanmaz; WS-04 (Identity) ekler.
/// </summary>
internal static class AuthorizationSetup
{
    public static IServiceCollection AddApiAuthorization(this IServiceCollection services)
    {
        services.AddAuthentication();
        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

        services.AddSingleton<IAuthorizationMiddlewareResultHandler, SchemeAwareAuthorizationResultHandler>();

        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUser, HttpContextCurrentUser>();

        return services;
    }

    /// <summary>
    /// Kimlik şeması henüz kayıtlı değilken çerçevenin varsayılan davranışı (challenge/forbid)
    /// exception fırlatır ve 500 üretir. Bu işleyici şema yoksa doğrudan 401/403 durumunu verir;
    /// gövdeyi durum kodu sayfaları Problem Details olarak yazar. Şema kayıtlıysa (WS-04 sonrası)
    /// çerçevenin varsayılan işleyicisine devreder.
    /// </summary>
    private sealed class SchemeAwareAuthorizationResultHandler(IAuthenticationSchemeProvider schemes) : IAuthorizationMiddlewareResultHandler
    {
        private readonly AuthorizationMiddlewareResultHandler _default = new();

        public async Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
        {
            if (authorizeResult.Challenged && policy.AuthenticationSchemes.Count == 0
                && await schemes.GetDefaultChallengeSchemeAsync() is null)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            if (authorizeResult.Forbidden && policy.AuthenticationSchemes.Count == 0
                && await schemes.GetDefaultForbidSchemeAsync() is null)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            await _default.HandleAsync(next, context, policy, authorizeResult);
        }
    }
}
