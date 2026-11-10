using System.Buffers;
using System.Diagnostics;

namespace SokagimdakiSorun.Api.Observability;

/// <summary>
/// Her isteğe bir korelasyon kimliği verir: geçerli bir <c>X-Correlation-Id</c> başlığı geldiyse
/// onu, gelmediyse W3C trace kimliğini kullanır. Kimlik yanıt başlığına yazılır ve isteğin tüm
/// loglarına <c>CorrelationId</c> kapsamı olarak eklenir.
/// </summary>
internal sealed class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    public const string HeaderName = "X-Correlation-Id";

    private const int MaxLength = 64;

    private static readonly SearchValues<char> AllowedChars =
        SearchValues.Create("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_.");

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = ReadValidHeader(context) ?? Activity.Current?.TraceId.ToHexString() ?? context.TraceIdentifier;

        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = correlationId;
            return Task.CompletedTask;
        });

        using (logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId }))
        {
            await next(context);
        }
    }

    // Dışarıdan gelen değer log enjeksiyonuna karşı sınırlı bir karakter kümesiyle kabul edilir.
    private static string? ReadValidHeader(HttpContext context)
    {
        if (!context.Request.Headers.TryGetValue(HeaderName, out var values) || values.Count != 1)
        {
            return null;
        }

        var value = values[0];
        if (string.IsNullOrEmpty(value) || value.Length > MaxLength || value.AsSpan().ContainsAnyExcept(AllowedChars))
        {
            return null;
        }

        return value;
    }
}
