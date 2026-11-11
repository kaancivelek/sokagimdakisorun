// Ek içeriği akışı (ADR-0006 §6): tarayıcı yalnızca bu Route Handler'ı görür; API adresi ve token sunucuda kalır.
// API yolu istemciden gelen bir URL'den değil, doğrulanmış kimliklerden kurulur.
import { createApiClient, isId } from "@/lib/api/client";
import { apiBaseUrl } from "@/lib/env";
import { readSession } from "@/lib/session/server";

const INLINE_TYPES = /^image\/(jpeg|png|webp|gif|heic|heif)$/i;

export async function GET(_request: Request, ctx: RouteContext<"/attachments/[complaintId]/[attachmentId]">) {
  const { complaintId, attachmentId } = await ctx.params;
  if (!isId(complaintId) || !isId(attachmentId)) return new Response(null, { status: 404 });

  const session = await readSession();
  if (!session) return new Response(null, { status: 401 });

  const api = createApiClient({ baseUrl: apiBaseUrl(), accessToken: session.accessToken, timeoutMs: 60_000 });
  let upstream: Response;
  try {
    upstream = await api.raw("GET", `/api/v1/complaints/${complaintId}/attachments/${attachmentId}/content`);
  } catch {
    return new Response(null, { status: 502 });
  }
  if (!upstream.ok || !upstream.body) {
    // Hata gövdesi (Problem Details) aktarılmaz; yalnızca durum kodu. 404 kapsam dışı erişimi de kapsar.
    return new Response(null, { status: upstream.status === 401 || upstream.status === 404 ? upstream.status : 502 });
  }

  const contentType = upstream.headers.get("content-type") ?? "";
  const inline = INLINE_TYPES.test(contentType.split(";")[0]!.trim());
  const headers = new Headers({
    "Content-Type": inline ? contentType : "application/octet-stream",
    "Content-Disposition": inline ? "inline" : "attachment",
    "Cache-Control": "private, no-store",
    "X-Content-Type-Options": "nosniff",
    "Content-Security-Policy": "default-src 'none'; sandbox",
  });
  const length = upstream.headers.get("content-length");
  if (length) headers.set("Content-Length", length);
  return new Response(upstream.body, { status: 200, headers });
}
