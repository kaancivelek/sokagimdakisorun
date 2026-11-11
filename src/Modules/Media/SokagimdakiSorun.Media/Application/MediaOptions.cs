using System.ComponentModel.DataAnnotations;

namespace SokagimdakiSorun.Media.Application;

/// <summary>
/// <c>Media</c> yapılandırma bölümü (ortam değişkeni ayırıcısı <c>__</c>, ör. <c>Media__RootPath</c>).
/// <see cref="RootPath"/> dışındaki tüm değerlerin varsayılanı vardır.
/// </summary>
internal sealed class MediaOptions
{
    public const string SectionName = "Media";

    /// <summary>Bayt cinsinden üst sınırın sert tavanı: içerik doğrulama için bellekte tamponlanır (ADR-0004 olumsuz sonuçlar).</summary>
    public const long MaxFileSizeCeiling = 50L * 1024 * 1024;

    /// <summary>
    /// Medya kök dizini. Yerelde <c>./.local/media</c> (git dışı), Railway'de Volume'un bağlandığı yol. Göreli yol
    /// sürecin çalışma dizinine göre çözülür. Tanımlı değilse dosya saklanamaz ve <c>/health/ready</c> "Unhealthy" döner.
    /// </summary>
    public string? RootPath { get; set; }

    /// <summary>Kabul edilen azami dosya boyutu (bayt). Varsayılan 10 MiB.</summary>
    [Range(1L, MaxFileSizeCeiling)]
    public long MaxFileSizeBytes { get; set; } = 10L * 1024 * 1024;

    /// <summary>Bu süreden uzun <c>Temporary</c> kalan dosyalar silinir. Intake zaman aşımından uzun olmalıdır.</summary>
    [Range(typeof(TimeSpan), "00:01:00", "30.00:00:00")]
    public TimeSpan TemporaryRetention { get; set; } = TimeSpan.FromHours(48);

    /// <summary>
    /// Metadata'sı olmayan dosya, ancak bu süreden eskiyse yetim sayılır. "Dosyayı yaz → metadata'yı kaydet" sırasının
    /// arasındaki dosyalar böylece silinmez.
    /// </summary>
    [Range(typeof(TimeSpan), "00:01:00", "7.00:00:00")]
    public TimeSpan OrphanFileGracePeriod { get; set; } = TimeSpan.FromHours(1);

    /// <summary><c>OrphanMediaCleaner</c> turları arasındaki süre.</summary>
    [Range(typeof(TimeSpan), "00:00:01", "1.00:00:00")]
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Temizlikte tek sorguda işlenen azami kayıt/dosya sayısı.</summary>
    [Range(1, 1000)]
    public int CleanupBatchSize { get; set; } = 100;

    /// <summary><c>false</c> ise <c>OrphanMediaCleaner</c> çalışmaz.</summary>
    public bool CleanupEnabled { get; set; } = true;
}
