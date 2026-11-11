<#
.SYNOPSIS
  Yerelde WhatsApp konuşması simüle eder: Meta Cloud API webhook şekline dayalı fixture'dan gövde üretir, ham gövdeyi
  WhatsApp__AppSecret ile HMAC-SHA256 imzalar (X-Hub-Signature-256) ve API'nin webhook uç noktasına POST eder.

.DESCRIPTION
  Yalnızca yerel geliştirme içindir (WhatsApp__Provider=Mock). Gerçek Meta'ya hiçbir istek atmaz. Tüm değerler temsilidir;
  gerçek telefon numarası kullanmayın. Botun cevaplarını görmek için -ShowReplies (docker compose ile çalışan PostgreSQL'den okur).
  Ayrıntı: tools/whatsapp-simulator/README.md

.EXAMPLE
  ./tools/whatsapp-simulator/send-webhook.ps1 -Type text -Text "merhaba" -ShowReplies
  ./tools/whatsapp-simulator/send-webhook.ps1 -Type location -Latitude 40.4105 -Longitude 26.6712
  ./tools/whatsapp-simulator/send-webhook.ps1 -Type image -MediaId sample-1
  ./tools/whatsapp-simulator/send-webhook.ps1 -Type text -Text "1" -Repeat 2      # aynı olayı iki kez gönder (tekrar koruması)
  ./tools/whatsapp-simulator/send-webhook.ps1 -Type status -MessageId wamid.mock.xxx -Status delivered
#>
[CmdletBinding()]
param(
    [ValidateSet('text', 'location', 'image', 'audio', 'status')]
    [string] $Type = 'text',
    [string] $Text = 'merhaba',
    [double] $Latitude = 40.4105,
    [double] $Longitude = 26.6712,
    [string] $MediaId = 'mock-sample',
    [string] $MessageId,
    [ValidateSet('sent', 'delivered', 'read', 'failed')]
    [string] $Status = 'delivered',
    [string] $From = '905550000001',
    [string] $Name = 'Temsili Vatandaş',
    [string] $PhoneNumberId = '100000000000001',
    [string] $BaseUrl = 'http://localhost:5180',
    [string] $AppSecret = $env:WhatsApp__AppSecret,
    [int] $Repeat = 1,
    [switch] $ShowReplies
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrEmpty($AppSecret)) {
    throw 'AppSecret yok: -AppSecret verin veya WhatsApp__AppSecret ortam değişkenini API ile aynı sahte değere ayarlayın.'
}

if ([string]::IsNullOrEmpty($MessageId)) {
    $MessageId = 'wamid.sim.' + [guid]::NewGuid().ToString('N')
}

$fixture = Join-Path $PSScriptRoot "fixtures/$Type.json"
$template = [System.IO.File]::ReadAllText($fixture, [System.Text.Encoding]::UTF8)
$invariant = [System.Globalization.CultureInfo]::InvariantCulture
$body = $template.
    Replace('{{PHONE_NUMBER_ID}}', $PhoneNumberId).
    Replace('{{FROM}}', $From).
    Replace('{{NAME}}', $Name).
    Replace('{{MESSAGE_ID}}', $MessageId).
    Replace('{{TIMESTAMP}}', [string][DateTimeOffset]::UtcNow.ToUnixTimeSeconds()).
    Replace('{{TEXT}}', (ConvertTo-Json -InputObject $Text -Compress)).
    Replace('{{LATITUDE}}', $Latitude.ToString($invariant)).
    Replace('{{LONGITUDE}}', $Longitude.ToString($invariant)).
    Replace('{{MEDIA_ID}}', $MediaId).
    Replace('{{STATUS}}', $Status)

$bytes = [System.Text.Encoding]::UTF8.GetBytes($body)
$hmac = New-Object System.Security.Cryptography.HMACSHA256 (, [System.Text.Encoding]::UTF8.GetBytes($AppSecret))
try {
    $hash = $hmac.ComputeHash($bytes)
}
finally {
    $hmac.Dispose()
}
$signature = 'sha256=' + (($hash | ForEach-Object { $_.ToString('x2') }) -join '')

$uri = $BaseUrl.TrimEnd('/') + '/api/integrations/whatsapp/webhook'
for ($i = 1; $i -le $Repeat; $i++) {
    $response = Invoke-WebRequest -Uri $uri -Method Post -Body $bytes -ContentType 'application/json' `
        -Headers @{ 'X-Hub-Signature-256' = $signature } -UseBasicParsing
    Write-Host "[$i/$Repeat] $Type ($MessageId) → HTTP $($response.StatusCode)"
}

if ($ShowReplies) {
    # İşleyicilerin turunu bekle (InboxProcessor + OutboundSender varsayılan 1 sn aralıkla tarar).
    Start-Sleep -Seconds 3
    $sql = "SELECT to_char(m.created_at, 'HH24:MI:SS') AS saat, m.delivery_status AS durum, m.text_body AS metin " +
        "FROM messaging.messages m JOIN messaging.conversations c ON c.id = m.conversation_id " +
        "WHERE c.external_contact_id = '$From' AND m.direction = 'Outbound' ORDER BY m.created_at DESC LIMIT 5;"
    docker compose exec -T postgres sh -c "psql -U `"`$POSTGRES_USER`" -d `"`$POSTGRES_DB`" -c `"$sql`""
}
