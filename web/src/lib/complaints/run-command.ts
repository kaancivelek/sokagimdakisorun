// Komut çalıştırma: form verisinden istek gövdesi + sonuç yorumlama. Next.js'e bağımlı değildir (birim testi).
import type { components } from "../../api/schema.d.ts";
import { assertId, isId, type ApiClient } from "../api/client.ts";
import { problemFieldErrors, problemMessage } from "../api/problem.ts";
import { findCommand } from "./commands.ts";

type ComplaintDetail = components["schemas"]["ComplaintDetail"];

export type CommandOutcome =
  | { status: "ok"; message: string }
  /** 409 `concurrency.conflict`: çağıran güncel veriyi yeniden yüklemelidir. */
  | { status: "conflict"; message: string; reload: true }
  | { status: "error"; message: string; fieldErrors: Record<string, string[]>; reload: boolean; unauthenticated: boolean };

function text(form: FormData, key: string): string | null {
  const value = form.get(key);
  if (typeof value !== "string") return null;
  const trimmed = value.trim();
  return trimmed === "" ? null : trimmed;
}

function idOrNull(form: FormData, key: string): string | null {
  const value = text(form, key);
  return value && isId(value) ? value : null;
}

/** Gövde: her zaman `version` + komutun şemasındaki alanlar. Boş alan `null` gönderilir; zorunluluğa API karar verir. */
export function buildCommandBody(action: string, form: FormData): Record<string, string | null> {
  const command = findCommand(action);
  if (!command) throw new Error(`Bilinmeyen komut: ${action}`);
  const body: Record<string, string | null> = { version: text(form, "version") };
  for (const field of command.fields) {
    body[field] =
      field === "targetComplaintId" || field === "serviceUnitId" || field === "assigneeUserId"
        ? idOrNull(form, field)
        : text(form, field);
  }
  return body;
}

export async function runCommand(
  client: ApiClient,
  complaintId: string,
  action: string,
  form: FormData,
): Promise<CommandOutcome> {
  const command = findCommand(action);
  if (!command) {
    return { status: "error", message: "Bilinmeyen işlem.", fieldErrors: {}, reload: false, unauthenticated: false };
  }
  const result = await client.post<ComplaintDetail>(
    `/api/v1/complaints/${assertId(complaintId)}/${command.action}`,
    buildCommandBody(action, form),
  );
  if (result.ok) return { status: "ok", message: `“${command.label}” işlemi uygulandı.` };

  if (result.problem.code === "concurrency.conflict") {
    return { status: "conflict", message: problemMessage(result.problem), reload: true };
  }
  return {
    status: "error",
    message: problemMessage(result.problem),
    fieldErrors: problemFieldErrors(result.problem),
    // Yasak geçiş de bayat veriye işarettir (durum başka biri tarafından değişmiş olabilir): güncel veriyi göster.
    reload: result.status === 409,
    unauthenticated: result.status === 401,
  };
}
