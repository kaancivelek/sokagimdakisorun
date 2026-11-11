using Microsoft.AspNetCore.Identity;
using SokagimdakiSorun.Identity.Contracts;

namespace SokagimdakiSorun.Identity.Domain;

/// <summary>
/// ASP.NET Core Identity kullanıcısı + proje alanları (docs/DOMAIN_MODEL.md §3.1). Panel kullanıcısı (Staff) ve
/// şikâyetçi (Reporter) aynı tabloda tutulur; şikâyetçinin parola hash'i yoktur (veritabanı CHECK kısıtıyla da korunur).
/// </summary>
internal sealed class User : IdentityUser<Guid>
{
    public const int DisplayNameMaxLength = 100;
    public const string DefaultReporterDisplayName = "WhatsApp kullanıcısı";

    private User()
    {
    }

    public UserKind Kind { get; private set; }

    public UserStatus Status { get; private set; }

    public string DisplayName { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; private set; }

    public static User CreateStaff(string email, string displayName, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        UserName = email,
        Email = email,
        EmailConfirmed = true,
        DisplayName = displayName,
        Kind = UserKind.Staff,
        Status = UserStatus.Active,
        LockoutEnabled = true,
        CreatedAt = now,
    };

    public static User CreateReporter(string? displayName, DateTimeOffset now)
    {
        var id = Guid.CreateVersion7();
        return new User
        {
            Id = id,
            // Identity kullanıcı adını zorunlu tutar; şikâyetçi için anlamsız, benzersiz bir değer üretilir.
            UserName = $"reporter-{id:N}",
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? DefaultReporterDisplayName : Truncate(displayName.Trim()),
            Kind = UserKind.Reporter,
            Status = UserStatus.Active,
            LockoutEnabled = false,
            CreatedAt = now,
        };
    }

    public void SetStatus(UserStatus status) => Status = status;

    private static string Truncate(string value) =>
        value.Length <= DisplayNameMaxLength ? value : value[..DisplayNameMaxLength];
}
