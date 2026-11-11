"use client";

import { useActionState } from "react";

import type { FormState } from "@/lib/form-state";

/** Server Action'ı çağıran basit form: sonucu (API `code`'una göre seçilmiş Türkçe mesaj) gösterir. */
export function ActionForm({
  action,
  children,
  submitLabel,
  className,
  inline,
}: {
  action: (prev: FormState, form: FormData) => Promise<FormState>;
  children?: React.ReactNode;
  submitLabel: string;
  className?: string;
  inline?: boolean;
}) {
  const [state, formAction, pending] = useActionState<FormState, FormData>(action, {});
  return (
    <form action={formAction} className={className ?? (inline ? "row" : "stack")}>
      {state.error && (
        <div className="alert error" role="alert" style={inline ? { width: "100%" } : undefined}>
          {state.error}
          {state.fieldErrors &&
            Object.entries(state.fieldErrors).map(([field, messages]) => (
              <div key={field}>
                {field}: {messages.join(" ")}
              </div>
            ))}
        </div>
      )}
      {state.ok && state.message && <div className="alert ok">{state.message}</div>}
      {children}
      <div>
        <button type="submit" disabled={pending}>
          {pending ? "Kaydediliyor…" : submitLabel}
        </button>
      </div>
    </form>
  );
}
