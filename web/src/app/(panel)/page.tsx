import type { Metadata } from "next";
import Link from "next/link";

export const metadata: Metadata = { title: "Özet" };

export default function DashboardPage() {
  return (
    <>
      <h1>Özet</h1>
      <div className="card">
        <h2>İstatistikler henüz hazır değil</h2>
        <p>
          Özet sayılar, kategori dağılımı ve zaman serisi Reporting modülünün uç noktalarından gelecek
          (<code>/api/v1/reports/summary</code>, <code>/by-category</code>, <code>/timeline</code>). Bu uç noktalar
          henüz API sözleşmesinde yok; panel istatistiği kendisi hesaplamaz.
        </p>
        <p>
          <Link href="/complaints">Şikâyet listesine git →</Link>
        </p>
      </div>
    </>
  );
}
