# Sokağımdaki Sorun

Vatandaşların kentsel sorunları **WhatsApp** üzerinden bildirdiği, bildirimlerin bir yönetim panelinde toplanıp moderatörler tarafından takip edilerek sonuçlandırıldığı açık kaynak ruhlu bir sosyal sorumluluk projesi.

Ürün davranışı [FixMyStreet](https://github.com/mysociety/fixmystreet)'in vatandaş odaklı bildirim modelinden esinlenir; **FixMyStreet kodu bu depoda yer almaz ve kopyalanmaz.**

> **Durum (2026-11-11):** Mimari hazırlık tamamlandı; proje iskeleti (WS-01) hazır: solution, boş modül projeleri, test iskeletleri ve yerel PostgreSQL. Henüz iş mantığı, uç nokta ve veritabanı yok. Güncel durum için bkz. [docs/IMPLEMENTATION_ROADMAP.md](docs/IMPLEMENTATION_ROADMAP.md).

## Adlandırma

| Kavram | Değer |
|---|---|
| Görünen ad | Sokağımdaki Sorun |
| Depo / klasör adı | `sokagimdakisorun` |
| Kod kökü (solution, namespace) | `SokagimdakiSorun` |
| Eski çalışma adı | KentGözü — skill dosyalarındaki `KentGozu.*` adları `SokagimdakiSorun.*` olarak okunur |

## Teknoloji yığını

| Katman | Teknoloji |
|---|---|
| Frontend (moderatör/yönetici paneli) | Next.js 16, TypeScript |
| Backend | ASP.NET Core Web API (.NET 10 LTS), C# — modüler monolith |
| ORM | Entity Framework Core 10 + Npgsql |
| Veritabanı | PostgreSQL 18 (modül başına schema) |
| Vatandaş kanalı | WhatsApp Business Platform Cloud API (önce mock sağlayıcı) |
| Medya | Yerel dosya sistemi → ileride Railway Volume |
| Deployment | Railway (sonraki faz) |

Sürüm gerekçeleri: [ADR-0005](docs/decisions/ADR-0005-runtime-and-framework-versions.md).

## Yerel çalıştırma

Gereksinimler: Git, Docker Desktop (Compose), .NET SDK 10.0 (`global.json` 10.0 hattına sabitler). Node.js 26 ve npm yalnızca panel (`web/`, WS-09) eklendiğinde gerekir.

```bash
# 1) Yerel ortam dosyasını oluştur (.env git dışıdır; örnekteki değerler sahtedir)
cp .env.example .env

# 2) Yalnızca PostgreSQL 18 konteynerini başlat (host portu 54318, yalnızca 127.0.0.1)
docker compose up -d

# 3) Derle ve test et
dotnet build
dotnet test
```

- Compose dosyasını başlatmadan doğrulamak için: `docker compose --env-file .env.example config`.
- PostgreSQL 18 imajında veri dizini `/var/lib/postgresql/18/docker`'dır; volume `/var/lib/postgresql` yoluna bağlanır (imaj dokümanından doğrulandı). Veriyi silmek için: `docker compose down -v`.
- API ve panel konteynerde çalışmaz; host makinede çalıştırılır ([docs/ARCHITECTURE.md §10](docs/ARCHITECTURE.md#10-yerel-geliştirme-ortamı)). `dotnet` `.env` dosyasını okumaz; API'nin ihtiyaç duyduğu `ConnectionStrings__Default` gibi değerleri ortam değişkeni olarak verin (örnek: `.env.example`). API host'u WS-02'de doldurulur; şu an `src/SokagimdakiSorun.Api` yalnızca boş bir iskelettir.
- Testler xunit v3 + Microsoft Testing Platform kullanır; `global.json` içindeki `test.runner` ayarı `dotnet test`'in bu modda çalışması için gereklidir. İskelet aşamasında test yoktur ve "sıfır test" sonucu başarı sayılır (`tests/Directory.Build.props`).

### Çözüm düzeni

`SokagimdakiSorun.slnx` tüm projeleri içerir. Proje referans kuralları ve modül sınırları: [docs/ARCHITECTURE.md §2](docs/ARCHITECTURE.md#2-çözüm-düzeni-hedef). Bir modül başka modülün yalnızca `*.Contracts` projesine referans verebilir.

## Doküman haritası

Her yeni çalışma oturumu (Claude Code sohbeti) işe başlamadan önce **kendi kapsamıyla ilgili** dokümanları okur.

| Doküman | İçerik |
|---|---|
| [docs/PROJECT_BRIEF.md](docs/PROJECT_BRIEF.md) | Amaç, kapsam, aktörler, FixMyStreet ilişkisi, başlangıç envanteri |
| [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) | Modül sınırları, iletişim kuralları, akışlar, kimlik, yerel ortam, test stratejisi |
| [docs/DOMAIN_MODEL.md](docs/DOMAIN_MODEL.md) | Entity ilişkileri, tablo sahipliği, şikâyet yaşam döngüsü, intake durum makinesi |
| [docs/API_CONTRACT_STRATEGY.md](docs/API_CONTRACT_STRATEGY.md) | HTTP sözleşme kuralları, hata formatı, planlanan uç noktalar, modüller arası sözleşmeler |
| [docs/IMPLEMENTATION_ROADMAP.md](docs/IMPLEMENTATION_ROADMAP.md) | Fazlar, sıra, kilometre taşları, güncel durum |
| [docs/WORKSTREAMS_AND_BRANCHES.md](docs/WORKSTREAMS_AND_BRANCHES.md) | Sohbet başına sorumluluk, branch, dosya sahipliği, PR şablonu |
| [docs/decisions/](docs/decisions/) | Mimari karar kayıtları (ADR) |

## Çalışma kuralları (özet)

- `main` entegrasyon dalıdır; implementasyon doğrudan `main` üzerinde yapılmaz.
- Her çalışma konusu ayrı sohbet + ayrı branch + ayrı Git worktree ile yürütülür.
- Bir modül başka modülün tablolarına veya `DbContext`'ine erişmez; yalnızca `*.Contracts` projesini kullanır.
- Secret'lar depoya girmez. `.env.example` yalnızca sahte değer içerir.
- Çalıştırılmamış test "başarılı" olarak raporlanmaz.

Ayrıntı: [docs/WORKSTREAMS_AND_BRANCHES.md](docs/WORKSTREAMS_AND_BRANCHES.md).

## Lisans

[GNU Affero General Public License v3](LICENSE) (AGPL-3.0). Değiştirilmiş sürümü ağ üzerinden kullanıcılara sunan herkes, o sürümün kaynak kodunu da bu kullanıcılara sunmakla yükümlüdür (lisansın 13. maddesi).
