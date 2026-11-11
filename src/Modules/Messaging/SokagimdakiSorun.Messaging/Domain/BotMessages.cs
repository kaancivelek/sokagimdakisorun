using System.Globalization;
using System.Text;
using SokagimdakiSorun.Complaints.Contracts;

namespace SokagimdakiSorun.Messaging.Domain;

/// <summary>
/// Şikâyetçiye giden tüm serbest metinler tek yerde (Türkçe). Gerçek entegrasyonda pencere dışı bildirimler için Meta onaylı
/// şablonlar gerekir (WS-10); bu metinler yalnızca 24 saatlik pencere içinde gönderilir.
/// </summary>
internal static class BotMessages
{
    public const string CancelHint = "İstediğiniz an \"iptal\" yazarak vazgeçebilirsiniz.";

    public static string Welcome(IReadOnlyList<ComplaintCategorySummary> menu) =>
        "Merhaba, Sokağımdaki Sorun'a hoş geldiniz. Bildirmek istediğiniz sorunun kategorisini numarasıyla seçin:\n"
        + Menu(menu) + "\n" + CancelHint;

    public static string ChooseCategoryAgain(IReadOnlyList<ComplaintCategorySummary> menu) =>
        "Lütfen listeden bir kategori numarası seçin:\n" + Menu(menu);

    public static string CategoryNoLongerAvailable(IReadOnlyList<ComplaintCategorySummary> menu) =>
        "Seçtiğiniz kategori artık kullanılmıyor. Lütfen yeniden seçin:\n" + Menu(menu);

    public const string NoCategories =
        "Şu anda başvuru alamıyoruz. Lütfen daha sonra tekrar yazın.";

    public static string AskDescription(int maxLength) =>
        string.Create(CultureInfo.InvariantCulture, $"Sorunu kısaca anlatır mısınız? (en fazla {maxLength} karakter)");

    public static string DescriptionInvalid(int maxLength) =>
        string.Create(CultureInfo.InvariantCulture, $"Lütfen sorunu metin olarak yazın (en fazla {maxLength} karakter).");

    public const string AskLocation =
        "Sorunun konumunu WhatsApp'tan \"Konum\" olarak paylaşın veya açık adresi yazın.";

    public static string LocationInvalid(int maxLength) =>
        string.Create(CultureInfo.InvariantCulture,
            $"Konumu WhatsApp'tan \"Konum\" olarak paylaşın veya açık adresi yazın (en fazla {maxLength} karakter).");

    public const string AskMedia =
        "Varsa sorunun bir fotoğrafını gönderin. Fotoğraf eklemek istemiyorsanız \"atla\" yazın.";

    public const string MediaRejected =
        "Fotoğraf kabul edilemedi (yalnızca JPEG, PNG veya WebP; boyut sınırı aşılmamalı). Başka bir fotoğraf gönderin veya \"atla\" yazın.";

    public static string TooManyPhotos(int max) =>
        string.Create(CultureInfo.InvariantCulture, $"En fazla {max} fotoğraf ekleyebilirsiniz.");

    public const string Unsupported =
        "Bu mesaj türü (ses, video, belge vb.) desteklenmiyor. Yalnızca metin, konum ve fotoğraf gönderebilirsiniz.";

    public const string Cancelled =
        "Başvurunuz iptal edildi. Yeni bir sorun bildirmek için istediğiniz zaman yazabilirsiniz.";

    public const string Expired =
        "Uzun süre yanıt alınamadığı için başvurunuz tamamlanmadan kapatıldı. Yeni bir bildirim için tekrar yazabilirsiniz.";

    public const string NeedsHumanHelp =
        "Başvurunuzu bu kanaldan tamamlayamadık. Yeniden denemek için tekrar yazabilirsiniz.";

    public const string SubmitFailed =
        "Başvurunuz şu anda kaydedilemedi. Lütfen biraz sonra \"1\" yazarak yeniden onaylayın.";

    public const string AttachmentDropped =
        "Eklediğiniz fotoğraflar artık kullanılamıyor ve başvurudan çıkarıldı.";

    public static string Summary(IntakeSession session, IReadOnlyList<ComplaintCategorySummary> categories)
    {
        ArgumentNullException.ThrowIfNull(session);
        var category = categories.FirstOrDefault(c => c.Id == session.CategoryId)?.Name ?? "(kategori artık kullanılmıyor)";
        var location = session.Latitude is { } lat && session.Longitude is { } lon
            ? string.Create(CultureInfo.InvariantCulture, $"paylaşılan konum ({lat:F5}, {lon:F5})")
            : session.AddressText;

        var builder = new StringBuilder("Başvuru özeti:\n");
        builder.Append("Kategori: ").Append(category).Append('\n');
        builder.Append("Açıklama: ").Append(session.DescriptionDraft).Append('\n');
        builder.Append("Konum: ").Append(location).Append('\n');
        builder.Append(CultureInfo.InvariantCulture, $"Fotoğraf: {session.MediaFileIds.Count} adet\n\n");
        builder.Append("1. Onayla\n2. Kategoriyi düzelt\n3. Açıklamayı düzelt\n4. Konumu düzelt\n");
        builder.Append(CancelHint);
        return builder.ToString();
    }

    public static string InformationReceived(string publicReference) =>
        $"Ek bilginiz {publicReference} numaralı başvurunuza eklendi. Teşekkür ederiz.";

    public const string InformationMediaRejected =
        "Fotoğraf kabul edilemedi (yalnızca JPEG, PNG veya WebP; boyut sınırı aşılmamalı). Ek bilgiyi metin veya başka bir fotoğraf olarak gönderebilirsiniz.";

    public const string InformationFailed =
        "Ek bilginiz şu anda iletilemedi. Lütfen biraz sonra yeniden gönderin.";

    public const string InformationNeedsContent =
        "Lütfen ek bilgiyi metin veya fotoğraf olarak gönderin.";

    private static string Menu(IReadOnlyList<ComplaintCategorySummary> menu)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < menu.Count; i++)
        {
            builder.Append(CultureInfo.InvariantCulture, $"{i + 1}. {menu[i].Name}\n");
        }

        return builder.ToString().TrimEnd('\n');
    }
}
