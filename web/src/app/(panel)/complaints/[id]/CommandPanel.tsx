"use client";

import { useActionState } from "react";

import { FIELD_LABELS, type CommandDefinition, type CommandField } from "@/lib/complaints/commands";

import { commandAction, type CommandState } from "./actions";

interface Option {
  id: string;
  label: string;
}

export interface CommandPanelProps {
  complaintId: string;
  version: string;
  commands: CommandDefinition[];
  serviceUnits: Option[];
  members: Option[];
}

/** Düğmeler yalnızca API'nin `allowedActions` listesinden gelir (sunucu tarafında süzülür). */
export function CommandPanel(props: CommandPanelProps) {
  if (props.commands.length === 0) {
    return <p className="muted">Bu kayıt için şu an yapabileceğiniz bir işlem yok.</p>;
  }
  return (
    <div className="stack">
      {props.commands.map((command) => (
        // Yeniden yüklemede (409 sonrası) gizli `version` alanı props'tan güncellenir; çakışma mesajı korunur.
        <CommandForm key={command.action} command={command} {...props} />
      ))}
    </div>
  );
}

function CommandForm({
  command,
  complaintId,
  version,
  serviceUnits,
  members,
}: CommandPanelProps & { command: CommandDefinition }) {
  const [state, action, pending] = useActionState<CommandState, FormData>(
    commandAction.bind(null, complaintId, command.action),
    null,
  );
  const fieldError = (field: CommandField) =>
    state?.status === "error" ? state.fieldErrors[field]?.join(" ") : undefined;

  return (
    <details className="command" open={state?.status === "error"}>
      <summary style={command.tone === "danger" ? { color: "var(--danger)" } : undefined}>{command.label}</summary>
      <form action={action} className="stack" style={{ marginTop: "0.5rem" }}>
        <input type="hidden" name="version" value={version} />
        {state && state.status !== "ok" && (
          <div className={`alert ${state.status === "conflict" ? "warn" : "error"}`} role="alert">
            {state.message}
          </div>
        )}
        {command.fields.map((field) => (
          <label key={field}>
            {FIELD_LABELS[field]}
            {field === "serviceUnitId" ? (
              <select name={field} defaultValue="">
                <option value="">Seçin…</option>
                {serviceUnits.map((o) => (
                  <option key={o.id} value={o.id}>
                    {o.label}
                  </option>
                ))}
              </select>
            ) : field === "assigneeUserId" ? (
              <select name={field} defaultValue="">
                <option value="">— Kişi seçilmedi —</option>
                {members.map((o) => (
                  <option key={o.id} value={o.id}>
                    {o.label}
                  </option>
                ))}
              </select>
            ) : field === "targetComplaintId" ? (
              <input name={field} placeholder="Asıl şikâyetin kimliği (GUID)" autoComplete="off" />
            ) : (
              <textarea name={field} maxLength={2000} />
            )}
            {fieldError(field) && <span style={{ color: "var(--danger)" }}>{fieldError(field)}</span>}
          </label>
        ))}
        <div>
          <button type="submit" disabled={pending}>
            {pending ? "Uygulanıyor…" : command.label}
          </button>
        </div>
      </form>
    </details>
  );
}
