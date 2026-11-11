// Sunucu tarafı ortam değişkenleri. `NEXT_PUBLIC_` öneki kullanılmaz: API adresi ve secret tarayıcıya gitmez.
// Bu dosya proxy.ts'ten de içe aktarıldığı için `server-only` yerine yalnızca sunucuda çağrılan fonksiyonlar içerir.

export function apiBaseUrl(): string {
  const value = process.env.API_BASE_URL;
  if (!value) throw new Error("API_BASE_URL tanımlı değil (web/.env.example).");
  return value;
}

export function sessionSecret(): string {
  const value = process.env.SESSION_SECRET;
  if (!value || value.length < 32) throw new Error("SESSION_SECRET tanımlı değil veya 32 karakterden kısa.");
  return value;
}
