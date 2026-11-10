using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace SokagimdakiSorun.Api.ErrorHandling;

/// <summary>
/// Yakalanmamış exception'ları loglar ve ayrıntı sızdırmayan bir Problem Details yanıtına çevirir.
/// Exception mesajı, tipi ve yığın izi yanıta hiçbir ortamda yazılmaz; yalnızca loga gider.
/// </summary>
internal sealed partial class UnhandledExceptionHandler(
    IProblemDetailsService problemDetailsService,
    ILogger<UnhandledExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        // İstemci bağlantıyı kapattıysa yazılacak yanıt yoktur.
        if (exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested)
        {
            LogRequestAborted(logger, httpContext.Request.Method, httpContext.Request.Path);
            return true;
        }

        int status;
        if (exception is BadHttpRequestException badRequest)
        {
            status = badRequest.StatusCode;
            LogBadRequest(logger, status, httpContext.Request.Method, httpContext.Request.Path);
        }
        else
        {
            status = StatusCodes.Status500InternalServerError;
            LogUnhandled(logger, exception, httpContext.Request.Method, httpContext.Request.Path);
        }

        httpContext.Response.StatusCode = status;

        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = new ProblemDetails { Status = status },
        });
    }

    [LoggerMessage(EventId = 1000, Level = LogLevel.Error, Message = "Yakalanmamış hata: {Method} {Path}")]
    private static partial void LogUnhandled(ILogger logger, Exception exception, string method, PathString path);

    [LoggerMessage(EventId = 1001, Level = LogLevel.Information, Message = "Geçersiz istek ({StatusCode}): {Method} {Path}")]
    private static partial void LogBadRequest(ILogger logger, int statusCode, string method, PathString path);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Debug, Message = "İstemci isteği iptal etti: {Method} {Path}")]
    private static partial void LogRequestAborted(ILogger logger, string method, PathString path);
}
