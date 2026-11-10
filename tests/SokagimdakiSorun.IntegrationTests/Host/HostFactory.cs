using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SokagimdakiSorun.IntegrationTests.Host;

/// <summary>
/// Gerçek <c>Program</c> pipeline'ını in-memory çalıştıran fabrika. Veritabanı veya dış servis kullanmaz.
/// </summary>
internal static class HostFactory
{
    public static WebApplicationFactory<Program> Create(string environment = "Development", Action<IServiceCollection>? configureServices = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            builder.ConfigureLogging(logging => logging.ClearProviders());
            if (configureServices is not null)
            {
                builder.ConfigureTestServices(configureServices);
            }
        });

    /// <summary>
    /// WS-04 öncesi gerçek bir kimlik şeması olmadığından, kimliği doğrulanmış istek senaryosu için
    /// test şeması: <see cref="TestAuthHandler.UserHeader"/> başlığı varsa kullanıcı doğrulanır.
    /// </summary>
    public static void AddTestAuthentication(IServiceCollection services) =>
        services.AddAuthentication(TestAuthHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });

    /// <summary>Health uç noktasının arkasındaki servisi exception fırlatan bir sahte ile değiştirir.</summary>
    public static void ReplaceHealthServiceWithThrowing(IServiceCollection services) =>
        services.AddSingleton<HealthCheckService, ThrowingHealthCheckService>();

    public static async Task<JsonElement> ReadProblemAsync(HttpResponseMessage response)
    {
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        return await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
    }

    internal sealed class TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "Test";
        public const string UserHeader = "X-Test-User";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.ContainsKey(UserHeader))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var identity = new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, Guid.CreateVersion7().ToString())], SchemeName);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
        }
    }

    internal sealed class ThrowingHealthCheckService : HealthCheckService
    {
        public const string SecretMessage = "GIZLI-AYRINTI connection=Host=db;Password=hunter2";

        public override Task<HealthReport> CheckHealthAsync(Func<HealthCheckRegistration, bool>? predicate, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(SecretMessage);
    }
}
