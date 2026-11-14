import type { Metadata } from "next";
import Link from "next/link";
import { notFound } from "next/navigation";

import type { components } from "@/api/schema";
import { ProblemAlert } from "@/components/ProblemAlert";
import { isId } from "@/lib/api/client";
import { visibleCommands } from "@/lib/complaints/commands";
import { loadLookups } from "@/lib/complaints/lookups";
import {
  ACTOR_KIND_LABELS,
  ATTACHMENT_SOURCE_LABELS,
  formatDateTime,
  label,
  SOURCE_LABELS,
  statusLabel,
  VISIBILITY_LABELS,
} from "@/lib/format";
import { apiClient, redirectToLoginIfUnauthenticated } from "@/lib/session/server";

import { CommandPanel } from "./CommandPanel";
import { CommentForm } from "./CommentForm";
import { Conversation } from "./Conversation";

export const metadata: Metadata = { title: "Şikâyet detayı" };

type Schemas = components["schemas"];

export default async function ComplaintDetailPage({ params }: PageProps<"/complaints/[id]">) {
  const { id } = await params;
  if (!isId(id)) notFound();

  const api = await apiClient();
  const detail = await api.get<Schemas["ComplaintDetail"]>(`/api/v1/complaints/${id}`);
  if (!detail.ok) {
    redirectToLoginIfUnauthenticated(detail.status);
    // Kapsam dışı şikâyet API'de 404'tür (varlık sızdırılmaz).
    if (detail.status === 404) notFound();
    return <ProblemAlert problem={detail.problem} />;
  }
  const complaint = detail.data;

  const [history, comments, attachments, messages, lookups] = await Promise.all([
    api.get<Schemas["ComplaintHistory"]>(`/api/v1/complaints/${id}/history`),
    api.get<Schemas["CommentItem"][]>(`/api/v1/complaints/${id}/comments`),
    api.get<Schemas["AttachmentItem"][]>(`/api/v1/complaints/${id}/attachments`),
    api.get<Schemas["ComplaintConversation"]>(`/api/v1/complaints/${id}/messages`),
    loadLookups(api),
  ]);

  const { commands, unknown } = visibleCommands(complaint.allowedActions);
  const { location } = complaint;
  const lat = location.latitude == null ? null : Number(location.latitude);
  const lon = location.longitude == null ? null : Number(location.longitude);
  const hasCoords = lat !== null && lon !== null && Number.isFinite(lat) && Number.isFinite(lon);

  return (
    <>
      <p>
        <Link href="/complaints">← Şikâyetler</Link>
      </p>
      <h1>
        {complaint.publicReference} <span className="badge">{statusLabel(complaint.status)}</span>
      </h1>

      <div className="grid-2">
        <div>
          <section className="card">
            <h2>Başvuru</h2>
            <dl className="props">
              <dt>Kategori</dt>
              <dd>{complaint.category.name}</dd>
              <dt>Şikâyetçi</dt>
              <dd>{complaint.reporter.displayName}</dd>
              <dt>Kanal</dt>
              <dd>{complaint.sourceChannel}</dd>
              <dt>Başvuru</dt>
              <dd>{formatDateTime(complaint.submittedAt)}</dd>
              <dt>Son güncelleme</dt>
              <dd>{formatDateTime(complaint.updatedAt)}</dd>
              {complaint.resolvedAt && (
                <>
                  <dt>Çözüldü</dt>
                  <dd>{formatDateTime(complaint.resolvedAt)}</dd>
                </>
              )}
              {complaint.closedAt && (
                <>
                  <dt>Kapatıldı</dt>
                  <dd>{formatDateTime(complaint.closedAt)}</dd>
                </>
              )}
              <dt>Atama</dt>
              <dd>
                {complaint.activeAssignment
                  ? `${complaint.activeAssignment.serviceUnit.name}${complaint.activeAssignment.assignee ? ` · ${complaint.activeAssignment.assignee.displayName}` : ""} (${formatDateTime(complaint.activeAssignment.assignedAt)})`
                  : "Atanmamış"}
              </dd>
              {complaint.suggestedServiceUnit && (
                <>
                  <dt>Önerilen birim</dt>
                  <dd>{complaint.suggestedServiceUnit.name}</dd>
                </>
              )}
              {complaint.duplicateOf && (
                <>
                  <dt>Asıl şikâyet</dt>
                  <dd>
                    <Link href={`/complaints/${complaint.duplicateOf.id}`}>{complaint.duplicateOf.publicReference}</Link>
                  </dd>
                </>
              )}
            </dl>
            <h2 style={{ marginTop: "1rem" }}>Açıklama</h2>
            <p className="prewrap">{complaint.description}</p>
          </section>

          <section className="card">
            <h2>Konum</h2>
            <dl className="props">
              <dt>Adres</dt>
              <dd>{location.addressText ?? "—"}</dd>
              <dt>Kaynak</dt>
              <dd>{location.source === "WhatsAppPin" ? "WhatsApp konumu" : "Metin adres"}</dd>
              <dt>Koordinat</dt>
              <dd>
                {hasCoords ? (
                  <>
                    {lat.toFixed(5)}, {lon.toFixed(5)}{" "}
                    <a
                      href={`https://www.openstreetmap.org/?mlat=${lat}&mlon=${lon}#map=18/${lat}/${lon}`}
                      target="_blank"
                      rel="noopener noreferrer"
                    >
                      Haritada aç
                    </a>
                  </>
                ) : (
                  "—"
                )}
              </dd>
            </dl>
          </section>

          <section className="card" id="ekler">
            <h2>Fotoğraflar ve ekler</h2>
            {!attachments.ok ? (
              <ProblemAlert problem={attachments.problem} />
            ) : attachments.data.length === 0 ? (
              <p className="muted">Ek yok.</p>
            ) : (
              <div className="photos">
                {attachments.data.map((a) => {
                  // Tarayıcı yalnızca panelin Route Handler'ını görür; API adresi ve token sızmaz (ADR-0006 §6).
                  const href = `/attachments/${complaint.id}/${a.id}`;
                  return (
                    <figure key={a.id} style={{ margin: 0 }}>
                      <a href={href} target="_blank" rel="noopener noreferrer">
                        {/* eslint-disable-next-line @next/next/no-img-element -- oturumlu akış; next/image optimizasyonu uygun değil */}
                        <img src={href} alt={`Ek: ${label(ATTACHMENT_SOURCE_LABELS, a.source)}`} loading="lazy" />
                      </a>
                      <figcaption className="muted">
                        {label(ATTACHMENT_SOURCE_LABELS, a.source)} · {formatDateTime(a.createdAt)}
                      </figcaption>
                    </figure>
                  );
                })}
              </div>
            )}
          </section>

          <section className="card">
            <h2>Durum geçmişi</h2>
            {!history.ok ? (
              <ProblemAlert problem={history.problem} />
            ) : (
              <table>
                <thead>
                  <tr>
                    <th>Zaman</th>
                    <th>Geçiş</th>
                    <th>Yapan</th>
                    <th>İç gerekçe</th>
                    <th>Şikâyetçiye not</th>
                  </tr>
                </thead>
                <tbody>
                  {history.data.statusChanges.map((h) => (
                    <tr key={h.id}>
                      <td>{formatDateTime(h.occurredAt)}</td>
                      <td>
                        {h.fromStatus ? `${statusLabel(h.fromStatus)} → ` : ""}
                        {statusLabel(h.toStatus)}
                      </td>
                      <td>
                        {h.actor?.displayName ?? label(ACTOR_KIND_LABELS, h.actorKind)}
                        <div className="muted">{label(SOURCE_LABELS, h.source)}</div>
                      </td>
                      <td className="prewrap">{h.reason ?? "—"}</td>
                      <td className="prewrap">{h.reporterNote ?? "—"}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            )}
          </section>

          <section className="card">
            <h2>Atama hareketleri</h2>
            {!history.ok ? null : history.data.assignments.length === 0 ? (
              <p className="muted">Atama yok.</p>
            ) : (
              <table>
                <thead>
                  <tr>
                    <th>Birim / kişi</th>
                    <th>Atandı</th>
                    <th>Kaldırıldı</th>
                  </tr>
                </thead>
                <tbody>
                  {history.data.assignments.map((a) => (
                    <tr key={a.id}>
                      <td>
                        {a.serviceUnit.name}
                        {a.assignee ? ` · ${a.assignee.displayName}` : ""}
                      </td>
                      <td>
                        {formatDateTime(a.assignedAt)} — {a.assignedBy.displayName}
                        {a.reason && <div className="prewrap muted">{a.reason}</div>}
                      </td>
                      <td>
                        {a.unassignedAt ? (
                          <>
                            {formatDateTime(a.unassignedAt)}
                            {a.unassignedBy ? ` — ${a.unassignedBy.displayName}` : ""}
                            {a.unassignReason && <div className="prewrap muted">{a.unassignReason}</div>}
                          </>
                        ) : (
                          <span className="badge">Aktif</span>
                        )}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            )}
          </section>

          <section className="card">
            <h2>WhatsApp konuşması</h2>
            <Conversation result={messages} />
          </section>
        </div>

        <div>
          <section className="card">
            <h2>İşlemler</h2>
            <CommandPanel
              complaintId={complaint.id}
              version={complaint.version}
              commands={commands}
              serviceUnits={lookups.serviceUnits.filter((u) => u.isActive).map((u) => ({ id: u.id, label: u.name }))}
              members={lookups.members.map((m) => ({ id: m.id, label: m.displayName }))}
            />
            {unknown.length > 0 && (
              <p className="muted">Panelin henüz desteklemediği işlemler: {unknown.join(", ")}</p>
            )}
          </section>

          <section className="card">
            <h2>Yorumlar</h2>
            {!comments.ok ? (
              <ProblemAlert problem={comments.problem} />
            ) : comments.data.length === 0 ? (
              <p className="muted">Henüz yorum yok.</p>
            ) : (
              <ul className="stack" style={{ listStyle: "none", padding: 0 }}>
                {comments.data.map((c) => (
                  <li key={c.id} className="command">
                    <div className="row">
                      <strong>{c.author.displayName}</strong>
                      <span className="badge">{label(VISIBILITY_LABELS, c.visibility)}</span>
                      <span className="muted">
                        {formatDateTime(c.createdAt)} · {label(SOURCE_LABELS, c.source)}
                      </span>
                    </div>
                    <p className="prewrap" style={{ margin: "0.25rem 0 0" }}>
                      {c.body}
                    </p>
                  </li>
                ))}
              </ul>
            )}
            <CommentForm complaintId={complaint.id} />
          </section>
        </div>
      </div>
    </>
  );
}
