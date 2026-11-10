using System.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace SokagimdakiSorun.IntegrationTests.Host;

/// <summary>
/// WS-02 kabul ölçütleri: health uç noktaları, Problem Details hata formatı,
/// varsayılan-reddet yetkilendirme ve korelasyon kimliği.
/// </summary>
public sealed class HostPipelineTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // /health/ready WS-03'ten beri veritabanını kontrol eder; veritabanısız ve veritabanlı davranışı
    // Database/ReadinessTests.cs içinde doğrulanır.
    [Fact]
    public async Task Live_endpoint_answers_anonymously_without_details()
    {
        await using var factory = HostFactory.Create("Production");
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health/live", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Fallback_policy_requires_authenticated_user()
    {
        await using var factory = HostFactory.Create();
        var provider = factory.Services.GetRequiredService<IAuthorizationPolicyProvider>();

        var fallback = await provider.GetFallbackPolicyAsync();

        Assert.NotNull(fallback);
        Assert.Contains(fallback.Requirements, r => r is Microsoft.AspNetCore.Authorization.Infrastructure.DenyAnonymousAuthorizationRequirement);
    }

    [Fact]
    public async Task Anonymous_request_without_auth_scheme_is_rejected_with_problem_details()
    {
        // WS-04 öncesi gerçek durum: kayıtlı kimlik şeması yok. Yetki meta verisi olmayan
        // (eşleşmeyen yol dâhil) her istek 500 değil, 401 Problem Details almalıdır.
        await using var factory = HostFactory.Create("Production");
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/v1/does-not-exist", Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var problem = await HostFactory.ReadProblemAsync(response);
        Assert.Equal(401, problem.GetProperty("status").GetInt32());
        Assert.Equal("auth.unauthenticated", problem.GetProperty("code").GetString());
        Assert.Equal("https://sokagimdakisorun/errors/auth.unauthenticated", problem.GetProperty("type").GetString());
        Assert.False(string.IsNullOrEmpty(problem.GetProperty("traceId").GetString()));
    }

    [Fact]
    public async Task Anonymous_request_with_registered_auth_scheme_is_rejected_with_problem_details()
    {
        // WS-04 sonrası durum: şema kayıtlıyken çerçevenin challenge akışı devreye girer.
        await using var factory = HostFactory.Create("Production", HostFactory.AddTestAuthentication);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/v1/does-not-exist", Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var problem = await HostFactory.ReadProblemAsync(response);
        Assert.Equal("auth.unauthenticated", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Unknown_path_for_authenticated_user_returns_not_found_problem_details()
    {
        await using var factory = HostFactory.Create("Production", HostFactory.AddTestAuthentication);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(HostFactory.TestAuthHandler.UserHeader, "1");

        using var response = await client.GetAsync("/api/v1/does-not-exist", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var problem = await HostFactory.ReadProblemAsync(response);
        Assert.Equal(404, problem.GetProperty("status").GetInt32());
        Assert.Equal("resource.not_found", problem.GetProperty("code").GetString());
        Assert.False(string.IsNullOrEmpty(problem.GetProperty("traceId").GetString()));
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    public async Task Unhandled_exception_returns_problem_details_without_leaking_details(string environment)
    {
        await using var factory = HostFactory.Create(environment, HostFactory.ReplaceHealthServiceWithThrowing);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health/live", Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(Ct);
        var problem = await HostFactory.ReadProblemAsync(response);
        Assert.Equal(500, problem.GetProperty("status").GetInt32());
        Assert.Equal("server.error", problem.GetProperty("code").GetString());
        Assert.False(string.IsNullOrEmpty(problem.GetProperty("traceId").GetString()));
        Assert.False(problem.TryGetProperty("detail", out _));
        Assert.False(problem.TryGetProperty("exception", out _));
        Assert.DoesNotContain("GIZLI-AYRINTI", body, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", body, StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(InvalidOperationException), body, StringComparison.Ordinal);
        Assert.DoesNotContain("ThrowingHealthCheckService", body, StringComparison.Ordinal);
        Assert.DoesNotContain(" at ", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Valid_correlation_id_is_echoed_and_invalid_one_is_replaced()
    {
        await using var factory = HostFactory.Create("Production");
        using var client = factory.CreateClient();

        using var valid = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        valid.Headers.Add("X-Correlation-Id", "abc-123_x.y");
        using var validResponse = await client.SendAsync(valid, Ct);

        using var invalid = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        invalid.Headers.TryAddWithoutValidation("X-Correlation-Id", "bad value {INJECT} " + new string('x', 80));
        using var invalidResponse = await client.SendAsync(invalid, Ct);

        Assert.Equal("abc-123_x.y", Assert.Single(validResponse.Headers.GetValues("X-Correlation-Id")));
        var replaced = Assert.Single(invalidResponse.Headers.GetValues("X-Correlation-Id"));
        Assert.DoesNotContain("INJECT", replaced, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(replaced));
    }
}
