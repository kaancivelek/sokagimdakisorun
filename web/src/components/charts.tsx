// Paketsiz, yalnızca CSS ile çizilen grafikler (Server Component). Her grafiğin yanında kesin sayılar metin/tablo olarak
// da verilir; ekran okuyucular çubukları değil sayıları okur.
import type { Bar, TimelineColumn } from "@/lib/reports/dashboard";

export function BarList({ bars, caption }: { bars: Bar[]; caption: string }) {
  return (
    <table className="bar-list">
      <caption className="sr-only">{caption}</caption>
      <tbody>
        {bars.map((b) => (
          <tr key={b.key}>
            <th scope="row">{b.label}</th>
            <td className="bar-cell" aria-hidden="true">
              <span className="bar" style={{ width: `${b.percent}%` }} />
            </td>
            <td className="num">{b.count}</td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}

export function TimelineChart({ columns, caption }: { columns: TimelineColumn[]; caption: string }) {
  return (
    <>
      <div className="timeline-legend" aria-hidden="true">
        <span>
          <i className="swatch opened" /> Açılan
        </span>
        <span>
          <i className="swatch closed" /> Kapanan
        </span>
      </div>
      <div className="timeline" aria-hidden="true">
        {columns.map((c) => (
          <div key={c.key} className="timeline-col" title={`${c.title}: ${c.opened} açılan, ${c.closed} kapanan`}>
            <div className="timeline-bars">
              <span className="tbar opened" style={{ height: `${c.openedPercent}%` }} />
              <span className="tbar closed" style={{ height: `${c.closedPercent}%` }} />
            </div>
            <span className="timeline-label">{c.label}</span>
          </div>
        ))}
      </div>
      <details className="timeline-table">
        <summary>Sayıları tablo olarak göster</summary>
        <table>
          <caption className="sr-only">{caption}</caption>
          <thead>
            <tr>
              <th scope="col">Dönem</th>
              <th scope="col">Açılan</th>
              <th scope="col">Kapanan</th>
            </tr>
          </thead>
          <tbody>
            {columns.map((c) => (
              <tr key={c.key}>
                <th scope="row">{c.title}</th>
                <td className="num">{c.opened}</td>
                <td className="num">{c.closed}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </details>
    </>
  );
}
