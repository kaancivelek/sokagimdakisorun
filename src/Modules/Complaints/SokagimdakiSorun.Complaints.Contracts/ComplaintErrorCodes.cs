namespace SokagimdakiSorun.Complaints.Contracts;

/// <summary>Complaints modülünün kararlı hata kodları (<c>SharedKernel.Error.Code</c>; API_CONTRACT_STRATEGY §4).</summary>
public static class ComplaintErrorCodes
{
    /// <summary>Şikâyet yok veya çağıranın görme yetkisi yok (404).</summary>
    public const string NotFound = "complaint.not_found";

    /// <summary>Geçiş tablosunda olmayan geçiş (409).</summary>
    public const string InvalidTransition = "complaint.invalid_transition";

    /// <summary>İstemcinin gönderdiği <c>version</c> güncel değil veya eşzamanlı güncelleme (409).</summary>
    public const string ConcurrencyConflict = "concurrency.conflict";

    /// <summary>Komut bu kullanıcıya açık değil (ör. <c>Closed</c> yeniden açma yalnızca Admin) (403).</summary>
    public const string ActionForbidden = "complaint.action_forbidden";

    /// <summary>İstek doğrulanamadı (400); ayrıntı <c>errors</c> alanında.</summary>
    public const string ValidationFailed = "validation.failed";

    /// <summary>Tekrar hedefi geçersiz: kendisi, <c>Duplicate</c> durumunda veya bulunamadı (422).</summary>
    public const string DuplicateTargetInvalid = "complaint.duplicate_target_invalid";

    /// <summary>Hizmet birimi yok veya etkin değil (422).</summary>
    public const string ServiceUnitInvalid = "complaint.service_unit_invalid";

    /// <summary>Atanan kişi hizmet biriminin üyesi değil (422).</summary>
    public const string AssigneeNotMember = "complaint.assignee_not_member";

    /// <summary>Kategori yok veya etkin değil (intake) (400).</summary>
    public const string CategoryInvalid = "complaint.category_invalid";

    /// <summary>Şikâyetçi kaydı geçersiz (yok veya şikâyetçi değil) (400).</summary>
    public const string ReporterInvalid = "complaint.reporter_invalid";

    /// <summary>Eklenecek dosya bulunamadı veya silinmiş (422).</summary>
    public const string AttachmentUnavailable = "complaint.attachment_unavailable";
}
