using System.Globalization;
using System.Text.Json;

namespace SokagimdakiSorun.Messaging.TestSupport;

/// <summary>
/// Geliştirici simülatörüyle (<c>tools/whatsapp-simulator/fixtures</c>) <b>aynı</b> Meta Cloud API webhook şablonlarından gövde üretir.
/// Tüm değerler temsilidir. Bu dosya hem SokagimdakiSorun.IntegrationTests hem SokagimdakiSorun.Messaging.Tests tarafından derlenir.
/// </summary>
internal static class WebhookFixtures
{
    public const string PhoneNumberId = "100000000000001";
    public const string AppSecret = "test-app-secret-not-real";
    public const string VerifyToken = "test-verify-token-not-real";

    private static readonly Lazy<string> FixtureDirectory = new(() =>
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "SokagimdakiSorun.slnx")))
            {
                return Path.Combine(dir.FullName, "tools", "whatsapp-simulator", "fixtures");
            }
        }

        throw new InvalidOperationException("Depo kökü (SokagimdakiSorun.slnx) bulunamadı.");
    });

    public static string NewMessageId() => $"wamid.test.{Guid.CreateVersion7():N}";

    public static string Text(string from, string text, string? messageId = null) =>
        Build("text", from, messageId ?? NewMessageId(), text: text);

    public static string Location(string from, double latitude, double longitude, string? messageId = null) =>
        Build("location", from, messageId ?? NewMessageId(), latitude: latitude, longitude: longitude);

    public static string Image(string from, string mediaId, string? messageId = null) =>
        Build("image", from, messageId ?? NewMessageId(), mediaId: mediaId);

    public static string Audio(string from, string? messageId = null) =>
        Build("audio", from, messageId ?? NewMessageId(), mediaId: "audio-1");

    public static string Status(string providerMessageId, string status, string from = "905550000001") =>
        Build("status", from, providerMessageId, status: status);

    public static string Build(
        string type, string from, string messageId, string text = "", double latitude = 0, double longitude = 0,
        string mediaId = "", string status = "delivered", string name = "Temsili Vatandaş", long? timestamp = null)
    {
        var template = File.ReadAllText(Path.Combine(FixtureDirectory.Value, $"{type}.json"));
        return template
            .Replace("{{PHONE_NUMBER_ID}}", PhoneNumberId, StringComparison.Ordinal)
            .Replace("{{FROM}}", from, StringComparison.Ordinal)
            .Replace("{{NAME}}", name, StringComparison.Ordinal)
            .Replace("{{MESSAGE_ID}}", messageId, StringComparison.Ordinal)
            .Replace("{{TIMESTAMP}}", (timestamp ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds()).ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{{TEXT}}", JsonSerializer.Serialize(text), StringComparison.Ordinal)
            .Replace("{{LATITUDE}}", latitude.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{{LONGITUDE}}", longitude.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{{MEDIA_ID}}", mediaId, StringComparison.Ordinal)
            .Replace("{{STATUS}}", status, StringComparison.Ordinal);
    }
}
