using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VoiceAssistant.API.Services;

public class ScreenAnalysisIntentService
{
    public const int ParseMaxTokens = 48;

    private static readonly Regex ScreenCandidatePattern = new(
        @"(?:\b(?:что\s+(?:сейчас\s+)?(?:на\s+)?экране?|(?:объясн|покаж|разбер|прочита|опиш)\w*(?:\s+\w+){0,4}\s+экран\w*)\b|\b(?:что|куда)\s+(?:здесь\s+)?нажать(?:\s+\w+){0,5}\s+\b(?:интерфейс\w*|кнопк\w*|экране?)\b|\b(?:read|explain|describe)\s+(?:the\s+)?screen\b)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly GeminiService _gemini;
    private readonly ILogger<ScreenAnalysisIntentService> _logger;

    public ScreenAnalysisIntentService(GeminiService gemini, ILogger<ScreenAnalysisIntentService> logger)
    {
        _gemini = gemini;
        _logger = logger;
    }

    public static bool IsCandidate(string text) => !string.IsNullOrWhiteSpace(text) && ScreenCandidatePattern.IsMatch(text);

    public async Task<bool> IsScreenAnalysisRequestAsync(string text, string? geminiApiKey, CancellationToken cancellationToken)
    {
        if (!IsCandidate(text)) return false;

        const string instruction = """
            Классифицируй единственную реплику пользователя для голосового ассистента.
            Верни только JSON: {"type":"screen_analyze"} или {"type":"chat"}.
            screen_analyze только когда пользователь явно просит показать, объяснить,
            прочитать или подсказать действие по текущему экрану устройства.
            Упоминание слова «экран» в абстрактном разговоре само по себе означает chat.
            """;

        try
        {
            var response = new StringBuilder();
            await foreach (var chunk in _gemini.StreamResponseAsync(
                               "",
                               [new GeminiMessage("user", $"{instruction}\n\nРеплика:\n{text}")],
                               model: "gemini-3.5-flash",
                               maxTokens: ParseMaxTokens,
                               apiKey: geminiApiKey,
                               enableGrounding: false,
                               cancellationToken: cancellationToken))
            {
                response.Append(chunk);
            }

            return IsScreenAnalysisJson(response.ToString());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Screen analysis intent classification failed");
            return false;
        }
    }

    public static bool IsScreenAnalysisJson(string raw)
    {
        var json = raw.Trim();
        if (json.StartsWith("```", StringComparison.Ordinal))
        {
            var firstLineEnd = json.IndexOf('\n');
            var lastFence = json.LastIndexOf("```", StringComparison.Ordinal);
            if (firstLineEnd >= 0 && lastFence > firstLineEnd)
                json = json[(firstLineEnd + 1)..lastFence].Trim();
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("type", out var type) &&
                   type.ValueKind == JsonValueKind.String &&
                   string.Equals(type.GetString(), "screen_analyze", StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
