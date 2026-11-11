# Sokağımdaki Sorun — Yönetim Paneli

Next.js 16 (App Router, TypeScript), Node.js 26, npm. Mimari: [ADR-0006](../docs/decisions/ADR-0006-admin-panel-authentication.md) (BFF).

## Çalıştırma

```bash
cp .env.example .env.local   # API_BASE_URL ve SESSION_SECRET (sahte değerleri değiştirin)
npm ci
npm run dev                  # http://localhost:3000
```

API yerelde `dotnet run --project src/SokagimdakiSorun.Api` ile (`http://localhost:5180`) çalışmalıdır. Giriş için ilk Admin: kök `.env.example` içindeki `Seed__AdminEmail` / `Seed__AdminPassword`.

## Komutlar

| Komut | Ne yapar |
|---|---|
| `npm run gen:api` | `../docs/api/openapi.v1.json` → `src/api/schema.d.ts` (elle DTO yazılmaz; sözleşme değişince yeniden üretin ve commit'leyin) |
| `npm run lint` | ESLint (`eslint-config-next`) |
| `npm run typecheck` | `next typegen` + `tsc --noEmit` |
| `npm test` | `node:test` birim testleri (oturum, `allowedActions`, 409, BFF sınırı) |
| `npm run build` | Üretim derlemesi |

## Kurallar

- Tarayıcı .NET API'yi çağırmaz. API çağrıları yalnızca Server Component, Server Action, Route Handler ve `src/proxy.ts` içindedir.
- Token'lar yalnızca şifreli `HttpOnly; Secure; SameSite=Lax` çerezde (`ss_session`, AES-256-GCM). `NEXT_PUBLIC_` değişkeni kullanılmaz.
- Komut düğmeleri yalnızca API'nin `allowedActions` listesinden; geçiş/yetki/zorunlu alan kuralı panelde yoktur.
- Hata mesajları Problem Details `code` alanından (`src/lib/api/problem.ts`).
- `Secure` çerez nedeniyle yerelde `http://localhost` kullanın (tarayıcılar localhost'u güvenli bağlam sayar); başka bir HTTP adresinde oturum çerezi saklanmaz.
