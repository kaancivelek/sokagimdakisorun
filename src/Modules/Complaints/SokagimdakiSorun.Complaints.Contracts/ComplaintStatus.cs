using System.Text.Json.Serialization;

namespace SokagimdakiSorun.Complaints.Contracts;

/// <summary>
/// Şikâyet durumu (docs/DOMAIN_MODEL.md §4.1). Geçişler yalnızca Complaints modülünün komutlarıyla olur. JSON'da (outbox
/// event yükü dâhil) her zaman metin olarak yazılır; kalıcı yük üye sırasına bağlı değildir.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ComplaintStatus>))]
public enum ComplaintStatus
{
    /// <summary>Şikâyetçinin onayladığı, sisteme alınmış başvuru.</summary>
    Submitted,

    /// <summary>Moderatör ilk incelemeyi yapıyor.</summary>
    UnderReview,

    /// <summary>Şikâyetçiden ek bilgi bekleniyor.</summary>
    AwaitingInformation,

    /// <summary>Geçerli bulundu, işleme kabul edildi.</summary>
    Accepted,

    /// <summary>Hizmet birimine (ve isteğe bağlı kişiye) atandı.</summary>
    Assigned,

    /// <summary>Çözüm çalışması sürüyor.</summary>
    InProgress,

    /// <summary>Sorumlu taraf çözüldüğünü bildirdi.</summary>
    Resolved,

    /// <summary>Son kontrol yapıldı, kayıt kapatıldı (yetkili yeniden açabilir).</summary>
    Closed,

    /// <summary>Kapsam dışı veya geçersiz (terminal).</summary>
    Rejected,

    /// <summary>Başka bir şikâyetin tekrarı (terminal).</summary>
    Duplicate,

    /// <summary>Kötüye kullanım (terminal).</summary>
    Spam,

    /// <summary>Şikâyetçi geri çekti (terminal).</summary>
    Withdrawn,
}

/// <summary>Konumun nasıl alındığı.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<LocationSource>))]
public enum LocationSource
{
    /// <summary>WhatsApp konum mesajı (koordinat).</summary>
    WhatsAppPin,

    /// <summary>Açık adres metni.</summary>
    TextAddress,
}
