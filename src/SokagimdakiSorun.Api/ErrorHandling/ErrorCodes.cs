namespace SokagimdakiSorun.Api.ErrorHandling;

/// <summary>
/// Modülün kendi kodunu vermediği durumlarda kullanılan genel hata kodları
/// (docs/API_CONTRACT_STRATEGY.md §4 tablosu).
/// </summary>
internal static class ErrorCodes
{
    public const string ValidationFailed = "validation.failed";
    public const string Unauthenticated = "auth.unauthenticated";
    public const string Forbidden = "auth.forbidden";
    public const string NotFound = "resource.not_found";
    public const string MethodNotAllowed = "request.method_not_allowed";
    public const string Conflict = "request.conflict";
    public const string PayloadTooLarge = "request.too_large";
    public const string UnsupportedMediaType = "request.unsupported_media_type";
    public const string UnprocessableEntity = "request.unprocessable";
    public const string RateLimitExceeded = "rate_limit.exceeded";
    public const string BadRequest = "request.invalid";
    public const string ServerError = "server.error";

    public static string ForStatus(int status) => status switch
    {
        StatusCodes.Status400BadRequest => ValidationFailed,
        StatusCodes.Status401Unauthorized => Unauthenticated,
        StatusCodes.Status403Forbidden => Forbidden,
        StatusCodes.Status404NotFound => NotFound,
        StatusCodes.Status405MethodNotAllowed => MethodNotAllowed,
        StatusCodes.Status409Conflict => Conflict,
        StatusCodes.Status413PayloadTooLarge => PayloadTooLarge,
        StatusCodes.Status415UnsupportedMediaType => UnsupportedMediaType,
        StatusCodes.Status422UnprocessableEntity => UnprocessableEntity,
        StatusCodes.Status429TooManyRequests => RateLimitExceeded,
        >= 500 => ServerError,
        _ => BadRequest,
    };

    public static string DefaultTitle(int status) => status switch
    {
        StatusCodes.Status400BadRequest => "İstek doğrulanamadı.",
        StatusCodes.Status401Unauthorized => "Kimlik doğrulaması gerekiyor.",
        StatusCodes.Status403Forbidden => "Bu işlem için yetkiniz yok.",
        StatusCodes.Status404NotFound => "Kaynak bulunamadı.",
        StatusCodes.Status405MethodNotAllowed => "Bu HTTP yöntemi desteklenmiyor.",
        StatusCodes.Status409Conflict => "İstek mevcut durumla çakışıyor.",
        StatusCodes.Status413PayloadTooLarge => "İstek gövdesi çok büyük.",
        StatusCodes.Status415UnsupportedMediaType => "İçerik türü desteklenmiyor.",
        StatusCodes.Status422UnprocessableEntity => "İstek iş kuralına uymuyor.",
        StatusCodes.Status429TooManyRequests => "Çok fazla istek gönderildi.",
        >= 500 => "Beklenmeyen bir hata oluştu.",
        _ => "İstek işlenemedi.",
    };
}
