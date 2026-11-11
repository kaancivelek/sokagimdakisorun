import type { Metadata } from "next";

import { safeNextPath } from "@/lib/session/lifecycle";

import { LoginForm } from "./LoginForm";

export const metadata: Metadata = { title: "Giriş" };

export default async function LoginPage({ searchParams }: PageProps<"/login">) {
  const params = await searchParams;
  const next = safeNextPath(typeof params.next === "string" ? params.next : undefined);
  const notice = params.reason === "expired" ? "Oturumunuzun süresi doldu. Lütfen yeniden giriş yapın." : undefined;
  return (
    <main className="login">
      <LoginForm next={next} notice={notice} />
      <p className="muted">Hesaplar yönetici tarafından oluşturulur; kendi kendine kayıt yoktur.</p>
    </main>
  );
}
