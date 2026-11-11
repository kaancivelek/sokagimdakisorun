// Oturum yaşam döngüsü: giriş, yenileme kararı, yenileme. Next.js'e bağımlı değildir (birim testi için).
import type { components } from "../../api/schema.d.ts";
import { createApiClient, type ApiClientOptions } from "../api/client.ts";
import type { ApiProblem } from "../api/problem.ts";
import { REFRESH_LEEWAY_MS } from "./constants.ts";
import type { Session } from "./types.ts";

type AccessTokenResponse = components["schemas"]["AccessTokenResponse"];
type CurrentUser = components["schemas"]["CurrentUserResponse"];

export type SessionResult = { ok: true; session: Session } | { ok: false; problem: ApiProblem };

function expiresAt(token: AccessTokenResponse, now: number): number {
  const seconds = Number(token.expiresIn);
  return now + (Number.isFinite(seconds) && seconds > 0 ? seconds : 0) * 1000;
}

/** Erişim token'ı bitmiş veya bitmek üzereyse `true`. */
export function needsRefresh(session: Session, now: number): boolean {
  return session.accessTokenExpiresAt - REFRESH_LEEWAY_MS <= now;
}

type Deps = Pick<ApiClientOptions, "baseUrl" | "fetchImpl"> & { now?: () => number };

/** E-posta + parola → token'lar → `/auth/me`. Token'lar yalnızca dönen `Session` nesnesinde (sunucuda) kalır. */
export async function login(email: string, password: string, deps: Deps): Promise<SessionResult> {
  const now = deps.now ?? Date.now;
  const anonymous = createApiClient(deps);
  const tokens = await anonymous.post<AccessTokenResponse>("/api/v1/auth/login", { email, password });
  if (!tokens.ok) return { ok: false, problem: tokens.problem };
  return completeSession(tokens.data, now(), deps);
}

/** Yenileme token'ıyla yeni token çifti alır; kullanıcı bilgisi de tazelenir (rol değişikliği yansısın). */
export async function refresh(session: Session, deps: Deps): Promise<SessionResult> {
  const now = deps.now ?? Date.now;
  const anonymous = createApiClient(deps);
  const tokens = await anonymous.post<AccessTokenResponse>("/api/v1/auth/refresh", {
    refreshToken: session.refreshToken,
  });
  if (!tokens.ok) return { ok: false, problem: tokens.problem };
  return completeSession(tokens.data, now(), deps);
}

async function completeSession(tokens: AccessTokenResponse, now: number, deps: Deps): Promise<SessionResult> {
  const authed = createApiClient({ ...deps, accessToken: tokens.accessToken });
  const me = await authed.get<CurrentUser>("/api/v1/auth/me");
  if (!me.ok) return { ok: false, problem: me.problem };
  return {
    ok: true,
    session: {
      accessToken: tokens.accessToken,
      refreshToken: tokens.refreshToken,
      accessTokenExpiresAt: expiresAt(tokens, now),
      user: me.data,
    },
  };
}

/** Yönlendirme hedefi yalnızca site içi göreli yol olabilir (açık yönlendirme engeli). */
export function safeNextPath(value: string | null | undefined): string {
  if (!value || !value.startsWith("/") || value.startsWith("//") || value.startsWith("/\\")) return "/";
  if (value.startsWith("/login")) return "/";
  return value;
}
