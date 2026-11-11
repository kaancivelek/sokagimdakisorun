using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using SokagimdakiSorun.Complaints.Contracts;
using SokagimdakiSorun.SharedKernel;

namespace SokagimdakiSorun.Complaints.Endpoints;

/// <summary>
/// <see cref="Error"/>'u Problem Details'e çevirir. <c>code</c> uzantısı modülün kararlı kodunu taşır; <c>type</c> ve
/// <c>traceId</c> host'un merkezi normalizasyonunca eklenir (API_CONTRACT_STRATEGY §4).
/// </summary>
internal static class ProblemResults
{
    public static ProblemHttpResult From(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);

        var status = error.Type switch
        {
            ErrorType.Validation => StatusCodes.Status400BadRequest,
            ErrorType.Unauthorized => StatusCodes.Status401Unauthorized,
            ErrorType.Forbidden => StatusCodes.Status403Forbidden,
            ErrorType.NotFound => StatusCodes.Status404NotFound,
            ErrorType.Conflict => StatusCodes.Status409Conflict,
            ErrorType.BusinessRule => StatusCodes.Status422UnprocessableEntity,
            _ => StatusCodes.Status500InternalServerError,
        };

        if (error.ValidationErrors is { Count: > 0 } errors)
        {
            var validation = new HttpValidationProblemDetails(errors.ToDictionary(e => e.Key, e => e.Value))
            {
                Status = status,
                Title = error.Message,
            };
            validation.Extensions["code"] = error.Code;
            return TypedResults.Problem(validation);
        }

        return TypedResults.Problem(
            statusCode: status,
            title: error.Message,
            extensions: new Dictionary<string, object?> { ["code"] = error.Code });
    }

    public static ProblemHttpResult Validation(string field, string message) =>
        From(Error.Validation(ComplaintErrorCodes.ValidationFailed, "İstek doğrulanamadı.", new Dictionary<string, string[]> { [field] = [message] }));

    public static ProblemHttpResult NotFound(string code, string message) => From(Error.NotFound(code, message));
}
