# WhatsApp simülatörü (yalnızca yerel, mock sağlayıcı)

Gerçek Meta hesabı olmadan uçtan uca intake akışını denemek için imzalı webhook istekleri gönderir (WS-07).
Gövdeler `fixtures/` altındaki, Meta WhatsApp Cloud API webhook şekline dayalı şablonlardan üretilir; aynı fixture'lar
otomatik testlerde de kullanılır. **Güven sınırı:** şablonlar ve mock sağlayıcı gerçek Meta davranışını kanıtlamaz (V-8, WS-10).

## Hazırlık

```powershell
docker compose up -d                       # PostgreSQL
$env:WhatsApp__Provider = 'Mock'
$env:WhatsApp__AppSecret = 'local-dev-app-secret'      # sahte; API ve betik aynı değeri kullanır
$env:WhatsApp__VerifyToken = 'local-dev-verify-token'  # sahte
dotnet run --project src/SokagimdakiSorun.Api          # http://localhost:5180
```

Kategori menüsünün dolu olması için `Complaints__DemoSeed__Enabled=true` (Development) önerilir.

## Örnek konuşma

```powershell
$s = './tools/whatsapp-simulator/send-webhook.ps1'
& $s -Type text -Text 'merhaba' -ShowReplies          # intake başlar, kategori menüsü
& $s -Type text -Text '1' -ShowReplies                # kategori
& $s -Type text -Text 'Kaldırımda büyük çukur var (temsili).'
& $s -Type location -Latitude 40.4105 -Longitude 26.6712   # veya: -Type text -Text 'Yazıcızade Mah. ...'
& $s -Type image -MediaId sample-1                    # mock geçerli küçük bir PNG döner; 'atla' da yazılabilir
& $s -Type text -Text '1' -ShowReplies                # onay → tek şikâyet + "Başvurunuz alındı" (referans)
```

Diğer senaryolar: `-Text 'iptal'`, özet adımında `2`/`3`/`4` (düzelt), `-Type audio` (desteklenmeyen tür),
`-Repeat 2` (aynı olayı iki kez gönder: ikinci kayıt oluşmaz), `-Type status -MessageId <wamid> -Status read`
(giden mesajın teslim durumu). Mock medya kimlikleri: `mock-missing` (kalıcı hata), `mock-transient` (geçici hata, yeniden denenir).

`-ShowReplies`, `docker compose exec` ile son 5 giden mesajı gösterir. Bot cevapları veritabanındadır; loglara metin yazılmaz.

Webhook GET doğrulaması:

```powershell
Invoke-WebRequest 'http://localhost:5180/api/integrations/whatsapp/webhook?hub.mode=subscribe&hub.verify_token=local-dev-verify-token&hub.challenge=12345'
```
