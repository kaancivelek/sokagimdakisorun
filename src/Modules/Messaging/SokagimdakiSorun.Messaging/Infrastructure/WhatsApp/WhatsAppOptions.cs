using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;

namespace SokagimdakiSorun.Messaging.Infrastructure.WhatsApp;

/// <summary>Sağlayıcı seçimi (<c>WhatsApp__Provider</c>).</summary>
internal enum WhatsAppProvider
{
    /// <summary>Sahte sağlayıcı: gerçek çağrı yapmaz (WS-07).</summary>
    Mock,

    /// <summary>Meta WhatsApp Cloud API (WS-10, <see cref="CloudWhatsAppClient"/>).</summary>
    CloudApi,
}

/// <summary>
/// <c>WhatsApp</c> yapılandırma bölümü (ARCHITECTURE §9). <see cref="VerifyToken"/>, <see cref="AppSecret"/> ve
/// <see cref="AccessToken"/> secret'tır: yalnızca ortam değişkeninden gelir, loglanmaz ve doğrulama mesajlarına yazılmaz.
/// <list type="bullet">
///   <item><c>Mock</c>: secret zorunlu değildir; tanımlı değilse webhook istekleri reddedilir (build-zamanı OpenAPI üretimi secret istemez).</item>
///   <item><c>CloudApi</c>: <see cref="AccessToken"/>, <see cref="AppSecret"/>, <see cref="VerifyToken"/>, <see cref="PhoneNumberId"/> ve
///         <c>vNN.N</c> biçiminde <see cref="GraphApiVersion"/> zorunludur; eksikse açılış net bir hatayla durur (Mock'a düşülmez).</item>
/// </list>
/// </summary>
internal sealed partial class WhatsAppOptions : IValidatableObject
{
    public const string SectionName = "WhatsApp";

    public WhatsAppProvider Provider { get; set; } = WhatsAppProvider.Mock;

    /// <summary>Webhook GET el sıkışmasında beklenen <c>hub.verify_token</c>.</summary>
    public string? VerifyToken { get; set; }

    /// <summary>POST gövdesinin HMAC-SHA256 imzası (<c>X-Hub-Signature-256</c>) için anahtar.</summary>
    public string? AppSecret { get; set; }

    /// <summary>Graph API erişim token'ı (yalnızca <c>CloudApi</c>).</summary>
    public string? AccessToken { get; set; }

    /// <summary>İşletme numarasının kimliği (yalnızca <c>CloudApi</c>; giden mesajda konuşmanın numarası yoksa kullanılır).</summary>
    public string? PhoneNumberId { get; set; }

    /// <summary>Graph API sürümü, ör. <c>v27.0</c> (V-8: resmî Graph API changelog'undaki biçim).</summary>
    public string? GraphApiVersion { get; set; }

    /// <summary>Webhook gövdesi için azami boyut; aşılırsa 413.</summary>
    [Range(1024, 10 * 1024 * 1024)]
    public int MaxWebhookBodyBytes { get; set; } = 1024 * 1024;

    /// <summary>
    /// Gelen medya için indirme üst sınırı (meta verideki <c>file_size</c> ve indirilen bayt). Asıl dosya kuralları Media modülündedir
    /// (<c>Media__MaxFileSizeBytes</c>); bu sınır yalnızca gereksiz büyük indirmeyi baştan keser. Meta'nın resim sınırı 5 MB'dir.
    /// </summary>
    [Range(1024, 50 * 1024 * 1024)]
    public int MaxMediaBytes { get; set; } = 10 * 1024 * 1024;

