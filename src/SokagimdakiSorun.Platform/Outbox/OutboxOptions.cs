using System.ComponentModel.DataAnnotations;

namespace SokagimdakiSorun.Platform.Outbox;

/// <summary><c>Outbox</c> yapılandırma bölümü. Tüm değerlerin varsayılanı vardır; ortam değişkeni gerekmez.</summary>
public sealed class OutboxOptions
{
    public const string SectionName = "Outbox";

    /// <summary><c>false</c> ise dağıtıcı çalışmaz (ör. bakım). Varsayılan <c>true</c>.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Bekleyen satır yokken iki tarama arasındaki süre.</summary>
    [Range(typeof(TimeSpan), "00:00:00.010", "00:10:00")]
    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Tek transaction'da kilitlenip işlenen azami satır sayısı.</summary>
    [Range(1, 500)]
    public int BatchSize { get; set; } = 20;

    /// <summary>Bu kadar başarısız denemeden sonra satır dead-letter olur.</summary>
    [Range(1, 100)]
    public int MaxAttempts { get; set; } = 8;

    /// <summary>Üstel backoff'un ilk adımı: deneme n için taban × 2^(n-1), <see cref="MaxRetryDelay"/> ile sınırlı.</summary>
    [Range(typeof(TimeSpan), "00:00:00.001", "01:00:00")]
    public TimeSpan BaseRetryDelay { get; set; } = TimeSpan.FromSeconds(5);

    [Range(typeof(TimeSpan), "00:00:00.001", "1.00:00:00")]
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromMinutes(15);
}
