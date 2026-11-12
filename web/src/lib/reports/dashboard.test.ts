// Özet sayfası: sorgu oluşturma ve gösterim modeli (sayılar API'den, panel hesaplamaz).
import assert from "node:assert/strict";
import { describe, it } from "node:test";

import { buildUrl } from "../api/client.ts";
import {
  barPercent,
  categoryView,
  formatDay,
  parseDashboardFilters,
  rangeText,
  summaryView,
  timelineView,
  toRangeQuery,
  toTimelineQuery,
  type SummaryReport,
  type TimelineReport,
} from "./dashboard.ts";

describe("parseDashboardFilters", () => {
  it("defaults: no range, daily buckets", () => {
    assert.deepEqual(parseDashboardFilters({}), { from: undefined, to: undefined, bucket: "Day" });
  });

  it("passes dates through untouched (API validates) and allow-lists bucket", () => {
    const f = parseDashboardFilters({ from: " 2026-11-01 ", to: "2026-13-99", bucket: "Month" });
    assert.equal(f.from, "2026-11-01");
    assert.equal(f.to, "2026-13-99");
    assert.equal(f.bucket, "Day");
    assert.equal(parseDashboardFilters({ bucket: "Week" }).bucket, "Week");
  });

  it("caps very long values", () => {
    assert.equal(parseDashboardFilters({ from: "x".repeat(500) }).from?.length, 32);
  });
});

describe("report queries", () => {
  it("summary/by-category send only from/to; timeline adds bucket; empty values are omitted", () => {
    const f = parseDashboardFilters({ from: "2026-11-01", bucket: "Week", extra: "1" });
    assert.equal(
      buildUrl("http://api", "/api/v1/reports/summary", toRangeQuery(f)),
      "http://api/api/v1/reports/summary?from=2026-11-01",
    );
    assert.equal(
      buildUrl("http://api", "/api/v1/reports/timeline", toTimelineQuery(f)),
      "http://api/api/v1/reports/timeline?from=2026-11-01&bucket=Week",
    );
    assert.equal(
      buildUrl("http://api", "/api/v1/reports/timeline", toTimelineQuery(parseDashboardFilters({}))),
      "http://api/api/v1/reports/timeline?bucket=Day",
    );
  });
});

describe("summaryView / categoryView", () => {
  it("uses the API total and counts as-is, labels statuses in Turkish, keeps API order", () => {
    const report: SummaryReport = {
      from: null,
      to: null,
      timeZone: "Europe/Istanbul",
      total: "7",
      byStatus: [
        { status: "Submitted", count: 2 },
        { status: "Closed", count: "4" },
        { status: "Spam", count: 0 },
      ],
    };
    const view = summaryView(report);
    // Toplam API'den gelir; panel byStatus'u toplamaz (burada bilerek 6 ≠ 7).
    assert.equal(view.total, 7);
    assert.deepEqual(
      view.bars.map((b) => [b.label, b.count, b.percent]),
      [
        ["Alındı", 2, 50],
        ["Kapatıldı", 4, 100],
        ["Spam", 0, 0],
      ],
    );
  });

  it("empty category report yields no bars", () => {
    const view = categoryView({ from: null, to: null, timeZone: "Europe/Istanbul", total: 0, items: [] });
    assert.equal(view.total, 0);
    assert.deepEqual(view.bars, []);
  });
});

describe("timelineView", () => {
  const base: TimelineReport = {
    from: "2026-11-01",
    to: "2026-11-03",
    bucket: "Day",
    timeZone: "Europe/Istanbul",
    totalOpened: 3,
    totalClosed: 1,
    points: [
      { start: "2026-11-01", end: "2026-11-01", opened: 2, closed: 0 },
      { start: "2026-11-02", end: "2026-11-02", opened: 0, closed: 1 },
      { start: "2026-11-03", end: "2026-11-03", opened: 1, closed: 0 },
    ],
  };

  it("scales bars to the largest value and keeps API totals", () => {
    const view = timelineView(base);
    assert.equal(view.isEmpty, false);
    assert.equal(view.totalOpened, 3);
    assert.deepEqual(
      view.columns.map((c) => [c.opened, c.closed, c.openedPercent, c.closedPercent]),
      [
        [2, 0, 100, 0],
        [0, 1, 0, 50],
        [1, 0, 50, 0],
      ],
    );
    assert.equal(view.columns[0]!.title, "1 Kasım 2026");
  });

  it("week buckets show a date range; all-zero series is empty", () => {
    const view = timelineView({
      ...base,
      bucket: "Week",
      totalOpened: 0,
      totalClosed: 0,
      points: [{ start: "2026-11-02", end: "2026-11-08", opened: 0, closed: 0 }],
    });
    assert.equal(view.isEmpty, true);
    assert.equal(view.columns[0]!.title, "2 Kasım 2026 – 8 Kasım 2026");
    assert.equal(view.columns[0]!.openedPercent, 0);
  });
});

describe("formatting", () => {
  it("formatDay does not shift Istanbul local days", () => {
    assert.equal(formatDay("2026-01-01"), "1 Ocak 2026");
    assert.equal(formatDay("2026-12-31"), "31 Aralık 2026");
    assert.equal(formatDay("bad"), "—");
  });

  it("rangeText", () => {
    assert.equal(rangeText(null, null), "Tüm zamanlar");
    assert.equal(rangeText("2026-11-01", null), "1 Kasım 2026 ve sonrası");
    assert.equal(rangeText("2026-11-01", "2026-11-30"), "1 Kasım 2026 – 30 Kasım 2026");
  });

  it("barPercent guards zero/negative/NaN", () => {
    assert.equal(barPercent(5, 0), 0);
    assert.equal(barPercent(-1, 10), 0);
    assert.equal(barPercent(Number.NaN, 10), 0);
    assert.equal(barPercent(10, 10), 100);
  });
});
