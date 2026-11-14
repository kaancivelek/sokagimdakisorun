using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SokagimdakiSorun.Messaging.TestSupport;

/// <summary>
/// Graph API'nin yerine geçen sahte <see cref="HttpMessageHandler"/> (WS-10). Hiçbir ağ çağrısı yapmaz; istekleri kaydeder ve
/// resmî doküman şekline uygun yanıtlar üretir. Sıradaki yanıtlar <see cref="Enqueue"/> ile senaryolaştırılır; kuyruk boşken
/// <c>POST …/messages</c> kabul edilir, kayıtlı medya meta verisi ve içeriği döner, diğer istekler 404'tür.
/// <b>Güven sınırı:</b> bu sahte, gerçek Meta davranışını kanıtlamaz.
/// </summary>
internal sealed class FakeGraphHandler : HttpMessageHandler
{
    public const string AccessToken = "test-access-token-not-real";
    public const string GraphVersion = "v27.0";
    public const string MediaHost = "https://media.example.test/";

    private readonly ConcurrentQueue<Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> _scripted = new();
    private readonly ConcurrentDictionary<string, (byte[] Content, string MimeType, bool IncludeHash, long? DeclaredSize)> _media = new();
    private readonly ConcurrentQueue<RecordedRequest> _requests = new();
    private int _sequence;

    public IReadOnlyCollection<RecordedRequest> Requests => _requests;

    public IEnumerable<RecordedRequest> MessageRequests => _requests.Where(r => r.Uri.AbsolutePath.EndsWith("/messages", StringComparison.Ordinal));

    /// <summary>Sıradaki istek(ler)in yanıtını belirler.</summary>
    public void Enqueue(params Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>[] responses)
    {
        foreach (var response in responses)
        {
            _scripted.Enqueue(response);
        }
    }

    /// <summary>Medya kimliği için meta veri + indirilebilir içerik kaydeder (indirme URL'si <see cref="MediaUrl"/>).</summary>
    public void RegisterMedia(string mediaId, byte[] content, string mimeType, bool includeHash = true, long? declaredSize = null) =>
        _media[mediaId] = (content, mimeType, includeHash, declaredSize);

    public static Uri MediaUrl(string mediaId) => new($"{MediaHost}download/{mediaId}?token-like=signed-value");

    public static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Accepted(string providerMessageId) =>
        (_, _) => Task.FromResult(Json(HttpStatusCode.OK, new JsonObject
        {
            ["messaging_product"] = "whatsapp",
            ["contacts"] = new JsonArray { new JsonObject { ["input"] = "x", ["wa_id"] = "x" } },
            ["messages"] = new JsonArray { new JsonObject { ["id"] = providerMessageId, ["message_status"] = "accepted" } },
        }.ToJsonString()));

    public static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> GraphError(HttpStatusCode status, int code) =>
        (_, _) => Task.FromResult(Json(status, new JsonObject
        {
            ["error"] = new JsonObject
            {
                ["message"] = $"(#{code}) test error",
                ["type"] = "OAuthException",
                ["code"] = code,
                ["error_data"] = new JsonObject { ["messaging_product"] = "whatsapp", ["details"] = "test details" },
                ["fbtrace_id"] = "trace",
            },
        }.ToJsonString()));

    public static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Status(HttpStatusCode status, string body = "") =>
        (_, _) => Task.FromResult(Json(status, body));

    public static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> NetworkFailure() =>
        (_, _) => throw new HttpRequestException("simulated connection failure to " + MediaHost);

    public static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Hang() =>
        async (_, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("unreachable");
        };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        _requests.Enqueue(new RecordedRequest(
            request.Method, request.RequestUri!, request.Headers.Authorization?.ToString(), request.Content?.Headers.ContentType?.MediaType, body));

        if (_scripted.TryDequeue(out var scripted))
        {
            return await scripted(request, cancellationToken);
        }

        var uri = request.RequestUri!;
        if (request.Method == HttpMethod.Post && uri.AbsolutePath.EndsWith("/messages", StringComparison.Ordinal))
        {
            return await Accepted($"wamid.fake.{Interlocked.Increment(ref _sequence)}.{Guid.CreateVersion7():N}")(request, cancellationToken);
        }

        if (request.Method == HttpMethod.Get && uri.Host == "graph.facebook.com")
        {
            var mediaId = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Last();
            if (_media.TryGetValue(mediaId, out var media))
            {
                var metadata = new JsonObject
                {
                    ["messaging_product"] = "whatsapp",
                    ["url"] = MediaUrl(mediaId).ToString(),
                    ["mime_type"] = media.MimeType,
                    ["file_size"] = media.DeclaredSize ?? media.Content.Length,
                    ["id"] = mediaId,
                };
                if (media.IncludeHash)
                {
                    metadata["sha256"] = Convert.ToHexStringLower(SHA256.HashData(media.Content));
                }

                return Json(HttpStatusCode.OK, metadata.ToJsonString());
            }
        }

        if (request.Method == HttpMethod.Get && uri.ToString().StartsWith(MediaHost, StringComparison.Ordinal))
        {
            var mediaId = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Last();
            if (_media.TryGetValue(mediaId, out var media))
            {
                var content = new ByteArrayContent(media.Content);
                content.Headers.ContentType = MediaTypeHeaderValue.Parse(media.MimeType);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            }
        }

        return Json(HttpStatusCode.NotFound, """{"error":{"message":"not found","code":100}}""");
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>Kaydedilmiş istek. <see cref="Json"/> gövdeyi ayrıştırır.</summary>
    internal sealed record RecordedRequest(HttpMethod Method, Uri Uri, string? Authorization, string? ContentType, string? Body)
    {
        public JsonElement Json => JsonDocument.Parse(Body!).RootElement;
    }
}
