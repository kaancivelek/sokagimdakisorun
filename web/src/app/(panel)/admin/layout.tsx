import { requireSession } from "@/lib/session/server";

/** Kullanıcı deneyimi için: Admin olmayan kullanıcıya menü göstermeyiz. Asıl yetki kontrolü API'dedir (403). */
export default async function AdminLayout({ children }: { children: React.ReactNode }) {
  const session = await requireSession();
  if (!session.user.roles.includes("Admin")) {
    return <div className="alert error">Bu bölüm yalnızca yöneticilere açıktır.</div>;
  }
  return <>{children}</>;
}
