// Şikâyet komutları: API'nin `allowedActions` içinde döndürdüğü eylem adlarının GÖRÜNÜM bilgisi.
// Burada geçiş kuralı, yetki veya "gerekçe zorunlu mu" bilgisi YOKTUR (DOMAIN_MODEL §4.5): hangi düğmenin
// görüneceğine yalnızca `allowedActions` karar verir; zorunlu alan hataları API'den (400 `validation.failed`) gelir.
// Alan listesi yalnızca istek gövdesinin şeklidir (OpenAPI: CommandRequest / MarkDuplicateRequest / AssignRequest).
import type { components } from "../../api/schema.d.ts";

type Schemas = components["schemas"];

export type CommandBody = Schemas["CommandRequest"] | Schemas["MarkDuplicateRequest"] | Schemas["AssignRequest"];

export type CommandField = "reason" | "reporterNote" | "targetComplaintId" | "serviceUnitId" | "assigneeUserId";

export interface CommandDefinition {
  /** API yol parçası ve `allowedActions` değeri. */
  action: string;
  label: string;
  /** İstek gövdesinin alanları (`version` her zaman eklenir). */
  fields: readonly CommandField[];
  /** Görsel vurgu; davranışı değiştirmez. */
  tone?: "danger";
}

const COMMAND_REQUEST: readonly CommandField[] = ["reason", "reporterNote"];

export const COMMANDS: readonly CommandDefinition[] = [
  { action: "start-review", label: "İncelemeye al", fields: COMMAND_REQUEST },
  { action: "accept", label: "Kabul et", fields: COMMAND_REQUEST },
  { action: "assign", label: "Birime ata / yeniden ata", fields: ["serviceUnitId", "assigneeUserId", "reason"] },
  { action: "unassign", label: "Atamayı kaldır", fields: COMMAND_REQUEST },
  { action: "start-work", label: "Çalışmayı başlat", fields: COMMAND_REQUEST },
  { action: "request-information", label: "Şikâyetçiden bilgi iste", fields: COMMAND_REQUEST },
  { action: "resume", label: "Bilgi beklemeyi sonlandır", fields: COMMAND_REQUEST },
  { action: "resolve", label: "Çözüldü olarak işaretle", fields: COMMAND_REQUEST },
  { action: "close", label: "Kapat", fields: COMMAND_REQUEST },
  { action: "reopen", label: "Yeniden aç", fields: COMMAND_REQUEST },
  { action: "reject", label: "Reddet", fields: COMMAND_REQUEST, tone: "danger" },
  {
    action: "mark-duplicate",
    label: "Tekrar olarak işaretle",
    fields: ["targetComplaintId", "reason", "reporterNote"],
    tone: "danger",
  },
  { action: "mark-spam", label: "Spam olarak işaretle", fields: COMMAND_REQUEST, tone: "danger" },
];

const BY_ACTION = new Map(COMMANDS.map((c) => [c.action, c]));

export function findCommand(action: string): CommandDefinition | undefined {
  return BY_ACTION.get(action);
}

/**
 * Gösterilecek komutlar: yalnızca API'nin izin verdiği ve panelin istek gövdesini bildiği eylemler, API sırasıyla.
 * Bilinmeyen bir eylem adı (sözleşmeye yeni eklenmiş) gösterilmez, ayrıca raporlanır.
 */
export function visibleCommands(allowedActions: readonly string[]): {
  commands: CommandDefinition[];
  unknown: string[];
} {
  const commands: CommandDefinition[] = [];
  const unknown: string[] = [];
  for (const action of new Set(allowedActions)) {
    const command = BY_ACTION.get(action);
    if (command) commands.push(command);
    else unknown.push(action);
  }
  return { commands, unknown };
}

export const FIELD_LABELS: Record<CommandField, string> = {
  reason: "İç gerekçe (şikâyetçiye gönderilmez)",
  reporterNote: "Şikâyetçiye iletilecek not / soru",
  targetComplaintId: "Asıl şikâyet",
  serviceUnitId: "Hizmet birimi",
  assigneeUserId: "Sorumlu kişi (isteğe bağlı)",
};
