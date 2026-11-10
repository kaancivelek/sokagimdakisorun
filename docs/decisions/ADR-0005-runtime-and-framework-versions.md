# ADR-0005 — Çalışma zamanı ve çerçeve sürümleri

| | |
|---|---|
| Durum | Kabul edildi |
| Tarih | 2026-11-11 |
| Doğrulama tarihi | 2026-11-11 (aşağıdaki resmî kaynaklardan) |

## Bağlam

Sürümler varsayımla değil, kurulu araçlar ve güncel resmî destek takvimleri doğrulanarak seçilmelidir. Ölçüt: **uzun süreli destek (LTS) + geliştirme makinesinde hâlihazırda kurulu olması**.

### Doğrulanan durum

| Teknoloji | Resmî durum (2026-11-11) | Makinede kurulu |
|---|---|---|
| .NET | **10 — LTS** (Kasım 2025; destek sonu Kasım 2028; güncel yama 10.0.15). 11 — STS, bir gün önce (10 Kasım 2026) yayımlandı. 8 ve 9 destek dışı. | SDK 10.0.401, runtime 10.0.12 |
| Node.js | **26 — LTS** ("Lithium"; güncel LTS yaması 26.13.0). 24 ve 22 de LTS. 27 alfa. | 26.4.0 |
| Next.js | **16** — güncel kararlı ana sürüm (16.4.6). 15 için güvenlik desteği Ocak 2027'de bitiyor. | Kurulu değil (proje bağımlılığı olacak) |
| PostgreSQL | **18** — güncel ana sürüm (18.6; destek sonu Kasım 2030). 19 yalnızca RC. | Kurulu değil (konteyner olarak çalışacak) |
| EF Core sağlayıcısı | Npgsql EF Core sağlayıcısının 10.0 hattı EF Core 10'u hedefler ve PostgreSQL 18 özelliklerini destekler | — |
| Paket yöneticisi | npm 11.17.0 kurulu; pnpm kurulu değil | — |

## Karar

| Katman | Seçim |
|---|---|
| Backend çalışma zamanı | **.NET 10 (LTS)**, C# varsayılan dil sürümü |
| Web çerçevesi | ASP.NET Core 10, Minimal API |
| ORM | EF Core 10 + Npgsql sağlayıcısı (10.x) |
| Veritabanı | **PostgreSQL 18** |
| Frontend çalışma zamanı | **Node.js 26 (LTS)** |
| Frontend çerçevesi | **Next.js 16**, App Router, TypeScript |
| Paket yöneticisi (frontend) | **npm** |

Uygulama kuralları:

- `global.json` .NET SDK'yı 10.0 hattına sabitler (aynı hat içinde ileri yuvarlamaya izin verir).
- NuGet paket sürümleri merkezi olarak yönetilir (`Directory.Packages.props`); sürümler tek yerde durur.
- `web/package.json` içindeki `engines` alanı Node 26'yı belirtir; kilit dosyası (`package-lock.json`) commit edilir.
- Yerel PostgreSQL konteyneri ana sürüme sabitlenir (`18`), `latest` etiketi kullanılmaz.
- Yama (patch) güncellemeleri serbesttir. **Ana sürüm değişikliği bu ADR'nin güncellenmesini gerektirir.**
- Tam paket sürümleri, ilgili iş akışı tarafından kurulum anında güncel kararlı sürüm doğrulanarak belirlenir.

## Gerekçe

- **.NET 10:** kurulu ve LTS. .NET 11 yalnızca bir günlük ve kısa destekli (STS); ekosistem paketlerinin uyumu henüz oturmamıştır. Bir dönemlik proje için LTS'in kararlılığı, yeni sürümün getirilerinden değerlidir.
- **Node 26:** kurulu olan hat zaten LTS'tir; ek kurulum gerekmez.
- **Next.js 16:** güncel ve aktif desteklenen ana sürüm. 15 ile başlamak, birkaç ay içinde destek dışı kalacak bir sürüme yatırım olurdu.
- **PostgreSQL 18:** güncel kararlı sürüm, en uzun destek penceresi. 19 henüz yayımlanmadı.
- **npm:** kurulu. pnpm'in bu ölçekte (tek frontend paketi) sağlayacağı fayda, ek araç kurulumunu gerektirmez.

## Değerlendirilen alternatifler

| Alternatif | Neden seçilmedi |
|---|---|
| .NET 11 (STS) | Bir günlük sürüm; destek süresi LTS'ten uzun değil; paket uyumluluğu belirsiz. |
| .NET 8 | Destek dışı. |
| Node.js 24 LTS | Geçerli bir seçenek, ancak makinede 26 kurulu ve o da LTS. |
| Next.js 15 | Destek sonu yakın. |
| PostgreSQL 17 | Geçerli, ancak 18'e göre avantajı yok; yeni proje için daha kısa destek penceresi. |
| pnpm | Kurulu değil; tek paketli frontend için gereksiz. |

## Sonuçlar

**Dikkat edilecek / doğrulanacak**

- Kurulu .NET runtime (10.0.12) ve Node (26.4.0) güncel yamaların gerisindedir. Sürüm hattı doğrudur; güvenlik yamaları için proje sahibinin uygun bir zamanda güncellemesi önerilir. **Bu oturumda hiçbir araç kurulmamış veya güncellenmemiştir.**
- Next.js 16'nın asgari Node.js gereksinimi bu oturumda doğrulanmadı; panel iş akışı başlarken resmî dokümandan kontrol edilir (V-7).
- Railway'in sunduğu PostgreSQL ana sürümü deployment anında kontrol edilir; yerel ve üretim ana sürümleri aynı tutulur (V-9).
- Resmî PostgreSQL 18 konteyner imajının veri dizini yapılandırması bootstrap iş akışında imaj dokümanından doğrulanır (V-4).
- Bazı üçüncü taraf paketlerin (ör. EF Core adlandırma konvansiyonu eklentisi) EF Core 10 uyumu ilgili iş akışında doğrulanır (V-2).

## Kaynaklar

- .NET destek politikası: https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core
- Node.js sürümleri: https://nodejs.org/en/about/previous-releases
- Next.js sürüm takvimi (ikincil kaynak): https://endoflife.date/nextjs
- PostgreSQL sürüm politikası: https://www.postgresql.org/support/versioning/
- Npgsql EF Core sağlayıcısı 10.0 sürüm notları: https://www.npgsql.org/efcore/release-notes/10.0.html
