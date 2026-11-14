// Şikâyet detayındaki "WhatsApp konuşması": API yanıtı → gösterim modeli.
// Telefon numarası yalnızca API'nin maskeli hâliyle gösterilir; mesaj metni düz metindir (React kaçışlar, HTML yok).
import type { components } from "../../api/schema.d.ts";
import { formatDateTime } from "../format.ts";

export type ComplaintConversation = components["schemas"]["ComplaintConversation"];
export type ConversationMessage = components["schemas"]["ConversationMessageItem"];

export const DIRECTION_LABELS: Record<string, string> = {
  Inbound: "Şikâyetçi",
  Outbound: "Sistem",
};

export const MESSAGE_TYPE_LABELS: Record<string, string> = {
  Text: "Metin",
  Location: "Konum",
  Image: "Fotoğraf",
  Unsupported: "Desteklenmeyen tür",
};

export const DELIVERY_STATUS_LABELS: Record<string, string> = {
  Received: "Alındı",
  Queued: "Kuyrukta",
  Sending: "Gönderiliyor",
  Sent: "Gönderildi",
  Delivered: "Teslim edildi",
  Read: "Okundu",
  Failed: "Gönderilemedi",
};

export type DeliveryTone = "ok" | "pending" | "error" | "neutral";

const TONES: Record<string, DeliveryTone> = {
  Received: "neutral",
  Queued: "pending",
  Sending: "pending",
  Sent: "ok",
  Delivered: "ok",
  Read: "ok",
  Failed: "error",
};

function labelOf(map: Record<string, string>, value: string): string {
  return map[value] ?? value;
}

export interface MessageView {
  id: string;
  inbound: boolean;
  directionLabel: string;
  typeLabel: string;
  deliveryLabel: string;
  deliveryTone: DeliveryTone;
  /** Ham metin; JSX içinde metin düğümü olarak render edilir. */
  text: string | null;
  coords: { lat: number; lon: number } | null;
  hasMedia: boolean;
  failureReason: string | null;
  createdAt: string;
  createdAtText: string;
}

function toCoords(m: ConversationMessage): MessageView["coords"] {
  if (m.latitude == null || m.longitude == null) return null;
  const lat = Number(m.latitude);
  const lon = Number(m.longitude);
  if (!Number.isFinite(lat) || !Number.isFinite(lon) || Math.abs(lat) > 90 || Math.abs(lon) > 180) return null;
  return { lat, lon };
}

export function toMessageView(m: ConversationMessage): MessageView {
  return {
    id: m.id,
    inbound: m.direction === "Inbound",
    directionLabel: labelOf(DIRECTION_LABELS, m.direction),
    typeLabel: labelOf(MESSAGE_TYPE_LABELS, m.messageType),
    deliveryLabel: labelOf(DELIVERY_STATUS_LABELS, m.deliveryStatus),
    deliveryTone: TONES[m.deliveryStatus] ?? "neutral",
    text: m.text && m.text.length > 0 ? m.text : null,
    coords: toCoords(m),
    hasMedia: m.hasMedia,
    failureReason: m.failureReason ?? null,
    createdAt: m.createdAt,
    createdAtText: formatDateTime(m.createdAt),
  };
}

/** API eskiden yeniye döner; sıra yine de zaman damgasına göre (eşitlikte API sırası) güvenceye alınır. */
export function conversationView(conversation: ComplaintConversation) {
  const messages = conversation.messages
    .map((m, index) => ({ m, index, t: Date.parse(m.createdAt) }))
    .sort((a, b) => (Number.isNaN(a.t) || Number.isNaN(b.t) || a.t === b.t ? a.index - b.index : a.t - b.t))
    .map(({ m }) => toMessageView(m));
  return { maskedContact: conversation.maskedContact ?? null, messages };
}
