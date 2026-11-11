using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SokagimdakiSorun.IntegrationTests.Database.Support;
using SokagimdakiSorun.IntegrationTests.Host;

namespace SokagimdakiSorun.IntegrationTests.Identity;

/// <summary>WS-04: Admin'e özel kullanıcı yönetimi ve rol/durum değişikliğinin oturumlara etkisi.</summary>
[Collection(DatabaseCollection.Name)]
[Trait("Category", "Database")]
public sealed class UserManagementTests(PostgresFixture postgres)
{
    private const string ModeratorEmail = "moderator@example.test";
    private const string ModeratorPassword = "Moderator-Parola-2026!";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Admin_creates_moderator_who_can_log_in_but_cannot_use_admin_endpoints()
    {
        await using var host = await IdentityTestHost.StartAsync(postgres);
        var admin = await host.LoginSuccessfullyAsync(IdentityTestHost.AdminEmail, IdentityTestHost.AdminPassword);
        var moderatorId = await host.CreateModeratorAsync(admin.AccessToken, ModeratorEmail, ModeratorPassword);

        var moderator = await host.LoginSuccessfullyAsync(ModeratorEmail, ModeratorPassword);
        using var moderatorClient = host.CreateClient(moderator.AccessToken);

        using (var me = await moderatorClient.GetAsync("/api/v1/auth/me", Ct))
        {
            Assert.Equal(HttpStatusCode.OK, me.StatusCode);
            var body = await me.Content.ReadFromJsonAsync<JsonElement>(Ct);
            Assert.Equal(["Moderator"], body.GetProperty("roles").EnumerateArray().Select(r => r.GetString()));
        }

        var adminOnly = new[]
        {
            new HttpRequestMessage(HttpMethod.Get, "/api/v1/users"),
            new HttpRequestMessage(HttpMethod.Get, $"/api/v1/users/{moderatorId}"),
            new HttpRequestMessage(HttpMethod.Post, "/api/v1/users")
            {
                Content = JsonContent.Create(new { email = "x@example.test", displayName = "X", password = ModeratorPassword }),
            },
            new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/users/{moderatorId}") { Content = JsonContent.Create(new { role = "Admin" }) },
        };
        foreach (var request in adminOnly)
        {
            using (request)
            using (var response = await moderatorClient.SendAsync(request, Ct))
            {
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
                Assert.Equal("auth.forbidden", (await HostFactory.ReadProblemAsync(response)).GetProperty("code").GetString());
            }
        }
    }

    [Fact]
    public async Task Admin_lists_users_without_secrets_and_duplicate_email_is_conflict()
    {
        await using var host = await IdentityTestHost.StartAsync(postgres);
        var admin = await host.LoginSuccessfullyAsync(IdentityTestHost.AdminEmail, IdentityTestHost.AdminPassword);
        await host.CreateModeratorAsync(admin.AccessToken, ModeratorEmail, ModeratorPassword);
        using var client = host.CreateClient(admin.AccessToken);

        using (var list = await client.GetAsync("/api/v1/users?page=1&pageSize=10", Ct))
        {
            Assert.Equal(HttpStatusCode.OK, list.StatusCode);
            var raw = await list.Content.ReadAsStringAsync(Ct);
            var body = JsonDocument.Parse(raw).RootElement;
            Assert.Equal(2, body.GetProperty("totalCount").GetInt32());
            Assert.Equal(
                [IdentityTestHost.AdminEmail, ModeratorEmail],
                body.GetProperty("items").EnumerateArray().Select(u => u.GetProperty("email").GetString()));
            AuthEndpointTests.AssertNoSecrets(raw, admin);
        }

        using (var duplicate = await client.PostAsJsonAsync(
            "/api/v1/users", new { email = ModeratorEmail.ToUpperInvariant(), displayName = "Kopya", password = ModeratorPassword }, Ct))
        {
            Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
            Assert.Equal("user.email_taken", (await HostFactory.ReadProblemAsync(duplicate)).GetProperty("code").GetString());
        }

        using var weak = await client.PostAsJsonAsync("/api/v1/users", new { email = "zayif@example.test", displayName = "Zayıf", password = "kisa" }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);
        Assert.True((await HostFactory.ReadProblemAsync(weak)).GetProperty("errors").TryGetProperty("password", out _));

        using var tooLarge = await client.GetAsync("/api/v1/users?pageSize=101", Ct);
        Assert.Equal(HttpStatusCode.BadRequest, tooLarge.StatusCode);
    }

    [Fact]
    public async Task Suspending_moderator_revokes_existing_tokens_and_blocks_login()
    {
        await using var host = await IdentityTestHost.StartAsync(postgres);
        var admin = await host.LoginSuccessfullyAsync(IdentityTestHost.AdminEmail, IdentityTestHost.AdminPassword);
        var moderatorId = await host.CreateModeratorAsync(admin.AccessToken, ModeratorEmail, ModeratorPassword);
        var moderator = await host.LoginSuccessfullyAsync(ModeratorEmail, ModeratorPassword);
        using var adminClient = host.CreateClient(admin.AccessToken);

        using (var suspend = await adminClient.PatchAsJsonAsync($"/api/v1/users/{moderatorId}", new { status = "Suspended" }, Ct))
        {
            Assert.Equal(HttpStatusCode.OK, suspend.StatusCode);
            Assert.Equal("Suspended", (await suspend.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("status").GetString());
        }

        using (var moderatorClient = host.CreateClient(moderator.AccessToken))
        using (var me = await moderatorClient.GetAsync("/api/v1/auth/me", Ct))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
        }

        using (var anonymous = host.CreateClient())
        using (var refresh = await anonymous.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = moderator.RefreshToken }, Ct))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
        }

        using var login = await host.LoginAsync(ModeratorEmail, ModeratorPassword);
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
    }

    [Fact]
    public async Task Promoting_moderator_takes_effect_after_new_login_and_admin_cannot_demote_self()
    {
        await using var host = await IdentityTestHost.StartAsync(postgres);
        var admin = await host.LoginSuccessfullyAsync(IdentityTestHost.AdminEmail, IdentityTestHost.AdminPassword);
        var moderatorId = await host.CreateModeratorAsync(admin.AccessToken, ModeratorEmail, ModeratorPassword);
        using var adminClient = host.CreateClient(admin.AccessToken);

        using (var promote = await adminClient.PatchAsJsonAsync($"/api/v1/users/{moderatorId}", new { role = "Admin" }, Ct))
        {
            Assert.Equal(HttpStatusCode.OK, promote.StatusCode);
            var roles = (await promote.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("roles");
            Assert.Equal(["Admin"], roles.EnumerateArray().Select(r => r.GetString()));
        }

        var promoted = await host.LoginSuccessfullyAsync(ModeratorEmail, ModeratorPassword);
        using (var promotedClient = host.CreateClient(promoted.AccessToken))
        using (var list = await promotedClient.GetAsync("/api/v1/users", Ct))
        {
            Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        }

        using (var me = await adminClient.GetAsync("/api/v1/auth/me", Ct))
        {
            var adminId = (await me.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
            using var self = await adminClient.PatchAsJsonAsync($"/api/v1/users/{adminId}", new { role = "Moderator" }, Ct);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, self.StatusCode);
            Assert.Equal("user.cannot_modify_self", (await HostFactory.ReadProblemAsync(self)).GetProperty("code").GetString());
        }

        using var missing = await adminClient.PatchAsJsonAsync($"/api/v1/users/{Guid.CreateVersion7()}", new { status = "Suspended" }, Ct);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("user.not_found", (await HostFactory.ReadProblemAsync(missing)).GetProperty("code").GetString());
    }
}
