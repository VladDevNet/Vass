using System.Text;
using System.Text.Json;

namespace VoiceAssistant.API.Services;

// A provider-neutral representation of a model proposal. The model may choose
// an operation, but never receives a database, Android API, or arbitrary URL
// handle. AssistantToolBroker validates and executes only this allowlist.
public sealed record AssistantToolCall(string Name, JsonElement Arguments, string? CallId);

// ModelContent is the exact candidate content returned by Gemini. It must be
// appended unchanged before a functionResponse so Gemini can correlate the
// response with its call and retain any provider-owned thought signature.
public sealed record AssistantToolModelResponse(
    JsonElement? ModelContent,
    IReadOnlyList<AssistantToolCall> Calls,
    string? Text,
    bool ProviderAvailable)
{
    public bool HasModelContent => ModelContent is not null;

    public static AssistantToolModelResponse Unavailable() => new(null, [], null, false);
    public static AssistantToolModelResponse Empty() => new(null, [], null, true);
}

// Raw Gemini function-calling transport. It deliberately does not execute a
// function and does not infer intent from text; the agent turn owns the loop.
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

    public async Task<AssistantToolModelResponse> GenerateAsync(
        string systemPrompt,
        IReadOnlyList<JsonElement> contents,
        string? apiKey,
        CancellationToken cancellationToken)
    {
        var key = string.IsNullOrWhiteSpace(apiKey) ? _configuration["Gemini:ApiKey"] : apiKey;
        if (string.IsNullOrWhiteSpace(key) || contents.Count == 0)
            return AssistantToolModelResponse.Unavailable();

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
                            Когда сервер вернёт результат инструмента, используй именно его:
                            можешь вызвать следующий необходимый инструмент либо дай короткий
                            естественный ответ пользователю. Никогда не выдумывай receipt.
                            """
                    }
                }
            },
            tools = new object[] { new { functionDeclarations = GetDeclarations() } },
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
                _logger.LogWarning("Assistant tool generation failed with Gemini status {Status}", response.StatusCode);
                return AssistantToolModelResponse.Unavailable();
            }

            return ParseResponse(body);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Assistant tool generation failed");
            return AssistantToolModelResponse.Unavailable();
        }
    }

    internal static AssistantToolModelResponse ParseResponse(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            if (!document.RootElement.TryGetProperty("candidates", out var candidates) ||
                candidates.ValueKind != JsonValueKind.Array || candidates.GetArrayLength() == 0 ||
                !candidates[0].TryGetProperty("content", out var content) ||
                content.ValueKind != JsonValueKind.Object ||
                !content.TryGetProperty("parts", out var parts) || parts.ValueKind != JsonValueKind.Array)
            {
                return AssistantToolModelResponse.Empty();
            }

            var calls = new List<AssistantToolCall>();
            var visibleText = new StringBuilder();
            foreach (var part in parts.EnumerateArray())
            {
                if (part.TryGetProperty("functionCall", out var call) && call.ValueKind == JsonValueKind.Object &&
                    call.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                {
                    var arguments = call.TryGetProperty("args", out var args) && args.ValueKind == JsonValueKind.Object
                        ? args.Clone()
                        : JsonSerializer.SerializeToElement(new { });
                    calls.Add(new AssistantToolCall(
                        name.GetString() ?? "",
                        arguments,
                        call.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null));
                    continue;
                }

                if ((part.TryGetProperty("thought", out var thought) && thought.ValueKind == JsonValueKind.True) ||
                    !part.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                visibleText.Append(text.GetString());
            }

            return new AssistantToolModelResponse(
                content.Clone(),
                calls,
                visibleText.Length == 0 ? null : visibleText.ToString(),
                ProviderAvailable: true);
        }
        catch (JsonException)
        {
            return AssistantToolModelResponse.Empty();
        }
    }

    private static object[] GetDeclarations() =>
    [
        new { name = "memory_status", description = "Показывает доступность и количество сохраненных воспоминаний.", parameters = EmptyObjectSchema() },
        new { name = "memory_list", description = "Показывает недавние записи долгосрочной памяти.", parameters = EmptyObjectSchema() },
        new { name = "memory_search", description = "Ищет в долгосрочной памяти по смыслу запроса пользователя.", parameters = ObjectSchema(new { query = StringProperty("Короткий поисковый запрос.") }, ["query"]) },
        new { name = "conversation_search", description = "Ищет в собственной прошлой истории диалогов пользователя и возвращает короткие фрагменты с датой и message ID. Используй, когда пользователь явно просит вспомнить прежний разговор. query и диапазон дат можно сочетать; для «вчера/позавчера» передай fromDateLocal и toDateLocal как включительные даты yyyy-MM-dd, вычисленные из текущего локального времени. Если предмет поиска и дата неясны, сначала задай уточняющий вопрос.", parameters = ObjectSchema(new { query = StringProperty("Необязательные ключевые слова или короткий смысл прежнего разговора."), fromDateLocal = StringProperty("Необязательная начальная локальная дата yyyy-MM-dd, включительно."), toDateLocal = StringProperty("Необязательная конечная локальная дата yyyy-MM-dd, включительно.") }, []) },
        new { name = "memory_remember", description = "Сохраняет одну связанную, осмысленную запись в долгосрочную память только по явной просьбе пользователя. Для ссылки сохрани URL полностью.", parameters = ObjectSchema(new { text = StringProperty("Готовая осмысленная запись; не команда и не местоимение.") }, ["text"]) },
        new { name = "memory_correct", description = "Исправляет найденную запись памяти. Сначала получи ID через memory_search или memory_list.", parameters = ObjectSchema(new { memoryId = StringProperty("UUID записи."), text = StringProperty("Новая осмысленная формулировка.") }, ["memoryId", "text"]) },
        new { name = "memory_forget", description = "Удаляет одну конкретную найденную запись памяти. Сначала получи ID через memory_search или memory_list.", parameters = ObjectSchema(new { memoryId = StringProperty("UUID записи.") }, ["memoryId"]) },
        new { name = "reminder_create", description = "Создает одно локальное напоминание только по явной просьбе пользователя. Передавай короткий текст задачи и точный будущий local date-time в ISO формате yyyy-MM-ddTHH:mm:ss. Если времени недостаточно, сначала задай уточняющий вопрос, а не вызывай инструмент.", parameters = ObjectSchema(new { text = StringProperty("Короткая задача без слов 'напомни мне'."), dueAtLocal = StringProperty("Будущий локальный момент в формате yyyy-MM-ddTHH:mm:ss.") }, ["text", "dueAtLocal"]) },
        new { name = "screen_capture_once", description = "Запрашивает один снимок текущего экрана только когда пользователь явно попросил сделать, посмотреть или объяснить экран. Android покажет системное подтверждение.", parameters = EmptyObjectSchema() },
        new { name = "open_vass", description = "Разворачивает Vass из overlay в обычное полноэкранное приложение.", parameters = EmptyObjectSchema() },
        new { name = "youtube_search", description = "Открывает поиск YouTube по запросу пользователя.", parameters = ObjectSchema(new { query = StringProperty("Что искать на YouTube, без слов открыть или найти.") }, ["query"]) },
        new { name = "youtube_watch", description = "Открывает конкретное видео YouTube только с переданным пользователем URL/ID из контекста.", parameters = ObjectSchema(new { videoId = StringProperty("Ровно 11 символов YouTube video ID.") }, ["videoId"]) },
    ];

    private static object EmptyObjectSchema() => new { type = "object", properties = new { } };
    private static object StringProperty(string description) => new { type = "string", description };
    private static object ObjectSchema(object properties, string[] required) => new { type = "object", properties, required };
}
