export const SESSION_COOKIE = "ss_session";
/** Yenileme token'ının API tarafındaki ömrü 7 gün (WS-04); çerez bundan uzun yaşamaz. */
export const SESSION_MAX_AGE_SECONDS = 7 * 24 * 60 * 60;
/** Erişim token'ı bitmeden bu kadar önce yenilenir. */
export const REFRESH_LEEWAY_MS = 60_000;

export const sessionCookieOptions = {
  httpOnly: true,
  secure: true,
  sameSite: "lax" as const,
  path: "/",
  maxAge: SESSION_MAX_AGE_SECONDS,
};
