"use client";

import { useActionState } from "react";

import { loginAction, type LoginState } from "./actions";

export function LoginForm({ next, notice }: { next: string; notice?: string }) {
  const [state, action, pending] = useActionState<LoginState, FormData>(loginAction, {});
  return (
    <form action={action} className="card stack">
      <h1>Yönetim Paneli Girişi</h1>
      {notice && !state.error && <div className="alert warn">{notice}</div>}
      {state.error && (
        <div className="alert error" role="alert">
          {state.error}
        </div>
      )}
      <input type="hidden" name="next" value={next} />
      <label>
        E-posta
        <input name="email" type="email" autoComplete="username" required defaultValue={state.email} />
      </label>
      <label>
        Parola
        <input name="password" type="password" autoComplete="current-password" required />
      </label>
      <button type="submit" disabled={pending}>
        {pending ? "Giriş yapılıyor…" : "Giriş yap"}
      </button>
    </form>
  );
}
