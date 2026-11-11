"use server";

import { redirect } from "next/navigation";

import { problemMessage } from "@/lib/api/problem";
import { apiBaseUrl } from "@/lib/env";
import { login, safeNextPath } from "@/lib/session/lifecycle";
import { clearSession, readSession, writeSession } from "@/lib/session/server";
import { createApiClient } from "@/lib/api/client";

export interface LoginState {
  error?: string;
  email?: string;
}

export async function loginAction(_prev: LoginState, formData: FormData): Promise<LoginState> {
  const email = String(formData.get("email") ?? "").trim();
  const password = String(formData.get("password") ?? "");
  if (!email || !password) return { error: "E-posta ve parola zorunludur.", email };

  const result = await login(email, password, { baseUrl: apiBaseUrl() });
  if (!result.ok) return { error: problemMessage(result.problem), email };

  await writeSession(result.session);
  redirect(safeNextPath(String(formData.get("next") ?? "")));
}

export async function logoutAction(): Promise<void> {
  const session = await readSession();
  if (session) {
    // API tarafında kullanıcının tüm token'larını geçersiz kılar (WS-04); hata olsa da yerel oturum silinir.
    await createApiClient({ baseUrl: apiBaseUrl(), accessToken: session.accessToken }).post("/api/v1/auth/logout");
  }
  await clearSession();
  redirect("/login");
}
