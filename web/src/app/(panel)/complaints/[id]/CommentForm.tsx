"use client";

import { useActionState } from "react";

import { addCommentAction, type CommentState } from "./actions";

export function CommentForm({ complaintId }: { complaintId: string }) {
  const [state, action, pending] = useActionState<CommentState, FormData>(
    addCommentAction.bind(null, complaintId),
    {},
  );
  return (
    // Başarılı gönderimde form yeniden oluşturulur (alanlar temizlenir).
    <form key={state.ok ? state.at : "form"} action={action} className="stack">
      {state.error && (
        <div className="alert error" role="alert">
          {state.error}
        </div>
      )}
      <label>
        Yorum
        <textarea name="body" maxLength={4000} required />
        {state.fieldErrors?.body && <span style={{ color: "var(--danger)" }}>{state.fieldErrors.body.join(" ")}</span>}
      </label>
      <label>
        Görünürlük
        <select name="visibility" defaultValue="Internal">
          <option value="Internal">İç not (şikâyetçiye gitmez)</option>
          <option value="ReporterVisible">Şikâyetçiye görünür</option>
        </select>
      </label>
      <div>
        <button type="submit" disabled={pending}>
          {pending ? "Ekleniyor…" : "Yorum ekle"}
        </button>
      </div>
    </form>
  );
}
