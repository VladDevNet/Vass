using System.Text;
using System.Text.Json;

namespace VoiceAssistant.API.Services;

// A provider-neutral representation of a model proposal. The model may choose
// an operation, but never receives a database, Android API, or arbitrary URL
// handle. AssistantToolBroker validates and executes only this allowlist.
public sealed record AssistantToolCall(string Name, JsonElement Arguments, string? CallId);

public sealed class AssistantToolPlannerService
{
    private const string Model = "gemini-3.5-flash";
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AssistantToolPlannerService> _logger;

    public AssistantToolPlannerService(
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        ILogger<AssistantToolPlannerService> logger)
    {
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<IReadOnlyList<AssistantToolCall>> PlanAsync(
        string systemPrompt,
        IReadOnlyList<GeminiMessage> conversation,
        string? apiKey,
        CancellationToken cancellationToken)
    {
        var key = string.IsNullOrWhiteSpace(apiKey) ? _configuration["Gemini:ApiKey"] : apiKey;
        if (string.IsNullOrWhiteSpace(key)) return [];

        var contents = conversation
            .Where(message => !string.IsNullOrWhiteSpace(message.Content))
            .TakeLast(12)
            .Select(message => new
            {
                role = message.Role.Equals("user", StringComparison.OrdinalIgnoreCase) ? "user" : "model",
                parts = new[] { new { text = message.Content } }
            })
            .ToArray();
        if (contents.Length == 0) return [];

        var payload = new
        {
            contents,
            systemInstruction = new
            {
                parts = new[]
                {
                    new
                    {
                        text = $$"""
                            {{systemPrompt}}

                            Ты управляешь только объявленными инструментами Vass. Вызывай
                            инструмент, когда пользователь явно просит выполнить действие
                            или получить данные: сохранить/найти/исправить память, сделать
                            разовый снимок экрана, развернуть Vass или открыть YouTube.
                            Сам выбирай инструмент по смыслу фразы, а не по отдельным
                            словам. Не вызывай инструмент при обычном разговоре.
                            Никогда не выдумывай результат действия: сервер вернет receipt.
                            """
                    }
                }
            },
            tools = new[] { new { functionDeclarations = GetDeclarations() } },
            toolConfig = new { functionCallingConfig = new { mode = "AUTO" } },
            generationConfig = new { maxOutputTokens = 512, thinkingConfig = new { thinkingBudget = 0 } }
        };

        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{Model}:generateContent?key={key}";
        using var http = _httpClientFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(30);
        try
        {
            var response = await http.PostAsync(
                url,
                new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
                cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Assistant tool planning failed with Gemini status {Status}", response.StatusCode);
                return [];
            }

            return ParseCalls(body);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Assistant tool planning failed");
            return [];
        }
    }

    internal static IReadOnlyList<AssistantToolCall> ParseCalls(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            if (!document.RootElement.TryGetProperty("candidates", out var candidates) ||
                candidates.ValueKind != JsonValueKind.Array || candidates.GetArrayLength() == 0 ||
                !candidates[0].TryGetProperty("content", out var content) ||
                !content.TryGetProperty("parts", out var parts) || parts.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var calls = new List<AssistantToolCall>();
            foreach (var part in parts.EnumerateArray())
            {
                if (!part.TryGetProperty("functionCall", out var call) || call.ValueKind != JsonValueKind.Object ||
                    !call.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String ||
                    !call.TryGetProperty("args", out var args) || args.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                calls.Add(new AssistantToolCall(
                    name.GetString() ?? "",
                    args.Clone(),
                    call.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null));
            }
            return calls;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static object[] GetDeclarations() =>
    [
        new { name = "memory_status", description = "Показывает доступность и количество сохраненных воспоминаний.", parameters = EmptyObjectSchema() },
        new { name = "memory_list", description = "Показывает недавние записи долгосрочной памяти.", parameters = EmptyObjectSchema() },
        new { name = "memory_search", description = "Ищет в долгосрочной памяти по смыслу запроса пользователя.", parameters = ObjectSchema(new { query = StringProperty("Короткий поисковый запрос.") }, ["query"]) },
        new { name = "memory_remember", description = "Сохраняет одну связанную, осмысленную запись в долгосрочную память только по явной просьбе пользователя. Для ссылки сохрани URL полностью.", parameters = ObjectSchema(new { text = StringProperty("Готовая осмысленная запись; не команда и не местоимение.") }, ["text"]) },
        new { name = "memory_correct", description = "Исправляет найденную запись памяти. Сначала получи ID через memory_search или memory_list.", parameters = ObjectSchema(new { memoryId = StringProperty("UUID записи."), text = StringProperty("Новая осмысленная формулировка.") }, ["memoryId", "text"]) },
        new { name = "memory_forget", description = "Удаляет одну конкретную найденную запись памяти. Сначала получи ID через memory_search или memory_list.", parameters = ObjectSchema(new { memoryId = StringProperty("UUID записи.") }, ["memoryId"]) },
        new { name = "screen_capture_once", description = "Запрашивает один снимок текущего экрана только когда пользователь явно попросил сделать, посмотреть или объяснить экран. Android покажет системное подтверждение.", parameters = EmptyObjectSchema() },
        new { name = "open_vass", description = "Разворачивает Vass из overlay в обычное полноэкранное приложение.", parameters = EmptyObjectSchema() },
        new { name = "youtube_search", description = "Открывает поиск YouTube по запросу пользователя.", parameters = ObjectSchema(new { query = StringProperty("Что искать на YouTube, без слов открыть или найти.") }, ["query"]) },
        new { name = "youtube_watch", description = "Открывает конкретное видео YouTube только с переданным пользователем URL/ID из контекста.", parameters = ObjectSchema(new { videoId = StringProperty("Ровно 11 символов YouTube video ID.") }, ["videoId"]) },
    ];

    private static object EmptyObjectSchema() => new { type = "object", properties = new { } };
    private static object StringProperty(string description) => new { type = "string", description };
    private static object ObjectSchema(object properties, string[] required) => new { type = "object", properties, required };
}
