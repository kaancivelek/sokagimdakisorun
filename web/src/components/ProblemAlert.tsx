import { problemMessage, type ApiProblem } from "@/lib/api/problem";

/** API hatasını `code` alanına göre Türkçe gösterir. Metin React tarafından kaçışlanır (HTML olarak render edilmez). */
export function ProblemAlert({ problem }: { problem: ApiProblem }) {
  return (
    <div className="alert error" role="alert">
      {problemMessage(problem)}
      {problem.traceId && <div className="muted">İz kimliği: {problem.traceId}</div>}
    </div>
  );
}
