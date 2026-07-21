using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using VoiceAssistant.API.Controllers;
using VoiceAssistant.API.Services;

namespace VoiceAssistant.API.IntegrationTests;

// Stands in for generativelanguage.googleapis.com -- TestWebApplicationFactory
// swaps this in for the unnamed IHttpClientFactory client that
// GeminiService.StreamResponseAsync (and friends) resolve via CreateClient(),
// so integration tests never make a real network call. Returns a canned SSE
// body in Gemini's own streamGenerateContent?alt=sse shape, so GeminiService's
// real parsing code runs unmodified against it.
//
// ChatController.Send() fires up to three concurrent StreamResponseAsync
// calls per turn: the main reply (maxOutputTokens: 8192), a
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
    private static readonly string ReminderParseMarker = $"\"maxOutputTokens\":{ReminderService.ParseMaxTokens}";
    private static readonly string ExternalActionParseMarker = $"\"maxOutputTokens\":{ExternalActionService.ParseMaxTokens}";
    private static readonly string ScreenAnalysisParseMarker = $"\"maxOutputTokens\":{ScreenAnalysisIntentService.ParseMaxTokens}";
    private static readonly string ExplicitMemoryNormalizeMarker = $"\"maxOutputTokens\":{LongTermMemoryService.ExplicitMemoryNormalizeMaxTokens}";
    private static readonly Regex UrlPattern = new(@"https?://[^\s<>()]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);

        var replyText = DefaultReplyText;
        if (HasFunctionResponse(body))
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(BuildToolContinuation(body), Encoding.UTF8, "application/json")
            };
        }
        else if (body.Contains("\"functionDeclarations\"", StringComparison.Ordinal))
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(BuildToolPlan(ReadAllText(body)), Encoding.UTF8, "application/json")
            };
        }
        else if (body.Contains(ScreenAnalysisParseMarker, StringComparison.Ordinal))
        {
            replyText = ReadPromptText(body).Contains("объясни", StringComparison.OrdinalIgnoreCase)
                ? "{\"type\":\"screen_analyze\"}"
                : "{\"type\":\"chat\"}";
        }
        else if (body.Contains(ExternalActionParseMarker, StringComparison.Ordinal))
        {
            replyText = ReadPromptText(body).Contains("Высоцкого", StringComparison.Ordinal)
                ? "{\"type\":\"youtube_search\",\"query\":\"песни Высоцкого\",\"videoId\":null}"
                : "{\"type\":\"chat\",\"query\":null,\"videoId\":null}";
        }
        else if (body.Contains(ReminderParseMarker, StringComparison.Ordinal))
        {
            replyText = "{\"isReminder\":true,\"needsClarification\":false,\"text\":\"позвонить врачу\",\"dueAtLocal\":\"2030-01-01T09:00:00\"}";
        }
        else if (body.Contains(PreambleCheckMarker, StringComparison.Ordinal) ||
            body.Contains(CustomInstructionCheckMarker, StringComparison.Ordinal))
        {
            replyText = "NONE";
        }
        else if (body.Contains(ExplicitMemoryNormalizeMarker, StringComparison.Ordinal))
        {
            var fact = ReadExplicitMemorySource(ReadPromptText(body));
            replyText = fact.Contains("космонавтом", StringComparison.OrdinalIgnoreCase)
                ? "{\"facts\":[\"Пользователь хочет стать космонавтом\"]}"
                : fact.Contains("возраст Белла", StringComparison.OrdinalIgnoreCase)
                    ? "{\"facts\":[\"Беллу 15 лет\"]}"
                    : UrlPattern.Match(fact) is { Success: true } url
                        ? $"{{\"facts\":[\"{url.Value}\"]}}"
                        : "{\"facts\":[]}";
        }
        else if (ReadAllText(body).Contains("Подбери конкретный ролик", StringComparison.Ordinal))
        {
            replyText = "Нашёл подходящий ролик: https://www.youtube.com/watch?v=bWGXT5wjkd4";
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

    private static string BuildToolPlan(string content)
    {
        var url = UrlPattern.Match(content).Value;
        if (content.Contains("истори", StringComparison.OrdinalIgnoreCase) &&
            content.Contains("космонав", StringComparison.OrdinalIgnoreCase))
            return BuildFunctionCall("conversation_search", new { query = "космонавт" });
        if (content.Contains("сначала найди", StringComparison.OrdinalIgnoreCase) &&
            content.Contains("космонав", StringComparison.OrdinalIgnoreCase))
            return BuildFunctionCall("memory_search", new { query = "космонавт" });
        if (content.Contains("экране", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("screen", StringComparison.OrdinalIgnoreCase))
            return BuildFunctionCall("screen_capture_once", new { });
        if (content.Contains("без downgrade", StringComparison.OrdinalIgnoreCase))
            return BuildFunctionCall("periodic_reminder_create", new
            {
                text = "принять витамин D",
                startAtLocal = "2030-01-01T09:00:00",
                rrule = "FREQ=DAILY;INTERVAL=2"
            });
        if (content.Contains("каждый день", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("периодичес", StringComparison.OrdinalIgnoreCase))
            return BuildFunctionCall("periodic_reminder_create", new
            {
                text = "принять витамин D",
                startAtLocal = "2030-01-01T09:00:00",
                rrule = "FREQ=DAILY"
            });
        if (content.Contains("напомни", StringComparison.OrdinalIgnoreCase))
            return BuildFunctionCall("reminder_create", new { text = "позвонить врачу", dueAtLocal = "2030-01-01T09:00:00" });
        if (content.Contains("космонав", StringComparison.OrdinalIgnoreCase))
            return BuildFunctionCall("memory_remember", new { text = "Пользователь хочет стать космонавтом" });
        if (content.Contains("возраст Белла", StringComparison.OrdinalIgnoreCase))
            return BuildFunctionCall("memory_remember", new { text = "Беллу 15 лет" });
        if ((content.Contains("запомни", StringComparison.OrdinalIgnoreCase) ||
             content.Contains("запиши", StringComparison.OrdinalIgnoreCase) ||
             content.Contains("сохрани", StringComparison.OrdinalIgnoreCase) ||
             content.Contains("положить", StringComparison.OrdinalIgnoreCase)) && !string.IsNullOrWhiteSpace(url))
            return BuildFunctionCall("memory_remember", new { text = $"Сохраненная ссылка на YouTube: {url}" });
        if (content.Contains("Высоцкого", StringComparison.OrdinalIgnoreCase))
            return BuildFunctionCall("youtube_search", new { query = "песни Высоцкого" });
        if (content.Contains("Запускай", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(url))
            return BuildFunctionCall("youtube_watch", new { videoId = "bWGXT5wjkd4" });
        if (content.Contains("запусти это видео", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(url))
        {
            var id = UrlPattern.Match(url).Value.Contains("bWGXT5wjkd4", StringComparison.Ordinal)
                ? "bWGXT5wjkd4"
                : "bWGXT5wjkd4";
            return BuildFunctionCall("youtube_watch", new { videoId = id });
        }
        return "{}";
    }

    private static string BuildToolContinuation(string body)
    {
        if (HasFunctionResponse(body, "memory_remember"))
        {
            var text = ReadAllText(body).Contains("космонав", StringComparison.OrdinalIgnoreCase)
                ? "Сохранила в долгосрочную память: Пользователь хочет стать космонавтом."
                : ReadAllText(body).Contains("Белла", StringComparison.OrdinalIgnoreCase)
                    ? "Сохранила в долгосрочную память: Беллу 15 лет."
                    : "Сохранила подтвержденную запись в долгосрочную память.";
            return BuildModelText(text);
        }

        if (HasFunctionResponse(body, "memory_search"))
            return BuildFunctionCall("memory_remember", new { text = "Пользователь хочет стать космонавтом" });

        if (HasFunctionResponse(body, "conversation_search"))
            return BuildFunctionCall("memory_remember", new { text = "Пользователь хочет стать космонавтом" });

        if (HasFunctionResponse(body, "reminder_create"))
            return BuildModelText("Передала напоминание телефону и жду его подтверждения.");
        if (HasFunctionResponse(body, "periodic_reminder_create") &&
            ReadAllText(body).Contains("без downgrade", StringComparison.OrdinalIgnoreCase))
            return BuildFunctionCall("reminder_create", new { text = "принять витамин D", dueAtLocal = "2030-01-01T09:00:00" });
        if (HasFunctionResponse(body, "periodic_reminder_create"))
            return BuildModelText("Передала периодическое напоминание телефону и жду его подтверждения.");

        if (HasFunctionResponse(body, "youtube_watch"))
            return BuildModelText("Открываю выбранное видео в YouTube.");
        if (HasFunctionResponse(body, "youtube_search"))
            return BuildModelText("Открываю поиск в YouTube.");
        if (HasFunctionResponse(body, "open_vass"))
            return BuildModelText("Возвращаюсь в Vass.");
        if (HasFunctionResponse(body, "memory_status") || HasFunctionResponse(body, "memory_list"))
            return BuildModelText("Проверила долгосрочную память.");

        return BuildModelText("Инструмент выполнился, результат получен.");
    }

    private static string BuildFunctionCall(string name, object args)
    {
        var payload = JsonSerializer.Serialize(new
        {
            candidates = new[]
            {
                new
                {
                    content = new
                    {
                        role = "model",
                        parts = new[] { new { functionCall = new { name, args, id = "test-call" } } }
                    }
                }
            }
        });
        return payload;
    }

    private static string BuildModelText(string text)
    {
        return JsonSerializer.Serialize(new
        {
            candidates = new[]
            {
                new
                {
                    content = new
                    {
                        role = "model",
                        parts = new[] { new { text } }
                    }
                }
            }
        });
    }

    private static bool HasFunctionResponse(string body, string? expectedName = null)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("contents", out var contents) || contents.ValueKind != JsonValueKind.Array)
                return false;

            foreach (var content in contents.EnumerateArray())
            {
                if (!content.TryGetProperty("parts", out var parts) || parts.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var part in parts.EnumerateArray())
                {
                    if (!part.TryGetProperty("functionResponse", out var response) || response.ValueKind != JsonValueKind.Object)
                        continue;
                    if (expectedName is null)
                        return true;
                    if (response.TryGetProperty("name", out var name) &&
                        name.ValueKind == JsonValueKind.String &&
                        name.GetString() == expectedName &&
                        response.TryGetProperty("id", out var id) &&
                        id.ValueKind == JsonValueKind.String &&
                        id.GetString() == "test-call")
                        return true;
                }
            }
        }
        catch (JsonException)
        {
            return false;
        }

        return false;
    }

    private static string ReadPromptText(string body)
    {
        using var document = JsonDocument.Parse(body);
        return document.RootElement
            .GetProperty("contents")[0]
            .GetProperty("parts")[0]
            .GetProperty("text")
            .GetString() ?? "";
    }

    private static string ReadAllText(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("contents", out var contents)) return "";
            var text = new List<string>();
            foreach (var content in contents.EnumerateArray())
            {
                foreach (var part in content.GetProperty("parts").EnumerateArray())
                {
                    if (part.TryGetProperty("text", out var value)) text.Add(value.GetString() ?? "");
                }
            }
            return string.Join('\n', text);
        }
        catch (JsonException)
        {
            return "";
        }
    }

    private static string ReadExplicitMemorySource(string prompt)
    {
        const string marker = "Исходная информация:";
        var index = prompt.IndexOf(marker, StringComparison.Ordinal);
        return index < 0 ? prompt : prompt[(index + marker.Length)..].Trim();
    }
}
