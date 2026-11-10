using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace SokagimdakiSorun.Api.Security;

/// <summary>
/// Hız sınırlayıcı altyapısı. Politikalar burada tanımlanır; uç noktalara uygulanması ilgili
/// modülün işidir (login: WS-04, webhook: WS-07). Modüller host projesine referans veremediği
/// için politikayı adıyla kullanır: <c>.RequireRateLimiting("auth-login")</c>.
/// </summary>
internal static class RateLimitingSetup
{
    /// <summary>Giriş ve token uç noktaları: istemci IP'si başına dakikada 10 istek.</summary>
    public const string AuthLoginPolicy = "auth-login";

    /// <summary>WhatsApp webhook uç noktası: istemci IP'si başına dakikada 300 istek.</summary>
    public const string WebhookPolicy = "webhook";

    public static IServiceCollection AddApiRateLimiting(this IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            // Gövdesiz 429; durum kodu sayfaları onu Problem Details'e (rate_limit.exceeded) çevirir.
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = (context, _) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter =
                        ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                }

                return ValueTask.CompletedTask;
            };

            options.AddPolicy(AuthLoginPolicy, httpContext => PerClientIp(httpContext, permitLimit: 10));
            options.AddPolicy(WebhookPolicy, httpContext => PerClientIp(httpContext, permitLimit: 300));
        });

        return services;
    }

    private static RateLimitPartition<string> PerClientIp(HttpContext httpContext, int permitLimit) =>
        RateLimitPartition.GetFixedWindowLimiter(
            httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = permitLimit,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            });
}
