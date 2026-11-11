import type { Metadata } from "next";

import type { components } from "@/api/schema";
import { ActionForm } from "@/components/ActionForm";
import { ProblemAlert } from "@/components/ProblemAlert";
import { apiClient, redirectToLoginIfUnauthenticated } from "@/lib/session/server";

import { createCategoryAction, updateCategoryAction } from "../actions";

export const metadata: Metadata = { title: "Kategoriler" };

export default async function CategoriesPage() {
  const api = await apiClient();
  const result = await api.get<components["schemas"]["CategoryResponse"][]>("/api/v1/categories");
  if (!result.ok) redirectToLoginIfUnauthenticated(result.status);

  return (
    <>
      <h1>Kategoriler</h1>
      <section className="card">
        <h2>Yeni kategori</h2>
        <ActionForm action={createCategoryAction} submitLabel="Oluştur" className="filters">
          <label>
            Kod
            <input name="code" required maxLength={64} />
          </label>
          <label>
            Ad
            <input name="name" required maxLength={200} />
          </label>
          <label>
            Açıklama
            <input name="description" maxLength={1000} />
          </label>
          <label>
            Sıra
            <input name="sortOrder" type="number" defaultValue={0} style={{ width: "6rem" }} />
          </label>
          <label>
            <span>
              <input name="isActive" type="checkbox" defaultChecked /> Aktif
            </span>
          </label>
        </ActionForm>
      </section>

      {!result.ok ? (
        <ProblemAlert problem={result.problem} />
      ) : (
        <section className="card">
          <h2>Mevcut kategoriler</h2>
          <div className="stack">
            {result.data.map((c) => (
              <div key={c.id} className="command">
                <div className="muted">Kod: {c.code}</div>
                <ActionForm action={updateCategoryAction.bind(null, c.id)} submitLabel="Kaydet" className="filters">
                  <label>
                    Ad
                    <input name="name" defaultValue={c.name} required maxLength={200} />
                  </label>
                  <label>
                    Açıklama
                    <input name="description" defaultValue={c.description ?? ""} maxLength={1000} />
                  </label>
                  <label>
                    Sıra
                    <input
                      name="sortOrder"
                      type="number"
                      defaultValue={Number(c.sortOrder)}
                      style={{ width: "6rem" }}
                    />
                  </label>
                  <label>
                    <span>
                      <input name="isActive" type="checkbox" defaultChecked={c.isActive} /> Aktif
                    </span>
                  </label>
                </ActionForm>
              </div>
            ))}
          </div>
        </section>
      )}
    </>
  );
}
