// Oturum katmanı: şifreleme bütünlüğü, yenileme kararı, giriş/yenileme akışı, güvenli yönlendirme.
import assert from "node:assert/strict";
import { describe, it } from "node:test";

import { seal, unseal } from "./crypto.ts";
import { login, needsRefresh, refresh, safeNextPath } from "./lifecycle.ts";
import type { Session } from "./types.ts";

const SECRET = "test-secret-test-secret-test-secret-0001";
const OTHER = "test-secret-test-secret-test-secret-0002";

const session: Session = {
  accessToken: "access-1",
  refreshToken: "refresh-1",
  accessTokenExpiresAt: 1_000_000,
  user: { id: "11111111-1111-1111-1111-111111111111", email: "a@example.test", displayName: "A", roles: ["Moderator"] },
};

function json(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

describe("crypto", () => {
  it("round-trips and does not expose tokens in plaintext", () => {
    const sealed = seal(session, SECRET);
    assert.ok(!sealed.includes("access-1") && !sealed.includes("refresh-1"));
    assert.deepEqual(unseal<Session>(sealed, SECRET), session);
  });

  it("rejects tampered values and other keys", () => {
    const sealed = seal(session, SECRET);
    const parts = sealed.split(".");
    const ct = Buffer.from(parts[2]!, "base64url");
    ct[0] = ct[0]! ^ 1;
    parts[2] = ct.toString("base64url");
    assert.equal(unseal(parts.join("."), SECRET), null);
    assert.equal(unseal(sealed, OTHER), null);
    assert.equal(unseal("garbage", SECRET), null);
    assert.equal(unseal(undefined, SECRET), null);
  });

  it("refuses short secrets", () => {
    assert.throws(() => seal(session, "short"));
  });
});

describe("needsRefresh", () => {
  it("refreshes within the 60 s leeway, not before", () => {
    assert.equal(needsRefresh(session, 1_000_000 - 120_000), false);
    assert.equal(needsRefresh(session, 1_000_000 - 30_000), true);
    assert.equal(needsRefresh(session, 2_000_000), true);
  });
});

describe("login / refresh", () => {
  it("login exchanges credentials for tokens, loads /me, sends bearer only to API", async () => {
    const calls: { url: string; auth: string | null; body: unknown }[] = [];
    const fetchImpl = (async (input: string | URL | Request, init?: RequestInit) => {
      const url = String(input);
      const headers = new Headers(init?.headers);
      calls.push({ url, auth: headers.get("Authorization"), body: init?.body ? JSON.parse(String(init.body)) : null });
      if (url.endsWith("/api/v1/auth/login")) return json(200, { accessToken: "acc", refreshToken: "ref", expiresIn: 900 });
      if (url.endsWith("/api/v1/auth/me")) return json(200, session.user);
      return json(404, {});
    }) as typeof fetch;

    const result = await login("a@example.test", "pw", { baseUrl: "http://api.local", fetchImpl, now: () => 5_000 });
    assert.ok(result.ok);
    assert.equal(result.session.accessToken, "acc");
    assert.equal(result.session.accessTokenExpiresAt, 5_000 + 900_000);
    assert.equal(calls[0]!.url, "http://api.local/api/v1/auth/login");
    assert.equal(calls[0]!.auth, null);
    assert.deepEqual(calls[0]!.body, { email: "a@example.test", password: "pw" });
    assert.equal(calls[1]!.auth, "Bearer acc");
  });

  it("login returns the problem (code) on 401", async () => {
    const fetchImpl = (async () =>
      json(401, { status: 401, code: "auth.invalid_credentials", title: "x" })) as unknown as typeof fetch;
    const result = await login("a", "b", { baseUrl: "http://api.local", fetchImpl });
    assert.ok(!result.ok);
    assert.equal(result.problem.code, "auth.invalid_credentials");
  });

  it("refresh failure is reported so the caller can end the session", async () => {
    const fetchImpl = (async () =>
      json(401, { status: 401, code: "auth.invalid_refresh_token" })) as unknown as typeof fetch;
    const result = await refresh(session, { baseUrl: "http://api.local", fetchImpl });
    assert.ok(!result.ok);
  });

  it("network failure becomes a problem, not an exception", async () => {
    const fetchImpl = (async () => {
      throw new TypeError("fetch failed");
    }) as unknown as typeof fetch;
    const result = await login("a", "b", { baseUrl: "http://api.local", fetchImpl });
    assert.ok(!result.ok);
    assert.equal(result.problem.code, "network.unavailable");
  });
});

describe("safeNextPath", () => {
  it("allows only same-site relative paths", () => {
    assert.equal(safeNextPath("/complaints?page=2"), "/complaints?page=2");
    assert.equal(safeNextPath("//evil.example"), "/");
    assert.equal(safeNextPath("/\\evil.example"), "/");
    assert.equal(safeNextPath("https://evil.example"), "/");
    assert.equal(safeNextPath("/login"), "/");
    assert.equal(safeNextPath(undefined), "/");
  });
});
