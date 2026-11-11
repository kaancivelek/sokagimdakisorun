// .NET API istemcisi — YALNIZCA sunucuda (Server Component, Server Action, Route Handler, proxy) kullanılır.
// Next.js'e bağımlı değildir; `fetch` enjekte edilebilir (birim testi). Bearer başlığını tarayıcı değil bu katman ekler.
import type { ApiProblem } from "./problem.ts";

export type ApiResult<T> = { ok: true; status: number; data: T } | { ok: false; status: number; problem: ApiProblem };

export type QueryValue = string | number | boolean | undefined | null | ReadonlyArray<string>;

export interface RequestOptions {
  query?: Record<string, QueryValue>;
  body?: unknown;
  signal?: AbortSignal;
}

export interface ApiClientOptions {
  baseUrl: string;
  accessToken?: string;
  fetchImpl?: typeof fetch;
  timeoutMs?: number;
}

export function buildUrl(baseUrl: string, path: string, query?: Record<string, QueryValue>): string {
  if (!path.startsWith("/api/")) throw new Error("API yolu /api/ ile başlamalıdır.");
  const url = new URL(path, baseUrl.endsWith("/") ? baseUrl : `${baseUrl}/`);
  for (const [key, value] of Object.entries(query ?? {})) {
    if (value === undefined || value === null || value === "") continue;
    if (Array.isArray(value)) {
      for (const item of value) if (item) url.searchParams.append(key, item);
    } else {
      url.searchParams.set(key, String(value));
    }
  }
  return url.toString();
}

/** Yol parçası olarak kullanılacak kimlik: yalnızca GUID biçimi kabul edilir (yol enjeksiyonuna karşı). */
export function assertId(id: string): string {
  if (!/^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$/.test(id)) {
    throw new Error("Geçersiz kimlik.");
  }
  return id;
}

export function isId(id: string | undefined | null): id is string {
  try {
    return typeof id === "string" && assertId(id) === id;
  } catch {
    return false;
  }
}

export function createApiClient(options: ApiClientOptions) {
  const fetchImpl = options.fetchImpl ?? fetch;
  const timeoutMs = options.timeoutMs ?? 15_000;

  async function raw(method: string, path: string, opts: RequestOptions = {}): Promise<Response> {
    const headers: Record<string, string> = { Accept: "application/json, application/problem+json" };
    if (options.accessToken) headers.Authorization = `Bearer ${options.accessToken}`;
    if (opts.body !== undefined) headers["Content-Type"] = "application/json";
    return fetchImpl(buildUrl(options.baseUrl, path, opts.query), {
      method,
      headers,
      body: opts.body === undefined ? undefined : JSON.stringify(opts.body),
      cache: "no-store",
      redirect: "manual",
      signal: opts.signal ?? AbortSignal.timeout(timeoutMs),
    });
  }

  async function request<T>(method: string, path: string, opts: RequestOptions = {}): Promise<ApiResult<T>> {
    let response: Response;
    try {
      response = await raw(method, path, opts);
    } catch {
      return { ok: false, status: 0, problem: { status: 0, code: "network.unavailable" } };
    }
    if (response.ok) {
      const text = response.status === 204 ? "" : await response.text();
      return { ok: true, status: response.status, data: (text ? JSON.parse(text) : undefined) as T };
    }
    return { ok: false, status: response.status, problem: await readProblem(response) };
  }

  return {
    raw,
    request,
    get: <T>(path: string, opts?: RequestOptions) => request<T>("GET", path, opts),
    post: <T>(path: string, body?: unknown, opts?: RequestOptions) => request<T>("POST", path, { ...opts, body }),
    patch: <T>(path: string, body?: unknown, opts?: RequestOptions) => request<T>("PATCH", path, { ...opts, body }),
    put: <T>(path: string, body?: unknown, opts?: RequestOptions) => request<T>("PUT", path, { ...opts, body }),
    del: <T>(path: string, opts?: RequestOptions) => request<T>("DELETE", path, opts),
  };
}

export type ApiClient = ReturnType<typeof createApiClient>;

export async function readProblem(response: Response): Promise<ApiProblem> {
  try {
    const parsed = (await response.json()) as ApiProblem;
    if (parsed && typeof parsed === "object") return { ...parsed, status: parsed.status ?? response.status };
  } catch {
    // Gövde yoksa veya JSON değilse durum koduyla yetinilir.
  }
  return { status: response.status };
}
