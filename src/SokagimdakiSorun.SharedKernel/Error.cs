namespace SokagimdakiSorun.SharedKernel;

/// <summary>
/// Beklenen bir iş hatasının türü. Host bu türü HTTP durum koduna eşler
/// (docs/API_CONTRACT_STRATEGY.md §4); modüller HTTP durum kodu bilmez.
/// </summary>
public enum ErrorType
{
    /// <summary>Sınıflandırılmamış hata (500 olarak ele alınır).</summary>
    Failure = 0,

    /// <summary>Girdi biçimi veya doğrulama hatası (400).</summary>
    Validation,

    /// <summary>Kaynak yok veya kullanıcının görme yetkisi yok (404).</summary>
    NotFound,

    /// <summary>Yasak durum geçişi veya eşzamanlılık çakışması (409).</summary>
    Conflict,

    /// <summary>Biçim doğru ama iş kuralı ihlali (422).</summary>
    BusinessRule,

    /// <summary>Kimlik doğrulanmadı (401).</summary>
    Unauthorized,

    /// <summary>Kimlik var, yetki yok (403).</summary>
    Forbidden,
}

/// <summary>
/// Beklenen bir iş hatası. <see cref="Code"/> kararlı ve makinece okunabilir
/// <c>&lt;alan&gt;.&lt;hata&gt;</c> biçimindedir; istemci davranışını buna göre seçer.
/// </summary>
public sealed record Error
{
    /// <summary>Yeni bir hata oluşturur.</summary>
    /// <param name="code">Kararlı hata kodu, ör. <c>complaint.invalid_transition</c>.</param>
    /// <param name="message">Kullanıcıya gösterilebilecek kısa açıklama. Kişisel veri veya iç ayrıntı içermez.</param>
    /// <param name="type">Hatanın türü.</param>
    /// <param name="validationErrors">Yalnızca doğrulama hatalarında: alan adı → mesaj listesi.</param>
    public Error(string code, string message, ErrorType type, IReadOnlyDictionary<string, string[]>? validationErrors = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        Code = code;
        Message = message;
        Type = type;
        ValidationErrors = validationErrors;
    }

    /// <summary>Kararlı hata kodu.</summary>
    public string Code { get; }

    /// <summary>Kısa açıklama.</summary>
    public string Message { get; }

    /// <summary>Hatanın türü.</summary>
    public ErrorType Type { get; }

    /// <summary>Alan bazlı doğrulama mesajları; doğrulama dışı hatalarda <see langword="null"/>.</summary>
    public IReadOnlyDictionary<string, string[]>? ValidationErrors { get; }

    /// <summary>Doğrulama hatası.</summary>
    public static Error Validation(string code, string message, IReadOnlyDictionary<string, string[]>? validationErrors = null) =>
        new(code, message, ErrorType.Validation, validationErrors);

    /// <summary>Kaynak bulunamadı (veya görme yetkisi yok).</summary>
    public static Error NotFound(string code, string message) => new(code, message, ErrorType.NotFound);

    /// <summary>Çakışma.</summary>
    public static Error Conflict(string code, string message) => new(code, message, ErrorType.Conflict);

    /// <summary>İş kuralı ihlali.</summary>
    public static Error BusinessRule(string code, string message) => new(code, message, ErrorType.BusinessRule);

    /// <summary>Kimlik doğrulanmadı.</summary>
    public static Error Unauthorized(string code, string message) => new(code, message, ErrorType.Unauthorized);

    /// <summary>Yetki yok.</summary>
    public static Error Forbidden(string code, string message) => new(code, message, ErrorType.Forbidden);

    /// <summary>Sınıflandırılmamış hata.</summary>
    public static Error Failure(string code, string message) => new(code, message, ErrorType.Failure);
}
