using System.Net;
using System.Text;
using System.Text.Json;
using VoiceAssistant.API.Controllers;

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
// "does this need a preamble" check (maxOutputTokens: ChatController.PreambleCheckMaxTokens),
// and a background "did the user ask to remember something" check
// (maxOutputTokens: ChatController.CustomInstructionCheckMaxTokens).
// Distinguishing by that field keeps the two background checks harmless
// no-ops (they short-circuit on the literal string "NONE") while still
// exercising the real main-reply path. Referencing ChatController's own
// public consts (PROJECT-AUDIT-2026-07-10 QA-01) instead of repeating the
// literals here means a future change to either number fails to compile
// instead of silently reducing test fidelity.
public class FakeGeminiHandler : HttpMessageHandler
{
    public const string DefaultReplyText = "Привет! Это тестовый ответ ассистента.";

    private static readonly string PreambleCheckMarker = $"\"maxOutputTokens\":{ChatController.PreambleCheckMaxTokens}";
    private static readonly string CustomInstructionCheckMarker = $"\"maxOutputTokens\":{ChatController.CustomInstructionCheckMaxTokens}";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);

        var replyText = DefaultReplyText;
        if (body.Contains(PreambleCheckMarker, StringComparison.Ordinal) ||
            body.Contains(CustomInstructionCheckMarker, StringComparison.Ordinal))
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
