using System.Security.Cryptography;
using System.Text;

namespace SokagimdakiSorun.Messaging.Infrastructure.WhatsApp;

/// <summary>
/// Webhook imza doğrulaması (ADR-0003 §4): <c>X-Hub-Signature-256: sha256=&lt;hex&gt;</c>, anahtar uygulama secret'ı, girdi
/// <b>ham gövde baytları</b>. Karşılaştırma sabit zamanlıdır. İmza ve secret hiçbir yere yazılmaz.
/// </summary>
internal static class WebhookSignatureVerifier
{
    public const string HeaderName = "X-Hub-Signature-256";
    private const string Prefix = "sha256=";

    public static bool IsValid(ReadOnlySpan<byte> body, string? signatureHeader, string? appSecret)
    {
        if (string.IsNullOrEmpty(appSecret) || string.IsNullOrWhiteSpace(signatureHeader))
        {
            return false;
        }

        var header = signatureHeader.Trim();
        if (!header.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) || header.Length != Prefix.Length + 64)
        {
            return false;
        }

        byte[] provided;
        try
        {
            provided = Convert.FromHexString(header.AsSpan(Prefix.Length));
        }
        catch (FormatException)
        {
            return false;
        }

        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(appSecret), body);
        return CryptographicOperations.FixedTimeEquals(expected, provided);
    }

    /// <summary>Başlık değerini üretir (geliştirici aracı ve testler için).</summary>
    public static string Sign(ReadOnlySpan<byte> body, string appSecret) =>
        Prefix + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(appSecret), body));

    /// <summary>Sabit zamanlı metin eşitliği (verify token).</summary>
    public static bool SecretEquals(string? provided, string? expected)
    {
        if (string.IsNullOrEmpty(provided) || string.IsNullOrEmpty(expected))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(provided)), SHA256.HashData(Encoding.UTF8.GetBytes(expected)));
    }
}
