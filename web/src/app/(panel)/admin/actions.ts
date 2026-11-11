"use server";

import { revalidatePath } from "next/cache";
import { redirect } from "next/navigation";

import type { components } from "@/api/schema";
import { isId, type ApiResult } from "@/lib/api/client";
import { bool, int, str, toFormState, type FormState } from "@/lib/form-state";
import { apiClient } from "@/lib/session/server";

type S = components["schemas"];

// Her action oturumu `apiClient()` ile yeniden doğrular; Admin yetkisini API denetler (Moderatöre 403).

function finish<T>(result: ApiResult<T>, path: string, message: string): FormState {
  if (!result.ok && result.status === 401) redirect("/login?reason=expired");
  if (result.ok) revalidatePath(path);
  return toFormState(result, message);
}

const INVALID: FormState = { error: "Geçersiz kayıt." };

// --- Kategoriler ---

export async function createCategoryAction(_prev: FormState, form: FormData): Promise<FormState> {
  const api = await apiClient();
  const body: S["CreateCategoryRequest"] = {
    code: str(form, "code"),
    name: str(form, "name"),
    description: str(form, "description"),
    sortOrder: int(form, "sortOrder") ?? 0,
    isActive: bool(form, "isActive"),
  };
  return finish(await api.post("/api/v1/categories", body), "/admin/categories", "Kategori oluşturuldu.");
}

export async function updateCategoryAction(id: string, _prev: FormState, form: FormData): Promise<FormState> {
  if (!isId(id)) return INVALID;
  const api = await apiClient();
  const body: S["UpdateCategoryRequest"] = {
    name: str(form, "name"),
    description: str(form, "description"),
    sortOrder: int(form, "sortOrder") ?? 0,
    isActive: bool(form, "isActive"),
  };
  return finish(await api.patch(`/api/v1/categories/${id}`, body), "/admin/categories", "Kategori güncellendi.");
}

// --- Hizmet birimleri ---

export async function createServiceUnitAction(_prev: FormState, form: FormData): Promise<FormState> {
  const api = await apiClient();
  const body: S["CreateServiceUnitRequest"] = {
    code: str(form, "code"),
    name: str(form, "name"),
    isActive: bool(form, "isActive"),
  };
  return finish(await api.post("/api/v1/service-units", body), "/admin/service-units", "Hizmet birimi oluşturuldu.");
}

export async function updateServiceUnitAction(id: string, _prev: FormState, form: FormData): Promise<FormState> {
  if (!isId(id)) return INVALID;
  const api = await apiClient();
  const body: S["UpdateServiceUnitRequest"] = { name: str(form, "name"), isActive: bool(form, "isActive") };
  return finish(await api.patch(`/api/v1/service-units/${id}`, body), "/admin/service-units", "Birim güncellendi.");
}

export async function addMemberAction(unitId: string, _prev: FormState, form: FormData): Promise<FormState> {
  const userId = str(form, "userId");
  if (!isId(unitId)) return INVALID;
  if (!isId(userId)) return { error: "Kullanıcı seçin." };
  const api = await apiClient();
  const body: S["AddMemberRequest"] = { userId };
  return finish(await api.post(`/api/v1/service-units/${unitId}/members`, body), "/admin/service-units", "Üye eklendi.");
}

export async function removeMemberAction(unitId: string, userId: string): Promise<FormState> {
  if (!isId(unitId) || !isId(userId)) return INVALID;
  const api = await apiClient();
  return finish(
    await api.del(`/api/v1/service-units/${unitId}/members/${userId}`),
    "/admin/service-units",
    "Üye çıkarıldı.",
  );
}

// --- Kullanıcılar ---

const ROLES = ["Admin", "Moderator"] as const;
const STATUSES = ["Active", "Suspended"] as const;

function role(form: FormData): S["StaffRole"] {
  const value = str(form, "role");
  return (ROLES as readonly string[]).includes(value ?? "") ? (value as S["StaffRole"]) : null;
}

export async function createUserAction(_prev: FormState, form: FormData): Promise<FormState> {
  const api = await apiClient();
  const body: S["CreateUserRequest"] = {
    email: str(form, "email"),
    displayName: str(form, "displayName"),
    // Parola yalnızca bu sunucu çağrısında taşınır; loglanmaz, yanıta dönmez.
    password: typeof form.get("password") === "string" ? (form.get("password") as string) : null,
    role: role(form),
  };
  return finish(await api.post("/api/v1/users", body), "/admin/users", "Kullanıcı oluşturuldu.");
}

export async function updateUserAction(id: string, _prev: FormState, form: FormData): Promise<FormState> {
  if (!isId(id)) return INVALID;
  const status = str(form, "status");
  const api = await apiClient();
  const body: S["UpdateUserRequest"] = {
    role: role(form),
    status: (STATUSES as readonly string[]).includes(status ?? "") ? (status as S["UserStatus"]) : null,
    displayName: str(form, "displayName"),
  };
  return finish(await api.patch(`/api/v1/users/${id}`, body), "/admin/users", "Kullanıcı güncellendi.");
}
