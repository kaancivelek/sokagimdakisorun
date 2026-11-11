using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SokagimdakiSorun.Identity.Application;
using SokagimdakiSorun.Identity.Contracts;
using SokagimdakiSorun.Identity.Domain;
using SokagimdakiSorun.Identity.Endpoints;
using SokagimdakiSorun.Identity.Infrastructure;
using SokagimdakiSorun.Platform.Persistence;

namespace SokagimdakiSorun.Identity;

/// <summary>
/// Identity modülünün host tarafından çağrılan kayıt noktaları (docs/ARCHITECTURE.md §3).
/// </summary>
public static class IdentityModule
{
    /// <summary>Erişim token'ı ömrü. Kısa tutulur; panel (BFF) yenileme token'ıyla yeniler.</summary>
    public static readonly TimeSpan AccessTokenLifetime = TimeSpan.FromMinutes(15);

    /// <summary>Yenileme token'ı ömrü.</summary>
    public static readonly TimeSpan RefreshTokenLifetime = TimeSpan.FromDays(7);

    /// <summary>Hesap kilitlemeden önce izin verilen ardışık başarısız giriş sayısı.</summary>
    public const int MaxFailedAccessAttempts = 5;

    /// <summary>Kilit süresi.</summary>
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Identity modülünün DI kayıtlarını ekler: <c>identity</c> bağlamı, ASP.NET Core Identity çekirdeği, yerleşik bearer
    /// token kimlik şeması (varsayılan şema olarak; host'un varsayılan-reddet politikası bu şemayla çalışır),
    /// yetkilendirme politikaları, <see cref="IUserDirectory"/>, <see cref="IReporterRegistry"/> ve ilk Admin tohumlaması.
    /// Veritabanına kayıt sırasında bağlanılmaz.
    /// </summary>
    public static IServiceCollection AddIdentityModule(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddModuleDbContext<IdentityModuleDbContext>();
        services.TryAddSingleton(TimeProvider.System);

        // Varsayılan şema StaffSession: yerleşik bearer şemasına devreder + oturumu (güvenlik damgası, durum) doğrular.
        services.AddAuthentication(StaffSessionAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, StaffSessionAuthenticationHandler>(StaffSessionAuthenticationHandler.SchemeName, null)
            .AddBearerToken(IdentityConstants.BearerScheme, options =>
            {
                options.BearerTokenExpiration = AccessTokenLifetime;
                options.RefreshTokenExpiration = RefreshTokenLifetime;
            });

        services.AddIdentityCore<User>(options =>
            {
                options.User.RequireUniqueEmail = false; // şikâyetçinin e-postası yok; benzersizlik veritabanı index'inde
                options.SignIn.RequireConfirmedAccount = true; // → StaffOnlyUserConfirmation
                options.Password.RequiredLength = 12;
                options.Lockout.AllowedForNewUsers = true;
                options.Lockout.MaxFailedAccessAttempts = MaxFailedAccessAttempts;
                options.Lockout.DefaultLockoutTimeSpan = LockoutDuration;
            })
            .AddRoles<Role>()
            .AddEntityFrameworkStores<IdentityModuleDbContext>()
            .AddSignInManager();

        // Varsayılanların yerine (Replace: kayıt sırasından bağımsız tek uygulama).
        services.Replace(ServiceDescriptor.Scoped<IUserClaimsPrincipalFactory<User>, StaffClaimsPrincipalFactory>());
        services.Replace(ServiceDescriptor.Scoped<IUserConfirmation<User>, StaffOnlyUserConfirmation>());

        services.AddAuthorizationBuilder()
            .AddPolicy(IdentityPolicies.Admin, policy => policy.RequireRole(IdentityRoles.Admin))
            .AddPolicy(IdentityPolicies.Staff, policy => policy.RequireRole(IdentityRoles.Admin, IdentityRoles.Moderator));

        services.AddScoped<IUserDirectory, UserDirectory>();
        services.AddScoped<IReporterRegistry, ReporterRegistry>();

        // Platform'un migration servisinden sonra kaydedildiği için ondan sonra başlar.
        services.AddOptions<SeedOptions>().Bind(configuration.GetSection(SeedOptions.SectionName));
        services.AddHostedService<AdminSeeder>();

        return services;
    }

    /// <summary>Identity modülünün uç nokta gruplarını eşler: <c>/api/v1/auth</c>, <c>/api/v1/users</c>.</summary>
    public static IEndpointRouteBuilder MapIdentityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        AuthEndpoints.Map(endpoints);
        UserEndpoints.Map(endpoints);

        return endpoints;
    }
}
