using SokagimdakiSorun.Complaints.Contracts;

namespace SokagimdakiSorun.Messaging.Domain;

/// <summary>
/// Şikâyetçi bildirim politikası — <b>tek tablo</b> (API_CONTRACT_STRATEGY §7.2). Event handler'ları yalnızca bunu sorar;
/// kural başka yerde kopyalanmaz. Metinler yalnızca şikâyetçiye gösterilebilir <c>reporterNote</c>'u içerir, iç gerekçe event'te
/// zaten yoktur.
/// </summary>
internal static class NotificationPolicy
{
    private sealed record Rule(Func<ComplaintStatus, bool> AppliesFrom, Func<string, string?, string> Text);

    private static readonly Rule Never = new(_ => false, (_, _) => string.Empty);

    private static readonly IReadOnlyDictionary<ComplaintStatus, Rule> Rules = new Dictionary<ComplaintStatus, Rule>
    {
        [ComplaintStatus.AwaitingInformation] = new(_ => true, (reference, note) =>
            $"{reference} numaralı başvurunuz için ek bilgiye ihtiyacımız var:\n{note}\n\n"
            + "Cevabınızı bu sohbete yazabilir veya fotoğraf gönderebilirsiniz. Başvurunuzu geri çekmek için \"geri çek\" yazın."),
        [ComplaintStatus.InProgress] = new(_ => true, (reference, note) =>
            WithNote($"{reference} numaralı başvurunuz üzerinde çalışma başladı.", note)),
        [ComplaintStatus.Resolved] = new(_ => true, (reference, note) =>
            WithNote($"{reference} numaralı başvurunuzdaki sorunun çözüldüğü bildirildi.", note)),
        [ComplaintStatus.Rejected] = new(_ => true, (reference, note) =>
            WithNote($"{reference} numaralı başvurunuz değerlendirme sonucunda kabul edilmedi.", note)),
        [ComplaintStatus.Duplicate] = new(_ => true, (reference, note) =>
            WithNote($"{reference} numaralı başvurunuz daha önce bildirilmiş bir sorunla aynı olduğu için o kayıt üzerinden takip edilecek.", note)),
        [ComplaintStatus.Withdrawn] = new(_ => true, (reference, note) =>
            WithNote($"{reference} numaralı başvurunuz geri çekildi.", note)),
        [ComplaintStatus.Closed] = new(from => from == ComplaintStatus.Accepted, (reference, note) =>
            WithNote($"{reference} numaralı başvurunuz kapatıldı.", note)),
        [ComplaintStatus.UnderReview] = Never,
        [ComplaintStatus.Accepted] = Never,
        [ComplaintStatus.Assigned] = Never,
        [ComplaintStatus.Spam] = Never,
    };

    /// <summary><c>Submitted</c>: her zaman, referans numarasıyla.</summary>
    public static string ForSubmitted(string publicReference) =>
        $"Başvurunuz alındı. Referans numaranız: {publicReference}. Başvurunuzun durumu değiştikçe sizi bu sohbetten bilgilendireceğiz.";

    /// <summary>Durum değişikliği için gönderilecek metin; bildirim gerekmiyorsa <see langword="null"/>.</summary>
    public static string? ForStatusChange(ComplaintStatus from, ComplaintStatus to, string publicReference, string? reporterNote)
    {
        if (!Rules.TryGetValue(to, out var rule) || !rule.AppliesFrom(from))
        {
            return null;
        }

        return rule.Text(publicReference, string.IsNullOrWhiteSpace(reporterNote) ? null : reporterNote.Trim());
    }

    /// <summary>Tabloda tanımlı durumlar (test: her durum için açık karar var mı).</summary>
    internal static IEnumerable<ComplaintStatus> CoveredStatuses => Rules.Keys;

    private static string WithNote(string text, string? note) => note is null ? text : $"{text}\nNot: {note}";
}
