import type { Metadata } from "next";
import Link from "next/link";

import { BarList, TimelineChart } from "@/components/charts";
import { ProblemAlert } from "@/components/ProblemAlert";
import {
  BUCKET_OPTIONS,
  categoryView,
  FIELD_LABELS,
  parseDashboardFilters,
  rangeText,
  summaryView,
  timelineView,
  toRangeQuery,
  toTimelineQuery,
  type CategoryReport,
  type SummaryReport,
  type TimelineReport,
} from "@/lib/reports/dashboard";
import { apiClient, redirectToLoginIfUnauthenticated } from "@/lib/session/server";

export const metadata: Metadata = { title: "Özet" };

// Tüm sayılar Reporting uç noktalarından gelir (kapsam duyarlı: moderatör yalnızca görebildiklerini görür).
// Panel istatistik hesaplamaz; tarih biçimi/aralık kuralları da API'dedir (400 validation.failed).
export default async function DashboardPage({ searchParams }: PageProps<"/">) {
  const filters = parseDashboardFilters(await searchParams);
  const api = await apiClient();
  const [summary, byCategory, timeline] = await Promise.all([
    api.get<SummaryReport>("/api/v1/reports/summary", { query: toRangeQuery(filters) }),
    api.get<CategoryReport>("/api/v1/reports/by-category", { query: toRangeQuery(filters) }),
    api.get<TimelineReport>("/api/v1/reports/timeline", { query: toTimelineQuery(filters) }),
  ]);
  for (const r of [summary, byCategory, timeline]) if (!r.ok) redirectToLoginIfUnauthenticated(r.status);

  const hasRange = Boolean(filters.from || filters.to);

  return (
    <>
      <h1>Özet</h1>
      <form className="card filters" method="get" action="/">
        <label>
          Başvuru tarihi (başlangıç)
          <input type="date" name="from" defaultValue={filters.from} />
        </label>
        <label>
          Başvuru tarihi (bitiş)
          <input type="date" name="to" defaultValue={filters.to} />
        </label>
        <label>
          Zaman serisi
          <select name="bucket" defaultValue={filters.bucket}>
            {BUCKET_OPTIONS.map((o) => (
              <option key={o.value} value={o.value}>
                {o.label}
              </option>
            ))}
          </select>
        </label>
        <div className="row">
          <button type="submit">Uygula</button>
          <Link className="button secondary" href="/">
            Temizle
          </Link>
        </div>
        <p className="muted" style={{ flexBasis: "100%", margin: 0 }}>
          Tarihler Türkiye saatine (Europe/Istanbul) göre gündür; iki uç dâhildir. Aralık seçilmezse durum ve kategori
          sayıları tüm zamanları, zaman serisi son 30 günü gösterir.
        </p>
      </form>

      <div className="grid-2">
        <section className="card" aria-labelledby="status-heading">
          <h2 id="status-heading">Duruma göre şikâyetler</h2>
          {!summary.ok ? (
            <ProblemAlert problem={summary.problem} fieldLabels={FIELD_LABELS} />
          ) : (
            (() => {
              const view = summaryView(summary.data);
              return (
                <>
                  <p className="kpi">
                    <span className="kpi-value">{view.total}</span> şikâyet
                    <span className="muted"> · {rangeText(summary.data.from, summary.data.to)}</span>
                  </p>
                  {view.total === 0 ? (
                    <p className="muted">Bu aralıkta başvuru yok.</p>
                  ) : (
                    <BarList bars={view.bars} caption="Duruma göre şikâyet sayıları" />
                  )}
                </>
              );
            })()
          )}
        </section>

        <section className="card" aria-labelledby="category-heading">
          <h2 id="category-heading">Kategori dağılımı</h2>
          {!byCategory.ok ? (
            <ProblemAlert problem={byCategory.problem} fieldLabels={FIELD_LABELS} />
          ) : (
            (() => {
              const view = categoryView(byCategory.data);
              return (
                <>
                  <p className="muted">{rangeText(byCategory.data.from, byCategory.data.to)}</p>
                  {view.bars.length === 0 || view.total === 0 ? (
                    <p className="muted">Bu aralıkta başvuru yok.</p>
                  ) : (
                    <BarList bars={view.bars} caption="Kategoriye göre şikâyet sayıları" />
                  )}
                </>
              );
            })()
          )}
        </section>
      </div>

      <section className="card" aria-labelledby="timeline-heading">
        <h2 id="timeline-heading">Açılan ve kapanan şikâyetler</h2>
        {!timeline.ok ? (
          <ProblemAlert problem={timeline.problem} fieldLabels={FIELD_LABELS} />
        ) : (
          (() => {
            const view = timelineView(timeline.data);
            const bucketLabel = timeline.data.bucket === "Week" ? "haftalık" : "günlük";
            return (
              <>
                <p className="muted">
                  {rangeText(timeline.data.from, timeline.data.to)}
                  {hasRange ? "" : " (son 30 gün)"} · {bucketLabel} · Toplam {view.totalOpened} açılan,{" "}
                  {view.totalClosed} kapanan
                </p>
                {view.isEmpty ? (
                  <p className="muted">Bu aralıkta açılan veya kapanan şikâyet yok.</p>
                ) : (
                  <TimelineChart columns={view.columns} caption="Döneme göre açılan ve kapanan şikâyetler" />
                )}
                <p className="muted small">
                  Açılan: başvuru günü. Kapanan: “Kapatıldı” durumuna geçiş günü (reddedilen, spam, tekrar ve geri
                  çekilenler dâhil değildir).
                </p>
              </>
            );
          })()
        )}
      </section>

      <p>
        <Link href="/complaints">Şikâyet listesine git →</Link>
      </p>
    </>
  );
}
