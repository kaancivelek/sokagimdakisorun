import type { Metadata } from "next";
import Link from "next/link";

import type { components } from "@/api/schema";
import { ActionForm } from "@/components/ActionForm";
import { ProblemAlert } from "@/components/ProblemAlert";
import { formatDateTime, label, ROLE_LABELS, USER_STATUS_LABELS } from "@/lib/format";
import { apiClient, redirectToLoginIfUnauthenticated } from "@/lib/session/server";

import { createUserAction, updateUserAction } from "../actions";

export const metadata: Metadata = { title: "Kullanıcılar" };

const PAGE_SIZE = 25;

export default async function UsersPage({ searchParams }: PageProps<"/admin/users">) {
  const raw = (await searchParams).page;
  const page = Math.max(1, Number.parseInt(typeof raw === "string" ? raw : "1", 10) || 1);
  const api = await apiClient();
  const result = await api.get<components["schemas"]["UserPage"]>("/api/v1/users", {
    query: { page, pageSize: PAGE_SIZE },
  });
  if (!result.ok) redirectToLoginIfUnauthenticated(result.status);
  const pageCount = result.ok ? Math.max(1, Math.ceil(Number(result.data.totalCount) / PAGE_SIZE)) : 1;

  return (
    <>
      <h1>Panel kullanıcıları</h1>
      <section className="card">
        <h2>Yeni kullanıcı</h2>
        <p className="muted">Parola kuralları API tarafından denetlenir (en az 12 karakter, büyük/küçük harf, rakam, sembol).</p>
        <ActionForm action={createUserAction} submitLabel="Oluştur" className="filters">
          <label>
            E-posta
            <input name="email" type="email" required autoComplete="off" />
          </label>
          <label>
            Görünen ad
            <input name="displayName" required maxLength={200} />
          </label>
          <label>
            Geçici parola
            <input name="password" type="password" required autoComplete="new-password" />
          </label>
          <label>
            Rol
            <select name="role" defaultValue="Moderator">
              <option value="Moderator">Moderatör</option>
              <option value="Admin">Yönetici</option>
            </select>
          </label>
        </ActionForm>
      </section>

      {!result.ok ? (
        <ProblemAlert problem={result.problem} />
      ) : (
        <section className="card">
          <table>
            <thead>
              <tr>
                <th>Kullanıcı</th>
                <th>Durum</th>
                <th>Oluşturulma</th>
                <th>Düzenle</th>
              </tr>
            </thead>
            <tbody>
              {result.data.items.map((u) => (
                <tr key={u.id}>
                  <td>
                    {u.displayName}
                    <div className="muted">{u.email}</div>
                    <div className="muted">{u.roles.map((r) => label(ROLE_LABELS, r)).join(", ")}</div>
                  </td>
                  <td>
                    {label(USER_STATUS_LABELS, u.status)}
                    {u.isLockedOut && <div className="badge">Kilitli</div>}
                  </td>
                  <td>{formatDateTime(u.createdAt)}</td>
                  <td>
                    <ActionForm action={updateUserAction.bind(null, u.id)} submitLabel="Kaydet" inline>
                      <input name="displayName" defaultValue={u.displayName} aria-label="Görünen ad" />
                      <select name="role" defaultValue={u.roles.includes("Admin") ? "Admin" : "Moderator"} aria-label="Rol">
                        <option value="Moderator">Moderatör</option>
                        <option value="Admin">Yönetici</option>
                      </select>
                      <select name="status" defaultValue={u.status === "Suspended" ? "Suspended" : "Active"} aria-label="Durum">
                        <option value="Active">Aktif</option>
                        <option value="Suspended">Askıda</option>
                      </select>
                    </ActionForm>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
          <nav className="pagination" aria-label="Sayfalama">
            {page > 1 && (
              <Link className="button secondary" href={`/admin/users?page=${page - 1}`}>
                ← Önceki
              </Link>
            )}
            <span className="muted">
              {page} / {pageCount}
            </span>
            {page < pageCount && (
              <Link className="button secondary" href={`/admin/users?page=${page + 1}`}>
                Sonraki →
              </Link>
            )}
          </nav>
        </section>
      )}
    </>
  );
}
