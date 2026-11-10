using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace SokagimdakiSorun.Api.ErrorHandling;

/// <summary>
/// Tüm hata yanıtlarını RFC 9457 Problem Details biçimine getirir
/// (docs/API_CONTRACT_STRATEGY.md §4): kararlı <c>code</c>, <c>type</c> ve <c>traceId</c>.
/// </summary>
internal static class ProblemDetailsSetup
{
    /// <summary>Hata kodlarının <c>type</c> URI'si için taban.</summary>
    public const string TypeBaseUri = "https://sokagimdakisorun/errors/";

    /// <summary>Problem Details uzantı alanı: kararlı hata kodu.</summary>
    public const string CodeExtension = "code";

    /// <summary>Problem Details uzantı alanı: loglarla eşleştirme kimliği.</summary>
    public const string TraceIdExtension = "traceId";

    public static IServiceCollection AddApiProblemDetails(this IServiceCollection services)
    {
        services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
            Normalize(context.ProblemDetails, context.HttpContext));
        services.AddExceptionHandler<UnhandledExceptionHandler>();

        return services;
    }

    private static void Normalize(ProblemDetails problem, HttpContext httpContext)
    {
        var status = problem.Status ?? httpContext.Response.StatusCode;
        problem.Status = status;

        // Modül kendi kodunu verdiyse korunur; yoksa duruma göre genel kod atanır.
        if (problem.Extensions.TryGetValue(CodeExtension, out var existing) && existing is string code && code.Length > 0)
        {
            problem.Title ??= ErrorCodes.DefaultTitle(status);
        }
        else
        {
            code = ErrorCodes.ForStatus(status);
            problem.Extensions[CodeExtension] = code;
            problem.Title = ErrorCodes.DefaultTitle(status);
        }

        problem.Type = TypeBaseUri + code;
        problem.Instance = null;

        // İç ayrıntıların sızmaması için çerçevenin eklediği ek alanlar temizlenir.
        problem.Extensions.Remove("exception");
        problem.Extensions.Remove("requestId");
        problem.Extensions[TraceIdExtension] = Activity.Current?.Id ?? httpContext.TraceIdentifier;
    }
}
