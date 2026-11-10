# Sokağımdaki Sorun — Proje Özeti

| | |
|---|---|
| Doküman durumu | Kabul edildi (mimari hazırlık oturumu) |
| Son güncelleme | 2026-11-11 |
| İlgili dokümanlar | [ARCHITECTURE](ARCHITECTURE.md) · [DOMAIN_MODEL](DOMAIN_MODEL.md) · [ROADMAP](IMPLEMENTATION_ROADMAP.md) |

## 1. Problem ve amaç

Bir vatandaş bozuk kaldırım, çukur, yanmayan sokak lambası veya biriken çöp gördüğünde bunu kime, hangi kanaldan bildireceğini çoğu zaman bilmez; bildirse de akıbetini takip edemez.

**Sokağımdaki Sorun**, vatandaşın zaten kullandığı WhatsApp üzerinden birkaç mesajla sorun bildirmesini, bildirimin bir yönetim platformunda toplanmasını ve moderatörler tarafından izlenebilir bir yaşam döngüsüyle sonuçlandırılmasını sağlar.

İlişkili sürdürülebilir kalkınma hedefleri: SKA 11 (Sürdürülebilir Şehirler ve Topluluklar), SKA 16 (hesap verebilir kurumlar).

## 2. Aktörler

| Aktör | Kanal | Ne yapar |
|---|---|---|
| Şikâyetçi (Reporter) | WhatsApp | Sorun bildirir, ek bilgi verir, sonucu öğrenir. Panel hesabı yoktur. |
| Moderatör | Next.js paneli | Şikâyeti inceler, kabul/ret eder, birime atar, ilerletir, çözer. |
| Yönetici (Admin) | Next.js paneli | Kullanıcı, kategori, hizmet birimi ve yönlendirme kurallarını yönetir; kapatılmış kaydı yeniden açar. |
| Sistem | Arka plan işleyicileri | Webhook işleme, bildirim gönderimi, intake zaman aşımı. |

Şikâyetçi de bir `User`'dır; ayrı bir `Customer` kavramı yoktur.

## 3. Kapsam

### MVP içinde

- WhatsApp üzerinden adım adım şikâyet toplama (kategori → açıklama → konum → isteğe bağlı fotoğraf → onay).
- Şikâyet yaşam döngüsü, durum geçmişi, atama, yorum, ek bilgi isteme.
- Moderatör/yönetici paneli: giriş, dashboard, liste/filtre, detay, durum değiştirme, atama, konum ve fotoğraf görüntüleme.
- Şikâyetçiye durum bildirimi (WhatsApp).
- Mock WhatsApp sağlayıcısı ile uçtan uca yerel çalışma.
- Yerel dosya sisteminde medya saklama.

### MVP dışında (bilinçli olarak)

- Vatandaşa açık web sitesi, herkese açık şikâyet haritası.
- Gerçek belediye sistemi entegrasyonu (Open311 vb.).
- İdari sınır servisi (MapIt), otomatik adres çözümleme (geocoding).
- Çoklu dil, mobil uygulama, ses/video/belge mesajları.
- Mikroservis, mesaj kuyruğu (RabbitMQ/Kafka), object storage.
- `Staff` rolü, ince taneli izin (permission) tabloları, moderatör performans metrikleri.

### Sonraki fazlar

- Gerçek WhatsApp Cloud API bağlantısı (Meta uygulaması, WABA, onaylı şablonlar).
- Railway deployment ve Railway Volume.

## 4. FixMyStreet ile ilişki

FixMyStreet yalnızca **ürün davranışı referansıdır**. Workspace'teki `fixmystreet-master/` klasörü bu deponun dışındadır, değiştirilmez ve depoya eklenmez.

