namespace SokagimdakiSorun.Messaging.Infrastructure.WhatsApp;

/// <summary>
/// WhatsApp sağlayıcısı soyutlaması (ADR-0003 §6). Uygulama katmanı Meta'nın JSON şekline veya Graph API yollarına bağlı değildir.
/// İlk uygulama <see cref="MockWhatsAppClient"/>; gerçek Cloud API adaptörü WS-10'dadır. Seçim: <c>WhatsApp__Provider</c>.
/// </summary>
internal interface IWhatsAppClient
{
    /// <summary>
    /// Serbest metin gönderir. Exception fırlatmak yerine sonucu sınıflandırır: geçici hata yeniden denenir, kalıcı hata denenmez.
    /// Açık bir veritabanı transaction'ı içinde çağrılmaz.
    /// </summary>
    Task<SendResult> SendTextAsync(OutboundText message, CancellationToken cancellationToken);

    /// <summary>Konum mesajı gönderir (serbest mesaj; hizmet penceresi açık olmalıdır). Sınıflandırma <see cref="SendTextAsync"/> ile aynıdır.</summary>
    Task<SendResult> SendLocationAsync(OutboundLocation message, CancellationToken cancellationToken);

    /// <summary>
    /// Onaylı, etkileşimli olmayan şablon mesajı gönderir (pencere dışında gönderilebilen tek tür). Şablonun gövdesi sağlayıcıdadır;
    /// burada yalnızca ad, dil kodu ve gövde parametreleri taşınır.
    /// </summary>
    Task<SendResult> SendTemplateAsync(OutboundTemplate message, CancellationToken cancellationToken);

    /// <summary>Gelen medyayı indirir (Cloud API'de: medya kimliği → kısa ömürlü URL → yetkili indirme).</summary>
    Task<MediaDownloadResult> DownloadMediaAsync(string providerMediaId, CancellationToken cancellationToken);
}

/// <summary>Gönderilecek metin.</summary>
/// <param name="BusinessPhoneNumberId">Gönderen işletme numarası.</param>
/// <param name="RecipientId">Alıcının sağlayıcı kimliği (<c>wa_id</c>). Kişisel veridir; loglanmaz.</param>
/// <param name="Text">Metin. Loglanmaz.</param>
/// <param name="MessageId">Bizim giden mesaj kimliğimiz (izleme ve mock için).</param>
internal sealed record OutboundText(string BusinessPhoneNumberId, string RecipientId, string Text, Guid MessageId);

/// <summary>Gönderilecek konum. Ad ve adres isteğe bağlıdır; hiçbiri loglanmaz.</summary>
internal sealed record OutboundLocation(
    string BusinessPhoneNumberId, string RecipientId, double Latitude, double Longitude, string? Name, string? Address, Guid MessageId);

/// <summary>Gönderilecek şablon.</summary>
/// <param name="TemplateName">Sağlayıcıdaki onaylı şablon adı.</param>
/// <param name="LanguageCode">Şablonun dil kodu (ör. <c>tr</c>).</param>
/// <param name="BodyParameters">Gövdedeki konumsal parametreler (<c>{{1}}</c>, <c>{{2}}</c>…) sırasıyla. Loglanmaz.</param>
internal sealed record OutboundTemplate(
    string BusinessPhoneNumberId, string RecipientId, string TemplateName, string LanguageCode, IReadOnlyList<string> BodyParameters,
    Guid MessageId);

/// <summary>Gönderim sonucu sınıfı.</summary>
internal enum SendOutcome
{
    Sent,
    TransientFailure,
    PermanentFailure,
}

/// <summary>Gönderim sonucu.</summary>
/// <param name="Outcome">Sınıf.</param>
/// <param name="ProviderMessageId">Başarıda sağlayıcının mesaj kimliği.</param>
/// <param name="Error">Hatada kısa, kişisel veri içermeyen açıklama (ör. sağlayıcı hata kodu).</param>
internal sealed record SendResult(SendOutcome Outcome, string? ProviderMessageId, string? Error)
{
    public static SendResult Success(string providerMessageId) => new(SendOutcome.Sent, providerMessageId, null);

    public static SendResult Transient(string error) => new(SendOutcome.TransientFailure, null, error);

    public static SendResult Permanent(string error) => new(SendOutcome.PermanentFailure, null, error);
}

/// <summary>Medya indirme sonucu sınıfı.</summary>
internal enum MediaDownloadOutcome
{
    Downloaded,
    TransientFailure,
    PermanentFailure,
}

/// <summary>Medya indirme sonucu. <see cref="Content"/> çağıran tarafından dispose edilir.</summary>
internal sealed record MediaDownloadResult(MediaDownloadOutcome Outcome, Stream? Content, string? ContentType, string? Error)
{
    public static MediaDownloadResult Success(Stream content, string contentType) =>
        new(MediaDownloadOutcome.Downloaded, content, contentType, null);

    public static MediaDownloadResult Transient(string error) => new(MediaDownloadOutcome.TransientFailure, null, null, error);

    public static MediaDownloadResult Permanent(string error) => new(MediaDownloadOutcome.PermanentFailure, null, null, error);
}
