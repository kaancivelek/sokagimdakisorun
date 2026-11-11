using System.Net;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SokagimdakiSorun.Identity.Contracts;
using SokagimdakiSorun.Identity.Domain;
using SokagimdakiSorun.Identity.Infrastructure;
using SokagimdakiSorun.IntegrationTests.Database.Support;

namespace SokagimdakiSorun.IntegrationTests.Identity;

/// <summary>
/// WS-04: <see cref="IReporterRegistry"/> (idempotent, eşzamanlılığa dayanıklı), <see cref="IUserDirectory"/> ve
/// şikâyetçinin panelde oturum açamaması — gerçek PostgreSQL 18 üzerinde.
/// </summary>
[Collection(DatabaseCollection.Name)]
[Trait("Category", "Database")]
public sealed class ReporterTests(PostgresFixture postgres)
{
    private const string WaId = "905551112233";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Concurrent_find_or_create_with_same_external_id_creates_single_reporter()
    {
        await using var host = await IdentityTestHost.StartAsync(postgres);
        const int callers = 12;
        using var start = new SemaphoreSlim(0);

        var calls = Enumerable.Range(0, callers).Select(async _ =>
        {
            await using var scope = host.CreateScope(); // her çağrı ayrı DbContext / bağlantı
            var registry = scope.ServiceProvider.GetRequiredService<IReporterRegistry>();
            await start.WaitAsync(Ct);
            return await registry.FindOrCreateAsync(ExternalIdentityProvider.WhatsApp, WaId, "Ayşe", Ct);
        }).ToList();
        start.Release(callers);
        var results = await Task.WhenAll(calls);

        Assert.All(results, r => Assert.True(r.IsSuccess));
        var userId = Assert.Single(results.Select(r => r.Value.UserId).Distinct());
        Assert.Single(results, r => r.Value.Created);

        await using var verify = host.CreateScope();
        var db = verify.ServiceProvider.GetRequiredService<IdentityModuleDbContext>();
        Assert.Equal(1, await db.Users.CountAsync(u => u.Kind == UserKind.Reporter, Ct));
        var identity = await db.ExternalIdentities.SingleAsync(Ct);
        Assert.Equal(userId, identity.UserId);

        // Sonraki çağrı da aynı kaydı döndürür.
        var again = await verify.ServiceProvider.GetRequiredService<IReporterRegistry>()
            .FindOrCreateAsync(ExternalIdentityProvider.WhatsApp, WaId, null, Ct);
        Assert.Equal(new ReporterRegistration(userId, Created: false), again.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("12345678901234567890123456789012345678901234567890123456789012345")]
    public async Task Invalid_external_id_is_validation_error(string externalId)
    {
        await using var host = await IdentityTestHost.StartAsync(postgres);
        await using var scope = host.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<IReporterRegistry>()
            .FindOrCreateAsync(ExternalIdentityProvider.WhatsApp, externalId, null, Ct);

        Assert.True(result.IsFailure);
        Assert.Equal("identity.invalid_external_id", result.Error.Code);
    }

    [Fact]
    public async Task Reporter_is_passwordless_cannot_get_password_and_cannot_sign_in()
    {
        await using var host = await IdentityTestHost.StartAsync(postgres);
        await using var scope = host.CreateScope();
        var registration = (await scope.ServiceProvider.GetRequiredService<IReporterRegistry>()
            .FindOrCreateAsync(ExternalIdentityProvider.WhatsApp, WaId, null, Ct)).Value;
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var reporter = (await users.FindByIdAsync(registration.UserId.ToString()))!;

        Assert.Null(reporter.PasswordHash);
        Assert.Empty(await users.GetRolesAsync(reporter));
        Assert.False(await scope.ServiceProvider.GetRequiredService<SignInManager<User>>().CanSignInAsync(reporter));

        // Veritabanı kısıtı: şikâyetçiye parola hash'i yazılamaz.
        var addPassword = await Record.ExceptionAsync(() => users.AddPasswordAsync(reporter, "Sikayetci-Parola-2026!"));
        Assert.IsType<DbUpdateException>(addPassword);
        Assert.Contains("ck_users_reporter_has_no_password", addPassword.InnerException?.Message, StringComparison.Ordinal);

        // E-posta atanmış olsa bile panel girişi yapılamaz.
        await using var setupScope = host.CreateScope();
        var setupUsers = setupScope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var fresh = (await setupUsers.FindByIdAsync(registration.UserId.ToString()))!;
        Assert.True((await setupUsers.SetEmailAsync(fresh, "sikayetci@example.test")).Succeeded);

        using var login = await host.LoginAsync("sikayetci@example.test", "Sikayetci-Parola-2026!");
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
    }

    [Fact]
    public async Task User_directory_returns_summaries_in_single_batch_and_skips_unknown_ids()
    {
        await using var host = await IdentityTestHost.StartAsync(postgres);
        await using var scope = host.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<IReporterRegistry>();
        var first = (await registry.FindOrCreateAsync(ExternalIdentityProvider.WhatsApp, "905550000001", "Mehmet", Ct)).Value.UserId;
        var second = (await registry.FindOrCreateAsync(ExternalIdentityProvider.WhatsApp, "905550000002", null, Ct)).Value.UserId;
        var directory = scope.ServiceProvider.GetRequiredService<IUserDirectory>();

        var summaries = await directory.GetUsersAsync([first, second, first, Guid.CreateVersion7()], Ct);

        Assert.Equal(2, summaries.Count);
        Assert.Equal(new UserSummary(first, "Mehmet", UserKind.Reporter, UserStatus.Active), summaries[first]);
        Assert.Equal(User.DefaultReporterDisplayName, summaries[second].DisplayName);
        Assert.Equal(summaries[first], await directory.GetUserAsync(first, Ct));
        Assert.Null(await directory.GetUserAsync(Guid.CreateVersion7(), Ct));
        Assert.Empty(await directory.GetUsersAsync([], Ct));
    }

    [Fact]
    public async Task Identity_migration_creates_only_used_tables_in_identity_schema()
    {
        await using var host = await IdentityTestHost.StartAsync(postgres);
        await using var scope = host.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityModuleDbContext>();

        Assert.False(db.Database.HasPendingModelChanges(), "IdentityModuleDbContext modeli son migration ile uyumlu değil.");
        var tables = await db.Database
            .SqlQueryRaw<string>("SELECT table_name AS \"Value\" FROM information_schema.tables WHERE table_schema = 'identity' ORDER BY 1")
            .ToListAsync(Ct);
        Assert.Equal(["__ef_migrations_history", "external_identities", "roles", "user_roles", "users"], tables);
        Assert.Equal(["Admin", "Moderator"], await db.Roles.OrderBy(r => r.Name).Select(r => r.Name!).ToListAsync(Ct));
    }
}
