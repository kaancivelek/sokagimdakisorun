using System.ComponentModel.DataAnnotations;

namespace SokagimdakiSorun.Messaging.Infrastructure.WhatsApp;

/// <summary>Sağlayıcı seçimi (<c>WhatsApp__Provider</c>).</summary>
internal enum WhatsAppProvider
{
    /// <summary>Sahte sağlayıcı: gerçek çağrı yapmaz (WS-07).</summary>
    Mock,

    /// <summary>Meta WhatsApp Cloud API (WS-10'da uygulanacak).</summary>
    CloudApi,
}

/// <summary>
/// <c>WhatsApp</c> yapılandırma bölümü (ARCHITECTURE §9). <see cref="VerifyToken"/>, <see cref="AppSecret"/> ve
/// <see cref="AccessToken"/> secret'tır: yalnızca ortam değişkeninden gelir, loglanmaz. Tanımlı değilse webhook istekleri
/// reddedilir (açılış durmaz; build-zamanı OpenAPI üretimi secret istemez).
/// </summary>
internal sealed class WhatsAppOptions : IValidatableObject
{
    public const string SectionName = "WhatsApp";

    public WhatsAppProvider Provider { get; set; } = WhatsAppProvider.Mock;

    /// <summary>Webhook GET el sıkışmasında beklenen <c>hub.verify_token</c>.</summary>
    public string? VerifyToken { get; set; }

    /// <summary>POST gövdesinin HMAC-SHA256 imzası (<c>X-Hub-Signature-256</c>) için anahtar.</summary>
    public string? AppSecret { get; set; }

    /// <summary>Graph API erişim token'ı (WS-10; mock kullanmaz).</summary>
    public string? AccessToken { get; set; }

    /// <summary>İşletme numarasının kimliği (WS-10; mock gelen olaydaki değeri kullanır).</summary>
    public string? PhoneNumberId { get; set; }

    /// <summary>Graph API sürümü (WS-10, V-8).</summary>
    public string? GraphApiVersion { get; set; }

    /// <summary>Webhook gövdesi için azami boyut; aşılırsa 413.</summary>
    [Range(1024, 10 * 1024 * 1024)]
    public int MaxWebhookBodyBytes { get; set; } = 1024 * 1024;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Provider != WhatsAppProvider.Mock)
        {
            yield return new ValidationResult(
                "WhatsApp__Provider=CloudApi henüz uygulanmadı (WS-10). Yalnızca 'Mock' desteklenir.", [nameof(Provider)]);
        }
    }
}
