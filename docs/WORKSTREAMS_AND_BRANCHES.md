# Sokağımdaki Sorun — İş Akışları, Branch'ler ve PR Süreci

| | |
|---|---|
| Doküman durumu | Kabul edildi (mimari hazırlık oturumu) |
| Son güncelleme | 2026-11-11 |

Her iş akışı (workstream) **ayrı bir Claude Code sohbetinde, ayrı bir branch'te, ayrı bir Git worktree'de** yürütülür. Bu doküman her sohbetin neye sahip olduğunu ve neye dokunmayacağını tanımlar.

## 1. Git kuralları

- `main` tek entegrasyon dalıdır. İlk doküman commit'i dışında `main` üzerinde doğrudan çalışılmaz.
- Her iş akışı **güncel `main`'den** dallanır. Bir feature branch başka bir feature branch'in üzerine kurulmaz.
- Bağımlı olunan iş `main`'e girmeden tüketen iş başlamaz (bkz. [ROADMAP §2](IMPLEMENTATION_ROADMAP.md#2-bağımlılık-grafiği)).
- PR öncesi branch güncel `main` ile eşitlenir; build ve testler yeniden çalıştırılır.
- Branch'i `main`'e **proje sahibi** birleştirir. Sohbet kendiliğinden merge etmez.
- Secret, `.env`, yerel medya ve build çıktıları commit edilmez.
- Commit mesajları: kısa emir kipi başlık + gerekirse gövde. Önek: `feat:`, `fix:`, `chore:`, `docs:`, `test:`, `refactor:`.

### Remote yokken

Şu anda GitHub deposu ve remote **yoktur**. Bu durumda:

- Branch ve commit'ler yerelde kalır.
- "PR açıldı" denmez. Bunun yerine sohbet, PR açıklamasını §5'teki şablonla hazırlar ve branch'in incelemeye hazır olduğunu bildirir.
- Proje sahibi yerelde inceleyip `main`'e birleştirir (`git merge --no-ff <branch>`), ya da remote eklendikten sonra PR açılır.

### Worktree kullanımı

Aynı anda birden fazla sohbet aynı çalışma dizinini paylaşmaz.

```bash
git worktree add .worktrees/backend-identity -b feature/backend-identity main
```

- Worktree'ler depo içinde `.worktrees/` altında tutulur (git tarafından yok sayılır); böylece tüm proje dosyaları `sokagimdakisorun/` içinde kalır.
- Claude Code masaüstü uygulamasının kendi worktree özelliği de kullanılabilir.
- İş birleştirildikten sonra: `git worktree remove .worktrees/<ad>`.

## 2. Dosya sahipliği

| Yol | Sahibi | Diğerleri |
|---|---|---|
| `docs/` | Her sohbet kendi konusuyla ilgili bölümü günceller | ADR değişikliği PR'da açıkça belirtilir |
| Solution dosyası, `Directory.Build.props`, `Directory.Packages.props`, `global.json`, `.editorconfig` | WS-01 | Yalnızca paket sürümü eklemek için dokunulur; PR'da belirtilir |
| `docker-compose.yml`, `.env.example` | WS-01 | Yeni anahtar eklemek serbest; mevcut satır değiştirilmez |
| `src/SokagimdakiSorun.Api/` | WS-02 | Modül sohbetleri dokunmaz |
| `src/SokagimdakiSorun.Platform/`, `src/SokagimdakiSorun.SharedKernel/` | WS-03 (Platform), WS-02 (SharedKernel başlangıcı) | Ekleme yapmak PR'da gerekçelendirilir |
| `src/Modules/Identity/**` | WS-04 | Dokunmaz |
| `src/Modules/Media/**` | WS-05 | Dokunmaz |
| `src/Modules/Complaints/**` | WS-06 | Dokunmaz |
| `src/Modules/Messaging/**` | WS-07, sonra WS-10 | Dokunmaz |
| `src/Modules/Reporting/**` | WS-08 | Dokunmaz |
| `tests/SokagimdakiSorun.<Modül>.Tests/` | İlgili modül sohbeti | Dokunmaz |
| `tests/SokagimdakiSorun.IntegrationTests/` — ortak altyapı | WS-03 | Modüller yalnızca kendi alt klasörüne test ekler |
| `web/**` | WS-09 | Backend sohbetleri dokunmaz |
| `docs/api/openapi.v1.json` | Uç nokta ekleyen her backend sohbeti (üretilerek) | Elle düzenlenmez |

