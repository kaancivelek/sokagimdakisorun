using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SokagimdakiSorun.Messaging.Application;
using SokagimdakiSorun.Messaging.Infrastructure.WhatsApp;

namespace SokagimdakiSorun.Messaging.Endpoints;

/// <summary>
/// WhatsApp webhook'u (API_CONTRACT_STRATEGY §8; yol sürümsüz). Anonimdir ama: GET verify token ister; POST yalnızca ham gövde
/// üzerinde doğru HMAC-SHA256 imzasıyla kabul edilir, olay başına inbox satırı yazar ve hızlıca <c>200</c> döner. İş mantığı
/// <c>InboxProcessor</c>'dadır. Gövde, imza, token ve telefon numarası loglanmaz. Hız sınırı: host'un <c>webhook</c> politikası.
/// </summary>
internal static partial class WebhookEndpoints
{
    public const string Path = "/api/integrations/whatsapp/webhook";
    public const string RateLimitPolicy = "webhook";
    public const string InvalidSignatureCode = "webhook.invalid_signature";
    public const string VerificationFailedCode = "webhook.verification_failed";
    public const string InvalidPayloadCode = "webhook.invalid_payload";
    public const string PayloadTooLargeCode = "webhook.payload_too_large";
    private const int MaxChallengeLength = 256;

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup(Path).WithTags("WhatsApp Webhook").AllowAnonymous().RequireRateLimiting(RateLimitPolicy);

        group.MapGet("/", Verify)
            .WithName("VerifyWhatsAppWebhook")
            .WithSummary("Meta webhook el sıkışması: hub.mode=subscribe ve hub.verify_token eşleşirse hub.challenge düz metin döner.")
            .Produces<string>(StatusCodes.Status200OK, "text/plain")
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapPost("/", ReceiveAsync)
            .WithName("ReceiveWhatsAppWebhook")
            .WithSummary("Olay alımı. X-Hub-Signature-256 (ham gövde üzerinde HMAC-SHA256) zorunlu; olay başına inbox satırı yazılır, işleme arka plandadır.")
            .Accepts<object>("application/json")
            .Produces(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);
    }

    private static IResult Verify(
        [FromQuery(Name = "hub.mode")] string? mode,
        [FromQuery(Name = "hub.verify_token")] string? verifyToken,
        [FromQuery(Name = "hub.challenge")] string? challenge,
        IOptions<WhatsAppOptions> options,
        ILoggerFactory loggerFactory)
    {
        if (mode == "subscribe" && challenge is { Length: > 0 and <= MaxChallengeLength }
            && WebhookSignatureVerifier.SecretEquals(verifyToken, options.Value.VerifyToken))
        {
            return TypedResults.Text(challenge, "text/plain");
        }

        LogVerificationRejected(loggerFactory.CreateLogger(typeof(WebhookEndpoints)));
        return Problem(StatusCodes.Status403Forbidden, VerificationFailedCode, "Webhook doğrulaması başarısız.");
    }

    private static async Task<IResult> ReceiveAsync(
        HttpRequest request, WebhookIngestion ingestion, IOptions<WhatsAppOptions> options, ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger(typeof(WebhookEndpoints));
        var limit = options.Value.MaxWebhookBodyBytes;
        if (request.ContentLength > limit)
        {
            return Problem(StatusCodes.Status413PayloadTooLarge, PayloadTooLargeCode, "Webhook gövdesi çok büyük.");
        }

        var body = await ReadBodyAsync(request.Body, limit, cancellationToken);
        if (body is null)
        {
            return Problem(StatusCodes.Status413PayloadTooLarge, PayloadTooLargeCode, "Webhook gövdesi çok büyük.");
        }

        // İmza ham baytlar üzerinde, JSON ayrıştırmadan ÖNCE doğrulanır.
        if (!WebhookSignatureVerifier.IsValid(body, request.Headers[WebhookSignatureVerifier.HeaderName], options.Value.AppSecret))
        {
            if (string.IsNullOrEmpty(options.Value.AppSecret))
            {
                LogSecretNotConfigured(logger);
            }

            LogSignatureRejected(logger);
            return Problem(StatusCodes.Status401Unauthorized, InvalidSignatureCode, "Webhook imzası geçersiz veya eksik.");
        }

        IReadOnlyList<InboxEventDraft> drafts;
        try
        {
            drafts = WhatsAppWebhookParser.SplitEvents(body);
        }
        catch (FormatException)
        {
            LogInvalidPayload(logger);
            return Problem(StatusCodes.Status400BadRequest, InvalidPayloadCode, "Webhook gövdesi beklenen biçimde değil.");
        }

        var result = await ingestion.AcceptAsync(drafts, cancellationToken);
        LogAccepted(logger, result.Accepted, result.Duplicates);
        return TypedResults.Ok();
    }

    private static async Task<byte[]?> ReadBodyAsync(Stream body, int limit, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await body.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > limit)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static IResult Problem(int status, string code, string title) =>
        TypedResults.Problem(statusCode: status, title: title, extensions: new Dictionary<string, object?> { ["code"] = code });

    [LoggerMessage(Level = LogLevel.Warning, Message = "Webhook imzası reddedildi.")]
    private static partial void LogSignatureRejected(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "WhatsApp__AppSecret tanımlı değil; tüm webhook istekleri reddediliyor.")]
    private static partial void LogSecretNotConfigured(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Webhook doğrulama (GET) isteği reddedildi.")]
    private static partial void LogVerificationRejected(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "İmzası doğru webhook gövdesi ayrıştırılamadı.")]
    private static partial void LogInvalidPayload(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Webhook kabul edildi: {Accepted} yeni olay, {Duplicates} tekrar.")]
    private static partial void LogAccepted(ILogger logger, int accepted, int duplicates);
}
