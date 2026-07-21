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
        CancellationToken cancellationToken,
        bool emitSpeechFirstResponse = false)
    {
        var key = string.IsNullOrWhiteSpace(apiKey) ? _configuration["Gemini:ApiKey"] : apiKey;
        if (string.IsNullOrWhiteSpace(key) || contents.Count == 0)
            return AssistantToolModelResponse.Unavailable();
        var effectiveSystemPrompt = emitSpeechFirstResponse
            ? SpeechFirstResponseParser.AddInstructions(systemPrompt)
            : systemPrompt;

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
                            {{effectiveSystemPrompt}}

                            Ты управляешь только объявленными инструментами Vass. Вызывай
                            инструмент, когда пользователь явно просит выполнить действие
                            или получить данные: искать актуальную информацию в интернете, сохранить/найти/исправить память, управлять
                            напоминаниями, объяснить возможности, сделать разовый снимок экрана,
                            развернуть Vass, открыть YouTube или создать/открыть книгу в локальной библиотеке.
                            Сам выбирай инструмент по смыслу фразы, а не по отдельным
                            словам. Не вызывай инструмент при обычном разговоре.
                            Если пользователь просит свежие новости, текущую погоду, цену,
                            расписание, последние события, поиск в интернете или другую
                            меняющуюся публичную информацию, ОБЯЗАТЕЛЬНО сначала вызови
                            web_search. Не отвечай, что «посмотришь позже», и не придумывай
                            отсутствие доступа: после результата web_search используй только
                            подтвержденные им факты. Если поиск вернул неуспех, честно скажи,
                            что свежие сведения сейчас не удалось подтвердить.
                            За один интерактивный ход web_search можно вызвать только один раз.
                            После успешного поиска сразу дай ответ по его выжимке и не запускай
                            повторный поиск для той же просьбы.
                            Если к текущей реплике уже приложено изображение или документ
                            и пользователь просит разобрать именно его, используй это
                            вложение, а не запрашивай второй снимок экрана.
                            Если пользователь явно просит запомнить текущий документ или
                            изображение, вызови memory_remember с осмысленным названием или
                            кратким содержанием и saveCurrentAttachment=true. Никогда не
                            сохраняй вложение в память без прямой просьбы пользователя.
                            Для memory_remember формируй связную запись по смыслу всего
                            контекста, а не копируй команду пользователя. Для ссылок сохраняй
                            полный URL и понятное назначение ссылки.
                            Когда сервер вернёт результат инструмента, используй именно его:
                            можешь вызвать следующий необходимый инструмент либо дай короткий
                            естественный ответ пользователю. Никогда не выдумывай receipt.
                            Когда пользователь явно просит сделать или обновить подборку, книгу,
                            меню, список рецептов, ресторанов или развлечений, используй
                            library_write. В html передай полноценный красивый статический HTML
                            документа с CSS внутри style: без JavaScript, ссылок, изображений,
                            iframe, сетевых шрифтов и инструкций для приложения. Не выводи HTML
                            в ответ пользователю. Для открытия существующей книги сначала получи
                            её ID через library_list либо используй каталог из system context.
                            Для новой книги передай sectionTitle: повтори подходящее существующее
                            название раздела из каталога либо придумай короткое понятное название
                            без тегов и хештегов. Для обновления существующей книги сохраняй её
                            текущий раздел, если пользователь явно не попросил переместить книгу.
                            Для ненавязчивого знакомства с ещё не использованной возможностью
                            следуй только отдельному системному контексту discovery: сначала
                            получи candidates, а перед единственной короткой подсказкой обязательно
                            зафиксируй именно выбранную возможность через present. Явный отказ от
                            уже предложенной возможности фиксируй через decline; не угадывай отказ
                            по смене темы или обычному молчанию.
                            """
                    }
                }
            },
            tools = new object[] { new { functionDeclarations = GetDeclarations() } },
            toolConfig = new { functionCallingConfig = new { mode = "AUTO" } },
            generationConfig = new { maxOutputTokens = 4096, thinkingConfig = new { thinkingBudget = 0 } }
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
        new { name = "web_search", description = "Ищет актуальную публичную информацию через Google Search и возвращает проверенную выжимку с источниками. Вызывай до ответа на свежие новости, погоду, цены, расписания, последние события или явную просьбу поискать в интернете. За один интерактивный ход вызови его не более одного раза. Не используй для устойчивых общеизвестных фактов.", parameters = ObjectSchema(new { query = StringProperty("Короткий точный поисковый запрос по смыслу просьбы пользователя.") }, ["query"]) },
        new { name = "memory_list", description = "Показывает недавние записи долгосрочной памяти.", parameters = EmptyObjectSchema() },
        new { name = "memory_search", description = "Ищет в долгосрочной памяти по смыслу запроса пользователя.", parameters = ObjectSchema(new { query = StringProperty("Короткий поисковый запрос.") }, ["query"]) },
        new { name = "conversation_search", description = "Ищет в собственной прошлой истории диалогов пользователя и возвращает короткие фрагменты с датой и message ID. Используй, когда пользователь явно просит вспомнить прежний разговор. query и диапазон дат можно сочетать; для «вчера/позавчера» передай fromDateLocal и toDateLocal как включительные даты yyyy-MM-dd, вычисленные из текущего локального времени. Если предмет поиска и дата неясны, сначала задай уточняющий вопрос.", parameters = ObjectSchema(new { query = StringProperty("Необязательные ключевые слова или короткий смысл прежнего разговора."), fromDateLocal = StringProperty("Необязательная начальная локальная дата yyyy-MM-dd, включительно."), toDateLocal = StringProperty("Необязательная конечная локальная дата yyyy-MM-dd, включительно.") }, []) },
        new { name = "memory_remember", description = "Сохраняет одну связанную, осмысленную запись в долгосрочную память только по явной просьбе пользователя. Для ссылки сохрани URL полностью и объясни, что это за ссылка. Для текущего приложенного документа или изображения ставь saveCurrentAttachment=true только при явной просьбе сохранить вложение.", parameters = ObjectSchema(new { text = StringProperty("Готовая осмысленная запись; не команда и не местоимение."), category = StringProperty("Одна категория: profile, family, contacts, health, medications, allergies, habits, work, education, finance, home, pets, shopping, recipes, food, travel, transport, events, tasks, projects, hobbies, books, films, music, games, technology, links, documents, other."), saveCurrentAttachment = BooleanProperty("true только если пользователь явно просит сохранить приложенный к этой реплике документ или изображение.") }, ["text"]) },
        new { name = "memory_correct", description = "Исправляет найденную запись памяти. Сначала получи ID через memory_search или memory_list.", parameters = ObjectSchema(new { memoryId = StringProperty("UUID записи."), text = StringProperty("Новая осмысленная формулировка."), category = StringProperty("Необязательная новая категория из списка memory_remember.") }, ["memoryId", "text"]) },
        new { name = "memory_forget", description = "Удаляет одну конкретную найденную запись памяти. Сначала получи ID через memory_search или memory_list.", parameters = ObjectSchema(new { memoryId = StringProperty("UUID записи.") }, ["memoryId"]) },
        new { name = "reminder_create", description = "Создает одно локальное напоминание только по явной просьбе пользователя. Передавай короткий текст задачи и точный будущий local date-time в ISO формате yyyy-MM-ddTHH:mm:ss. Если времени недостаточно, сначала задай уточняющий вопрос, а не вызывай инструмент.", parameters = ObjectSchema(new { text = StringProperty("Короткая задача без слов 'напомни мне'."), dueAtLocal = StringProperty("Будущий локальный момент в формате yyyy-MM-ddTHH:mm:ss.") }, ["text", "dueAtLocal"]) },
        new { name = "periodic_reminder_create", description = "Создает одно периодическое локальное напоминание только по явной просьбе и только если manifest показывает reminder.periodic=available. Передавай ближайший точный первый запуск startAtLocal и RRULE без префикса RRULE:. V1 поддерживает только FREQ=DAILY; FREQ=WEEKLY с одним BYDAY; FREQ=MONTHLY для дня 1..28; FREQ=YEARLY кроме 29 февраля; FREQ=HOURLY с INTERVAL 1..168; FREQ=MINUTELY с INTERVAL 15..10080. Для HOURLY/MINUTELY startAtLocal должен быть ровно через один interval от текущего времени. COUNT, UNTIL, несколько BYDAY и INTERVAL>1 для календарных правил не поддерживаются — сначала уточни или честно сообщи ограничение.", parameters = ObjectSchema(new { text = StringProperty("Короткая задача без слов 'напоминай мне'."), startAtLocal = StringProperty("Ближайший первый локальный запуск yyyy-MM-ddTHH:mm:ss; секунды 00."), rrule = StringProperty("Поддерживаемая строка iCalendar RRULE без префикса, например FREQ=WEEKLY;BYDAY=SU или FREQ=HOURLY;INTERVAL=2.") }, ["text", "startAtLocal", "rrule"]) },
        new { name = "reminder_list", description = "Показывает активные напоминания пользователя. Используй, когда он спрашивает, что запланировано, или перед отменой, если конкретное напоминание неясно.", parameters = EmptyObjectSchema() },
        new { name = "reminder_cancel", description = "Отменяет конкретное активное напоминание по ID. Сначала получи ID через reminder_list, если пользователь не назвал его однозначно.", parameters = ObjectSchema(new { reminderId = IntegerProperty("Числовой ID конкретного активного напоминания.") }, ["reminderId"]) },
        new { name = "library_list", description = "Показывает оглавление локальной библиотеки пользователя: разделы, названия книг, ID, типы и количество версий. Используй перед открытием или обновлением, когда нужная книга не однозначна.", parameters = EmptyObjectSchema() },
        new { name = "library_write", description = "Создает новую локальную HTML-книгу или следующую версию существующей. Вызывай только по явной просьбе создать, оформить, собрать или обновить книгу/подборку. html должен быть полноценным статическим документом без JavaScript, ссылок, внешних ресурсов и iframe. Для новой книги передавай sectionTitle: существующий или новый понятный раздел без тегов.", parameters = ObjectSchema(new { title = StringProperty("Короткое понятное название книги."), kind = StringProperty("Один тип: recipes, restaurants, entertainment, guide, other."), html = StringProperty("Полный статический HTML с CSS в style, без внешних ресурсов."), sectionTitle = StringProperty("Название раздела библиотеки: выбери существующее из каталога или придумай новый понятный раздел без тегов."), summary = StringProperty("Короткое описание содержания для оглавления."), sourceUrls = StringArrayProperty("Необязательные HTTPS-источники, не более 12."), artifactId = StringProperty("Необязательный UUID существующей книги, если пользователь попросил новую версию."), revisionNote = StringProperty("Коротко, что изменилось в новой версии.") }, ["title", "kind", "html"]) },
        new { name = "library_open", description = "Открывает оглавление локальной библиотеки или одну конкретную книгу по ее UUID. Используй при явной просьбе показать библиотеку или открыть книгу.", parameters = ObjectSchema(new { artifactId = StringProperty("Необязательный UUID книги из library_list или каталога. Без него открывается оглавление.") }, []) },
        new { name = "capability_help", description = "Возвращает краткие возможности Vass, примеры естественных фраз и подсказку, где это находится в интерфейсе. Используй, когда пользователь спрашивает, что ты умеешь или как выполнить действие.", parameters = ObjectSchema(new { topic = StringProperty("Необязательная тема: память, напоминания, фото, файл, share, экран, YouTube, библиотека, overlay.") }, []) },
        new { name = "capability_discovery_status", description = "Возвращает content-free статус освоения возможностей пользователя: уже пробовал, ещё не пробовал или попросил больше не предлагать. Используй только для вопроса о возможностях или своём прогрессе, не для обычного ответа.", parameters = EmptyObjectSchema() },
        new { name = "capability_discovery_candidates", description = "Возвращает допустимые неиспользованные возможности для одной ненавязчивой подсказки. Вызывай только когда отдельный system context разрешает подсказку и она прямо уместна после ответа на текущий вопрос.", parameters = EmptyObjectSchema() },
        new { name = "capability_discovery_present", description = "Фиксирует одну реально показанную пользователю ненавязчивую подсказку. Сначала получи capabilityId через capability_discovery_candidates; вызывай только непосредственно перед короткой уместной фразой в финальном ответе.", parameters = ObjectSchema(new { capabilityId = StringProperty("Точный ID из capability_discovery_candidates.") }, ["capabilityId"]) },
        new { name = "capability_discovery_decline", description = "Навсегда отключает повторные подсказки по одной возможности только после явного отказа пользователя от ранее предложенной темы.", parameters = ObjectSchema(new { capabilityId = StringProperty("Точный ID явно отвергнутой возможности.") }, ["capabilityId"]) },
        new { name = "screen_capture_once", description = "Запрашивает один снимок текущего экрана только когда пользователь явно попросил сделать, посмотреть или объяснить экран. Android покажет системное подтверждение.", parameters = EmptyObjectSchema() },
        new { name = "open_vass", description = "Разворачивает Vass из overlay в обычное полноэкранное приложение.", parameters = EmptyObjectSchema() },
        new { name = "youtube_search", description = "Открывает поиск YouTube по запросу пользователя.", parameters = ObjectSchema(new { query = StringProperty("Что искать на YouTube, без слов открыть или найти.") }, ["query"]) },
        new { name = "youtube_watch", description = "Открывает конкретное видео YouTube только с переданным пользователем URL/ID из контекста.", parameters = ObjectSchema(new { videoId = StringProperty("Ровно 11 символов YouTube video ID.") }, ["videoId"]) },
    ];

    private static object EmptyObjectSchema() => new { type = "object", properties = new { } };
    private static object StringProperty(string description) => new { type = "string", description };
    private static object StringArrayProperty(string description) => new { type = "array", description, items = new { type = "string" } };
    private static object BooleanProperty(string description) => new { type = "boolean", description };
    private static object IntegerProperty(string description) => new { type = "integer", description };
    private static object ObjectSchema(object properties, string[] required) => new { type = "object", properties, required };
}
