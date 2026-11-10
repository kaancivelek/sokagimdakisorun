# ADR-0004 — Önce yerel dosya sistemi: medya saklama

| | |
|---|---|
| Durum | Kabul edildi |
| Tarih | 2026-11-11 |
| İlgili | [ARCHITECTURE §8](../ARCHITECTURE.md#8-medya), [DOMAIN_MODEL §3.4](../DOMAIN_MODEL.md#34-media-schema--media-modülü) |

## Bağlam

Şikâyetçiler fotoğraf gönderir. Hedef deployment Railway'dir ve ilk aşamada dosyalar Railway Volume üzerinde tutulacaktır; ancak Railway kurulumu sonraki fazdadır ve geliştirme yerelde başlar.

Kullanıcıdan gelen dosya güvenilmez girdidir: sahte MIME, aşırı boyut, kötü niyetli dosya adı, gömülü konum verisi içerebilir. Fotoğraflar kişisel veri de barındırabilir (yüz, plaka, ev).

Proje skill'i dosya metadata'sını şikâyet ek tablosunda (`storage_key` dâhil) tutmayı önerir. Bu ADR o önerinden bir noktada ayrılır; gerekçesi aşağıdadır.

## Karar

### 1. Soyutlama

- Fiziksel saklama `IMediaStorage` arkasındadır (Media modülünün içinde).
- İlk uygulama yerel dosya sistemidir. Kök dizin yapılandırmadan gelir (`Media__RootPath`); yerelde `./.local/media` (git dışı).
- Railway Volume için **ayrı bir uygulama gerekmez**: aynı dosya sistemi uygulaması, kök dizini Volume'un bağlandığı yola ayarlanarak kullanılır. Yol kodda sabitlenmez.
- Object storage ihtiyacı doğarsa yeni bir `IMediaStorage` uygulaması eklenir; diğer modüller etkilenmez.

### 2. Metadata sahipliği (skill önerisinden sapma)

- Dosya metadata'sı **Media modülüne** aittir: `media.media_files` (`storage_key`, içerik tipi, boyut, SHA-256, durum).
- Complaints modülü yalnızca ilişkiyi tutar: `complaint_attachments.media_file_id`.
- Diğer modüller dosyayı opak `MediaFileId` ile tanır. **`storage_key` ve fiziksel yol Media modülünün dışına çıkmaz.**

### 3. Kabul kuralları

Bir dosya ancak şunların tümünü geçerse saklanır:

- İçerik tipi allowlist'te (MVP: JPEG, PNG, WebP).
- Boyut sınırın altında (akış okunurken sayılarak; bildirilen uzunluğa güvenilmez).
- Dosya imzası (magic byte) bildirilen tiple uyumlu.
- SHA-256 hesaplanır.
- Depolama anahtarı sunucuda rastgele üretilir. Kullanıcının dosya adı yol bileşeni olarak **kullanılmaz**.

### 4. Yaşam döngüsü

`Temporary` → (bir şikâyete bağlanınca) `Committed` → (silinince) `Deleted`.

- Intake sırasında gelen fotoğraf `Temporary` olarak saklanır; şikâyet oluşunca `Committed` yapılır.
- Belirli süre `Temporary` kalan dosyalar `OrphanMediaCleaner` tarafından silinir.
- Dosya yazımı ile veritabanı commit'i atomik değildir. Sıra: dosyayı yaz → metadata'yı kaydet. Arada kalan dosyalar temizleyici tarafından toplanır.

### 5. Erişim

- Herkese açık statik dosya sunumu **yoktur**.
- Dosya, Complaints modülündeki yetkilendirilmiş uç nokta üzerinden akıtılır: önce şikâyet erişim yetkisi kontrol edilir, sonra Media'dan akış alınır.
- API yanıtlarında dosya yolu, storage key veya dizin yapısı bulunmaz.

### 6. Gizlilik

- EXIF/GPS verisi kamuya açılmaz. Saklama anında temizleme mi, sunum anında temizleme mi yapılacağı Media iş akışında belirlenir ve belgelenir.
- Dosya içeriği ve yolu loglanmaz.

### 7. Yedekleme

Veritabanı yedeği dosyaları kapsamaz. Medya yedeği ayrı planlanır ve deployment iş akışında belgelenir; geri yükleme denenmeden "yedek var" denmez.

## Gerekçe

- **Yerel-önce** yaklaşım, Railway hesabı beklenmeden medya akışının kurulup test edilmesini sağlar; Volume'a geçiş yalnızca bir yapılandırma değeridir.
- **Metadata'nın Media'da olması:**
  - Fiziksel yolun sızabileceği yüzeyi tek modüle indirir.
  - Intake sırasında (henüz şikâyet yokken) gelen dosyaların izlenebilmesini sağlar; aksi hâlde storage key'ler Messaging'in taslak verisine yayılırdı.
  - Yetim dosya temizliğini modülün kendi içinde çözülebilir kılar (başka modüllerin tablolarını taramadan).
- **Yetkilendirmenin Complaints'te kalması**, "bu dosyayı kim görebilir" sorusunun gerçek sahibinin şikâyet erişim kuralı olmasındandır.

## Değerlendirilen alternatifler

| Alternatif | Neden seçilmedi |
|---|---|
| Dosyayı PostgreSQL'de (bytea) saklamak | Veritabanını şişirir, yedeklemeyi ağırlaştırır; proje kuralı gereği yasak. |
| Baştan object storage (S3 uyumlu) | Ek servis ve secret; tek örnek MVP için gereksiz. Soyutlama sayesinde sonradan eklenebilir. |
| `storage_key`'i `complaint_attachments`'ta tutmak (skill önerisi) | Yol bilgisi iki modüle yayılır; şikâyet öncesi dosyalar sahipsiz kalır; temizlik modüller arası sorgu gerektirir. |
| Statik dosya sunucusu + tahmin edilemez URL | Yetkilendirme atlanır; URL sızarsa dosya herkese açılır. |
| Railway Volume için ayrı adaptör sınıfı | Volume zaten dosya sistemidir; ikinci uygulama yalnızca kod tekrarı olur. |

## Sonuçlar

**Olumlu**

- Yerelden Railway'e geçişte kod değişmez.
- Fiziksel saklama ayrıntısı tek modülde kapalıdır.

**Olumsuz / dikkat edilecek**

- Volume bağlı servis yatay ölçeklenemez; API tek örnek çalışır ve arka plan işleyicileri aynı süreçte kalır ([ARCHITECTURE §13](../ARCHITECTURE.md#13-deployment-sonraki-faz--şimdi-uygulanmaz)).
- Dosya akışı API sürecinden geçer; çok büyük dosyalarda bellek yerine akış (stream) kullanılmalıdır.
- Media için beşinci bir schema ve `DbContext` eklenir.
- Railway Volume kısıtları (bağlanma zamanı, izinler, yedekleme) deployment anında güncel dokümandan doğrulanmalıdır (yol haritası V-9).
