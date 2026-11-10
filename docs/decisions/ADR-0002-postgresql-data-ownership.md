# ADR-0002 — PostgreSQL veri sahipliği: modül başına schema ve DbContext

| | |
|---|---|
| Durum | Kabul edildi |
| Tarih | 2026-11-11 |
| İlgili | [ADR-0001](ADR-0001-modular-monolith.md), [DOMAIN_MODEL §3](../DOMAIN_MODEL.md#3-tablo-sahipliği) |

## Bağlam

Tek bir PostgreSQL örneği kullanılacak. Modüler monolith'te en sık bozulan sınır veridir: tek bir büyük `DbContext` ve modüller arası join'ler, modülleri fiilen birbirine yapıştırır. Öte yandan modül başına ayrı veritabanı bu ölçek için gereksizdir.

Ayrıca transactional outbox, olayın iş verisiyle **aynı transaction'da** yazılmasını gerektirir; bu, "her modül yalnızca kendi schema'sına yazar" kuralıyla ilk bakışta çelişir.

## Karar

### 1. Schema ve DbContext

- Tek veritabanı, **modül başına bir schema**: `identity`, `complaints`, `messaging`, `media`, `platform`.
- Her modülün **kendi `DbContext`'i** vardır ve yalnızca kendi schema'sını eşler.
- Her modül kendi migration'larının sahibidir. Migration'lar modülün kendi projesinde durur ve migration geçmiş tablosu modülün kendi schema'sında tutulur.
- Reporting modülünün schema'sı ve `DbContext`'i yoktur.

### 2. Modüller arası referans

- Schema'lar arasında **foreign key tanımlanmaz**. Başka modüle ait kimlik skaler kolon olarak saklanır (`reporter_user_id`, `media_file_id`, `source_intake_session_id` …).
- Bu kimliklerin geçerliliği, yazma anında sahibi modülün `Contracts` arayüzüyle doğrulanır.
- Modüller arası LINQ/SQL join yapılmaz. Birleşik görünüm gerekiyorsa uygulama katmanında, sözleşmelerden gelen DTO'larla birleştirilir (toplu sorgu ile; N+1 oluşturmadan).
- Modül **içinde** foreign key, `NOT NULL`, `UNIQUE`, `CHECK` kısıtları veritabanında tanımlanır.

### 3. Outbox: bilinçli ve dar bir istisna

- `platform.outbox_messages` **tek** tablodur ve migration'ının sahibi Platform'dur.
- Her modül `DbContext`'i bu tabloyu, Platform'un sağladığı ortak eşleme ile **migration dışı** olarak modeline ekler. Böylece modül, `SaveChanges` sırasında iş verisiyle birlikte outbox satırını aynı transaction'da yazar.
- Modüllerin bu tablodaki tek yetkisi **satır eklemektir**. Okuma, güncelleme ve silme yalnızca `OutboxDispatcher`'a aittir.
- Modüller outbox'a doğrudan değil, Platform'un sunduğu yardımcı üzerinden yazar.

Bu, "başka schema'ya yazma" yasağının tek istisnasıdır.

### 4. Migration uygulama

- Schema'lar arası FK olmadığı için modül migration'ları birbirinden bağımsız sırada uygulanabilir.
- Geliştirmede migration'lar açılışta uygulanabilir (yapılandırma anahtarıyla). Üretim yöntemi deployment iş akışında belirlenir.
- Migration'lar gözden geçirilmeden üretilip uygulanmaz; her PR migration adlarını ve etkisini bildirir.

### 5. Ortak konvansiyonlar

`snake_case` adlar · `timestamptz` (UTC) · Guid v7 birincil anahtarlar · enum'lar metin + `CHECK` · JSONB yalnızca sağlayıcı payload'ı için · cascade delete yok (şikâyet, yorum, geçmiş).

## Gerekçe

- **Schema sınırı görünür bir sınırdır:** bir sorguda başka schema adı görmek ihlalin kendisidir; incelemede hemen fark edilir.
- **Bağımsız migration sahipliği** çoklu sohbet düzeninin ön koşuludur: iki sohbet aynı migration zincirine yazarsa çakışma kaçınılmazdır.
- **FK'siz modül sınırı** modüllerin bağımsız evrilmesini ve test edilmesini sağlar. Kaybedilen referans bütünlüğü, yazma anı doğrulaması ve "silme yok, anonimleştirme var" kuralıyla telafi edilir.
- **Tek outbox tablosu**, tek dağıtıcı ve tek izleme noktası demektir. Modül başına outbox tablosu aynı garantiyi beş kat altyapıyla verirdi.

## Değerlendirilen alternatifler

| Alternatif | Neden seçilmedi |
|---|---|
| Tek `DbContext`, tek schema | Modüller arası join ve navigation property kaçınılmaz olur; migration tek zincirde çakışır. |
| Modül başına ayrı veritabanı | Aynı transaction'da outbox yazımı imkânsızlaşır; yerel ortam ve yedekleme karmaşıklaşır. |
| Modüller arası gerçek FK | Bütünlük güçlenir ama migration sırası bağımlı hâle gelir, modüller ayrı test edilemez, silme/anonimleştirme zorlaşır. |
| Modül başına outbox tablosu | Sınır kuralı istisnasız kalır ama beş tablo, beş dağıtıcı döngüsü gerekir. |
| Outbox'ı ayrı bağlantıyla yazmak | Atomiklik kaybolur; outbox'ın var olma nedeni ortadan kalkar. |

## Sonuçlar

**Olumlu**

- Her modül kendi verisinin tek sahibidir; sohbetler birbirinin migration'ına dokunmaz.
- İş verisi ile olayın atomikliği korunur.

**Olumsuz / dikkat edilecek**

- Modüller arası yetim referans teorik olarak mümkündür. Azaltma: yazma anında doğrulama; kullanıcı ve şikâyet kayıtlarının silinmemesi.
- Birleşik liste ekranları (ör. şikâyet + şikâyetçi adı) iki adımlı sorgu gerektirir. Toplu sorgu sözleşmeleriyle çözülür.
- Paylaşılan outbox eşlemesinin migration dışı tutulması teknik olarak doğrulanmalıdır (yol haritasında V-3). Mümkün olmazsa bu ADR güncellenir ve modül başına outbox'a geçilir.
- `snake_case` için kullanılacak yaklaşım EF Core 10 ile doğrulanmalıdır (V-2).
