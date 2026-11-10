# ADR-0001 — Modüler monolith

| | |
|---|---|
| Durum | Kabul edildi |
| Tarih | 2026-11-11 |
| Karar veren | Proje sahibi (teknoloji ve mimari tercihi), mimari hazırlık oturumu (ayrıntılar) |

## Bağlam

Proje tek geliştirici tarafından, bir dönem içinde, çoklu Claude Code sohbetiyle geliştirilecek. Beş ayrı sorumluluk alanı var: kimlik, şikâyet, mesajlaşma, medya, raporlama. Proje sahibi bu alanları ayrı ".NET servisleri" olarak düşünmüş, ancak hedefin modüler monolith olduğunu belirtmiştir.

Aynı anda iki ihtiyaç karşılanmalı:

- Alanlar birbirine karışmamalı; ayrı sohbetler birbirinin kodunu bozmadan ilerleyebilmeli.
- İşletim yükü (deployment, ağ, dağıtık hata ayıklama) tek kişinin taşıyabileceği düzeyde kalmalı.

## Karar

1. Backend **tek bir ASP.NET Core sürecidir**. Tüm modüller ve arka plan işleyicileri aynı host içinde çalışır.
2. Her modül kendi projesinde yaşar ve iki derlemeden oluşur: uygulama projesi (`SokagimdakiSorun.<Modül>`) ve dışa açık sözleşme projesi (`SokagimdakiSorun.<Modül>.Contracts`).
3. Bir modül başka modülün yalnızca `Contracts` projesine referans verebilir. Bu kural **proje referanslarıyla, derleme zamanında** zorlanır.
4. Modüller arası iletişim iki yoldan yapılır: senkron `Contracts` arayüzü veya outbox üzerinden integration event. Doğrudan tablo/`DbContext` erişimi yoktur.
5. Host projesi (`SokagimdakiSorun.Api`, "Core.Api" rolü) yalnızca composition root'tur; iş kuralı içermez.
6. `SharedKernel` çok küçük tutulur ve domain entity'si içermez.
7. Next.js paneli ayrı bir uygulamadır.

Modül listesi ve bağımlılık yönleri: [ARCHITECTURE §2–§3](../ARCHITECTURE.md#2-çözüm-düzeni-hedef).

## Gerekçe

- **Tek deployment, tek veritabanı transaction alanı:** dağıtık sistem sorunları (ağ hataları, servis keşfi, dağıtık izleme) hiç ortaya çıkmaz.
- **Derleme zamanı sınırları:** kural yalnızca dokümanda kalmaz; ihlal eden kod derlenmez. Çoklu sohbet düzeninde bu, kod incelemesine güvenmekten daha sağlamdır.
- **`Contracts` ayrımı:** tüketen sohbet, sahibi modülün iç yapısını bilmeden çalışabilir. Sözleşme değişikliği derleme hatasıyla görünür olur.
- **Geri dönülebilirlik:** bir modül ileride gerçekten ayrı servise dönüşmek zorunda kalırsa, sınırı zaten bellidir.

## Değerlendirilen alternatifler

| Alternatif | Neden seçilmedi |
|---|---|
| Mikroservisler | Tek geliştirici ve ders projesi ölçeği için işletim yükü orantısız. Volume kısıtı nedeniyle API zaten tek örnek çalışacak. |
| Tek proje, klasörlerle ayrım | Sınır yalnızca disiplinle korunur; farklı sohbetler kolayca başka modülün iç tiplerine uzanır. |
| Modül başına 3–4 proje (Domain/Application/Infrastructure/Api) | ~20 proje; bu ölçekte fayda sağlamadan build süresini ve karmaşıklığı artırır. Katmanlar modül içinde klasör olarak tutulur. |
| Mimari testleriyle (ör. tip bağımlılığı tarayan kütüphane) sınır denetimi | Ek bağımlılık. Proje referansı aynı işi bağımlılıksız yapıyor. |

## Sonuçlar

**Olumlu**

- Yerelde tek komutla çalışır; hata ayıklama tek süreçte.
- Sohbetler arası dosya çakışması az: her modül kendi klasöründe.

**Olumsuz / dikkat edilecek**

- Modülleri kapsayan atomik transaction yoktur; tutarlılık idempotency ve outbox ile sağlanır ([ADR-0002](ADR-0002-postgresql-data-ownership.md), [ADR-0003](ADR-0003-whatsapp-intake-and-complaint-lifecycle.md)).
- Bir arka plan işleyicisindeki ağır yük API ile aynı süreci paylaşır.
- `Contracts` projeleri özenle tasarlanmalı; buraya entity veya `IQueryable` sızdırmak sınırı fiilen kaldırır.
- Ortak dosyalar (solution, `Program.cs`) çakışma noktasıdır; bootstrap iş akışı bunları önceden hazırlar.
