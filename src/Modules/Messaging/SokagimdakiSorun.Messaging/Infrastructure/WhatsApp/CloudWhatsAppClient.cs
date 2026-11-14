using System.Buffers;
using System.Collections.Frozen;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SokagimdakiSorun.Messaging.Infrastructure.WhatsApp;

/// <summary>
/// Meta WhatsApp Cloud API adaptörü (<c>WhatsApp__Provider=CloudApi</c>, WS-10). Meta'nın JSON şekli ve Graph API yolları yalnızca
/// burada (ve webhook ayrıştırıcısında) bilinir (ADR-0003 §6). İstek şekilleri resmî dokümandan alınmıştır (V-8, 2026-11-14):
/// <list type="bullet">
///   <item>Gönderim: <c>POST https://graph.facebook.com/{sürüm}/{phone-number-id}/messages</c>, <c>Authorization: Bearer</c>,
///         gövde <c>messaging_product=whatsapp</c>, <c>recipient_type=individual</c>, <c>to</c>, <c>type</c> + türe özgü nesne.</item>
///   <item>Medya: <c>GET /{sürüm}/{media-id}</c> → <c>url</c>, <c>mime_type</c>, <c>sha256</c>, <c>file_size</c>; URL 5 dakika geçerlidir
///         ve token'la indirilir; indirme 404 verirse yeni URL istenmelidir (bu yüzden geçici sayılır).</item>
///   <item>Hata: <c>{"error":{"code":…}}</c>; yeniden denenebilir kodlar resmî hata kodu tablosundan.</item>
/// </list>
/// Exception fırlatmaz (iptal hariç): sonuç geçici/kalıcı olarak sınıflandırılır; yeniden deneme kararı çağırandadır
/// (<see cref="OutboundSender"/>, inbox işleyicisi). <b>Loglanmaz:</b> token, alıcı, metin, şablon parametresi, medya URL'si, istek yolu.
/// HTTP istemcisinin yerleşik istek logları kayıtta kapatılır (<c>RemoveAllLoggers</c>), çünkü onlar tam URL'yi yazar.
/// </summary>
internal sealed partial class CloudWhatsAppClient(
    IHttpClientFactory httpClientFactory,
    IOptions<WhatsAppOptions> options,
    ILogger<CloudWhatsAppClient> logger) : IWhatsAppClient
{
    /// <summary><see cref="IHttpClientFactory"/> içindeki adlandırılmış istemci.</summary>
    public const string HttpClientName = "WhatsApp.CloudApi";

    /// <summary>Graph API kökü (sabit; yapılandırmayla değiştirilemez, böylece token başka bir hosta gönderilemez).</summary>
    public static readonly Uri GraphBaseAddress = new("https://graph.facebook.com/");

    /// <summary>
    /// Resmî hata kodu tablosunda yeniden denenmesi önerilen kodlar: 2 (geçici kesinti), 4 (uygulama hız sınırı), 80007 (WABA hız sınırı),
    /// 130429 (throughput), 131000 (bilinmeyen hata, "tekrar dene"), 131016 (servis geçici olarak kullanılamıyor), 131056 (aynı çift
    /// arasında çok mesaj), 133004 (sunucu geçici olarak kullanılamıyor). Diğer kodlar kalıcıdır (ör. 131047 pencere kapalı, 131026
    /// teslim edilemez, 132xxx şablon, 190 token). 131049 "en az 24 saat bekle" dediği için backoff tavanını aşar → kalıcı.
    /// </summary>
    internal static readonly FrozenSet<int> TransientErrorCodes = new[] { 2, 4, 80007, 130429, 131000, 131016, 131056, 133004 }.ToFrozenSet();

    /// <summary>Media modülünün kabul ettiği türler (ADR-0004); başka tür indirilmeden reddedilir.</summary>
    internal static readonly FrozenSet<string> AllowedMediaTypes =
        new[] { "image/jpeg", "image/png", "image/webp" }.ToFrozenSet(StringComparer.Ordinal);

    private readonly WhatsAppOptions _options = options.Value;

    public Task<SendResult> SendTextAsync(OutboundText message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        var body = Envelope(message.RecipientId, "text");
        body["text"] = new JsonObject { ["preview_url"] = false, ["body"] = message.Text };
        return PostMessageAsync(message.BusinessPhoneNumberId, body, message.MessageId, "text", cancellationToken);
    }

    public Task<SendResult> SendLocationAsync(OutboundLocation message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        var location = new JsonObject { ["latitude"] = message.Latitude, ["longitude"] = message.Longitude };
        if (!string.IsNullOrWhiteSpace(message.Name))
        {
            location["name"] = message.Name;
        }

        if (!string.IsNullOrWhiteSpace(message.Address))
        {
            location["address"] = message.Address;
        }

        var body = Envelope(message.RecipientId, "location");
        body["location"] = location;
        return PostMessageAsync(message.BusinessPhoneNumberId, body, message.MessageId, "location", cancellationToken);
    }

    public Task<SendResult> SendTemplateAsync(OutboundTemplate message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        var template = new JsonObject
        {
            ["name"] = message.TemplateName,
            ["language"] = new JsonObject { ["code"] = message.LanguageCode },
        };
        if (message.BodyParameters.Count > 0)
        {
            var parameters = new JsonArray();
            foreach (var value in message.BodyParameters)
            {
                parameters.Add(new JsonObject { ["type"] = "text", ["text"] = value });
            }

            template["components"] = new JsonArray { new JsonObject { ["type"] = "body", ["parameters"] = parameters } };
        }

        var body = Envelope(message.RecipientId, "template");
        body["template"] = template;
        return PostMessageAsync(message.BusinessPhoneNumberId, body, message.MessageId, "template", cancellationToken);
    }

    public async Task<MediaDownloadResult> DownloadMediaAsync(string providerMediaId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(providerMediaId) || !MediaIdPattern().IsMatch(providerMediaId))
        {
            return Rejected(MediaDownloadResult.Permanent("invalid_media_id"));
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.HttpTimeout);
        var http = httpClientFactory.CreateClient(HttpClientName);

        // 1) Meta veri: kısa ömürlü URL, tür, boyut, özet.
        MediaMetadata metadata;
        try
        {
            using var request = Authorized(HttpMethod.Get, new Uri(GraphBaseAddress, $"{Version}/{Uri.EscapeDataString(providerMediaId)}"));
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token);
            var json = await ReadJsonAsync(response, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                var error = Classify(response.StatusCode, json);
                return Rejected(error.Outcome == SendOutcome.TransientFailure
                    ? MediaDownloadResult.Transient($"media_metadata:{error.Error}")
                    : MediaDownloadResult.Permanent($"media_metadata:{error.Error}"));
            }

            if (ParseMetadata(json) is not { } parsed)
            {
                return Rejected(MediaDownloadResult.Permanent("media_metadata_invalid"));
            }

            metadata = parsed;
        }
        catch (Exception ex) when (IsTransport(ex, cancellationToken))
        {
            return Rejected(MediaDownloadResult.Transient(TransportError(ex)));
        }

        if (!AllowedMediaTypes.Contains(metadata.MimeType))
        {
            return Rejected(MediaDownloadResult.Permanent("media_type_not_allowed"));
        }

        if (metadata.FileSize is { } declared && declared > _options.MaxMediaBytes)
        {
            return Rejected(MediaDownloadResult.Permanent("media_too_large"));
        }

        // 2) Yetkili indirme. URL loglanmaz ve hata metinlerine girmez.
        try
        {
            using var request = Authorized(HttpMethod.Get, metadata.Url);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                // 404: URL'nin süresi dolmuş olabilir → yeniden denemede yeni URL alınır (resmî doküman önerisi).
                var status = (int)response.StatusCode;
                return Rejected(response.StatusCode == HttpStatusCode.NotFound || IsTransientStatus(response.StatusCode)
                    ? MediaDownloadResult.Transient($"media_download:http_{status}")
                    : MediaDownloadResult.Permanent($"media_download:http_{status}"));
            }

            if (response.Content.Headers.ContentLength is { } length && length > _options.MaxMediaBytes)
            {
                return Rejected(MediaDownloadResult.Permanent("media_too_large"));
            }

            var buffer = new MemoryStream();
            var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            await using (stream.ConfigureAwait(false))
            {
                if (!await CopyWithLimitAsync(stream, buffer, _options.MaxMediaBytes, timeout.Token))
                {
                    await buffer.DisposeAsync();
                    return Rejected(MediaDownloadResult.Permanent("media_too_large"));
                }
            }

            if (!HashMatches(buffer, metadata.Sha256))
            {
                await buffer.DisposeAsync();
                return Rejected(MediaDownloadResult.Permanent("media_hash_mismatch"));
            }

            buffer.Position = 0;
            LogMediaDownloaded(logger, metadata.MimeType, buffer.Length);
            return MediaDownloadResult.Success(buffer, metadata.MimeType);
        }
        catch (Exception ex) when (IsTransport(ex, cancellationToken))
        {
            return Rejected(MediaDownloadResult.Transient(TransportError(ex)));
        }
    }

    private string Version => _options.GraphApiVersion
        ?? throw new InvalidOperationException("WhatsApp__GraphApiVersion tanımlı değil (açılış doğrulaması atlanmış).");

    private async Task<SendResult> PostMessageAsync(
        string businessPhoneNumberId, JsonObject body, Guid messageId, string kind, CancellationToken cancellationToken)
    {
        var phoneNumberId = IsNumeric(businessPhoneNumberId) ? businessPhoneNumberId : _options.PhoneNumberId;
        if (string.IsNullOrWhiteSpace(phoneNumberId) || !IsNumeric(phoneNumberId))
        {
            return Logged(messageId, kind, SendResult.Permanent("invalid_phone_number_id"));
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.HttpTimeout);
        try
        {
            var http = httpClientFactory.CreateClient(HttpClientName);
            using var request = Authorized(HttpMethod.Post, new Uri(GraphBaseAddress, $"{Version}/{phoneNumberId}/messages"));
            request.Content = JsonContent.Create(body);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token);
            var json = await ReadJsonAsync(response, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                return Logged(messageId, kind, Classify(response.StatusCode, json));
            }

            // Başarılı yanıtta kimlik yoksa mesaj yine de kabul edilmiş olabilir: yeniden denemek çift mesaj üretebilir → kalıcı.
            var providerId = Str((json?["messages"] as JsonArray)?.FirstOrDefault()?["id"]);
            return Logged(messageId, kind, string.IsNullOrWhiteSpace(providerId) || providerId.Length > Domain.Message.ProviderIdMaxLength
                ? SendResult.Permanent("invalid_provider_response")
                : SendResult.Success(providerId));
        }
        catch (Exception ex) when (IsTransport(ex, cancellationToken))
        {
            return Logged(messageId, kind, SendResult.Transient(TransportError(ex)));
        }
    }

    /// <summary>HTTP durumu + Graph hata kodu → geçici/kalıcı. Hata metni yalnızca kodları içerir (başlık/ayrıntı kişisel veri taşıyabilir).</summary>
    internal static SendResult Classify(HttpStatusCode status, JsonNode? json)
    {
        var code = json?["error"]?["code"] is JsonValue value && value.TryGetValue<int>(out var c) ? c : (int?)null;
        var error = code is null ? $"http_{(int)status}" : $"http_{(int)status}:provider_error:{code}";
        if (code == 131047)
        {
            error += ":window_closed";
        }

        // Kodsuz 5xx/429/408 ve tablodaki geçici kodlar yeniden denenir; deneme sayısı OutboundSender'da sınırlıdır.
        var transient = (code is { } known && TransientErrorCodes.Contains(known)) || IsTransientStatus(status);
        return transient ? SendResult.Transient(error) : SendResult.Permanent(error);
    }

    private static bool IsTransientStatus(HttpStatusCode status) =>
        (int)status >= 500 || status is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout;

    private static JsonObject Envelope(string recipientId, string type) => new()
    {
        ["messaging_product"] = "whatsapp",
        ["recipient_type"] = "individual",
        ["to"] = recipientId,
        ["type"] = type,
    };

    private HttpRequestMessage Authorized(HttpMethod method, Uri uri)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.AccessToken);
        return request;
    }

    private static async Task<JsonNode?> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static MediaMetadata? ParseMetadata(JsonNode? json)
    {
        var url = Str(json?["url"]);
        var mime = Str(json?["mime_type"]);
        if (url is null || mime is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }

        long? size = json?["file_size"] switch
        {
            JsonValue v when v.TryGetValue<long>(out var n) => n,
            JsonValue v when v.TryGetValue<string>(out var s) && long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var n) => n,
            _ => null,
        };

        var normalizedMime = mime.Split(';', 2)[0].Trim().ToLowerInvariant();
        return new MediaMetadata(uri, normalizedMime, size, Str(json?["sha256"]));
    }

    private static async Task<bool> CopyWithLimitAsync(Stream source, Stream target, long limit, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                total += read;
                if (total > limit)
                {
                    return false;
                }

                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }

            return true;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Meta verideki <c>sha256</c> ile içerik karşılaştırılır. Resmî doküman değerin kodlamasını belirtmez; onaltılık (64 karakter) ve
    /// base64 (32 bayt) kabul edilir, başka biçimde veya yoksa kontrol atlanır (doğrulanmadı — gerçek Meta testiyle teyit edilecek).
    /// </summary>
    internal static bool HashMatches(MemoryStream content, string? expected)
    {
        if (string.IsNullOrWhiteSpace(expected))
        {
            return true;
        }

        byte[] expectedBytes;
        if (expected.Length == 64 && expected.All(char.IsAsciiHexDigit))
        {
            expectedBytes = Convert.FromHexString(expected);
        }
        else
        {
            var decoded = new byte[48];
            if (!Convert.TryFromBase64String(expected, decoded, out var written) || written != SHA256.HashSizeInBytes)
            {
                return true;
            }

            expectedBytes = decoded[..written];
        }

        var actual = content.TryGetBuffer(out var segment)
            ? SHA256.HashData(segment.AsSpan())
            : SHA256.HashData(content.ToArray());
        return CryptographicOperations.FixedTimeEquals(actual, expectedBytes);
    }

    private static bool IsTransport(Exception ex, CancellationToken cancellationToken) =>
        ex is HttpRequestException or IOException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested);

    /// <summary>Taşıma hatası özeti; exception mesajı (URL/host içerebilir) kullanılmaz.</summary>
    private static string TransportError(Exception ex) => ex is OperationCanceledException ? "timeout" : "network_error";

    private static bool IsNumeric(string? value) => !string.IsNullOrEmpty(value) && value.Length <= 64 && value.All(char.IsAsciiDigit);

    private static string? Str(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var s) ? s : null;

    private SendResult Logged(Guid messageId, string kind, SendResult result)
    {
        if (result.Outcome == SendOutcome.Sent)
        {
            LogSent(logger, messageId, kind);
        }
        else
        {
            LogSendFailed(logger, messageId, kind, result.Outcome, result.Error ?? "unknown");
        }

        return result;
    }

    private MediaDownloadResult Rejected(MediaDownloadResult result)
    {
        LogMediaFailed(logger, result.Outcome, result.Error ?? "unknown");
        return result;
    }

    private sealed record MediaMetadata(Uri Url, string MimeType, long? FileSize, string? Sha256);

    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,128}$", RegexOptions.CultureInvariant)]
    private static partial Regex MediaIdPattern();

    [LoggerMessage(Level = LogLevel.Information, Message = "WhatsApp Cloud API gönderimi kabul edildi: mesaj {MessageId}, tür {Kind}.")]
    private static partial void LogSent(ILogger logger, Guid messageId, string kind);

    [LoggerMessage(Level = LogLevel.Warning, Message = "WhatsApp Cloud API gönderimi başarısız: mesaj {MessageId}, tür {Kind}, sonuç {Outcome}, hata {Error}.")]
    private static partial void LogSendFailed(ILogger logger, Guid messageId, string kind, SendOutcome outcome, string error);

    [LoggerMessage(Level = LogLevel.Information, Message = "WhatsApp medyası indirildi: tür {ContentType}, {Length} bayt.")]
    private static partial void LogMediaDownloaded(ILogger logger, string contentType, long length);

    [LoggerMessage(Level = LogLevel.Warning, Message = "WhatsApp medyası indirilemedi: sonuç {Outcome}, hata {Error}.")]
    private static partial void LogMediaFailed(ILogger logger, MediaDownloadOutcome outcome, string error);
}
