using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SokagimdakiSorun.IntegrationTests.Database.Support;
using SokagimdakiSorun.IntegrationTests.Host;

namespace SokagimdakiSorun.IntegrationTests.Identity;

/// <summary>
/// WS-04: giriş, yenileme, çıkış, <c>me</c>, kilitleme ve hız sınırı — gerçek host + gerçek PostgreSQL 18 üzerinde,
/// ilk Admin ortam değişkenlerinden (Seed__AdminEmail/Seed__AdminPassword) tohumlanmış olarak.
/// </summary>
[Collection(DatabaseCollection.Name)]
[Trait("Category", "Database")]
public sealed class AuthEndpointTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Seeded_admin_logs_in_and_me_returns_profile_without_secrets()
    {
        await using var host = await IdentityTestHost.StartAsync(postgres);

        var tokens = await host.LoginSuccessfullyAsync(IdentityTestHost.AdminEmail, IdentityTestHost.AdminPassword);
        using var tokenJson = JsonDocument.Parse(tokens.Raw);
        Assert.Equal(15 * 60, tokenJson.RootElement.GetProperty("expiresIn").GetInt32());

        using var client = host.CreateClient(tokens.AccessToken);
        using var response = await client.GetAsync("/api/v1/auth/me", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync(Ct);
        var me = JsonDocument.Parse(raw).RootElement;
        Assert.Equal(IdentityTestHost.AdminEmail, me.GetProperty("email").GetString());
        Assert.Equal(["Admin"], me.GetProperty("roles").EnumerateArray().Select(r => r.GetString()));
        AssertNoSecrets(raw, tokens);
    }

    [Theory]
    [InlineData(IdentityTestHost.AdminEmail, "yanlis-parola-123!")]
    [InlineData("olmayan@example.test", IdentityTestHost.AdminPassword)]
    public async Task Invalid_credentials_return_same_401_problem(string email, string password)
    {
        await using var host = await IdentityTestHost.StartAsync(postgres);

        using var response = await host.LoginAsync(email, password);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var problem = await HostFactory.ReadProblemAsync(response);
        Assert.Equal("auth.invalid_credentials", problem.GetProperty("code").GetString());
        Assert.False(problem.TryGetProperty("accessToken", out _));
    }

    [Fact]
    public async Task Missing_password_is_validation_error()
    {
        await using var host = await IdentityTestHost.StartAsync(postgres);

        using var response = await host.LoginAsync(IdentityTestHost.AdminEmail, "");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await HostFactory.ReadProblemAsync(response);
        Assert.Equal("validation.failed", problem.GetProperty("code").GetString());
        Assert.True(problem.GetProperty("errors").TryGetProperty("password", out _));
    }

    [Fact]
    public async Task Account_locks_after_five_failures_even_for_correct_password()
    {
        await using var host = await IdentityTestHost.StartAsync(postgres);

        for (var i = 0; i < 5; i++)
        {
            using var failed = await host.LoginAsync(IdentityTestHost.AdminEmail, "yanlis-parola-123!");
            Assert.Equal(HttpStatusCode.Unauthorized, failed.StatusCode);
        }

        using var locked = await host.LoginAsync(IdentityTestHost.AdminEmail, IdentityTestHost.AdminPassword);
        Assert.Equal(HttpStatusCode.Unauthorized, locked.StatusCode);
        Assert.Equal("auth.invalid_credentials", (await HostFactory.ReadProblemAsync(locked)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Login_is_rate_limited()
    {
        await using var host = await IdentityTestHost.StartAsync(postgres);

        // Politika: istemci IP'si başına dakikada 10 istek (host RateLimitingSetup.AuthLoginPolicy).
        for (var i = 0; i < 10; i++)
        {
            using var response = await host.LoginAsync("olmayan@example.test", "x");
            Assert.NotEqual(HttpStatusCode.TooManyRequests, response.StatusCode);
        }

        using var limited = await host.LoginAsync("olmayan@example.test", "x");
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal("rate_limit.exceeded", (await HostFactory.ReadProblemAsync(limited)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Refresh_issues_new_tokens_and_rejects_invalid_token()
    {
        await using var host = await IdentityTestHost.StartAsync(postgres);
        var tokens = await host.LoginSuccessfullyAsync(IdentityTestHost.AdminEmail, IdentityTestHost.AdminPassword);
        using var client = host.CreateClient();

        using var refreshed = await client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = tokens.RefreshToken }, Ct);
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        var newTokens = await IdentityTestHost.Tokens.ReadAsync(refreshed);
        using (var authed = host.CreateClient(newTokens.AccessToken))
        using (var me = await authed.GetAsync("/api/v1/auth/me", Ct))
        {
            Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        }

        // Erişim token'ı yenileme token'ı yerine kullanılamaz; bozuk değer de reddedilir.
        foreach (var bad in new[] { tokens.AccessToken, "bozuk-token" })
        {
            using var rejected = await client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = bad }, Ct);
            Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
            Assert.Equal("auth.invalid_refresh_token", (await HostFactory.ReadProblemAsync(rejected)).GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task Logout_invalidates_access_and_refresh_tokens()
    {
        await using var host = await IdentityTestHost.StartAsync(postgres);
        var tokens = await host.LoginSuccessfullyAsync(IdentityTestHost.AdminEmail, IdentityTestHost.AdminPassword);
        using var authed = host.CreateClient(tokens.AccessToken);

        using (var logout = await authed.PostAsync("/api/v1/auth/logout", null, Ct))
        {
            Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        }

        using (var me = await authed.GetAsync("/api/v1/auth/me", Ct))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
        }

        using var anonymous = host.CreateClient();
        using var refresh = await anonymous.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = tokens.RefreshToken }, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
    }

    [Theory]
    [InlineData("GET", "/api/v1/auth/me")]
    [InlineData("POST", "/api/v1/auth/logout")]
    [InlineData("GET", "/api/v1/users")]
    public async Task Anonymous_request_to_protected_endpoint_is_401_problem(string method, string path)
    {
        await using var host = await IdentityTestHost.StartAsync(postgres);
        using var client = host.CreateClient();

        using var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path), Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("auth.unauthenticated", (await HostFactory.ReadProblemAsync(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Logs_contain_no_email_password_or_token()
    {
        await using var host = await IdentityTestHost.StartAsync(postgres);
        const string moderatorEmail = "log.moderator@example.test";
        const string moderatorPassword = "Moderator-Parola-2026!";

        var tokens = await host.LoginSuccessfullyAsync(IdentityTestHost.AdminEmail, IdentityTestHost.AdminPassword);
        await host.CreateModeratorAsync(tokens.AccessToken, moderatorEmail, moderatorPassword);
        using (await host.LoginAsync(moderatorEmail, "yanlis-parola-123!"))
        using (await host.LoginAsync("olmayan.kisi@example.test", moderatorPassword))
        {
        }

        using var client = host.CreateClient();
        using (await client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = tokens.RefreshToken }, Ct))
        {
        }

        var logs = string.Join('\n', host.Logs.Messages);
        Assert.NotEmpty(host.Logs.Messages);
        Assert.Contains("Başarısız giriş denemesi", logs, StringComparison.Ordinal);
        foreach (var secret in new[]
        {
            IdentityTestHost.AdminEmail, IdentityTestHost.AdminPassword, moderatorEmail, moderatorPassword,
            "olmayan.kisi@example.test", "yanlis-parola-123!", tokens.AccessToken, tokens.RefreshToken,
        })
        {
            Assert.DoesNotContain(secret, logs, StringComparison.OrdinalIgnoreCase);
        }
    }

    internal static void AssertNoSecrets(string responseBody, IdentityTestHost.Tokens? tokens = null)
    {
        foreach (var field in new[] { "passwordHash", "securityStamp", "concurrencyStamp", "password_hash", "security_stamp" })
        {
            Assert.DoesNotContain(field, responseBody, StringComparison.OrdinalIgnoreCase);
        }

        if (tokens is not null)
        {
            Assert.DoesNotContain(tokens.AccessToken, responseBody, StringComparison.Ordinal);
            Assert.DoesNotContain(tokens.RefreshToken, responseBody, StringComparison.Ordinal);
        }
    }
}
