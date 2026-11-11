// Gösterim yardımcıları. Durum adları yalnızca görüntülenir (DOMAIN_MODEL §4.1); geçiş kuralı burada YOKTUR.
import type { components } from "../api/schema.d.ts";

export type ComplaintStatus = components["schemas"]["ComplaintStatus"];

const dateTime = new Intl.DateTimeFormat("tr-TR", {
  timeZone: "Europe/Istanbul",
  dateStyle: "medium",
  timeStyle: "short",
});

/** API her zaman UTC döner; gösterim `Europe/Istanbul`. */
export function formatDateTime(value: string | null | undefined): string {
  if (!value) return "—";
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? "—" : dateTime.format(date);
}

export const STATUS_LABELS: Record<ComplaintStatus, string> = {
  Submitted: "Alındı",
  UnderReview: "İnceleniyor",
  AwaitingInformation: "Bilgi bekleniyor",
  Accepted: "Kabul edildi",
  Assigned: "Atandı",
  InProgress: "Çalışılıyor",
  Resolved: "Çözüldü",
  Closed: "Kapatıldı",
  Rejected: "Reddedildi",
  Duplicate: "Tekrar",
  Spam: "Spam",
  Withdrawn: "Geri çekildi",
};

export const STATUS_VALUES = Object.keys(STATUS_LABELS) as ComplaintStatus[];

export function statusLabel(status: string | null | undefined): string {
  if (!status) return "—";
  return STATUS_LABELS[status as ComplaintStatus] ?? status;
}

export const ACTOR_KIND_LABELS: Record<string, string> = { Staff: "Personel", Reporter: "Şikâyetçi", System: "Sistem" };
export const SOURCE_LABELS: Record<string, string> = { Panel: "Panel", WhatsApp: "WhatsApp", System: "Sistem" };
export const VISIBILITY_LABELS: Record<string, string> = {
  Internal: "İç not (şikâyetçiye gitmez)",
  ReporterVisible: "Şikâyetçiye görünür",
};
export const ATTACHMENT_SOURCE_LABELS: Record<string, string> = {
  Intake: "Başvuru",
  InformationReply: "Bilgi yanıtı",
  Panel: "Panel",
};
export const USER_STATUS_LABELS: Record<string, string> = {
  Active: "Aktif",
  Suspended: "Askıda",
  Anonymized: "Anonimleştirildi",
};
export const ROLE_LABELS: Record<string, string> = { Admin: "Yönetici", Moderator: "Moderatör" };

export function label(map: Record<string, string>, value: string | null | undefined): string {
  if (!value) return "—";
  return map[value] ?? value;
}

/** Tarih girdisini (`YYYY-MM-DD`, İstanbul günü) UTC ISO aralık sınırına çevirir. İstanbul sabit UTC+3'tür. */
export function istanbulDayStartUtc(day: string | undefined): string | undefined {
  if (!day || !/^\d{4}-\d{2}-\d{2}$/.test(day)) return undefined;
  const date = new Date(`${day}T00:00:00+03:00`);
  return Number.isNaN(date.getTime()) ? undefined : date.toISOString();
}

/** Bitiş günü dâhil: API aralığı [from, to) olduğu için ertesi günün başlangıcı gönderilir. */
export function istanbulDayEndExclusiveUtc(day: string | undefined): string | undefined {
  const start = istanbulDayStartUtc(day);
  if (!start) return undefined;
  return new Date(new Date(start).getTime() + 24 * 60 * 60 * 1000).toISOString();
}
