import "server-only";

import type { components } from "@/api/schema";
import type { ApiClient } from "@/lib/api/client";

type Schemas = components["schemas"];

export interface Lookups {
  categories: Schemas["CategoryResponse"][];
  serviceUnits: Schemas["ServiceUnitResponse"][];
  /** Atanabilecek kişiler: birim üyelerinin birleşimi (kullanıcı listesi yalnızca Admin'e açık olduğu için). */
  members: Schemas["UserRef"][];
}

export async function loadLookups(api: ApiClient): Promise<Lookups> {
  const [categories, serviceUnits] = await Promise.all([
    api.get<Schemas["CategoryResponse"][]>("/api/v1/categories"),
    api.get<Schemas["ServiceUnitResponse"][]>("/api/v1/service-units"),
  ]);
  const units = serviceUnits.ok ? serviceUnits.data : [];
  const members = new Map<string, Schemas["UserRef"]>();
  for (const unit of units) for (const m of unit.members) members.set(m.id, m);
  return {
    categories: categories.ok ? categories.data : [],
    serviceUnits: units,
    members: [...members.values()].sort((a, b) => a.displayName.localeCompare(b.displayName, "tr")),
  };
}
