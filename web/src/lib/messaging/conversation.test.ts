// WhatsApp konuşma görünümü: sıra, etiketler, maskeli numara, metin düz kalır, 404 code ile gösterilir.
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { join } from "node:path";
import { describe, it } from "node:test";

import { createApiClient } from "../api/client.ts";
import { problemMessage } from "../api/problem.ts";
import { conversationView, toMessageView, type ComplaintConversation, type ConversationMessage } from "./conversation.ts";

const ID = "22222222-2222-2222-2222-222222222222";

function msg(overrides: Partial<ConversationMessage>): ConversationMessage {
  return {
    id: "00000000-0000-0000-0000-000000000001",
    direction: "Inbound",
    messageType: "Text",
    text: "Merhaba",
    latitude: null,
    longitude: null,
    hasMedia: false,
    deliveryStatus: "Received",
    failureReason: null,
    createdAt: "2026-11-12T21:30:00Z",
    ...overrides,
  };
}

describe("conversationView", () => {
  it("orders messages by time (ties keep API order) and keeps the masked contact as given", () => {
    const data: ComplaintConversation = {
      complaintId: ID,
      maskedContact: "90********01",
      messages: [
        msg({ id: "b", createdAt: "2026-11-12T21:31:00Z" }),
        msg({ id: "a", createdAt: "2026-11-12T21:30:00Z" }),
        msg({ id: "c", createdAt: "2026-11-12T21:31:00Z" }),
      ],
    };
    const view = conversationView(data);
    assert.equal(view.maskedContact, "90********01");
    assert.deepEqual(
      view.messages.map((m) => m.id),
      ["a", "b", "c"],
    );
  });

  it("maps direction, type and delivery status to Turkish labels; unknown values pass through", () => {
    const outbound = toMessageView(
      msg({ direction: "Outbound", messageType: "Text", deliveryStatus: "Failed", failureReason: "window_closed" }),
    );
    assert.equal(outbound.inbound, false);
    assert.equal(outbound.directionLabel, "Sistem");
    assert.equal(outbound.deliveryLabel, "Gönderilemedi");
    assert.equal(outbound.deliveryTone, "error");
    assert.equal(outbound.failureReason, "window_closed");

    const odd = toMessageView(msg({ messageType: "Sticker", deliveryStatus: "Mystery" }));
    assert.equal(odd.typeLabel, "Sticker");
    assert.equal(odd.deliveryLabel, "Mystery");
    assert.equal(odd.deliveryTone, "neutral");
  });

  it("formats time in Europe/Istanbul (21:30Z → 00:30 next day)", () => {
    const view = toMessageView(msg({ createdAt: "2026-11-12T21:30:00Z" }));
    assert.match(view.createdAtText, /13 Kas 2026/);
    assert.match(view.createdAtText, /00:30/);
  });

  it("keeps message text raw (rendered as a text node, never HTML)", () => {
    const view = toMessageView(msg({ text: "<img src=x onerror=alert(1)>" }));
    assert.equal(view.text, "<img src=x onerror=alert(1)>");
    const component = readFileSync(join(import.meta.dirname, "../../app/(panel)/complaints/[id]/Conversation.tsx"), "utf8");
    assert.doesNotMatch(component, /dangerouslySetInnerHTML|innerHTML/);
    assert.match(component, /\{m\.text\}/);
  });

  it("location coordinates are numeric and range-checked; empty text becomes null", () => {
    assert.deepEqual(toMessageView(msg({ messageType: "Location", latitude: "38.6", longitude: 27.4 })).coords, {
      lat: 38.6,
      lon: 27.4,
    });
    assert.equal(toMessageView(msg({ latitude: 200, longitude: 27 })).coords, null);
    assert.equal(toMessageView(msg({ text: "" })).text, null);
  });

  it("an empty conversation has no messages and no contact", () => {
    const view = conversationView({ complaintId: ID, maskedContact: null, messages: [] });
    assert.equal(view.maskedContact, null);
    assert.deepEqual(view.messages, []);
  });
});

describe("messages endpoint errors", () => {
  it("out-of-scope complaint (404 complaint.not_found) is shown by code", async () => {
    const api = createApiClient({
      baseUrl: "http://api",
      accessToken: "t",
      fetchImpl: async () =>
        new Response(JSON.stringify({ status: 404, title: "Şikâyet bulunamadı.", code: "complaint.not_found" }), {
          status: 404,
          headers: { "Content-Type": "application/problem+json" },
        }),
    });
    const result = await api.get<ComplaintConversation>(`/api/v1/complaints/${ID}/messages`);
    assert.equal(result.ok, false);
    if (result.ok) return;
    assert.equal(problemMessage(result.problem), "Şikâyet bulunamadı veya görüntüleme yetkiniz yok.");
  });
});
