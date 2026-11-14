// Özet sayfası: URL parametreleri → Reporting sorgusu ve API yanıtı → gösterim modeli.
// Sayılar YALNIZCA API'den gelir; burada toplama/oran/istatistik hesaplanmaz. Tek hesap, çubuk genişliği (görsel ölçek).
// Tarih doğrulaması da API'dedir (400 validation.failed, alan bazında Türkçe mesaj); panel değeri olduğu gibi iletir.
import type { components } from "../../api/schema.d.ts";
import type { QueryValue } from "../api/client.ts";
import { statusLabel } from "../format.ts";

type Schemas = components["schemas"];
export type SummaryReport = Schemas["SummaryReport"];
export type CategoryReport = Schemas["CategoryReport"];
export type TimelineReport = Schemas["TimelineReport"];
export type TimelineBucket = Schemas["TimelineBucket"];

export type SearchParams = Record<string, string | string[] | undefined>;

export interface DashboardFilters {
  from?: string;
  to?: string;
  bucket: TimelineBucket;
}

export const BUCKET_OPTIONS: ReadonlyArray<{ value: TimelineBucket; label: string }> = [
  { value: "Day", label: "Günlük" },
  { value: "Week", label: "Haftalık" },
];

function first(value: string | string[] | undefined): string | undefined {
  const v = Array.isArray(value) ? value[0] : value;
  const trimmed = v?.trim();
  // Uzunluk sınırı yalnızca URL'nin şişmesini önler; biçim kuralı API'dedir.
  return trimmed ? trimmed.slice(0, 32) : undefined;
}

export function parseDashboardFilters(params: SearchParams): DashboardFilters {
  const bucket = first(params.bucket);
  return {
    from: first(params.from),
    to: first(params.to),
    bucket: BUCKET_OPTIONS.find((o) => o.value === bucket)?.value ?? "Day",
  };
}

/** `summary` ve `by-category`: aralık verilmezse API tüm zamanları sayar. */
export function toRangeQuery(filters: DashboardFilters): Record<string, QueryValue> {
  return { from: filters.from, to: filters.to };
}

/** `timeline`: aralık verilmezse API bugünle biten 30 günü döner. */
export function toTimelineQuery(filters: DashboardFilters): Record<string, QueryValue> {
  return { from: filters.from, to: filters.to, bucket: filters.bucket };
}

/** Çubuk genişliği (%): yalnızca görsel ölçek, en büyük değere göre. */
export function barPercent(value: number, max: number): number {
  if (!Number.isFinite(value) || !Number.isFinite(max) || max <= 0 || value <= 0) return 0;
  return Math.min(100, (value / max) * 100);
}

function count(value: number | string | null | undefined): number {
  const n = Number(value ?? 0);
  return Number.isFinite(n) ? n : 0;
}

export interface Bar {
  key: string;
  label: string;
  count: number;
  percent: number;
}

function toBars(items: { key: string; label: string; count: number }[]): Bar[] {
  const max = items.reduce((m, i) => Math.max(m, i.count), 0);
  return items.map((i) => ({ ...i, percent: barPercent(i.count, max) }));
}

export function summaryView(report: SummaryReport) {
  return {
    total: count(report.total),
    bars: toBars(report.byStatus.map((s) => ({ key: s.status, label: statusLabel(s.status), count: count(s.count) }))),
  };
}

export function categoryView(report: CategoryReport) {
  return {
    total: count(report.total),
    bars: toBars(
      report.items.map((c) => ({ key: c.categoryId, label: c.categoryName || c.categoryCode, count: count(c.count) })),
    ),
  };
}

export interface TimelineColumn {
  key: string;
  label: string;
  title: string;
  opened: number;
  closed: number;
  openedPercent: number;
  closedPercent: number;
}

export function timelineView(report: TimelineReport) {
  const points = report.points.map((p) => ({ start: p.start, end: p.end, opened: count(p.opened), closed: count(p.closed) }));
  const max = points.reduce((m, p) => Math.max(m, p.opened, p.closed), 0);
  const columns: TimelineColumn[] = points.map((p) => {
    const range = p.start === p.end ? formatDay(p.start) : `${formatDay(p.start)} – ${formatDay(p.end)}`;
    return {
      key: p.start,
      label: formatDay(p.start, true),
      title: range,
      opened: p.opened,
      closed: p.closed,
      openedPercent: barPercent(p.opened, max),
      closedPercent: barPercent(p.closed, max),
    };
  });
  const totalOpened = count(report.totalOpened);
  const totalClosed = count(report.totalClosed);
  return { columns, totalOpened, totalClosed, isEmpty: totalOpened === 0 && totalClosed === 0 };
}

const DAY = /^(\d{4})-(\d{2})-(\d{2})$/;
const dayLong = new Intl.DateTimeFormat("tr-TR", { timeZone: "UTC", day: "numeric", month: "long", year: "numeric" });
const dayShort = new Intl.DateTimeFormat("tr-TR", { timeZone: "UTC", day: "numeric", month: "short" });

/**
 * API'nin `yyyy-MM-dd` değeri zaten `Europe/Istanbul` yerel günüdür (saat yok). Gün kaymasın diye
 * UTC gece yarısı olarak yorumlanıp UTC'de biçimlenir.
 */
export function formatDay(value: string | null | undefined, short = false): string {
  const m = value ? DAY.exec(value) : null;
  if (!m) return "—";
  const date = new Date(Date.UTC(Number(m[1]), Number(m[2]) - 1, Number(m[3])));
  return Number.isNaN(date.getTime()) ? "—" : (short ? dayShort : dayLong).format(date);
}

/** Yanıttaki aralık; `null` uçlar "tüm zamanlar" anlamına gelir. */
export function rangeText(from: string | null | undefined, to: string | null | undefined): string {
  if (!from && !to) return "Tüm zamanlar";
  if (from && to) return `${formatDay(from)} – ${formatDay(to)}`;
  return from ? `${formatDay(from)} ve sonrası` : `${formatDay(to)} ve öncesi`;
}

export const FIELD_LABELS: Record<string, string> = {
  from: "Başlangıç",
  to: "Bitiş",
  bucket: "Aralık türü",
};
