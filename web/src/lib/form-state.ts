import type { ApiResult } from "./api/client.ts";
import { problemFieldErrors, problemMessage } from "./api/problem.ts";

export interface FormState {
  ok?: boolean;
  message?: string;
  error?: string;
  fieldErrors?: Record<string, string[]>;
}

export function toFormState<T>(result: ApiResult<T>, successMessage: string): FormState {
  if (result.ok) return { ok: true, message: successMessage };
  const fieldErrors = problemFieldErrors(result.problem);
  return {
    error: problemMessage(result.problem),
    fieldErrors: Object.keys(fieldErrors).length > 0 ? fieldErrors : undefined,
  };
}

export function str(form: FormData, key: string): string | null {
  const value = form.get(key);
  if (typeof value !== "string") return null;
  const trimmed = value.trim();
  return trimmed === "" ? null : trimmed;
}

export function bool(form: FormData, key: string): boolean {
  return form.get(key) === "on" || form.get(key) === "true";
}

export function int(form: FormData, key: string): number | null {
  const value = str(form, key);
  if (value === null) return null;
  const n = Number.parseInt(value, 10);
  return Number.isFinite(n) ? n : null;
}
