import type { Metadata } from "next";
import Link from "next/link";

import type { components } from "@/api/schema";
import { ProblemAlert } from "@/components/ProblemAlert";
import {
  PAGE_SIZES,
  parseListFilters,
  SORT_OPTIONS,
  toApiQuery,
  toPageHref,
} from "@/lib/complaints/list-query";
import { loadLookups } from "@/lib/complaints/lookups";
import { formatDateTime, STATUS_LABELS, STATUS_VALUES, statusLabel } from "@/lib/format";
import { apiClient, redirectToLoginIfUnauthenticated } from "@/lib/session/server";

export const metadata: Metadata = { title: "Şikâyetler" };

type ComplaintPage = components["schemas"]["ComplaintPage"];

export default async function ComplaintsPage({ searchParams }: PageProps<"/complaints">) {
  const filters = parseListFilters(await searchParams);
  const api = await apiClient();
  const [result, lookups] = await Promise.all([
    api.get<ComplaintPage>("/api/v1/complaints", { query: toApiQuery(filters) }),
    loadLookups(api),
  ]);
  if (!result.ok) redirectToLoginIfUnauthenticated(result.status);

  const total = result.ok ? Number(result.data.totalCount) : 0;
  const pageCount = Math.max(1, Math.ceil(total / filters.pageSize));

  return (
    <>
      <h1>Şikâyetler</h1>
      <form className="card filters" method="get" action="/complaints">
        <label>
          Durum
          <select name="status" multiple defaultValue={filters.status} size={4}>
            {STATUS_VALUES.map((s) => (
              <option key={s} value={s}>
                {STATUS_LABELS[s]}
              </option>
            ))}
          </select>
        </label>
        <label>
          Kategori
          <select name="categoryId" defaultValue={filters.categoryId ?? ""}>
            <option value="">Tümü</option>
            {lookups.categories.map((c) => (
              <option key={c.id} value={c.id}>
                {c.name}
              </option>
            ))}
          </select>
        </label>
        <label>
          Hizmet birimi
          <select name="serviceUnitId" defaultValue={filters.serviceUnitId ?? ""}>
            <option value="">Tümü</option>
            {lookups.serviceUnits.map((u) => (
              <option key={u.id} value={u.id}>
                {u.name}
              </option>
            ))}
          </select>
        </label>
        <label>
          Atanan kişi
          <select name="assigneeUserId" defaultValue={filters.assigneeUserId ?? ""}>
            <option value="">Tümü</option>
            {lookups.members.map((m) => (
              <option key={m.id} value={m.id}>
                {m.displayName}
              </option>
            ))}
          </select>
        </label>
        <label>
          Başvuru tarihi (başlangıç)
          <input type="date" name="from" defaultValue={filters.from} />
        </label>
        <label>
          Başvuru tarihi (bitiş)
          <input type="date" name="to" defaultValue={filters.to} />
        </label>
        <label>
          Referans no
          <input name="reference" defaultValue={filters.reference} placeholder="ör. SS-…" maxLength={64} />
        </label>
        <label>
          Sıralama
          <select name="sort" defaultValue={filters.sort}>
            {SORT_OPTIONS.map((o) => (
              <option key={o.value} value={o.value}>
                {o.label}
              </option>
            ))}
          </select>
        </label>
        <label>
          Sayfa boyutu
          <select name="pageSize" defaultValue={String(filters.pageSize)}>
            {PAGE_SIZES.map((n) => (
              <option key={n} value={n}>
                {n}
              </option>
            ))}
          </select>
        </label>
        <div className="row">
          <button type="submit">Filtrele</button>
          <Link className="button secondary" href="/complaints">
            Temizle
          </Link>
        </div>
      </form>

      {!result.ok ? (
        <ProblemAlert problem={result.problem} />
      ) : result.data.items.length === 0 ? (
        <div className="card muted">Filtrelere uyan şikâyet yok.</div>
      ) : (
        <>
          <p className="muted">
            Toplam {total} kayıt · Sayfa {filters.page} / {pageCount}
          </p>
          <table>
            <thead>
              <tr>
                <th>Referans</th>
                <th>Durum</th>
                <th>Kategori</th>
                <th>Açıklama</th>
                <th>Adres</th>
                <th>Atama</th>
                <th>Başvuru</th>
                <th>Güncelleme</th>
              </tr>
            </thead>
            <tbody>
              {result.data.items.map((c) => (
                <tr key={c.id}>
                  <td>
                    <Link href={`/complaints/${c.id}`}>{c.publicReference}</Link>
                  </td>
                  <td>
                    <span className="badge">{statusLabel(c.status)}</span>
                  </td>
                  <td>{c.category.name}</td>
                  <td className="prewrap">{c.descriptionExcerpt}</td>
                  <td>{c.addressText ?? "—"}</td>
                  <td>
                    {c.activeAssignment
                      ? `${c.activeAssignment.serviceUnit.name}${c.activeAssignment.assignee ? ` · ${c.activeAssignment.assignee.displayName}` : ""}`
                      : "—"}
                  </td>
                  <td>{formatDateTime(c.submittedAt)}</td>
                  <td>{formatDateTime(c.updatedAt)}</td>
                </tr>
              ))}
            </tbody>
          </table>
          <nav className="pagination" aria-label="Sayfalama">
            {filters.page > 1 ? (
              <Link className="button secondary" href={toPageHref(filters, filters.page - 1)}>
                ← Önceki
              </Link>
            ) : null}
            <span className="muted">
              {filters.page} / {pageCount}
            </span>
            {filters.page < pageCount ? (
              <Link className="button secondary" href={toPageHref(filters, filters.page + 1)}>
                Sonraki →
              </Link>
            ) : null}
          </nav>
        </>
      )}
    </>
  );
}
