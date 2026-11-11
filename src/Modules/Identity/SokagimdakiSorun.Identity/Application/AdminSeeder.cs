using System.Reflection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SokagimdakiSorun.Identity.Contracts;
using SokagimdakiSorun.Identity.Domain;

namespace SokagimdakiSorun.Identity.Application;

/// <summary>İlk Admin'in tohumlanması: <c>Seed__AdminEmail</c>, <c>Seed__AdminPassword</c> (secret; yalnızca ortam değişkeni).</summary>
internal sealed class SeedOptions
{
    public const string SectionName = "Seed";

    public string? AdminEmail { get; set; }

    public string? AdminPassword { get; set; }
}

/// <summary>
/// Açılışta, Platform'un migration servisinden sonra çalışır (hosted service'ler kayıt sırasıyla başlar). İki anahtar da
/// tanımlıysa ve bu e-postayla kullanıcı yoksa Admin oluşturur; varsa hiçbir şeyi değiştirmez (parola sıfırlamaz).
/// Build-zamanı araçlarında (OpenAPI üretimi, <c>dotnet ef</c>) çalışmaz. Loglarda e-posta veya parola yer almaz.
/// </summary>
internal sealed partial class AdminSeeder(
    IServiceScopeFactory scopeFactory,
    IOptions<SeedOptions> options,
    TimeProvider time,
    ILogger<AdminSeeder> logger) : IHostedService
{
    private static readonly bool IsBuildTimeTooling =
        Assembly.GetEntryAssembly()?.GetName().Name is "GetDocument.Insider" or "ef";

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var email = options.Value.AdminEmail?.Trim();
        var password = options.Value.AdminPassword;
        if (IsBuildTimeTooling || string.IsNullOrEmpty(email) || string.IsNullOrEmpty(password))
        {
            return;
        }

        var scope = scopeFactory.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            if (await users.FindByEmailAsync(email) is not null)
            {
                return;
            }

            var admin = User.CreateStaff(email, "Yönetici", time.GetUtcNow());
            var result = await users.CreateAsync(admin, password);
            if (result.Succeeded)
            {
                result = await users.AddToRoleAsync(admin, IdentityRoles.Admin);
            }

            if (!result.Succeeded)
            {
                // Hata kodları kişisel veri içermez (ör. PasswordTooShort).
                throw new InvalidOperationException(
                    "İlk Admin tohumlanamadı (Seed__AdminEmail / Seed__AdminPassword): "
                    + string.Join(", ", result.Errors.Select(e => e.Code)));
            }

            LogAdminSeeded(logger, admin.Id);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Information, Message = "İlk Admin hesabı tohumlandı: {UserId}.")]
    private static partial void LogAdminSeeded(ILogger logger, Guid userId);
}
