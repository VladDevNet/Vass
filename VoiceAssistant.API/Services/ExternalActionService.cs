using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VoiceAssistant.API.Services;

public static class ExternalActionTypes
{
    public const string OpenVass = "open_vass";
    public const string YouTubeSearch = "youtube_search";
    public const string YouTubeWatch = "youtube_watch";
}

public record ExternalActionCommand(string Type, string? Query = null, string? VideoId = null);

public class ExternalActionService
{
    public const int ParseMaxTokens = 160;
    public const int MaxQueryLength = 200;

    private static readonly Regex YouTubeVideoIdPattern = new(
        @"^[A-Za-z0-9_-]{11}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly GeminiService _gemini;
    private readonly ILogger<ExternalActionService> _logger;

    public ExternalActionService(GeminiService gemini, ILogger<ExternalActionService> logger)
    {
        _gemini = gemini;
        _logger = logger;
    }

    public async Task<ExternalActionCommand?> ClassifyAsync(
        string message,
        string? geminiApiKey,
        CancellationToken cancellationToken)
    {
        var prompt = $$"""
            Классифицируй команду пользователя для мобильного голосового ассистента.
            Верни только JSON одного из видов:
            {"type":"chat","query":null,"videoId":null}
            {"type":"open_vass","query":null,"videoId":null}
            {"type":"youtube_search","query":"что искать","videoId":null}
            {"type":"youtube_watch","query":null,"videoId":"ровно 11 символов"}

            Правила:
            - open_vass: пользователь просит вернуться, развернуть или открыть Vass полностью;
            - youtube_search: пользователь просит найти, открыть или включить видео/музыку в YouTube;
            - youtube_watch: только если в сообщении уже есть конкретная YouTube-ссылка или video id;
            - для обычного разговора всегда chat;
            - не придумывай videoId и не добавляй URL;
            - query содержит только поисковую фразу, без слов «открой», «найди», «YouTube».

            Сообщение пользователя:
            ---
            {{message}}
            ---
            """;

        try
        {
            var raw = new StringBuilder();
            await foreach (var chunk in _gemini.StreamResponseAsync(
                               "",
                               [new GeminiMessage("user", prompt)],
                               model: "gemini-3.5-flash",
                               maxTokens: ParseMaxTokens,
                               apiKey: geminiApiKey,
                               enableGrounding: false,
                               cancellationToken: cancellationToken))
            {
                raw.Append(chunk);
            }

            return Parse(raw.ToString());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "External action classification failed");
            return null;
        }
    }

    public static ExternalActionCommand? Parse(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
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
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var typeElement) || typeElement.ValueKind != JsonValueKind.String)
                return null;

            var type = typeElement.GetString()?.Trim().ToLowerInvariant();
            var query = ReadString(root, "query");
            var videoId = ReadString(root, "videoId");

            return type switch
            {
                ExternalActionTypes.OpenVass => new ExternalActionCommand(ExternalActionTypes.OpenVass),
                ExternalActionTypes.YouTubeSearch => NormalizeSearch(query),
                ExternalActionTypes.YouTubeWatch or "youtube_play" => NormalizeWatch(videoId, query),
                _ => null
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ExternalActionCommand? NormalizeSearch(string? query)
    {
        query = NormalizeQuery(query);
        return query is null ? null : new ExternalActionCommand(ExternalActionTypes.YouTubeSearch, Query: query);
    }

    private static ExternalActionCommand? NormalizeWatch(string? videoId, string? fallbackQuery)
    {
        videoId = videoId?.Trim();
        if (!string.IsNullOrEmpty(videoId) && YouTubeVideoIdPattern.IsMatch(videoId))
            return new ExternalActionCommand(ExternalActionTypes.YouTubeWatch, VideoId: videoId);

        return NormalizeSearch(fallbackQuery);
    }

    private static string? NormalizeQuery(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;
        var normalized = Regex.Replace(query.Trim(), @"\s+", " ");
        return normalized.Length <= MaxQueryLength ? normalized : null;
    }

    private static string? ReadString(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;
}
