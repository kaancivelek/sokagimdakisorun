# Sokağımdaki Sorun — API Sözleşme Stratejisi

| | |
|---|---|
| Doküman durumu | Kabul edildi (mimari hazırlık oturumu) |
| Son güncelleme | 2026-11-12 |
| Bağlayıcılık | §1–§6 kuralları bağlayıcıdır. §7–§8'deki listeler **planlanan** yüzeydir; hiçbiri henüz uygulanmadı. |

Bu doküman iki tür sözleşmeyi yönetir:

- **HTTP sözleşmeleri** — Next.js paneli ile .NET API arasında (ve WhatsApp webhook'u).
- **Süreç içi sözleşmeler** — backend modülleri arasında (`*.Contracts` projeleri ve integration event'ler).

Amaç: ayrı sohbetlerde geliştirilen parçaların birbirini varsayımla değil, **yazılı ve makinece doğrulanabilir** sözleşmeyle tüketmesi.

## 1. Tek doğruluk kaynağı

| Sözleşme | Doğruluk kaynağı | Depodaki yeri |
|---|---|---|
| HTTP API | Koddan üretilen OpenAPI belgesi | `docs/api/openapi.v1.json` (commit edilen anlık görüntü) |
| Süreç içi sözleşmeler | `*.Contracts` projelerindeki C# arayüzleri ve kayıt tipleri | `src/Modules/<Modül>/SokagimdakiSorun.<Modül>.Contracts/` |
| Bu doküman | Kurallar ve planlanan yüzey | — |

Uç noktalar uygulandıkça doğruluk kaynağı OpenAPI anlık görüntüsüdür; bu dokümandaki planlanan tablolar ile çelişirse **anlık görüntü kazanır** ve tablo güncellenir.

### OpenAPI iş akışı

1. API, .NET 10'un yerleşik OpenAPI desteğiyle belgeyi koddan üretir. Ek Swagger üretici paketi eklenmez.
2. Belge **build sırasında** dosyaya yazılır ve `docs/api/openapi.v1.json` olarak commit edilir.
3. Uç nokta ekleyen/değiştiren her PR, anlık görüntünün güncel hâlini içerir. Fark, PR incelemesinde sözleşme değişikliği olarak görünür.
4. Üretilen belge ile commit edilen anlık görüntünün aynı olduğu bir testle doğrulanır (unutulan güncelleme build'i kırar).
5. Panel, TypeScript tiplerini bu dosyadan **üretir**. Elle DTO tipi yazılmaz.

Üretim mekanizmasının kesin kurulumu `feature/backend-host` sohbetinde resmî dokümandan doğrulanarak yapılır.

## 2. Genel HTTP kuralları

| Konu | Kural |
|---|---|
| Taban yol | `/api/v1/...` |
| Biçim | JSON, UTF-8, `camelCase` alan adları |
| Kimlikler | GUID metni. Şikâyet için ayrıca `publicReference` |
| Tarih/saat | ISO 8601, her zaman UTC (`2026-11-11T09:30:00Z`) |
| Enum | Metin (`"UnderReview"`), sayı değil |
| Boş değer | Bilinmeyen alan gönderilmez veya `null`; boş metin anlam taşımaz |
| Sürümleme | Kırıcı değişiklik → `/api/v2`. `v1` içinde yalnızca geriye uyumlu ekleme |
| Kaynak adları | Çoğul, `kebab-case` (`/service-units`) |
| Okuma | `GET`, yan etkisiz |
| Durum değiştiren işlem | `POST /complaints/{id}/<komut>` (bkz. §5) |

### DTO kuralları

- Domain entity'si doğrudan serileştirilmez. Her yanıt açıkça tanımlanmış bir DTO/projeksiyondur.
- Yanıtlarda **asla** bulunmayanlar: dosya yolu, storage key, ham webhook payload'ı, parola hash'i, token, başka kullanıcının telefon numarası (maskelenmemiş).
- Şikâyetçinin telefon numarası panelde maskeli gösterilir; tam numara ancak açık bir ihtiyaç ve yetkiyle, ayrı bir alanla verilir.
- İstek DTO'ları sunucuda doğrulanır; istemci doğrulaması yalnızca kullanıcı deneyimi içindir.

## 3. Sayfalama, filtreleme, sıralama

Liste uç noktaları sınırsız sonuç dönmez.

İstek: `?page=1&pageSize=25&sort=-createdAt&status=UnderReview&categoryId=...`

| Parametre | Kural |
|---|---|
| `page` | 1'den başlar, varsayılan 1 |
| `pageSize` | Varsayılan 25, azami 100 |
| `sort` | Tek alan; `-` öneki azalan. Yalnızca izin verilen alanlar |
| Filtreler | Uç nokta başına açıkça tanımlı; bilinmeyen filtre `400` |

Yanıt zarfı:

```json
{
  "items": [],
  "page": 1,
  "pageSize": 25,
  "totalCount": 137
}
```

## 4. Hata formatı

Tüm hatalar RFC 9457 Problem Details biçimindedir (`application/problem+json`).

```json
{
  "type": "https://sokagimdakisorun/errors/complaint.invalid_transition",
  "title": "Bu durum geçişine izin verilmiyor.",
  "status": 409,
  "code": "complaint.invalid_transition",
  "detail": "Closed durumundaki şikâyet InProgress durumuna alınamaz.",
  "traceId": "00-...",
  "errors": {
    "reason": ["Gerekçe zorunludur."]
  }
}
```

- `code`: kararlı, makine tarafından okunabilir hata kodu (`<alan>.<hata>`). Panel kullanıcıya gösterilecek davranışı **buna** göre seçer, `title` metnine göre değil.
- `errors`: yalnızca doğrulama hatalarında; alan adı → mesaj listesi.
- `traceId`: loglarla eşleştirmek için.
- Üretimde yığın izi, SQL metni, bağlantı bilgisi yanıta **girmez**.

| HTTP | Ne zaman | Örnek `code` |
|---|---|---|
| 400 | Biçim/doğrulama hatası | `validation.failed` |
| 401 | Kimlik doğrulanmadı | `auth.unauthenticated` |
| 403 | Kimlik var, yetki yok (rol veya kapsam) | `auth.forbidden` |
| 404 | Kaynak yok **veya** kullanıcının görme yetkisi yok | `complaint.not_found` |
| 409 | Yasak durum geçişi; eşzamanlılık çakışması | `complaint.invalid_transition`, `concurrency.conflict` |
| 422 | Biçim doğru ama iş kuralı ihlali | `complaint.duplicate_target_invalid` |
| 429 | Hız sınırı | `rate_limit.exceeded` |
| 500 | Beklenmeyen hata | `server.error` |

Kapsam dışındaki bir şikâyetin varlığını sızdırmamak için tekil kaynak isteklerinde `403` yerine `404` döndürülür.

Hata kodları kataloğu her modülün kendi sohbetinde genişletilir ve OpenAPI belgesinde görünür.

## 5. Durum geçişleri ve eşzamanlılık

- Şikâyet durumu genel bir "status güncelle" uç noktasıyla **değiştirilmez**. Her komut ayrı uç noktadır, kendi istek gövdesi ve yetki kuralı vardır. Geçiş tablosu: [DOMAIN_MODEL §4.3](DOMAIN_MODEL.md#43-izin-verilen-geçişler-bağlayıcı-tablo).
- Şikâyet detayı yanıtı iki alan içerir:
  - `version`: opak eşzamanlılık belirteci.
  - `allowedActions`: geçerli kullanıcı ve geçerli durum için izinli komut adları.
- Durum değiştiren her istek `version` gönderir. Sunucudaki sürüm farklıysa `409 concurrency.conflict` döner; panel güncel veriyi yeniden yükler.
- Panel hangi düğmeyi göstereceğine yalnızca `allowedActions`'a bakarak karar verir.

## 6. Kimlik ve yetki belgelendirmesi

- Her uç nokta OpenAPI'de güvenlik gereksinimiyle birlikte belgelenir: anonim mi, hangi rol(ler), kaynak kapsamı uygulanıyor mu.
- Varsayılan: kimlik doğrulaması zorunlu. Anonim uç noktalar açıkça işaretlenir.
- Panel → API çağrıları `Authorization: Bearer <token>` taşır; token'ı tarayıcı değil Next.js sunucusu ekler ([ADR-0006](decisions/ADR-0006-admin-panel-authentication.md)).
- Giriş ve webhook uç noktalarına hız sınırı uygulanır.

## 7. Modüller arası süreç içi sözleşmeler

> **Planlanan.** İmzalar yön göstericidir; kesin hâlini sahibi modülün sohbeti verir ve PR'da belgelenir. Tüketen modül, sözleşme `main`'e girdikten sonra işe başlar.

### 7.1 Senkron arayüzler

| Sahibi | Arayüz (çalışma adı) | Tüketen | Amaç |
|---|---|---|---|
| Identity | `IUserDirectory` | Complaints, Messaging, Reporting | Kimlikten görünen ad / tür / durum sorgulama (toplu sorgu destekli) |
| Identity | `IReporterRegistry` | Messaging | Dış kimlikten (`whatsapp`, `wa_id`) şikâyetçiyi bul veya oluştur — idempotent |
| Media | `IMediaService` | Messaging, Complaints | `StoreAsync` (doğrula + sakla → `MediaFileId`), `CommitAsync`, `OpenReadAsync` (akış + içerik tipi) |
| Complaints | `IComplaintIntake` | Messaging | `SubmitFromIntakeAsync` — `intakeSessionId` idempotency anahtarıdır; tekrar çağrıda mevcut şikâyeti döner |
| Complaints | `IComplaintReporterActions` | Messaging | Şikâyetçi adına `ProvideInformationAsync`, `WithdrawAsync` |
| Complaints | `IComplaintCatalog` | Messaging | Aktif kategori listesi (intake menüsü için) |
| Complaints | `IComplaintAccessPolicy` | Messaging, Reporting | "Bu kullanıcı bu şikâyeti görebilir mi?" ve kapsam filtresi |
| Complaints | `IComplaintStatistics` | Reporting | Kapsam duyarlı sayım ve dağılım sorguları |

> **WS-08 eklemesi (geriye uyumlu):** `IComplaintStatistics.GetTimelineAsync(actor, from, to, TimeZoneInfo timeZone, ct)` aşırı yüklemesi; günleri verilen zaman diliminin yerel gün sınırına göre kovalar (Reporting `Europe/Istanbul` geçer). Mevcut UTC'li imza değişmedi ve yeni yüklemeye `TimeZoneInfo.Utc` ile delege eder.

Kurallar:

- Arayüzler DTO alır ve döner; entity veya `IQueryable` sızdırmaz.
- Beklenen iş hataları `Result` ile döner, exception fırlatılmaz.
- Her metot `CancellationToken` alır.
- `SharedKernel`'deki `ICurrentUser` yalnızca HTTP isteği bağlamında geçerlidir; arka plan işleyicileri aktörü açıkça parametre olarak geçirir.

### 7.2 Integration event'ler

| Sahibi | Event | Taşıdığı | Dinleyen | Etkisi |
|---|---|---|---|---|
| Complaints | `ComplaintSubmitted` | `complaintId`, `publicReference`, `reporterUserId`, `occurredAt` | Messaging | "Başvurunuz alındı" mesajı |
| Complaints | `ComplaintStatusChanged` | `complaintId`, `publicReference`, `reporterUserId`, `fromStatus`, `toStatus`, `reporterNote`, `occurredAt` | Messaging | Bildirim politikasına göre mesaj; `AwaitingInformation` ise konuşmaya cevap bağlamı yazılır |

Kurallar:

- Event tipleri sahibi modülün `Contracts` projesinde tanımlanır; geriye uyumsuz değiştirilmez (alan eklenir, çıkarılmaz).
- Her event benzersiz `eventId` taşır; handler'lar bunu idempotency anahtarı olarak kullanır.
- Event **iç gerekçeyi (`reason`) taşımaz**; yalnızca şikâyetçiye gösterilebilir `reporterNote` taşır.

Şikâyetçi bildirim politikası (başlangıç; Messaging modülünde tek bir tabloda tutulur):

| Yeni durum | Bildirim |
|---|---|
| `Submitted` | Evet — referans numarasıyla |
| `AwaitingInformation` | Evet — soru metni |
| `InProgress` | Evet |
| `Resolved` | Evet |
| `Rejected`, `Duplicate`, `Withdrawn` | Evet |
| `Closed` | Yalnızca `Accepted`'tan doğrudan kapatıldıysa |
| `UnderReview`, `Accepted`, `Assigned`, `Spam` | Hayır |

## 8. Planlanan HTTP yüzeyi

> **Planlanan — henüz hiçbiri uygulanmadı.** Kesin yol, gövde ve yanıtlar ilgili sohbette belirlenir ve OpenAPI anlık görüntüsüne girer.

### Identity

| Metot | Yol | Yetki | Amaç |
|---|---|---|---|
| POST | `/api/v1/auth/login` | Anonim (hız sınırlı) | E-posta + parola → erişim ve yenileme token'ı |
| POST | `/api/v1/auth/refresh` | Anonim (yenileme token'ı ile) | Token yenileme |
| POST | `/api/v1/auth/logout` | Kimlik doğrulanmış | Kullanıcının tüm erişim ve yenileme token'larını geçersiz kıl (güvenlik damgası) |
| GET | `/api/v1/auth/me` | Kimlik doğrulanmış | Geçerli kullanıcı, roller |
| GET / POST | `/api/v1/users` | Admin | Panel kullanıcılarını listele / moderatör oluştur |
| GET | `/api/v1/users/{id}` | Admin | Tek panel kullanıcısı (WS-04'te eklendi) |
| PATCH | `/api/v1/users/{id}` | Admin | Rol, durum, görünen ad güncelle; rol/durum değişikliği kullanıcının oturumlarını kapatır |

### Complaints

| Metot | Yol | Yetki | Amaç |
|---|---|---|---|
| GET | `/api/v1/complaints` | Moderatör (kapsamlı) | Liste; filtre: `status`, `categoryId`, `serviceUnitId`, `assigneeUserId`, tarih aralığı, `reference` |
| GET | `/api/v1/complaints/{id}` | Moderatör (kapsamlı) | Detay + `version` + `allowedActions` |
| GET | `/api/v1/complaints/{id}/history` | Moderatör (kapsamlı) | Durum geçmişi ve atama hareketleri |
| GET / POST | `/api/v1/complaints/{id}/comments` | Moderatör (kapsamlı) | Yorumlar |
| GET | `/api/v1/complaints/{id}/attachments` | Moderatör (kapsamlı) | Ek metadata listesi |
| GET | `/api/v1/complaints/{id}/attachments/{attachmentId}/content` | Moderatör (kapsamlı) | Dosya akışı |
| POST | `/api/v1/complaints/{id}/start-review` | Moderatör | |
| POST | `/api/v1/complaints/{id}/accept` | Moderatör | |
| POST | `/api/v1/complaints/{id}/reject` | Moderatör | Gerekçe zorunlu |
| POST | `/api/v1/complaints/{id}/mark-duplicate` | Moderatör | Hedef şikâyet zorunlu |
| POST | `/api/v1/complaints/{id}/mark-spam` | Moderatör | |
| POST | `/api/v1/complaints/{id}/request-information` | Moderatör | Soru metni zorunlu |
| POST | `/api/v1/complaints/{id}/resume` | Moderatör | `AwaitingInformation`'dan giriş durumuna (`UnderReview` veya `InProgress`) döner; hedef istemciden alınmaz |
| POST | `/api/v1/complaints/{id}/assign` | Moderatör | Birim zorunlu; `Assigned` iken yeniden atama için de kullanılır |
| POST | `/api/v1/complaints/{id}/unassign` | Moderatör | |
| POST | `/api/v1/complaints/{id}/start-work` | Moderatör | |
| POST | `/api/v1/complaints/{id}/resolve` | Moderatör | Çözüm notu zorunlu |
| POST | `/api/v1/complaints/{id}/close` | Moderatör | |
| POST | `/api/v1/complaints/{id}/reopen` | Moderatör / Admin | `Closed` için yalnızca Admin |
| GET | `/api/v1/categories` | Moderatör | |
| POST / PATCH | `/api/v1/categories[/{id}]` | Admin | |
| GET | `/api/v1/service-units` | Moderatör | |
| POST / PATCH | `/api/v1/service-units[/{id}]` | Admin | Üyelik yönetimi dâhil |
| GET / PUT | `/api/v1/routing-rules` | Admin | |

> **WS-06 uygulandı** (anlık görüntü `docs/api/openapi.v1.json` esastır). Planlanandan farklar / eklemeler: komut gövdeleri `{ version, reason?, reporterNote? }` (`mark-duplicate`: `targetComplaintId`; `assign`: `serviceUnitId`, `assigneeUserId?`); komutlar güncel detayı döner. Üyelik `POST /api/v1/service-units/{id}/members` ve `DELETE /api/v1/service-units/{id}/members/{userId}` ile yönetilir. Liste filtreleri: `status` (tekrarlanabilir), `categoryId`, `serviceUnitId`, `assigneeUserId`, `submittedFrom`, `submittedTo` ([from, to), UTC), `reference`; `sort`: `createdAt` \| `submittedAt` \| `updatedAt` (`-` azalan; varsayılan `-createdAt`). Hata kodları: `complaint.not_found`, `complaint.invalid_transition`, `concurrency.conflict`, `complaint.action_forbidden` (403, ör. `Closed` yeniden açma), `complaint.duplicate_target_invalid`, `complaint.service_unit_invalid`, `complaint.assignee_not_member`, `complaint.attachment_not_found`, `category.not_found`, `category.code_taken`, `service_unit.not_found`, `service_unit.code_taken`, `service_unit.member_invalid`. Panel yanıtlarında telefon numarası **yoktur** (Identity sözleşmesi vermez); şikâyetçi görünen adıyla gösterilir.

### Messaging

| Metot | Yol | Yetki | Amaç |
|---|---|---|---|
| GET | `/api/integrations/whatsapp/webhook` | Anonim (verify token) | Meta el sıkışması |
| POST | `/api/integrations/whatsapp/webhook` | Anonim (**imza zorunlu**, hız sınırlı) | Olay alımı |
| GET | `/api/v1/complaints/{id}/messages` | Moderatör (kapsamlı) | Şikâyetle ilişkili mesajlar ve teslim durumları |

Webhook yolu sürümsüzdür: Meta tarafındaki yapılandırmaya bağlıdır ve API sürümünden bağımsız kalmalıdır.

> **WS-07 uygulandı (mock sağlayıcı).** Webhook GET: `hub.mode=subscribe` + sabit zamanlı `hub.verify_token` karşılaştırması → `hub.challenge` düz metin; aksi `403 webhook.verification_failed`. POST: ham gövde üzerinde `X-Hub-Signature-256` (HMAC-SHA256) doğrulanır, sonra ayrıştırılır; eksik/yanlış imza `401 webhook.invalid_signature` (hiçbir şey yazılmaz), biçimsiz gövde `400 webhook.invalid_payload`, `WhatsApp__MaxWebhookBodyBytes` aşımı `413 webhook.payload_too_large`; kabulde olay başına inbox satırı + boş `200`. Hız sınırı: host'un `webhook` politikası. `GET /api/v1/complaints/{id}/messages` → `{ complaintId, maskedContact, messages[] }` (`direction`, `messageType`, `text`, `latitude`, `longitude`, `hasMedia`, `deliveryStatus`, `failureReason`, `createdAt`); şikâyeti üreten intake'in konuşması + bildirimler + ek bilgi cevapları; en fazla 500, eskiden yeniye; kapsam `IComplaintAccessPolicy` ile, kapsam dışı `404 complaint.not_found`. Telefon maskeli (`90********01`); sağlayıcı kimlikleri, dosya kimliği ve ham payload yanıtta yoktur. Bildirim politikası tablosu `Messaging/Domain/NotificationPolicy.cs`'tedir (§7.2 ile birebir).

### Reporting

| Metot | Yol | Yetki | Amaç |
|---|---|---|---|
| GET | `/api/v1/reports/summary` | Moderatör (kapsamlı) | Duruma göre sayılar |
| GET | `/api/v1/reports/by-category` | Moderatör (kapsamlı) | Kategori dağılımı |
| GET | `/api/v1/reports/timeline` | Moderatör (kapsamlı) | Zaman içinde açılan/kapanan |

> **WS-08 uygulandı** (anlık görüntü `docs/api/openapi.v1.json` esastır). Üçü de `Bearer` (Admin, Moderator) ister; sayılar `IComplaintStatistics` üzerinden kapsam duyarlıdır (moderatör: atanmamışlar + üyesi olduğu birimlere atananlar; Admin: hepsi). Tarihler `Europe/Istanbul` **yerel günü** (`yyyy-MM-dd`, iki uç dâhil, 2000-01-01…2099-12-31). `summary` ve `by-category`: isteğe bağlı `from`, `to` (başvuru tarihi); yanıtlar `{ from, to, timeZone, total, byStatus[{status, count}] }` (12 durumun hepsi, 0 dâhil) ve `{ from, to, timeZone, total, items[{categoryId, categoryCode, categoryName, count}] }` (sayıya göre azalan). `timeline`: `from` (varsayılan: bitişle biten 30 gün), `to` (varsayılan: bugün), `bucket` = `Day` (varsayılan) \| `Week` (büyük/küçük harf duyarsız; hafta pazartesi başlar, uçtaki kısmi haftalar aralığa kırpılır); aralık en fazla 366 gün; yanıt `{ from, to, bucket, timeZone, totalOpened, totalClosed, points[{start, end, opened, closed}] }` — açılan `submitted_at`, kapanan `closed_at` gününe göre; boş kovalar 0. Bilinmeyen sorgu parametresi, hatalı tarih, ters veya aşırı geniş aralık, bilinmeyen kova → `400 validation.failed` (`errors` alan bazında). Boş veri hata değildir (sıfırlar / boş liste).

### Host

| Metot | Yol | Yetki | Amaç |
|---|---|---|---|
| GET | `/health/live`, `/health/ready` | Anonim | Sağlık kontrolü; secret veya ayrıntı sızdırmaz |

## 9. Sözleşme değişiklik süreci

1. **Önce sözleşme, sonra tüketici.** Panel sohbeti, ihtiyaç duyduğu uç nokta `main`'de ve anlık görüntüde yokken onu kendisi yazmaz; eksikliği raporlar.
2. **Eksik sözleşme raporu** şu bilgileri içerir: ihtiyaç duyulan uç nokta/arayüz, beklenen istek ve yanıt alanları, hangi ekran/akış için gerektiği. Rapor, sahibi modülün sohbetine girdi olur.
3. **Geriye uyumlu değişiklik** (yeni isteğe bağlı alan, yeni uç nokta): sahibi modülün PR'ı + güncel anlık görüntü.
4. **Kırıcı değişiklik** (alan silme/yeniden adlandırma, zorunluluk ekleme, anlam değişimi): PR açıklamasında açıkça işaretlenir, etkilenen tüketiciler listelenir; mümkünse önce yeni alan eklenir, tüketici geçer, sonra eskisi kaldırılır.
5. `*.Contracts` projesindeki değişiklik de aynı kurallara tabidir; derleme hatası tüketiciyi zaten uyarır.