    /// <summary>Tek bir Graph API isteğinin zaman aşımı.</summary>
    [Range(typeof(TimeSpan), "00:00:01", "00:05:00")]
    public TimeSpan HttpTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Pencere dışı gönderim için onaylı şablonlar (<c>WhatsApp__Templates__*</c>). Şablon gövdesi Meta'dadır, kodda değil.</summary>
    public WhatsAppTemplateOptions Templates { get; set; } = new();

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Provider == WhatsAppProvider.CloudApi)
        {
            // Mesajlar yalnızca anahtar adını içerir; değer asla yazılmaz.
            foreach (var (name, value) in new[]
                     {
                         (nameof(AccessToken), AccessToken), (nameof(AppSecret), AppSecret), (nameof(VerifyToken), VerifyToken),
                         (nameof(PhoneNumberId), PhoneNumberId), (nameof(GraphApiVersion), GraphApiVersion),
                     })
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    yield return new ValidationResult(
                        $"WhatsApp__Provider=CloudApi için WhatsApp__{name} zorunludur (boş veya tanımsız).", [name]);
                }
            }

            if (!string.IsNullOrWhiteSpace(GraphApiVersion) && !GraphVersionPattern().IsMatch(GraphApiVersion))
            {
                yield return new ValidationResult(
                    "WhatsApp__GraphApiVersion 'v<ana>.<alt>' biçiminde olmalıdır (ör. v27.0).", [nameof(GraphApiVersion)]);
            }

            if (!string.IsNullOrWhiteSpace(PhoneNumberId) && !NumericIdPattern().IsMatch(PhoneNumberId))
            {
                yield return new ValidationResult("WhatsApp__PhoneNumberId yalnızca rakamlardan oluşmalıdır.", [nameof(PhoneNumberId)]);
            }
        }

        foreach (var result in Templates.Validate())
        {
            yield return result;
        }
    }

    /// <summary>Graph API sürüm biçimi: <c>v27.0</c>.</summary>
    [GeneratedRegex(@"^v[1-9][0-9]{0,2}\.[0-9]{1,2}$", RegexOptions.CultureInvariant)]
    internal static partial Regex GraphVersionPattern();

    [GeneratedRegex(@"^[0-9]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex NumericIdPattern();
}

/// <summary><c>WhatsApp__Templates</c> bölümü.</summary>
internal sealed class WhatsAppTemplateOptions
{
    /// <summary>
    /// Şikâyet bildirimi (pencere kapalıyken). Şablonun gövdesinde tek konumsal parametre (<c>{{1}}</c>) bulunur: şikâyetin
    /// referansı (ör. <c>SS-XXXX-XXXX</c>). Tanımlı değilse pencere dışı bildirim gönderilmez (<c>Failed</c>, <c>window_closed</c>).
    /// </summary>
    public WhatsAppTemplate ComplaintUpdate { get; set; } = new();

    public IEnumerable<ValidationResult> Validate() => ComplaintUpdate.Validate($"{nameof(WhatsAppOptions.Templates)}__{nameof(ComplaintUpdate)}");
}

/// <summary>Tek bir onaylı şablonun adı ve dil kodu.</summary>
internal sealed partial class WhatsAppTemplate
{
    /// <summary>WhatsApp Manager'daki şablon adı (küçük harf, rakam, alt çizgi).</summary>
    public string? Name { get; set; }

    /// <summary>Şablonun dil/yerel ayar kodu, ör. <c>tr</c> veya <c>en_US</c>.</summary>
    public string? LanguageCode { get; set; }

    /// <summary>Ad ve dil kodu ikisi de tanımlı mı.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Name) && !string.IsNullOrWhiteSpace(LanguageCode);

    public IEnumerable<ValidationResult> Validate(string path)
    {
        var hasName = !string.IsNullOrWhiteSpace(Name);
        var hasLanguage = !string.IsNullOrWhiteSpace(LanguageCode);
        if (hasName != hasLanguage)
        {
            yield return new ValidationResult($"WhatsApp__{path}: Name ve LanguageCode birlikte tanımlanmalıdır.", [path]);
            yield break;
        }

        if (hasName && !NamePattern().IsMatch(Name!))
        {
            yield return new ValidationResult($"WhatsApp__{path}__Name yalnızca küçük harf, rakam ve '_' içerebilir.", [path]);
        }

        if (hasLanguage && !LanguagePattern().IsMatch(LanguageCode!))
        {
            yield return new ValidationResult($"WhatsApp__{path}__LanguageCode geçersiz (ör. 'tr', 'en_US').", [path]);
        }
    }

    [GeneratedRegex(@"^[a-z0-9_]{1,512}$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"^[a-z]{2,3}(_[A-Za-z]{2,4})?$", RegexOptions.CultureInvariant)]
    private static partial Regex LanguagePattern();
}
