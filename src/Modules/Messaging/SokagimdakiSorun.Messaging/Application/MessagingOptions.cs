using System.ComponentModel.DataAnnotations;
using SokagimdakiSorun.Messaging.Domain;

namespace SokagimdakiSorun.Messaging.Application;

/// <summary>
/// <c>Messaging</c> yapılandırma bölümü: arka plan işleyicileri ve intake kuralları. Tüm değerlerin varsayılanı vardır.
/// </summary>
internal sealed class MessagingOptions
{
    public const string SectionName = "Messaging";

    /// <summary><c>InboxProcessor</c> çalışsın mı.</summary>
    public bool InboxProcessorEnabled { get; set; } = true;

    /// <summary><c>OutboundSender</c> çalışsın mı.</summary>
    public bool OutboundSenderEnabled { get; set; } = true;

    /// <summary><c>IntakeExpirySweeper</c> çalışsın mı.</summary>
    public bool ExpirySweeperEnabled { get; set; } = true;

    /// <summary>İş yokken iki tarama arası.</summary>
    [Range(typeof(TimeSpan), "00:00:00.010", "00:10:00")]
    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromSeconds(1);

    [Range(1, 500)]
    public int BatchSize { get; set; } = 20;

    /// <summary>Inbox olayı / giden mesaj için azami deneme; sonra dead-letter / <c>Failed</c>.</summary>
    [Range(1, 100)]
    public int MaxAttempts { get; set; } = 8;

    [Range(typeof(TimeSpan), "00:00:00.001", "01:00:00")]
    public TimeSpan BaseRetryDelay { get; set; } = TimeSpan.FromSeconds(5);

    [Range(typeof(TimeSpan), "00:00:00.001", "1.00:00:00")]
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Alınan satırın kira süresi; işleyici çökerse satır bu süreden sonra yeniden alınır.</summary>
    [Range(typeof(TimeSpan), "00:00:01", "01:00:00")]
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Intake hareketsizlik süresi.</summary>
    [Range(typeof(TimeSpan), "00:01:00", "7.00:00:00")]
    public TimeSpan IntakeTimeout { get; set; } = TimeSpan.FromHours(2);

    [Range(typeof(TimeSpan), "00:00:01", "01:00:00")]
    public TimeSpan ExpirySweepInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Serbest metin gönderilebilen hizmet penceresi (son gelen mesajdan itibaren). Gerçek değer V-8'de doğrulanır.</summary>
    [Range(typeof(TimeSpan), "00:01:00", "7.00:00:00")]
    public TimeSpan ServiceWindow { get; set; } = TimeSpan.FromHours(24);

    [Range(1, 10)]
    public int MaxPhotosPerComplaint { get; set; } = 3;

    [Range(1, 20)]
    public int MaxInvalidInputs { get; set; } = 3;

    public IntakeRules ToIntakeRules() => new(IntakeTimeout, MaxPhotosPerComplaint, MaxInvalidInputs);
}

/// <summary>Üstel backoff + "equal jitter" (ARCHITECTURE §5.3).</summary>
internal static class RetryDelay
{
    public static TimeSpan Compute(int attempt, TimeSpan baseDelay, TimeSpan maxDelay, double jitterSample)
    {
        var exponential = baseDelay.TotalMilliseconds * Math.Pow(2, Math.Clamp(attempt - 1, 0, 30));
        var capped = Math.Min(exponential, maxDelay.TotalMilliseconds);
        return TimeSpan.FromMilliseconds((capped / 2) + (capped / 2 * Math.Clamp(jitterSample, 0, 1)));
    }
}
