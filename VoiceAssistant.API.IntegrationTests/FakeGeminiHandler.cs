using System.Net;
using System.Text;
using System.Text.Json;

namespace VoiceAssistant.API.IntegrationTests;

// Stands in for generativelanguage.googleapis.com -- TestWebApplicationFactory
// swaps this in for the unnamed IHttpClientFactory client that
// GeminiService.StreamResponseAsync (and friends) resolve via CreateClient(),
// so integration tests never make a real network call. Returns a canned SSE
// body in Gemini's own streamGenerateContent?alt=sse shape, so GeminiService's
// real parsing code runs unmodified against it.
//
// ChatController.Send() fires up to three concurrent StreamResponseAsync
// calls per turn: the main reply (maxOutputTokens: 2048, the default), a
// "does this need a preamble" check (maxOutputTokens: 30), and a background
// "did the user ask to remember something" check (maxOutputTokens: 200).
// Distinguishing by that field keeps the two background checks harmless
// no-ops (they short-circuit on the literal string "NONE") while still
// exercising the real main-reply path.
public class FakeGeminiHandler : HttpMessageHandler
{
    public const string DefaultReplyText = "Привет! Это тестовый ответ ассистента.";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);

        var replyText = DefaultReplyText;
        if (body.Contains("\"maxOutputTokens\":30", StringComparison.Ordinal) ||
            body.Contains("\"maxOutputTokens\":200", StringComparison.Ordinal))
        {
            replyText = "NONE";
        }

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(BuildSse(replyText), Encoding.UTF8, "text/event-stream")
        };
    }

    private static string BuildSse(string text)
    {
        var payload = JsonSerializer.Serialize(new
        {
            candidates = new[]
            {
                new { content = new { parts = new[] { new { text } } } }
            }
        });
        return $"data: {payload}\n\n";
    }
}
