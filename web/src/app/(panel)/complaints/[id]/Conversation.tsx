import type { ApiResult } from "@/lib/api/client";
import { ProblemAlert } from "@/components/ProblemAlert";
import { conversationView, type ComplaintConversation } from "@/lib/messaging/conversation";

/** WhatsApp konuşması (Server Component). Metinler düz metin düğümüdür; HTML olarak yorumlanmaz. */
export function Conversation({ result }: { result: ApiResult<ComplaintConversation> }) {
  if (!result.ok) return <ProblemAlert problem={result.problem} />;
  const view = conversationView(result.data);
  return (
    <>
      <p className="muted">
        Şikâyetçi numarası: {view.maskedContact ?? "—"} · Saatler Türkiye saatiyle (Europe/Istanbul).
      </p>
      {view.messages.length === 0 ? (
        <p className="muted">Bu şikâyete bağlı WhatsApp mesajı yok.</p>
      ) : (
        <ol className="chat" aria-label="Mesajlar, eskiden yeniye">
          {view.messages.map((m) => (
            <li key={m.id} className={`chat-msg ${m.inbound ? "inbound" : "outbound"}`}>
              <div className="chat-meta">
                <strong>{m.directionLabel}</strong>
                <span className="muted">{m.inbound ? "gelen" : "giden"}</span>
                <span className="badge">{m.typeLabel}</span>
                <time dateTime={m.createdAt} className="muted">
                  {m.createdAtText}
                </time>
              </div>
              {m.text && <p className="prewrap chat-text">{m.text}</p>}
              {m.coords && (
                <p className="chat-text">
                  Konum: {m.coords.lat.toFixed(5)}, {m.coords.lon.toFixed(5)}{" "}
                  <a
                    href={`https://www.openstreetmap.org/?mlat=${m.coords.lat}&mlon=${m.coords.lon}#map=18/${m.coords.lat}/${m.coords.lon}`}
                    target="_blank"
                    rel="noopener noreferrer"
                  >
                    Haritada aç
                  </a>
                </p>
              )}
              {m.hasMedia && (
                <p className="chat-text muted">
                  Fotoğraf saklandı — <a href="#ekler">Fotoğraflar ve ekler</a>
                </p>
              )}
              {!m.text && !m.coords && !m.hasMedia && <p className="chat-text muted">(içerik yok)</p>}
              <div className="chat-status">
                <span className={`delivery ${m.deliveryTone}`}>{m.deliveryLabel}</span>
                {m.failureReason && <span className="muted"> · {m.failureReason}</span>}
              </div>
            </li>
          ))}
        </ol>
      )}
    </>
  );
}