| FixMyStreet kavramı | Bu projedeki karşılığı | Fark |
|---|---|---|
| `problem` | `Complaint` | Web formu yerine WhatsApp intake ile oluşur |
| `problem.state` | `Complaint.Status` + `ComplaintStatusHistory` | Geçişler tek bir domain politikasında doğrulanır |
| `problem.send_state` | `Message.DeliveryStatus` | Gönderim durumu şikâyet durumundan ayrıdır (aynı ayrım korunur) |
| `body` | `ServiceUnit` | Temsili hizmet birimleri; gerçek kurum iddiası yok |
| `contacts` (kategori→kurum) | `ComplaintCategory` + `RoutingRule` | MVP'de yalnızca kategori bazlı öneri |
| `comment` | `ComplaintComment` | İç not / şikâyetçiye görünür ayrımı |
| MapIt ile bölge tespiti | — | MVP dışı; pilot alan tek bölge kabul edilir |
| Herkese açık bildirim sayfası | — | MVP'de tüm kayıtlar özeldir |

**Lisans notu (hukuki danışmanlık değildir):** FixMyStreet AGPL-3.0-or-later lisanslıdır. Bu projeye FixMyStreet'ten **kod, şablon veya çeviri metni (`.po`) kopyalanmaz**; yalnızca kavramlar ve iş akışı fikirleri kullanılır. Kopyalama ihtiyacı doğarsa önce lisans etkisi değerlendirilir. Bu depo da AGPL v3 ile lisanslandığından (OD-2) lisanslar uyumludur; yine de kopyalanan her parça kaynağı ve telif bildirimiyle birlikte işaretlenmelidir.

> Workspace kökündeki `CLAUDE.MD` daha önceki "FixMyStreet'i çatalla ve uyarla" yaklaşımını anlatır ve backend'in yeniden yazılmamasını önerir. O öneri **bu proje için geçerli değildir**; proje sahibi yeni teknoloji yığınıyla yeniden uygulama kararını vermiştir. Çelişki durumunda bu `docs/` klasörü esas alınır.

## 5. Adlandırma

| Kavram | Değer | Not |
|---|---|---|
| Görünen ad | Sokağımdaki Sorun | UI ve belgelerde |
| Depo / klasör | `sokagimdakisorun` | Değiştirilmez |
| Kod kökü | `SokagimdakiSorun` | Solution ve namespace öneki |
| Şikâyet referans öneki | `SS-` | Örn. `SS-7K3M-9QXA` |
| Eski çalışma adı | KentGözü | Skill dosyalarında geçer; `KentGozu.*` → `SokagimdakiSorun.*` |

## 6. Teknoloji yığını

| Katman | Seçim | Kaynak karar |
|---|---|---|
| Frontend | Next.js 16 (App Router), TypeScript, npm | [ADR-0005](decisions/ADR-0005-runtime-and-framework-versions.md), [ADR-0006](decisions/ADR-0006-admin-panel-authentication.md) |
| Backend | ASP.NET Core (.NET 10 LTS), Minimal API, modüler monolith | [ADR-0001](decisions/ADR-0001-modular-monolith.md) |
| Veri | PostgreSQL 18, EF Core 10 + Npgsql, modül başına schema | [ADR-0002](decisions/ADR-0002-postgresql-data-ownership.md) |
| Mesajlaşma | WhatsApp Cloud API, sağlayıcı bağımsız adaptör, inbox/outbox | [ADR-0003](decisions/ADR-0003-whatsapp-intake-and-complaint-lifecycle.md) |
| Medya | `IMediaStorage`; yerel dosya sistemi → Railway Volume | [ADR-0004](decisions/ADR-0004-local-first-media-storage.md) |
| Deployment | Railway (sonraki faz) | — |

## 7. Başlangıç envanteri (2026-11-11, doğrulanmış)

### Workspace

