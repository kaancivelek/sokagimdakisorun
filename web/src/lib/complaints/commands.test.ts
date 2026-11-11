// allowedActions gösterimi ve 409 davranışı.
import assert from "node:assert/strict";
import { describe, it } from "node:test";

import { createApiClient } from "../api/client.ts";
import { buildCommandBody, runCommand } from "./run-command.ts";
import { COMMANDS, visibleCommands } from "./commands.ts";

const ID = "22222222-2222-2222-2222-222222222222";
const UNIT = "33333333-3333-3333-3333-333333333333";

function form(entries: Record<string, string>): FormData {
  const f = new FormData();
  for (const [k, v] of Object.entries(entries)) f.set(k, v);
  return f;
}

function json(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/problem+json" } });
}

describe("visibleCommands", () => {
  it("shows exactly the API's allowedActions, in API order, nothing else", () => {
    const { commands, unknown } = visibleCommands(["reject", "start-review", "mark-spam"]);
    assert.deepEqual(
      commands.map((c) => c.action),
      ["reject", "start-review", "mark-spam"],
    );
    assert.deepEqual(unknown, []);
  });

  it("shows nothing when allowedActions is empty (terminal state or no permission)", () => {
    assert.deepEqual(visibleCommands([]).commands, []);
  });

  it("does not invent buttons for unknown actions; reports them", () => {
    const { commands, unknown } = visibleCommands(["future-command", "accept", "accept"]);
    assert.deepEqual(
      commands.map((c) => c.action),
      ["accept"],
    );
    assert.deepEqual(unknown, ["future-command"]);
  });

  it("catalogue holds no transition/permission data (only action, label, body fields, tone)", () => {
    for (const c of COMMANDS) {
      assert.deepEqual(Object.keys(c).filter((k) => !["action", "label", "fields", "tone"].includes(k)), []);
    }
  });
});

describe("buildCommandBody", () => {
  it("always sends version; empty optional fields become null (API decides what is required)", () => {
    assert.deepEqual(buildCommandBody("reject", form({ version: "42", reason: "  ", reporterNote: "Not" })), {
      version: "42",
      reason: null,
      reporterNote: "Not",
    });
  });

  it("assign sends unit/assignee ids; invalid ids are dropped", () => {
    assert.deepEqual(
      buildCommandBody("assign", form({ version: "7", serviceUnitId: UNIT, assigneeUserId: "../x", reason: "r" })),
      { version: "7", serviceUnitId: UNIT, assigneeUserId: null, reason: "r" },
    );
  });
});

describe("runCommand", () => {
  it("posts to the command endpoint with the version", async () => {
    let seen: { url: string; body: unknown } | undefined;
    const client = createApiClient({
      baseUrl: "http://api.local",
      accessToken: "t",
      fetchImpl: (async (url: string, init?: RequestInit) => {
        seen = { url: String(url), body: JSON.parse(String(init?.body)) };
        return new Response(JSON.stringify({ id: ID }), { status: 200 });
      }) as unknown as typeof fetch,
    });
    const outcome = await runCommand(client, ID, "start-review", form({ version: "5" }));
    assert.equal(outcome.status, "ok");
    assert.equal(seen?.url, `http://api.local/api/v1/complaints/${ID}/start-review`);
    assert.deepEqual(seen?.body, { version: "5", reason: null, reporterNote: null });
  });

  it("409 concurrency.conflict asks the caller to reload current data", async () => {
    const client = createApiClient({
      baseUrl: "http://api.local",
      fetchImpl: (async () => json(409, { status: 409, code: "concurrency.conflict" })) as unknown as typeof fetch,
    });
    const outcome = await runCommand(client, ID, "accept", form({ version: "1" }));
    assert.equal(outcome.status, "conflict");
    assert.ok(outcome.status === "conflict" && outcome.reload);
  });

  it("400 validation errors are surfaced by field, without reload", async () => {
    const client = createApiClient({
      baseUrl: "http://api.local",
      fetchImpl: (async () =>
        json(400, {
          status: 400,
          code: "validation.failed",
          errors: { Reason: ["Gerekçe zorunludur."] },
        })) as unknown as typeof fetch,
    });
    const outcome = await runCommand(client, ID, "reject", form({ version: "1" }));
    assert.equal(outcome.status, "error");
    assert.ok(outcome.status === "error");
    assert.deepEqual(outcome.fieldErrors, { reason: ["Gerekçe zorunludur."] });
    assert.equal(outcome.reload, false);
    assert.equal(outcome.message, "Girilen bilgileri kontrol edin.");
  });

  it("refuses unknown actions without calling the API", async () => {
    let called = false;
    const client = createApiClient({
      baseUrl: "http://api.local",
      fetchImpl: (async () => {
        called = true;
        return new Response("{}");
      }) as unknown as typeof fetch,
    });
    const outcome = await runCommand(client, ID, "delete-everything", form({}));
    assert.equal(outcome.status, "error");
    assert.equal(called, false);
  });

  it("rejects non-GUID complaint ids (path injection)", async () => {
    const client = createApiClient({ baseUrl: "http://api.local" });
    await assert.rejects(() => runCommand(client, "../../users", "accept", form({ version: "1" })));
  });
});
