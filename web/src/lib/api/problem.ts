import type { components } from "../../api/schema.d.ts";

/**
 * Üretilen `ProblemDetails` şeması RFC 9457 eklenti alanlarını (`code`, `errors`, `traceId`) içermiyor
 * (eksik sözleşme raporu, PR açıklaması). API bu alanları hatalarda gönderdiği için (API_CONTRACT_STRATEGY §4)
 * üretilen tip yalnızca bu üç alanla genişletilir; elle DTO yazılmaz.
 */
export type ApiProblem = components["schemas"]["ProblemDetails"] & {
  code?: string | null;
  errors?: Record<string, string[]> | null;
  traceId?: string | null;
};

/** Kullanıcıya gösterilecek mesaj `code` alanına göre seçilir; `title` metnine göre değil. */
const MESSAGES: Record<string, string> = {
  "auth.invalid_credentials": "E-posta veya parola hatalı ya da hesap geçici olarak kilitli.",
  "auth.invalid_refresh_token": "Oturumunuzun süresi doldu. Lütfen yeniden giriş yapın.",
  "auth.unauthenticated": "Oturumunuzun süresi doldu. Lütfen yeniden giriş yapın.",
  "auth.forbidden": "Bu işlem için yetkiniz yok.",
  "validation.failed": "Girilen bilgileri kontrol edin.",
  "request.invalid": "İstek geçersiz.",
  "rate_limit.exceeded": "Çok fazla deneme yapıldı. Lütfen biraz sonra tekrar deneyin.",
  "concurrency.conflict":
    "Bu kayıt siz işlem yaparken başka biri tarafından değiştirildi. Güncel veri yüklendi; lütfen kontrol edip tekrar deneyin.",
  "complaint.not_found": "Şikâyet bulunamadı veya görüntüleme yetkiniz yok.",
  "complaint.invalid_transition": "Şikâyetin mevcut durumunda bu işleme izin verilmiyor.",
  "complaint.action_forbidden": "Bu işlemi yapma yetkiniz yok.",
  "complaint.duplicate_target_invalid": "Seçilen hedef şikâyet tekrar kaydı için geçerli değil.",
  "complaint.service_unit_invalid": "Seçilen hizmet birimi geçerli değil veya aktif değil.",
  "complaint.assignee_not_member": "Seçilen kişi bu hizmet biriminin üyesi değil.",
  "complaint.attachment_not_found": "Ek bulunamadı.",
  "category.not_found": "Kategori bulunamadı.",
  "category.code_taken": "Bu kod başka bir kategoride kullanılıyor.",
  "service_unit.not_found": "Hizmet birimi bulunamadı.",
  "service_unit.code_taken": "Bu kod başka bir hizmet biriminde kullanılıyor.",
  "service_unit.member_invalid": "Bu kullanıcı birime üye yapılamaz.",
  "resource.not_found": "Kayıt bulunamadı.",
  "server.error": "Beklenmeyen bir hata oluştu. Lütfen tekrar deneyin.",
  "network.unavailable": "API'ye ulaşılamadı. Lütfen biraz sonra tekrar deneyin.",
};

const FALLBACK_BY_STATUS: Record<number, string> = {
  400: "İstek geçersiz.",
  401: "Oturumunuzun süresi doldu. Lütfen yeniden giriş yapın.",
  403: "Bu işlem için yetkiniz yok.",
  404: "Kayıt bulunamadı.",
  409: "İşlem mevcut kayıt durumuyla çakışıyor.",
  422: "İşlem iş kurallarına uymuyor.",
  429: "Çok fazla deneme yapıldı. Lütfen biraz sonra tekrar deneyin.",
};

const GENERIC = "Beklenmeyen bir hata oluştu. Lütfen tekrar deneyin.";

export function problemMessage(problem: ApiProblem): string {
  const code = problem.code ?? undefined;
  if (code && MESSAGES[code]) return MESSAGES[code];
  const status = Number(problem.status ?? 0);
  // Kataloğumuzda olmayan bir kod: API'nin `detail` metni Türkçedir ve iç ayrıntı sızdırmaz (§4).
  if (code && problem.detail) return problem.detail;
  return FALLBACK_BY_STATUS[status] ?? GENERIC;
}

/** Doğrulama hatalarındaki alan mesajları (API'den gelir; panel kendi kuralını yazmaz). */
export function problemFieldErrors(problem: ApiProblem): Record<string, string[]> {
  const result: Record<string, string[]> = {};
  for (const [key, messages] of Object.entries(problem.errors ?? {})) {
    const normalized = key.length > 0 ? key[0]!.toLowerCase() + key.slice(1) : key;
    result[normalized] = messages;
  }
  return result;
}
