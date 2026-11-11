import type { components } from "../../api/schema.d.ts";

export type CurrentUser = components["schemas"]["CurrentUserResponse"];

/** Yalnızca sunucuda, şifreli çerezde yaşar. Tarayıcıya hiçbir alanı (özellikle token'lar) gönderilmez. */
export interface Session {
  accessToken: string;
  refreshToken: string;
  /** Erişim token'ının sona erme zamanı (Unix ms). */
  accessTokenExpiresAt: number;
  user: CurrentUser;
}

/** Client Component'lere aktarılabilecek güvenli kullanıcı görünümü (token içermez). */
export interface SessionUser {
  id: string;
  displayName: string;
  email: string;
  roles: string[];
}

export function toSessionUser(session: Session): SessionUser {
  const { id, displayName, email, roles } = session.user;
  return { id, displayName, email, roles: [...roles] };
}