| Yol | İçerik | Durum |
|---|---|---|
| `sorunbildir/fixmystreet-master/` | FixMyStreet kaynak kodu | Salt okunur referans; depo dışı |
| `sorunbildir/.claude/skills/sokagimdakisorun-skill/` | Proje skill'i + 6 referans dosyası | Okundu; bu dokümanların temeli |
| `sorunbildir/.claude/skills/project-orchestrator/` | Planlama/orkestrasyon skill'i | Okundu |
| `sorunbildir/.claude/skills/lean-test-engineering/` | Test politikası skill'i | Okundu; test stratejisinin temeli |
| `sorunbildir/CLAUDE.MD` | Eski FixMyStreet uyarlama raporu | Bu projeyle çelişen öneri içerir (bkz. §4) |
| `sorunbildir/sokagimdakisorun/` | Bu depo | Oturum başında boştu |

### Kurulu araçlar

| Araç | Sürüm | Not |
|---|---|---|
| Git | 2.55.0 | Global kimlik tanımlı |
| GitHub CLI | 2.98.0 | Oturum açık; uzak depo yok |
| Docker Desktop | 29.7.2 | Daemon çalışıyor; projeyle ilgisiz konteynerler mevcut |
| Docker Compose | 5.4.0 | |
| .NET SDK | 10.0.401 (ayrıca 3.1.426) | Runtime 10.0.12 kurulu; güncel yama 10.0.15 |
| Node.js | 26.4.0 | 26.x LTS hattı; güncel LTS yaması 26.13.0 |
| npm | 11.17.0 | |
| pnpm | — | Kurulu değil → npm kullanılır |
| ripgrep (`rg`) | — | Kabukta yok; Claude Code'un Grep aracı veya `git grep` kullanılır |

### Henüz var olmayanlar

Proje konteynerleri ve veritabanı · GitHub deposu ve remote · Meta Developer App / WABA / telefon numarası · Railway projesi · gerçek secret'lar.

## 8. Başarı ölçütleri (MVP)

1. Mock sağlayıcı üzerinden simüle edilen bir WhatsApp konuşması, kullanıcı onayından sonra **tek** bir şikâyet üretir; aynı webhook tekrar gelse de ikinci kayıt oluşmaz.
2. Moderatör panelde şikâyeti görür, `Submitted`'tan `Closed`'a kadar ilerletir; her adım `ComplaintStatusHistory`'de aktör, zaman ve gerekçeyle görünür.
3. Yasak durum geçişi API tarafından reddedilir; panel bu kuralı kendisi uygulamaz.
4. Moderatör yalnızca yetki kapsamındaki şikâyetleri görür.
5. Fotoğraflar veritabanında değil dosya sisteminde saklanır; fiziksel yol hiçbir API yanıtında görünmez.
6. Tüm bunlar yerel makinede, gerçek secret olmadan çalışır ve testlerle kanıtlanır.

## 9. Açık kararlar

Proje sahibinin onayını bekleyen veya ilgili sohbette doğrulanacak konular. Ayrıntılı risk listesi: [IMPLEMENTATION_ROADMAP §6](IMPLEMENTATION_ROADMAP.md#6-riskler-ve-açık-kararlar).

| # | Konu | Varsayılan / öneri |
|---|---|---|
| OD-1 | Kod kökü adı | ✅ Karar verildi (2026-11-11): `SokagimdakiSorun` |
| OD-2 | Depo lisansı | ✅ Karar verildi (2026-11-11): GNU AGPL v3 — kök dizindeki `LICENSE` |
| OD-3 | `InProgress → AwaitingInformation` sonrası dönüş `InProgress`'e yapılabilsin mi? | Şimdilik hayır (yalnızca `UnderReview`); öneri: evet |
| OD-4 | Triage havuzu (atanmamış şikâyetler) tüm moderatörlere açık mı? | Evet |
| OD-5 | Skill dosyaları depoya taşınsın mı? | Evet, bootstrap sohbetinde (worktree oturumları skill'i görebilsin) |
| OD-6 | Pilot bölge ve gerçek kategori/birim verisi | Temsili veri ile başlanır |
