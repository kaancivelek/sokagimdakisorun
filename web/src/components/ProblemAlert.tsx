import { problemFieldErrors, problemMessage, type ApiProblem } from "@/lib/api/problem";

/**
 * API hatasını `code` alanına göre Türkçe gösterir. Metin React tarafından kaçışlanır (HTML olarak render edilmez).
 * `fieldLabels` verilirse `validation.failed` alan mesajları (API'nin Türkçe metni) da listelenir.
 */
export function ProblemAlert({ problem, fieldLabels }: { problem: ApiProblem; fieldLabels?: Record<string, string> }) {
  const fieldErrors = fieldLabels ? Object.entries(problemFieldErrors(problem)) : [];
  return (
    <div className="alert error" role="alert">
      {problemMessage(problem)}
      {fieldErrors.length > 0 && (
        <ul className="field-errors">
          {fieldErrors.map(([field, messages]) => (
            <li key={field}>
              {fieldLabels?.[field] ?? field}: {messages.join(" ")}
            </li>
          ))}
        </ul>
      )}
      {problem.traceId && <div className="muted">İz kimliği: {problem.traceId}</div>}
    </div>
  );
}
