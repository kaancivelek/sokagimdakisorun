# ADR-0003 — WhatsApp intake ve şikâyet yaşam döngüsü

| | |
|---|---|
| Durum | Kabul edildi |
| Tarih | 2026-11-11 |
| İlgili | [DOMAIN_MODEL §1, §4, §5](../DOMAIN_MODEL.md), [ARCHITECTURE §5](../ARCHITECTURE.md#5-ana-akışlar), [ADR-0002](ADR-0002-postgresql-data-ownership.md) |

## Bağlam

Vatandaşın tek kanalı WhatsApp'tır. Bu kanalın üç özelliği tasarımı belirler:

1. **Mesajlar parçalıdır.** Bir şikâyet tek mesajla gelmez; kategori, açıklama, konum, fotoğraf ayrı mesajlardır. Her mesajı şikâyet saymak anlamsız kayıtlar üretir.
2. **Webhook teslimi en az bir kezdir.** Aynı olay tekrar gelebilir; sıra garanti değildir.
3. **Giden mesaj dış sisteme bağlıdır.** Veritabanı commit'i ile mesaj gönderimi tek transaction olamaz. Ayrıca işletmenin serbest mesaj gönderebileceği süre sınırlıdır (24 saatlik pencere; ayrıntıları gerçek entegrasyonda doğrulanacak).

Buna ek olarak şikâyetin kendisi izlenebilir bir yaşam döngüsüne sahip olmalı ve kuralları tek yerde durmalıdır.

## Karar

### 1. Üç ayrı kavram

`Conversation` (iletişim bağlamı), `IntakeSession` (geçici toplama süreci) ve `Complaint` (kalıcı iş kaydı) ayrı entity'lerdir. İlk ikisi Messaging modülüne, üçüncüsü Complaints modülüne aittir.

- Konuşma başına en fazla bir aktif intake.
- Intake başına en fazla bir şikâyet.
- Şikâyet yalnızca **açık kullanıcı onayından** sonra oluşur.

### 2. Üç ayrı durum kümesi

| Küme | Sahibi | Örnek değerler |
|---|---|---|
| Intake durumu | Messaging | `AwaitingCategory`, `AwaitingConfirmation`, `Expired` |
| Şikâyet durumu | Complaints | `UnderReview`, `InProgress`, `Closed` |
| Mesaj teslim durumu | Messaging | `Queued`, `Sent`, `Delivered`, `Failed` |

Bunlar birbirine eşlenmez ve birbirinin yerine kullanılmaz. Bir bildirimin gönderilememesi şikâyetin durumunu değiştirmez.

### 3. Şikâyet yaşam döngüsü

- 12 durum ve [DOMAIN_MODEL §4.3](../DOMAIN_MODEL.md#43-izin-verilen-geçişler-bağlayıcı-tablo)'teki 23 geçiş bağlayıcıdır.
- Geçiş kuralları Complaints modülünün domain katmanında **tek bir politikada** yaşar. Durum yalnızca komut metotlarıyla değişir.
- Her geçiş, aynı transaction'da `complaint_status_history`'ye aktör, kaynak, gerekçe ve zamanla yazılır. Geçmiş yalnızca eklenir.
- `Reopened` bir durum değil, kaydı `UnderReview`'a döndüren gerekçeli bir komuttur.
- İç gerekçe (`reason`) ile şikâyetçiye gösterilecek not (`reporter_note`) ayrı alanlardır.
- API, panele `allowedActions` döner; panel ve WhatsApp handler'ı kural kopyalamaz.

### 4. Güvenilir gelen işleme (inbox)

- Webhook uç noktası yalnızca şunu yapar: imzayı **ham gövde üzerinde** doğrular (HMAC-SHA256, sabit zamanlı karşılaştırma) → olay başına inbox satırı yazar → hızlıca `200` döner.
- İş mantığı `InboxProcessor` arka plan işleyicisindedir.
- Tekrar koruması katmanlıdır: inbox `dedup_key`, mesaj `provider_message_id`, şikâyet `source_intake_session_id` — hepsi veritabanı UNIQUE kısıtıdır.
- Başarısız olay backoff ile yeniden denenir; azami denemede dead-letter olur.

### 5. Güvenilir giden işleme (outbox + gönderim kuyruğu)

- Complaints, durum değişikliğiyle aynı transaction'da outbox'a integration event yazar.
- Messaging'in handler'ı bildirim politikasına göre `messages` tablosuna `Queued` bir giden mesaj ekler (idempotent, `dedup_key`).
- `OutboundSender` mesajı sağlayıcıya gönderir. Açık transaction içinde HTTP çağrısı beklenmez.
- Gönderimden önce hizmet penceresi kontrol edilir. Pencere kapalıysa serbest metin gönderilmez; şablon yolu gerekir (gerçek entegrasyon iş akışında uygulanır; mock aşamasında mesaj açıklayıcı bir nedenle `Failed` olur).

### 6. Sağlayıcı bağımsızlığı

- Dış API `IWhatsAppClient` arkasındadır. Domain ve uygulama katmanı Meta'nın JSON şekline veya Graph API yollarına bağımlı değildir.
- İlk uygulama `MockWhatsAppClient`'tır. Gerçek adaptör ayrı iş akışıdır.
- Sağlayıcı seçimi yapılandırmayla yapılır (`WhatsApp__Provider`).

### 7. Modüller arası şikâyet oluşturma

Intake onaylandığında Messaging, Complaints'in `IComplaintIntake.SubmitFromIntakeAsync` sözleşmesini çağırır. `intakeSessionId` idempotency anahtarıdır: aynı kimlikle ikinci çağrı yeni şikâyet oluşturmaz, mevcut olanı döndürür. Böylece iki modülün transaction'ı ayrı olsa da çökme sonrası yeniden deneme güvenlidir.

## Gerekçe

- Intake'i şikâyetten ayırmak, yarım kalmış konuşmaların moderatör kuyruğunu kirletmesini önler ve "şikâyet" kavramını temiz tutar.
- Idempotency'yi veritabanı kısıtına dayandırmak, uygulama kodundaki yarış durumlarına karşı tek sağlam güvencedir.
- Webhook'u ince tutmak, sağlayıcının zaman aşımı ve tekrar davranışından bağımsız kalmayı sağlar.
- Kuralların tek politikada durması, üç farklı giriş noktasının (panel, WhatsApp, sistem) tutarlı davranmasını garanti eder.
- Mock-önce yaklaşım, Meta hesabı ve onay süreçleri beklenmeden uçtan uca akışın kurulup test edilmesine izin verir.

## Değerlendirilen alternatifler

| Alternatif | Neden seçilmedi |
|---|---|
| Her mesaj = taslak şikâyet | Çok sayıda yarım kayıt; "şikâyet" anlamını yitirir; yaşam döngüsü kirlenir. |
| Intake'i şikâyetin `Draft` durumu olarak modellemek | Konuşma akışı kuralları ile iş süreci kuralları aynı durum makinesine karışır; modül sınırı bulanır. |
| Webhook içinde senkron işleme | Yavaş yanıt ve tekrar teslim riski; hata durumunda olay kaybı. |
| Durumu serbestçe güncellenebilir alan + arayüzde kural | Kural üç yerde kopyalanır; ilk tutarsızlıkta veri bozulur. |
| Mesaj kuyruğu (RabbitMQ vb.) | Bu ölçekte PostgreSQL inbox/outbox yeterli; ek altyapı gerekmez. |
| Giden mesajı durum değişikliğiyle aynı istekte göndermek | Commit ve gönderim atomik olamaz; kayıp veya hayalet bildirim riski. |

## Sonuçlar

**Olumlu**

- Tekrar eden webhook, çöken işleyici ve geçici sağlayıcı hatası veri bozmadan atlatılır.
- Şikâyetin her hareketi kim/ne zaman/neden bilgisiyle izlenir.
- Gerçek WhatsApp entegrasyonu dar bir adaptör değişikliğine indirgenir.

**Olumsuz / dikkat edilecek**

- Bildirimler **en az bir kez** ve gecikmeli gider. Sağlayıcı tarafında idempotency garantisi yoksa, "gönderildi ama kaydedilemedi" durumunda çift mesaj olasılığı kalır; bu kabul edilen bir risktir ve gerçek entegrasyonda yeniden değerlendirilir.
- Üç arka plan döngüsü (inbox, outbox, gönderim) izlenmelidir; birikim health/log üzerinden görünür olmalıdır.
- Mock'un geçmesi gerçek Meta davranışını kanıtlamaz. Webhook payload şekli, mesaj tipleri, pencere ve şablon kuralları gerçek entegrasyonda resmî dokümandan doğrulanacaktır (yol haritası V-8).
- `AwaitingInformation`'dan dönüş, kaydın bu duruma girdiği duruma yapılır (OD-3, 2026-11-11: geçiş #23 eklendi).
