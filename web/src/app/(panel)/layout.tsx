import Link from "next/link";

import { logoutAction } from "@/app/login/actions";
import { label, ROLE_LABELS } from "@/lib/format";
import { requireSession } from "@/lib/session/server";

export default async function PanelLayout({ children }: { children: React.ReactNode }) {
  const session = await requireSession();
  const { displayName, roles } = session.user;
  // Rol yalnızca menü görünürlüğü içindir; yetki kararını API verir (Admin uç noktaları Moderatöre 403 döner).
  const isAdmin = roles.includes("Admin");
  return (
    <div className="shell">
      <nav className="sidebar" aria-label="Ana menü">
        <span className="brand">Sokağımdaki Sorun</span>
        <Link href="/">Özet</Link>
        <Link href="/complaints">Şikâyetler</Link>
        {isAdmin && (
          <>
            <span className="section">Yönetim</span>
            <Link href="/admin/categories">Kategoriler</Link>
            <Link href="/admin/service-units">Hizmet birimleri</Link>
            <Link href="/admin/users">Kullanıcılar</Link>
          </>
        )}
        <div className="user">
          <div>{displayName}</div>
          <div className="muted" style={{ color: "#cfd8e3" }}>
            {roles.map((r) => label(ROLE_LABELS, r)).join(", ")}
          </div>
          <form action={logoutAction} style={{ marginTop: "0.5rem" }}>
            <button type="submit" className="secondary">
              Çıkış yap
            </button>
          </form>
        </div>
      </nav>
      <main className="main">{children}</main>
    </div>
  );
}
