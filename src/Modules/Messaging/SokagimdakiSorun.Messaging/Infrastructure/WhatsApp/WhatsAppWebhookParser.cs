using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using SokagimdakiSorun.Messaging.Domain;

namespace SokagimdakiSorun.Messaging.Infrastructure.WhatsApp;

/// <summary>Webhook kabulünde olay başına çıkarılan inbox satırı taslağı.</summary>
internal sealed record InboxEventDraft(string DedupKey, InboxEventType EventType, string Payload);

/// <summary>Sağlayıcıdan bağımsız gelen mesaj (uygulama katmanının gördüğü şekil).</summary>
internal sealed record InboundMessage(
    string BusinessPhoneNumberId,
    string ContactId,
    string? ProfileName,
    string ProviderMessageId,
    DateTimeOffset? Timestamp,
    MessageType Kind,
    string ProviderType,
    string? Text,
    double? Latitude,
    double? Longitude,
    string? MediaId);

/// <summary>Sağlayıcıdan bağımsız teslim durumu olayı.</summary>
internal sealed record StatusUpdate(string ProviderMessageId, DeliveryStatus Status, DateTimeOffset? Timestamp, string? Error);

/// <summary>
/// Meta WhatsApp Cloud API webhook şekline dayalı ayrıştırıcı. Payload şekli ve mesaj tipleri mock fixture'larından alınmıştır;
/// gerçek Meta davranışı WS-10'da resmî dokümandan doğrulanacaktır (V-8). Bu sınıf dışında Meta JSON'u bilinmez.
/// </summary>
internal static class WhatsAppWebhookParser
{
    /// <summary>
    /// İmzası doğrulanmış gövdeyi olay başına taslaklara ayırır (DOMAIN_MODEL §3.3). <c>messages</c> alanı dışındaki değişiklikler
    /// ve tanınmayan öğeler atlanır. Gövde JSON değilse veya beklenen kök yoksa <see cref="FormatException"/>.
    /// </summary>
    public static IReadOnlyList<InboxEventDraft> SplitEvents(ReadOnlySpan<byte> body)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new FormatException("Webhook gövdesi geçerli JSON değil.", ex);
        }

        if (root is not JsonObject rootObject || rootObject["entry"] is not JsonArray entries)
        {
            throw new FormatException("Webhook gövdesinde 'entry' yok.");
        }

        var drafts = new List<InboxEventDraft>();
        foreach (var change in entries.OfType<JsonObject>().SelectMany(e => (e["changes"] as JsonArray)?.OfType<JsonObject>() ?? []))
        {
            if (Str(change["field"]) != "messages" || change["value"] is not JsonObject value)
            {
                continue;
            }

            var metadata = value["metadata"]?.DeepClone();
            var contacts = (value["contacts"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];

            foreach (var message in (value["messages"] as JsonArray)?.OfType<JsonObject>() ?? [])
            {
                var id = Str(message["id"]);
                if (string.IsNullOrWhiteSpace(id) || id.Length > Message.ProviderIdMaxLength)
                {
                    continue;
                }

                var from = Str(message["from"]);
                var contact = contacts.FirstOrDefault(c => Str(c["wa_id"]) == from) ?? contacts.FirstOrDefault();
                var payload = new JsonObject
                {
                    ["metadata"] = metadata?.DeepClone(),
                    ["contact"] = contact?.DeepClone(),
                    ["message"] = message.DeepClone(),
                };
                drafts.Add(new InboxEventDraft($"msg:{id}", InboxEventType.Message, payload.ToJsonString()));
            }

            foreach (var status in (value["statuses"] as JsonArray)?.OfType<JsonObject>() ?? [])
            {
                var id = Str(status["id"]);
                var state = Str(status["status"]);
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(state) || id.Length + state.Length > 180)
                {
                    continue;
                }

                var payload = new JsonObject { ["metadata"] = metadata?.DeepClone(), ["status"] = status.DeepClone() };
                drafts.Add(new InboxEventDraft($"status:{id}:{state}", InboxEventType.Status, payload.ToJsonString()));
            }
        }

        return drafts;
    }

    /// <summary>Inbox'taki mesaj olayını normalize eder; eksik zorunlu alan varsa <see cref="FormatException"/>.</summary>
    public static InboundMessage ParseMessage(string payload)
    {
        var root = JsonNode.Parse(payload) as JsonObject ?? throw new FormatException("Olay yükü nesne değil.");
        var message = root["message"] as JsonObject ?? throw new FormatException("Olay yükünde 'message' yok.");
        var businessId = Str(root["metadata"]?["phone_number_id"]);
        var from = Str(message["from"]) ?? Str(root["contact"]?["wa_id"]);
        var id = Str(message["id"]);
        if (string.IsNullOrWhiteSpace(businessId) || string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(id)
            || businessId.Length > Conversation.ExternalIdMaxLength || from.Length > Conversation.ExternalIdMaxLength)
        {
            throw new FormatException("Mesaj olayında kimlik alanları eksik veya geçersiz.");
        }

        var type = Str(message["type"]) ?? "unknown";
        var profileName = Str(root["contact"]?["profile"]?["name"]);
        var timestamp = UnixTime(message["timestamp"]);

        return type switch
        {
            "text" => Build(MessageType.Text, Str(message["text"]?["body"])),
            "location" when Dbl(message["location"]?["latitude"]) is { } lat && Dbl(message["location"]?["longitude"]) is { } lon =>
                Build(MessageType.Location, Str(message["location"]?["address"]) ?? Str(message["location"]?["name"]), lat, lon),
            "image" when Str(message["image"]?["id"]) is { Length: > 0 } mediaId =>
                Build(MessageType.Image, Str(message["image"]?["caption"]), mediaId: mediaId),
            "interactive" => Build(MessageType.Text,
                Str(message["interactive"]?["button_reply"]?["title"]) ?? Str(message["interactive"]?["list_reply"]?["title"])),
            "button" => Build(MessageType.Text, Str(message["button"]?["text"])),
            _ => Build(MessageType.Unsupported, null),
        };

        InboundMessage Build(MessageType kind, string? text, double? lat = null, double? lon = null, string? mediaId = null) =>
            new(businessId, from, profileName, id, timestamp, kind, type, text, lat, lon, mediaId);
    }

    /// <summary>Inbox'taki durum olayını normalize eder. Tanınmayan durum için <see langword="null"/>.</summary>
    public static StatusUpdate? ParseStatus(string payload)
    {
        var status = (JsonNode.Parse(payload) as JsonObject)?["status"] as JsonObject
            ?? throw new FormatException("Olay yükünde 'status' yok.");
        var id = Str(status["id"]) ?? throw new FormatException("Durum olayında kimlik yok.");
        DeliveryStatus? mapped = Str(status["status"]) switch
        {
            "sent" => DeliveryStatus.Sent,
            "delivered" => DeliveryStatus.Delivered,
            "read" => DeliveryStatus.Read,
            "failed" => DeliveryStatus.Failed,
            _ => null,
        };
        if (mapped is null)
        {
            return null;
        }

        // Hata kodu dışında (başlık/ayrıntı kişisel veri içerebilir) bir şey saklanmaz.
        var code = (status["errors"] as JsonArray)?.OfType<JsonObject>().Select(e => e["code"]?.ToString()).FirstOrDefault();
        return new StatusUpdate(id, mapped.Value, UnixTime(status["timestamp"]), code is null ? null : $"provider_error:{code}");
    }

    private static string? Str(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var s) ? s : null;

    private static double? Dbl(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<double>(out var d))
        {
            return d;
        }

        return value.TryGetValue<string>(out var s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? d : null;
    }

    private static DateTimeOffset? UnixTime(JsonNode? node)
    {
        var text = node is JsonValue value && value.TryGetValue<long>(out var n) ? n.ToString(CultureInfo.InvariantCulture) : Str(node);
        return long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && seconds is > 0 and < 32503680000
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;
    }
}
