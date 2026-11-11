import "server-only";

import { cookies } from "next/headers";
import { redirect } from "next/navigation";
import { cache } from "react";

import { createApiClient, type ApiClient } from "../api/client";
import { apiBaseUrl, sessionSecret } from "../env";
import { SESSION_COOKIE, sessionCookieOptions } from "./constants";
import { seal, unseal } from "./crypto";
import type { Session } from "./types";

/** Çerezdeki oturumu çözer. Token yenileme proxy.ts'te yapılır; burada yalnızca okunur. */
export const readSession = cache(async (): Promise<Session | null> => {
  const store = await cookies();
  return unseal<Session>(store.get(SESSION_COOKIE)?.value, sessionSecret());
});

/** Yalnızca Server Action / Route Handler içinden çağrılabilir (Next.js çerez yazma kuralı). */
export async function writeSession(session: Session): Promise<void> {
  const store = await cookies();
  store.set(SESSION_COOKIE, seal(session, sessionSecret()), sessionCookieOptions);
}

export async function clearSession(): Promise<void> {
  const store = await cookies();
  store.delete(SESSION_COOKIE);
}

/** Oturum yoksa giriş sayfasına yönlendirir. Server Action'lar da bunu kendi içinde çağırır (sayfa koruması action'ı korumaz). */
export async function requireSession(): Promise<Session> {
  const session = await readSession();
  if (!session) redirect("/login");
  return session;
}

/** Oturumun token'ıyla API istemcisi. */
export async function apiClient(): Promise<ApiClient> {
  const session = await requireSession();
  return createApiClient({ baseUrl: apiBaseUrl(), accessToken: session.accessToken });
}

/** API 401 döndüyse (token iptal edildi/kullanıcı askıya alındı) oturum geçersizdir. */
export function redirectToLoginIfUnauthenticated(status: number): void {
  if (status === 401) redirect("/login?reason=expired");
}
