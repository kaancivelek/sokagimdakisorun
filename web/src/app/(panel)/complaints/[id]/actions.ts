"use server";

import { revalidatePath } from "next/cache";
import { redirect } from "next/navigation";

import type { components } from "@/api/schema";
import { isId } from "@/lib/api/client";
import { problemFieldErrors, problemMessage } from "@/lib/api/problem";
import { runCommand, type CommandOutcome } from "@/lib/complaints/run-command";
import { apiClient } from "@/lib/session/server";

export type CommandState = (CommandOutcome & { at: number }) | null;

/**
 * Durum komutu. Sayfa koruması action'ı korumaz: `apiClient()` oturumu yeniden doğrular; yetkiyi API verir.
 * 409 çakışmada (ve başarıda) sayfa verisi yeniden yüklenir: `revalidatePath` güncel detay + `version` getirir.
 */
export async function commandAction(complaintId: string, action: string, _prev: CommandState, form: FormData) {
  if (!isId(complaintId)) return { status: "error", message: "Geçersiz şikâyet.", fieldErrors: {}, reload: false, unauthenticated: false, at: Date.now() } satisfies CommandState;
  const api = await apiClient();
  const outcome = await runCommand(api, complaintId, action, form);
  if (outcome.status === "error" && outcome.unauthenticated) redirect("/login?reason=expired");
  if (outcome.status === "ok" || outcome.reload) revalidatePath(`/complaints/${complaintId}`);
  return { ...outcome, at: Date.now() } satisfies CommandState;
}

export interface CommentState {
  ok?: boolean;
  error?: string;
  fieldErrors?: Record<string, string[]>;
  at?: number;
}

export async function addCommentAction(complaintId: string, _prev: CommentState, form: FormData): Promise<CommentState> {
  if (!isId(complaintId)) return { error: "Geçersiz şikâyet." };
  const body = String(form.get("body") ?? "").trim();
  const visibility = form.get("visibility") === "ReporterVisible" ? "ReporterVisible" : "Internal";
  const api = await apiClient();
  const result = await api.post<components["schemas"]["CommentItem"]>(`/api/v1/complaints/${complaintId}/comments`, {
    body: body === "" ? null : body,
    visibility,
  } satisfies components["schemas"]["AddCommentRequest"]);
  if (!result.ok) {
    if (result.status === 401) redirect("/login?reason=expired");
    return { error: problemMessage(result.problem), fieldErrors: problemFieldErrors(result.problem), at: Date.now() };
  }
  revalidatePath(`/complaints/${complaintId}`);
  return { ok: true, at: Date.now() };
}
