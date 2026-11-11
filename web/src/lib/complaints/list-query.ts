// Liste URL parametrelerini (kullanıcı girdisi) API sorgusuna çevirir. Geçersiz değerler atılır.
import { isId, type QueryValue } from "../api/client.ts";
import { istanbulDayEndExclusiveUtc, istanbulDayStartUtc, STATUS_VALUES } from "../format.ts";

/** OpenAPI / API_CONTRACT_STRATEGY §8: izin verilen sıralama alanları. */
export const SORT_OPTIONS = [
  { value: "-createdAt", label: "Oluşturma (yeni → eski)" },
  { value: "createdAt", label: "Oluşturma (eski → yeni)" },
  { value: "-submittedAt", label: "Başvuru (yeni → eski)" },
  { value: "submittedAt", label: "Başvuru (eski → yeni)" },
  { value: "-updatedAt", label: "Güncelleme (yeni → eski)" },
  { value: "updatedAt", label: "Güncelleme (eski → yeni)" },
] as const;

export const PAGE_SIZES = [25, 50, 100] as const;

export type SearchParams = Record<string, string | string[] | undefined>;

export interface ListFilters {
  status: string[];
  categoryId?: string;
  serviceUnitId?: string;
  assigneeUserId?: string;
  from?: string;
  to?: string;
  reference?: string;
  sort: string;
  page: number;
  pageSize: number;
}

function first(value: string | string[] | undefined): string | undefined {
  const v = Array.isArray(value) ? value[0] : value;
  return v === undefined || v.trim() === "" ? undefined : v.trim();
}

function all(value: string | string[] | undefined): string[] {
  if (value === undefined) return [];
  return (Array.isArray(value) ? value : [value]).filter((v) => v !== "");
}

const DAY = /^\d{4}-\d{2}-\d{2}$/;

export function parseListFilters(params: SearchParams): ListFilters {
  const page = Number.parseInt(first(params.page) ?? "1", 10);
  const pageSize = Number.parseInt(first(params.pageSize) ?? "25", 10);
  const sort = first(params.sort);
  const from = first(params.from);
  const to = first(params.to);
  const reference = first(params.reference);
  const id = (key: string) => {
    const v = first(params[key]);
    return isId(v) ? v : undefined;
  };
  return {
    status: all(params.status).filter((s) => (STATUS_VALUES as string[]).includes(s)),
    categoryId: id("categoryId"),
    serviceUnitId: id("serviceUnitId"),
    assigneeUserId: id("assigneeUserId"),
    from: from && DAY.test(from) ? from : undefined,
    to: to && DAY.test(to) ? to : undefined,
    reference: reference ? reference.slice(0, 64) : undefined,
    sort: SORT_OPTIONS.some((o) => o.value === sort) ? sort! : "-createdAt",
    page: Number.isFinite(page) && page >= 1 ? page : 1,
    pageSize: (PAGE_SIZES as readonly number[]).includes(pageSize) ? pageSize : 25,
  };
}

export function toApiQuery(filters: ListFilters): Record<string, QueryValue> {
  return {
    status: filters.status,
    categoryId: filters.categoryId,
    serviceUnitId: filters.serviceUnitId,
    assigneeUserId: filters.assigneeUserId,
    submittedFrom: istanbulDayStartUtc(filters.from),
    submittedTo: istanbulDayEndExclusiveUtc(filters.to),
    reference: filters.reference,
    sort: filters.sort,
    page: filters.page,
    pageSize: filters.pageSize,
  };
}

/** Panel URL'si (sayfalama bağlantıları için). */
export function toPageHref(filters: ListFilters, page: number): string {
  const params = new URLSearchParams();
  for (const s of filters.status) params.append("status", s);
  const entries: [string, string | number | undefined][] = [
    ["categoryId", filters.categoryId],
    ["serviceUnitId", filters.serviceUnitId],
    ["assigneeUserId", filters.assigneeUserId],
    ["from", filters.from],
    ["to", filters.to],
    ["reference", filters.reference],
    ["sort", filters.sort],
    ["pageSize", filters.pageSize],
    ["page", page],
  ];
  for (const [k, v] of entries) if (v !== undefined) params.set(k, String(v));
  return `/complaints?${params}`;
}
