import type { Metadata } from "next";

import type { components } from "@/api/schema";
import { ActionForm } from "@/components/ActionForm";
import { ProblemAlert } from "@/components/ProblemAlert";
import { apiClient, redirectToLoginIfUnauthenticated } from "@/lib/session/server";

import { addMemberAction, createServiceUnitAction, removeMemberAction, updateServiceUnitAction } from "../actions";

export const metadata: Metadata = { title: "Hizmet birimleri" };

type S = components["schemas"];

export default async function ServiceUnitsPage() {
  const api = await apiClient();
  const [units, users] = await Promise.all([
    api.get<S["ServiceUnitResponse"][]>("/api/v1/service-units"),
    // Üye adayları: panel kullanıcıları (ilk 100). Uygunluk (aktif personel vb.) kararını API verir.
    api.get<S["UserPage"]>("/api/v1/users", { query: { page: 1, pageSize: 100 } }),
  ]);
  if (!units.ok) redirectToLoginIfUnauthenticated(units.status);
  const candidates = users.ok ? users.data.items : [];

  return (
    <>
      <h1>Hizmet birimleri</h1>
      <section className="card">
        <h2>Yeni birim</h2>
        <ActionForm action={createServiceUnitAction} submitLabel="Oluştur" className="filters">
          <label>
            Kod
            <input name="code" required maxLength={64} />
          </label>
          <label>
            Ad
            <input name="name" required maxLength={200} />
          </label>
          <label>
            <span>
              <input name="isActive" type="checkbox" defaultChecked /> Aktif
            </span>
          </label>
        </ActionForm>
      </section>

      {!units.ok ? (
        <ProblemAlert problem={units.problem} />
      ) : (
        units.data.map((u) => (
          <section key={u.id} className="card">
            <h2>
              {u.name} <span className="muted">({u.code})</span>
            </h2>
            <ActionForm action={updateServiceUnitAction.bind(null, u.id)} submitLabel="Kaydet" className="filters">
              <label>
                Ad
                <input name="name" defaultValue={u.name} required maxLength={200} />
              </label>
              <label>
                <span>
                  <input name="isActive" type="checkbox" defaultChecked={u.isActive} /> Aktif
                </span>
              </label>
            </ActionForm>
            <h3>Üyeler</h3>
            {u.members.length === 0 ? (
              <p className="muted">Üye yok.</p>
            ) : (
              <ul>
                {u.members.map((m) => (
                  <li key={m.id} className="row">
                    {m.displayName}
                    <ActionForm action={removeMemberAction.bind(null, u.id, m.id)} submitLabel="Çıkar" inline />
                  </li>
                ))}
              </ul>
            )}
            <ActionForm action={addMemberAction.bind(null, u.id)} submitLabel="Üye ekle" inline>
              <select name="userId" defaultValue="" aria-label="Eklenecek kullanıcı">
                <option value="">Kullanıcı seçin…</option>
                {candidates
                  .filter((c) => !u.members.some((m) => m.id === c.id))
                  .map((c) => (
                    <option key={c.id} value={c.id}>
                      {c.displayName} ({c.email})
                    </option>
                  ))}
              </select>
            </ActionForm>
          </section>
        ))
      )}
    </>
  );
}
