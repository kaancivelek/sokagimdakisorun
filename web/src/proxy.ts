// Oturum koruması ve token yenileme (ADR-0006 §4). Next.js 16'da proxy varsayılan olarak Node.js runtime'ında çalışır.
// Bu katman yalnızca kullanıcı deneyimi içindir: yetki kararının sahibi API'dir; her Server Action oturumu ayrıca doğrular.
import { NextResponse, type NextRequest } from "next/server";

import { apiBaseUrl, sessionSecret } from "./lib/env";
import { SESSION_COOKIE, sessionCookieOptions } from "./lib/session/constants";
import { seal, unseal } from "./lib/session/crypto";
import { needsRefresh, refresh } from "./lib/session/lifecycle";
import type { Session } from "./lib/session/types";

const PUBLIC_PATHS = ["/login"];

function isPublic(pathname: string): boolean {
  return PUBLIC_PATHS.some((p) => pathname === p || pathname.startsWith(`${p}/`));
}

function toLogin(request: NextRequest, reason?: string): NextResponse {
  const { pathname, search } = request.nextUrl;
  // Ek içeriği (Route Handler) için yönlendirme yerine 401: <img> isteği HTML giriş sayfası almasın.
  const response = pathname.startsWith("/attachments/")
    ? new NextResponse(null, { status: 401 })
    : NextResponse.redirect(
        new URL(
          `/login?${new URLSearchParams({ next: `${pathname}${search}`, ...(reason ? { reason } : {}) })}`,
          request.url,
        ),
      );
  response.cookies.delete(SESSION_COOKIE);
  return response;
}

export async function proxy(request: NextRequest) {
  const { pathname } = request.nextUrl;
  const secret = sessionSecret();
  const session = unseal<Session>(request.cookies.get(SESSION_COOKIE)?.value, secret);

  if (isPublic(pathname)) {
    return NextResponse.next();
  }

  if (!session) return toLogin(request);

  if (!needsRefresh(session, Date.now())) return NextResponse.next();

  const refreshed = await refresh(session, { baseUrl: apiBaseUrl() });
  if (!refreshed.ok) return toLogin(request, "expired");

  const sealed = seal(refreshed.session, secret);
  // Yeni çerez hem yanıta (tarayıcı) hem bu isteğin devamına (Server Component'ler aynı istekte görsün) yazılır.
  request.cookies.set(SESSION_COOKIE, sealed);
  const response = NextResponse.next({ request: { headers: request.headers } });
  response.cookies.set(SESSION_COOKIE, sealed, sessionCookieOptions);
  return response;
}

export const config = {
  matcher: ["/((?!_next/static|_next/image|favicon.ico).*)"],
};
