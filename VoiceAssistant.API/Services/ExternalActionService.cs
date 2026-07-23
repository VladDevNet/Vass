using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace VoiceAssistant.API.Services;

public static class ExternalActionTypes
{
    public const string OpenVass = "open_vass";
    public const string YouTubeSearch = "youtube_search";
    public const string YouTubeWatch = "youtube_watch";
    public const string LibraryWrite = "library_write";
    public const string LibraryOpen = "library_open";
    public const string AssistantSleep = "assistant_sleep";
}

// The HTML itself is deliberately carried only in the client action envelope.
// ActionReceipts persist its type/status for diagnostics, never the document.
public sealed record LibraryArtifactAction(
    [property: JsonPropertyName("artifactId")] string? ArtifactId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("html")] string Html,
    [property: JsonPropertyName("sectionTitle")] string? SectionTitle,
    [property: JsonPropertyName("summary")] string? Summary,
    [property: JsonPropertyName("sourceUrls")] IReadOnlyList<string> SourceUrls,
    [property: JsonPropertyName("revisionNote")] string? RevisionNote);

public record ExternalActionCommand(
    string Type,
    string? Query = null,
    string? VideoId = null,
    LibraryArtifactAction? LibraryArtifact = null,
    string? ArtifactId = null);

public class ExternalActionService
{
    public const int ParseMaxTokens = 160;
    public const int MaxQueryLength = 200;

    private static readonly Regex YouTubeVideoIdPattern = new(
        @"^[A-Za-z0-9_-]{11}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex YouTubeUrlPattern = new(
        @"(?:youtube\.com/watch\?(?:[^\s#]*&)?v=|youtu\.be/)(?<id>[A-Za-z0-9_-]{11})(?:[^A-Za-z0-9_-]|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex LaunchFollowUpPattern = new(
        @"\b(запускай|запусти|включай|включи|проигрывай|проиграй|открывай|открой|давай\s+(?:его|это|перв(?:ое|ый)|втор(?:ое|ой))|play\s+it)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex SecondSelectionPattern = new(
        @"\b(втор(?:ое|ой|ую)|номер\s*2|2[- ]?(?:е|й|ю))\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly GeminiService _gemini;
    private readonly ILogger<ExternalActionService> _logger;

    public ExternalActionService(GeminiService gemini, ILogger<ExternalActionService> logger)
    {
        _gemini = gemini;
        _logger = logger;
    }

    public async Task<ExternalActionCommand?> ClassifyAsync(
        string message,
        IReadOnlyList<GeminiMessage> recentConversation,
        string? geminiApiKey,
        CancellationToken cancellationToken)
    {
        var contextualAction = ResolveFromContext(message, recentConversation);
        if (contextualAction is not null) return contextualAction;

        var contextText = string.Join(
            '\n',
            recentConversation.TakeLast(6).Select(item => $"{item.Role}: {item.Content}"));
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
            - если пользователь говорит «запускай», «включай», «открой его» и
              в недавнем контексте есть предложенная YouTube-ссылка, верни её videoId;
            - для обычного разговора всегда chat;
            - не придумывай videoId и не добавляй URL;
            - query содержит только поисковую фразу, без слов «открой», «найди», «YouTube».

            Недавний контекст:
            ---
            {{contextText}}
            ---

            Последнее сообщение пользователя:
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

    public static ExternalActionCommand? ResolveFromContext(
        string message,
        IReadOnlyList<GeminiMessage> recentConversation)
    {
        var directId = ExtractVideoIds(message).FirstOrDefault();
        if (directId is not null)
            return new ExternalActionCommand(ExternalActionTypes.YouTubeWatch, VideoId: directId);

        if (!LaunchFollowUpPattern.IsMatch(message)) return null;

        foreach (var item in recentConversation.Reverse())
        {
            var ids = ExtractVideoIds(item.Content).Distinct(StringComparer.Ordinal).ToList();
            if (ids.Count == 0) continue;
            var index = SecondSelectionPattern.IsMatch(message) && ids.Count > 1 ? 1 : 0;
            return new ExternalActionCommand(ExternalActionTypes.YouTubeWatch, VideoId: ids[index]);
        }

        return null;
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
                ExternalActionTypes.AssistantSleep => new ExternalActionCommand(ExternalActionTypes.AssistantSleep),
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

    private static IEnumerable<string> ExtractVideoIds(string text) =>
        YouTubeUrlPattern.Matches(text).Select(match => match.Groups["id"].Value);
}