**Başka modülde eksik veya hata görülürse** sohbet onu kendisi düzeltmez; "eksik sözleşme / bulunan sorun" olarak raporlar ([API_CONTRACT_STRATEGY §9](API_CONTRACT_STRATEGY.md#9-sözleşme-değişiklik-süreci)).

## 3. İş akışı kartları

Her kartta "Öneri" satırı, `project-orchestrator` skill'inin kaynak verimliliği ilkesine göre nitel bir model/efor önerisidir; mevcut hesap ve yapılandırmaya göre proje sahibi belirler.

---

### WS-01 — Proje iskeleti · `chore/project-bootstrap`

| | |
|---|---|
| Bağımlılık | — |
| Sahip olduğu | Solution, tüm boş projeler ve referansları, kök yapılandırma dosyaları, `docker-compose.yml`, `.env.example` |
| Dokunmaz | İş mantığı, entity, uç nokta yazmaz |
| Öneri | Orta model, orta efor |

**Kapsam**

- [ARCHITECTURE §2](ARCHITECTURE.md#2-çözüm-düzeni-hedef)'deki tüm projeleri oluştur; referans kurallarını uygula.
- Her modülde boş `Add<Modül>Module` / `Map<Modül>Endpoints` metotları.
- `global.json` (.NET 10 SDK), merkezi paket yönetimi, `.editorconfig`, nullable ve uyarı ayarları.
- `.gitattributes` ile satır sonu normalizasyonu (Windows'ta LF/CRLF uyarılarını gidermek için).
- Yalnızca PostgreSQL 18 içeren `docker-compose.yml` (çakışmayan host portu; V-4 doğrulaması).
- `.env.example` (sahte değerler), README'de yerel çalıştırma adımları.
- Test projesi iskeletleri (boş, derlenen).
- OD-5 onaylandıysa skill dosyalarını depoya taşı.

**Kabul ölçütleri**

- `dotnet build` uyarısız geçer; `dotnet test` çalışır (test olmasa da başarılı).
- Bir modül projesinden başka bir modülün ana projesine referans **yoktur**.
- `docker compose config` geçerli; konteyner yalnızca proje sahibinin onayıyla başlatılır.
- Depoda secret yok.

**Kapsam dışı:** Next.js iskeleti (WS-09), `Program.cs` içeriği (WS-02), herhangi bir `DbContext` (WS-03+).

---

### WS-02 — API host · `feature/backend-host`

| | |
|---|---|
| Bağımlılık | WS-01 |
| Sahip olduğu | `src/SokagimdakiSorun.Api/`, `SharedKernel`'in başlangıç tipleri |
| Dokunmaz | `src/Modules/**` içeriği |
| Öneri | Orta model, orta efor |

**Kapsam**

- Composition root: modül kayıt metotlarının çağrılması.
- Merkezi hata yönetimi ve Problem Details ([API_CONTRACT_STRATEGY §4](API_CONTRACT_STRATEGY.md#4-hata-formatı)).
- `SharedKernel`: `Result`/`Error`, `IIntegrationEvent`, `ICurrentUser`.
- OpenAPI üretimi + `docs/api/openapi.v1.json` anlık görüntüsü + eşitlik testi (V-1).
- `/health/live`, `/health/ready`.
- Yapılandırılmış loglama, korelasyon kimliği, hız sınırlayıcı altyapısı.
- Varsayılan-reddet yetkilendirme iskeleti (kimlik şeması WS-04'te bağlanır).

**Kabul ölçütleri**

- API ayağa kalkar; health uç noktaları yanıt verir.
- Bilinmeyen yol ve yakalanmamış hata, Problem Details biçiminde döner; yığın izi sızmaz (bileşen testi).
- OpenAPI anlık görüntüsü üretilir ve test, uyumsuzlukta kırılır.

**Testler:** `WebApplicationFactory` ile bileşen testleri.

---

### WS-03 — Veritabanı temeli · `feature/database-foundation`

| | |
|---|---|
| Bağımlılık | WS-02 |
| Sahip olduğu | `src/SokagimdakiSorun.Platform/`, entegrasyon testi ortak altyapısı |
| Dokunmaz | Modül entity'leri **yazmaz**. Tüm modüllerin tablolarını tek yerde toplamaz. |
| Öneri | Güçlü model veya yüksek efor (kalıcı altyapı kararı) |

**Kapsam**

- Npgsql bağlantı kurulumu, `snake_case` konvansiyonu (V-2), modül `DbContext`'leri için ortak taban/konvansiyon (schema, migration geçmiş tablosu).
- `PlatformDbContext` + `platform.outbox_messages` migration'ı.
- Modüllerin outbox'a kendi transaction'larında yazmasını sağlayan yardımcı (V-3).
- `OutboxDispatcher` arka plan işleyicisi: `SKIP LOCKED`, backoff, dead-letter, idempotent handler sözleşmesi.
- Migration çalıştırma yaklaşımı (geliştirmede açılışta; üretim yöntemi WS-11'de).
- `/health/ready` için veritabanı kontrolü.
- Testcontainers tabanlı PostgreSQL test fixture'ı.
- "Yeni modül `DbContext`'i nasıl eklenir" kısa rehberi (`docs/` altında).

**Kabul ölçütleri**

- Boş veritabanına migration uygulanır (gerçek PostgreSQL testi).
- Outbox satırı, iş verisiyle aynı transaction'da yazılır; rollback'te ikisi de yoktur (test).
- Dağıtıcı başarısız handler'ı yeniden dener ve azami denemede dead-letter yapar (test).
- İki eşzamanlı dağıtıcı aynı satırı işlemez (test).

---

### WS-04 — Identity modülü · `feature/backend-identity`

| | |
|---|---|
| Bağımlılık | WS-03 |
| Sahip olduğu | `src/Modules/Identity/**`, `identity` schema, Identity testleri |
| Sözleşme üretir | `IUserDirectory`, `IReporterRegistry` |
| Öneri | Güçlü model veya yüksek efor (güvenlik hassas) |

**Kapsam**

- ASP.NET Core Identity ile kullanıcı/rol modeli; `user_kind`, `status`; şikâyetçi kaydı parolasız (V-5).
- `external_identities` ve idempotent "bul veya oluştur".
- Auth uç noktaları ve token modeli ([ADR-0006](decisions/ADR-0006-admin-panel-authentication.md)); host'taki kimlik şemasının bağlanması için gereken kayıt.
- Admin için kullanıcı yönetimi uç noktaları; ilk Admin'in ortam değişkeninden tohumlanması.
- Giriş için hız sınırı ve hesap kilitleme.

**Kabul ölçütleri**

- Geçerli/geçersiz giriş, yenileme, çıkış davranışları testli.
- Aynı dış kimlikle iki eşzamanlı "oluştur" çağrısı tek kullanıcı üretir (PostgreSQL testi).
- Şikâyetçi kaydı panelde oturum açamaz.
- Moderatör, Admin uç noktalarına erişemez.
- Yanıtlarda parola hash'i veya token sızıntısı yok.

---

### WS-05 — Media modülü · `feature/backend-media`

| | |
|---|---|
| Bağımlılık | WS-03 (WS-04 ile paralel yürütülebilir) |
| Sahip olduğu | `src/Modules/Media/**`, `media` schema, Media testleri |
| Sözleşme üretir | `IMediaService` |
| Öneri | Orta model, orta efor |

**Kapsam**

- `IMediaStorage` soyutlaması + yerel dosya sistemi uygulaması ([ADR-0004](decisions/ADR-0004-local-first-media-storage.md)).
- Doğrulama: MIME allowlist, boyut sınırı, imza kontrolü, SHA-256, rastgele storage key.
- `media_files` metadata'sı; `Temporary → Committed` yaşam döngüsü; `OrphanMediaCleaner`.
- EXIF/GPS yaklaşımının belirlenmesi ve belgelenmesi.
- `/health/ready` için medya kökü yazılabilirlik kontrolü.

**Kabul ölçütleri**

- Büyük dosya, yanlış MIME, imza uyuşmazlığı reddedilir.
- Yol geçişi (path traversal) denemesi ve aynı adla üzerine yazma mümkün değildir.
- Sözleşme hiçbir yerde fiziksel yol veya storage key döndürmez.
- Testler geçici dizin kullanır; kalıcı bir yere yazmaz.

**Not:** HTTP uç noktası sunmaz. Dosya erişimi Complaints uç noktası üzerinden yetkilendirilir.

---

### WS-06 — Complaints modülü · `feature/backend-complaints`

| | |
|---|---|
| Bağımlılık | WS-04, WS-05 |
| Sahip olduğu | `src/Modules/Complaints/**`, `complaints` schema, Complaints testleri |
| Sözleşme tüketir | `Identity.Contracts`, `Media.Contracts` |
| Sözleşme üretir | `IComplaintIntake`, `IComplaintReporterActions`, `IComplaintCatalog`, `IComplaintAccessPolicy`, `IComplaintStatistics`; event'ler `ComplaintSubmitted`, `ComplaintStatusChanged` |
| Öneri | Güçlü model, yüksek efor (projenin çekirdeği) |
| Başlamadan önce | Tüm ön kararlar verildi: OD-3 evet (geçiş #23), OD-4 evet, OD-6 mock veri — Gelibolu / Yazıcızade ([PROJECT_BRIEF §10](PROJECT_BRIEF.md#10-pilot-bölge-od-6)) |

**Kapsam**

- [DOMAIN_MODEL §3.2](DOMAIN_MODEL.md#32-complaints-schema--complaints-modülü) tabloları ve kısıtları.
- Şikâyet aggregate'i ve **tek noktada** geçiş politikası ([§4.3](DOMAIN_MODEL.md#43-izin-verilen-geçişler-bağlayıcı-tablo)).
- Durum geçmişi, atama geçmişi, yorum, ek ilişkisi, tekrar ilişkisi.
- `PublicReference` üretimi.
- Kapsam duyarlı yetkilendirme ve liste filtresi.
- [API_CONTRACT_STRATEGY §8](API_CONTRACT_STRATEGY.md#8-planlanan-http-yüzeyi) Complaints uç noktaları; `allowedActions`; eşzamanlılık (V-6).
- Kategori, hizmet birimi, üyelik, yönlendirme kuralı yönetimi; temsili tohum verisi.
- Idempotent `SubmitFromIntake`.

**Kabul ölçütleri**

- Geçiş tablosundaki 23 satırın tamamı ve her kaynak durumdan en az bir yasak geçiş birim testiyle doğrulanır.
- Zorunlu gerekçe eksikse geçiş reddedilir.
- Durum + geçmiş + outbox tek transaction (PostgreSQL testi; rollback senaryosu dâhil).
- Aynı `intakeSessionId` ile iki çağrı tek şikâyet üretir (eşzamanlı senaryo dâhil).
- Kapsam dışı moderatör listede görmez, detayda `404` alır.
- Eşzamanlı güncellemede ikinci istek `409` alır.
- Tekrar hedefi kendisi veya başka bir `Duplicate` olamaz.
- `AwaitingInformation`'dan dönüş yalnızca giriş durumuna yapılır (#11 / #23); istemci hedef durumu seçemez.

---

### WS-07 — Messaging modülü (mock sağlayıcı) · `feature/backend-messaging`

| | |
|---|---|
| Bağımlılık | WS-06 |
| Sahip olduğu | `src/Modules/Messaging/**`, `messaging` schema, Messaging testleri |
| Sözleşme tüketir | `Identity.Contracts`, `Complaints.Contracts`, `Media.Contracts` |
| Öneri | Güçlü model, yüksek efor (idempotency ve hata toleransı) |

**Kapsam**

- Webhook GET doğrulaması; POST'ta ham gövde üzerinde HMAC-SHA256, sabit zamanlı karşılaştırma.
- Inbox kabulü (olay başına satır, `dedup_key`), `InboxProcessor`.
- Conversation, Message, IntakeSession ve intake durum makinesi ([DOMAIN_MODEL §5](DOMAIN_MODEL.md#5-intake-durum-makinesi)).
- `IWhatsAppClient` soyutlaması + `MockWhatsAppClient`.
- Giden mesaj kuyruğu, `OutboundSender` (backoff, azami deneme, 24 saat penceresi kontrolü), `IntakeExpirySweeper`.
- `ComplaintSubmitted` / `ComplaintStatusChanged` handler'ları ve bildirim politikası.
- `AwaitingInformation` cevabının şikâyete yönlendirilmesi.
- Şikâyete bağlı mesajları listeleyen uç nokta.
- Yerelde konuşma simüle etmek için imzalı webhook gönderen küçük bir geliştirici aracı/betiği.

**Kabul ölçütleri**

- Yanlış/eksik imza reddedilir; payload işlenmez.
- Aynı webhook iki kez gelince ikinci mesaj, intake veya şikâyet oluşmaz.
- Tam intake akışı onay sonrası tek şikâyet üretir; onay olmadan üretmez.
- İptal, zaman aşımı, düzeltme ve desteklenmeyen mesaj tipi senaryoları testli.
- Geçici gönderim hatası yeniden denenir; kalıcı hata `Failed` olur, sonsuz döngü yok.
- Aynı event iki kez işlenince tek giden mesaj oluşur.
- Loglarda telefon numarası, mesaj metni, token yok.

**Güven sınırı:** Bu iş akışının testleri mock sağlayıcıya karşıdır. Gerçek Meta davranışını kanıtlamaz; PR raporu bunu açıkça yazar.

**Kapsam dışı:** Gerçek Graph API çağrısı, şablon mesajlar, Meta yapılandırması (WS-10).

---

### WS-08 — Reporting modülü · `feature/backend-reporting`

| | |
|---|---|
| Bağımlılık | WS-06 |
| Sahip olduğu | `src/Modules/Reporting/**` |
| Sözleşme tüketir | `Complaints.Contracts`, `Identity.Contracts` |
| Öneri | Hafif–orta model, düşük–orta efor |

**Kapsam:** Özet sayılar, kategori dağılımı, zaman serisi uç noktaları. Kendi tablosu yoktur; veriyi `IComplaintStatistics` üzerinden alır. Sözleşmede eksik sorgu varsa Complaints sohbetine raporlanır.

**Kabul ölçütleri:** Sayılar kapsam duyarlıdır (moderatör yalnızca görebildiği şikâyetleri sayar); boş veri durumunda hata vermez; başka modülün tablosuna doğrudan sorgu yoktur.

---

### WS-09 — Yönetim paneli · `feature/frontend-admin`

| | |
|---|---|
| Bağımlılık | WS-06 (asgari); dashboard için WS-08, konuşma görünümü için WS-07 |
| Sahip olduğu | `web/**` |
| Dokunmaz | `src/**`, `tests/**`. Eksik uç noktayı kendisi yazmaz, raporlar. |
| Öneri | Orta model, orta efor |

**Kapsam**

- Next.js 16 iskeleti (App Router, TypeScript, npm) — V-7 doğrulaması.
- BFF oturum katmanı ([ADR-0006](decisions/ADR-0006-admin-panel-authentication.md)); OpenAPI'den tip üretimi.
- Ekranlar: giriş, dashboard, şikâyet listesi (filtre, sayfalama), şikâyet detayı (durum geçmişi, yorumlar, konum, fotoğraflar, konuşma), durum komutları, atama.
- Düğmeler `allowedActions`'a göre; hata gösterimi `code` alanına göre.
- Tarihler `Europe/Istanbul`; arayüz Türkçe.

**Kabul ölçütleri**

- Tarayıcıdan .NET API'ye doğrudan istek yoktur; token tarayıcıya ulaşmaz.
- Oturumsuz kullanıcı korumalı sayfaya erişemez.
- Durum geçiş kuralı veya yetki kuralı kodda kopyalanmamıştır.
- `409` çakışmasında güncel veri yeniden yüklenir.
- Lint ve tip kontrolü temiz; kritik akış için asgari test.

---

### WS-10 — Gerçek WhatsApp Cloud API · `feature/whatsapp-cloud-integration`

| | |
|---|---|
| Bağımlılık | WS-07 + proje sahibinin sağladığı Meta uygulaması, WABA, test numarası |
| Sahip olduğu | `src/Modules/Messaging/**` içinde gerçek sağlayıcı adaptörü |
| Öneri | Güçlü model, yüksek efor |

**Kapsam:** `IWhatsAppClient`'ın Cloud API uygulaması; medya indirme; şablon mesajlar ve pencere dışı gönderim; teslim durumu webhook'ları; V-8 doğrulamalarının tamamı.

**Kurallar:** Secret'lar yalnızca ortam değişkeninden; otomatik testler gerçek mesaj göndermez; gerçek gönderim yalnızca bilinçli, elle tetiklenen doğrulamadır.

---

### WS-11 — Railway deployment · `chore/railway-deployment`

| | |
|---|---|
| Bağımlılık | WS-07, WS-09 + GitHub deposu + Railway projesi |
| Sahip olduğu | Dockerfile'lar, Railway yapılandırması, deployment dokümanı |
| Öneri | Orta model, orta–yüksek efor |

**Kapsam:** API ve panel için konteyner imajları; Volume bağlama ve `Media__RootPath`; üretim migration yöntemi; health check; yedekleme dokümanı; V-9 doğrulamaları; CI.

## 4. Sohbet başlangıç şablonu

Her implementasyon sohbeti aşağıdaki bilgilerle başlatılır. Kartlardan kopyalanıp doldurulur.

```markdown
# <İş akışı adı> — <branch>

## Bağlam
Proje: Sokağımdaki Sorun (depo: sokagimdakisorun). Modüler monolith; ayrıntı docs/ARCHITECTURE.md.
Önce oku: docs/ARCHITECTURE.md, docs/DOMAIN_MODEL.md, docs/API_CONTRACT_STRATEGY.md,
docs/WORKSTREAMS_AND_BRANCHES.md (kendi kartın), ilgili ADR'ler: <liste>.
Skill: sokagimdakisorun-skill (+ ilgili references/ dosyası: <liste>), lean-test-engineering.

## Görev
- Branch: <branch> (güncel main'den, ayrı worktree'de)
- Sorumlu modül: <modül>
- Kullanabileceğin dosyalar: <yollar>
- Dokunma: <yollar>

## Sözleşmeler
- Tükettiğin: <Contracts arayüzleri / uç noktalar>
- Ürettiğin: <Contracts arayüzleri / event'ler / uç noktalar>

## Kabul ölçütleri
<karttan>

## Test kapsamı
<karttan>

## Çalışma yöntemi
1. git durumu ve branch'i doğrula.  2. Doküman ve skill'i oku.  3. İlgili kodu incele.
4. Kısa plan sun.  5. Asgari değişiklikle uygula.  6. Test ve build çalıştır.
7. PR açıklamasını docs/WORKSTREAMS_AND_BRANCHES.md §5 şablonuyla hazırla.
Mimariyle çelişki görürsen kod yazmadan önce bildir. Merge etme.
```

## 5. PR açıklama şablonu

```markdown
## Özet
<Ne yapıldı, neden — 2-4 cümle>

## Etkilenen modüller
<Liste. Kendi modülün dışında dokunulan dosya varsa gerekçesiyle>

## Mimari kararlar
<Bu PR'da verilen kararlar; yeni/güncellenen ADR'ler; dokümanlardan sapma varsa gerekçesi>

## Sözleşme değişiklikleri
- HTTP: <yeni/değişen uç noktalar; OpenAPI anlık görüntüsü güncellendi mi>
- Süreç içi: <yeni/değişen Contracts arayüzleri ve event'ler>
- Kırıcı değişiklik: <yok / var → etkilenen tüketiciler>

## Veritabanı
<Migration adları, hangi schema; geri alma notu; veri dönüşümü var mı>

## Ortam değişkenleri
<Yeni/değişen anahtarlar; .env.example güncellendi mi>

## Test sonuçları
- Çalıştırılan komutlar ve sonuçları: <komut → geçti/kaldı/atlandı sayıları>
- Çalıştırılmayanlar ve nedeni: <...>
- Güven sınırı: <testler neyi kanıtlıyor, neyi kanıtlamıyor (mock ≠ gerçek servis)>

## Güvenlik etkisi
<Yetki, kişisel veri, secret, girdi doğrulama açısından>

## Geriye dönük uyumluluk
<...>

## Bilinen riskler ve açık konular
<...>

## Yol haritası
<IMPLEMENTATION_ROADMAP durum tablosu güncellendi mi>
```
