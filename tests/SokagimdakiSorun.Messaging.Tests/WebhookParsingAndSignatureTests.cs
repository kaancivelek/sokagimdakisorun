using System.Text;
using SokagimdakiSorun.Messaging.Domain;
using SokagimdakiSorun.Messaging.Infrastructure.WhatsApp;
using SokagimdakiSorun.Messaging.TestSupport;

namespace SokagimdakiSorun.Messaging.Tests;

public sealed class WebhookParsingAndSignatureTests
{
    private const string From = "905550000001";

    [Fact]
    public void Signature_is_accepted_only_for_exact_body_and_secret()
    {
        var body = Encoding.UTF8.GetBytes(WebhookFixtures.Text(From, "merhaba"));
        var header = WebhookSignatureVerifier.Sign(body, WebhookFixtures.AppSecret);

        Assert.True(WebhookSignatureVerifier.IsValid(body, header, WebhookFixtures.AppSecret));
        Assert.True(WebhookSignatureVerifier.IsValid(body, header.ToUpperInvariant().Replace("SHA256=", "sha256=", StringComparison.Ordinal), WebhookFixtures.AppSecret));

        var tampered = (byte[])body.Clone();
        tampered[^3] ^= 0x01;
        Assert.False(WebhookSignatureVerifier.IsValid(tampered, header, WebhookFixtures.AppSecret));
        Assert.False(WebhookSignatureVerifier.IsValid(body, header, "baska-secret"));
        Assert.False(WebhookSignatureVerifier.IsValid(body, null, WebhookFixtures.AppSecret));
        Assert.False(WebhookSignatureVerifier.IsValid(body, "", WebhookFixtures.AppSecret));
        Assert.False(WebhookSignatureVerifier.IsValid(body, header[7..], WebhookFixtures.AppSecret)); // önek yok
        Assert.False(WebhookSignatureVerifier.IsValid(body, "sha256=" + new string('z', 64), WebhookFixtures.AppSecret)); // hex değil
        Assert.False(WebhookSignatureVerifier.IsValid(body, header, null)); // secret yapılandırılmamış
    }

    [Fact]
    public void Verify_token_comparison_requires_configured_exact_value()
    {
        Assert.True(WebhookSignatureVerifier.SecretEquals("abc", "abc"));
        Assert.False(WebhookSignatureVerifier.SecretEquals("abd", "abc"));
        Assert.False(WebhookSignatureVerifier.SecretEquals("abc", null));
        Assert.False(WebhookSignatureVerifier.SecretEquals(null, null));
    }

    [Fact]
    public void Each_message_and_status_becomes_one_event_with_stable_dedup_key()
    {
        var text = WebhookFixtures.Text(From, "merhaba", "wamid.A");
        var drafts = WhatsAppWebhookParser.SplitEvents(Encoding.UTF8.GetBytes(text));
        var draft = Assert.Single(drafts);
        Assert.Equal("msg:wamid.A", draft.DedupKey);
        Assert.Equal(InboxEventType.Message, draft.EventType);

        var status = Assert.Single(WhatsAppWebhookParser.SplitEvents(Encoding.UTF8.GetBytes(WebhookFixtures.Status("wamid.B", "read"))));
        Assert.Equal("status:wamid.B:read", status.DedupKey);
        Assert.Equal(InboxEventType.Status, status.EventType);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("{\"object\":\"x\"}")]
    public void Malformed_body_is_rejected(string body) =>
        Assert.Throws<FormatException>(() => WhatsAppWebhookParser.SplitEvents(Encoding.UTF8.GetBytes(body)));

    [Fact]
    public void Message_types_are_normalized()
    {
        var text = Parse(WebhookFixtures.Text(From, "Çukur var"));
        Assert.Equal((MessageType.Text, "Çukur var"), (text.Kind, text.Text));
        Assert.Equal(WebhookFixtures.PhoneNumberId, text.BusinessPhoneNumberId);
        Assert.Equal(From, text.ContactId);
        Assert.Equal("Temsili Vatandaş", text.ProfileName);
        Assert.NotNull(text.Timestamp);

        var location = Parse(WebhookFixtures.Location(From, 40.4105, 26.6712));
        Assert.Equal((MessageType.Location, 40.4105, 26.6712), (location.Kind, location.Latitude!.Value, location.Longitude!.Value));

        var image = Parse(WebhookFixtures.Image(From, "media-1"));
        Assert.Equal((MessageType.Image, "media-1"), (image.Kind, image.MediaId));

        var audio = Parse(WebhookFixtures.Audio(From));
        Assert.Equal((MessageType.Unsupported, "audio"), (audio.Kind, audio.ProviderType));
    }

    [Fact]
    public void Status_maps_to_delivery_status_and_keeps_only_error_code()
    {
        var payload = Payload(WebhookFixtures.Status("wamid.C", "delivered"));
        var status = WhatsAppWebhookParser.ParseStatus(payload);
        Assert.Equal(("wamid.C", DeliveryStatus.Delivered), (status!.ProviderMessageId, status.Status));

        Assert.Null(WhatsAppWebhookParser.ParseStatus(Payload(WebhookFixtures.Status("wamid.D", "deleted"))));
    }

    private static InboundMessage Parse(string body) => WhatsAppWebhookParser.ParseMessage(Payload(body));

    private static string Payload(string body) => Assert.Single(WhatsAppWebhookParser.SplitEvents(Encoding.UTF8.GetBytes(body))).Payload;
}
