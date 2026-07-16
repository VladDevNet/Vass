using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VoiceAssistant.API.Services;

public sealed record AssistantToolExecution(
    string Name,
    string Status,
    string Summary,
    ExternalActionCommand? ExternalAction = null,
    Guid? ActionReceiptId = null,
    bool RequestsScreenCapture = false,
    JsonElement? Data = null,
    string? CallId = null,
    ReminderDraft? Reminder = null);

// The only authority that turns a model proposal into a side effect. Every
// branch is owner-scoped and validates its arguments independently of Gemini.
public sealed class AssistantToolBroker
{
    public const int MaxCallsPerStep = 3;

    private readonly MemoryItemService _memory;
    private readonly ConversationSearchService _conversationSearch;
    private readonly ReminderService _reminders;
    private readonly ActionReceiptService _actionReceipts;

    public AssistantToolBroker(
        MemoryItemService memory,
        ConversationSearchService conversationSearch,
        ReminderService reminders,
        ActionReceiptService actionReceipts)
    {
        _memory = memory;
        _conversationSearch = conversationSearch;
        _reminders = reminders;
        _actionReceipts = actionReceipts;
    }

    public async Task<IReadOnlyList<AssistantToolExecution>> ExecuteAsync(
        IEnumerable<AssistantToolCall> calls,
        string userId,
        int sourceMessageId,
        AssistantRuntimeContext context,
        CancellationToken cancellationToken)
    {
        var results = new List<AssistantToolExecution>();
        var index = 0;
        var hasProposedClientAction = context.HasProposedClientAction;
        foreach (var call in calls)
        {
            AssistantToolExecution execution;
            if (index++ >= MaxCallsPerStep)
            {
                execution = new AssistantToolExecution(
                    call.Name,
                    "rejected",
                    "В одном шаге разрешено не более трех действий.",
                    Data: ToData(new { status = "rejected", code = "too_many_calls" }));
            }
            else if (IsClientAction(call.Name) && hasProposedClientAction)
            {
                execution = new AssistantToolExecution(
                    call.Name,
                    "rejected",
                    "В одном интерактивном ходе разрешено только одно действие на клиенте.",
                    Data: ToData(new { status = "rejected", code = "client_action_already_proposed" }));
            }
            else
            {
                execution = await ExecuteOneAsync(
                    call,
                    userId,
                    sourceMessageId,
                    context,
                    CreateOperationId(sourceMessageId, call),
                    cancellationToken);
            }

            if (execution.ExternalAction is not null)
                hasProposedClientAction = true;
            results.Add(execution with { CallId = call.CallId });
        }

        return results;
    }

    private async Task<AssistantToolExecution> ExecuteOneAsync(
        AssistantToolCall call,
        string userId,
        int sourceMessageId,
        AssistantRuntimeContext context,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        return call.Name switch
        {
            "memory_status" => await MemoryStatusAsync(userId, cancellationToken),
            "memory_list" => await MemoryListAsync(userId, cancellationToken),
            "memory_search" => await MemorySearchAsync(userId, GetString(call.Arguments, "query"), cancellationToken),
            "conversation_search" => await ConversationSearchAsync(
                userId,
                GetString(call.Arguments, "query"),
                GetString(call.Arguments, "fromDateLocal"),
                GetString(call.Arguments, "toDateLocal"),
                context.TimeZoneId,
                sourceMessageId,
                cancellationToken),
            "memory_remember" => await RememberAsync(userId, sourceMessageId, GetString(call.Arguments, "text"), operationId, cancellationToken),
            "memory_correct" => await CorrectAsync(userId, GetString(call.Arguments, "memoryId"), GetString(call.Arguments, "text"), operationId, cancellationToken),
            "memory_forget" => await ForgetAsync(userId, GetString(call.Arguments, "memoryId"), operationId, cancellationToken),
            "reminder_create" => await CreateReminderAsync(userId, sourceMessageId, GetString(call.Arguments, "text"), GetString(call.Arguments, "dueAtLocal"), context, cancellationToken),
            "screen_capture_once" => RequestScreenCapture(call.Name, context),
            "open_vass" => await ProposeActionAsync(call.Name, ExternalActionTypes.OpenVass, null, null, userId, sourceMessageId, context, cancellationToken),
            "youtube_search" => await ProposeActionAsync(call.Name, ExternalActionTypes.YouTubeSearch, GetString(call.Arguments, "query"), null, userId, sourceMessageId, context, cancellationToken),
            "youtube_watch" => await ProposeActionAsync(call.Name, ExternalActionTypes.YouTubeWatch, null, GetString(call.Arguments, "videoId"), userId, sourceMessageId, context, cancellationToken),
            _ => new(call.Name, "rejected", "Инструмент недоступен.",
                Data: ToData(new { status = "rejected", code = "tool_not_allowed" }))
        };
    }

    private async Task<AssistantToolExecution> MemoryStatusAsync(string userId, CancellationToken ct)
    {
        var status = await _memory.GetStatusAsync(userId, ct);
        return new("memory_status", status.Availability, $"Память: {status.Availability}; записей: {status.ActiveCount}.",
            Data: ToData(new { status = status.Availability, activeCount = status.ActiveCount, semanticSearchAvailable = status.SemanticSearchAvailable }));
    }

    private async Task<AssistantToolExecution> MemoryListAsync(string userId, CancellationToken ct)
    {
        var items = await _memory.ListAsync(userId, 8, ct);
        return new("memory_list", "ok", FormatItems(items),
            Data: ToData(new { status = "ok", items = ToToolItems(items) }));
    }

    private async Task<AssistantToolExecution> MemorySearchAsync(string userId, string? query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query))
            return new("memory_search", "invalid", "Не указан поисковый запрос.",
                Data: ToData(new { status = "invalid", code = "missing_query" }));

        var result = await _memory.SearchAsync(userId, query, ct);
        return new("memory_search", result.Status, FormatItems(result.Items),
            Data: ToData(new { status = result.Status, retrieval = result.Retrieval, items = ToToolItems(result.Items) }));
    }

    private async Task<AssistantToolExecution> ConversationSearchAsync(
        string userId,
        string? query,
        string? fromDateLocal,
        string? toDateLocal,
        string? timeZoneId,
        int sourceMessageId,
        CancellationToken ct)
    {
        var result = await _conversationSearch.SearchAsync(
            userId,
            query,
            fromDateLocal,
            toDateLocal,
            timeZoneId,
            sourceMessageId,
            ct);
        return new("conversation_search", result.Status,
            result.Hits.Count == 0
                ? "Подходящих фрагментов прежнего разговора нет."
                : string.Join("\n", result.Hits.Select(hit => $"[{hit.MessageId}; {hit.CreatedAt:O}; {hit.Role}] {hit.Excerpt}")),
            Data: ToData(new
            {
                status = result.Status,
                hits = result.Hits.Select(hit => new
                {
                    messageId = hit.MessageId,
                    sessionId = hit.SessionId,
                    role = hit.Role,
                    excerpt = hit.Excerpt,
                    createdAt = hit.CreatedAt
                })
            }));
    }

    private async Task<AssistantToolExecution> RememberAsync(
        string userId,
        int sourceMessageId,
        string? text,
        Guid operationId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 1000)
            return new("memory_remember", "invalid", "Нужна одна короткая осмысленная запись памяти.",
                Data: ToData(new { status = "invalid", code = "invalid_text" }));

        var normalized = text.Trim();
        var result = await _memory.RememberAsync(userId, normalized, sourceMessageId, operationId, ct);
        var confirmed = result.Code is "remembered" or "already_known";
        return new("memory_remember", result.Code,
            confirmed ? $"Запись памяти подтверждена: {normalized}" : "Запись памяти не подтверждена.",
            Data: ToData(new
            {
                status = result.Status,
                code = result.Code,
                memoryId = result.MemoryItemId,
                text = confirmed ? normalized : null
            }));
    }

    private async Task<AssistantToolExecution> CorrectAsync(
        string userId,
        string? id,
        string? text,
        Guid operationId,
        CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var memoryId) || string.IsNullOrWhiteSpace(text) || text.Length > 1000)
            return new("memory_correct", "invalid", "Нужны ID найденной записи и новая формулировка.",
                Data: ToData(new { status = "invalid", code = "invalid_arguments" }));

        var normalized = text.Trim();
        var result = await _memory.CorrectAsync(userId, memoryId, normalized, operationId, ct);
        return new("memory_correct", result.Code,
            result.Code == "corrected" ? $"Запись исправлена: {normalized}" : "Запись не была исправлена.",
            Data: ToData(new
            {
                status = result.Status,
                code = result.Code,
                memoryId = result.MemoryItemId,
                text = result.Code == "corrected" ? normalized : null
            }));
    }

    private async Task<AssistantToolExecution> ForgetAsync(
        string userId,
        string? id,
        Guid operationId,
        CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var memoryId))
            return new("memory_forget", "invalid", "Нужен ID конкретной записи памяти.",
                Data: ToData(new { status = "invalid", code = "invalid_memory_id" }));

        var result = await _memory.ForgetAsync(userId, memoryId, operationId, ct);
        return new("memory_forget", result.Code,
            result.Code == "forgotten" ? "Запись удалена из активной памяти." : "Запись не была удалена.",
            Data: ToData(new { status = result.Status, code = result.Code, memoryId = result.MemoryItemId }));
    }

    private async Task<AssistantToolExecution> CreateReminderAsync(
        string userId,
        int sourceMessageId,
        string? text,
        string? dueAtLocal,
        AssistantRuntimeContext context,
        CancellationToken ct)
    {
        if (!context.SupportsReminders)
            return new("reminder_create", "unavailable", "Для локального напоминания нужен связанный телефон и часовой пояс.",
                Data: ToData(new { status = "unavailable", code = "device_context_required" }));

        var result = await _reminders.CreateFromToolAsync(
            userId,
            sourceMessageId,
            text,
            dueAtLocal,
            context.DeviceId,
            context.TimeZoneId,
            ct);
        if (result.State != ReminderInterpretationState.Created || result.Reminder is not { } reminder)
        {
            return new("reminder_create", "needs_clarification", "Точное будущее время или параметры напоминания требуют уточнения.",
                Data: ToData(new { status = "needs_clarification", code = "invalid_or_ambiguous_schedule" }));
        }

        return new("reminder_create", "created", $"Напоминание сохранено: {reminder.Text}.",
            Data: ToData(new
            {
                status = "created",
                reminderId = reminder.Id,
                text = reminder.Text,
                dueAtUtc = reminder.DueAtUtc,
                timeZoneId = reminder.TimeZoneId,
                clientReceiptRequired = true
            }),
            Reminder: reminder);
    }

    private static AssistantToolExecution RequestScreenCapture(string name, AssistantRuntimeContext context)
    {
        if (!context.SupportsScreenAnalysis)
        {
            return new(name, "unavailable", "Снимок экрана недоступен на этом устройстве или в этом режиме.",
                Data: ToData(new { status = "unavailable", code = "screen_capture_unavailable" }));
        }

        if (context.HasVisualAttachment)
        {
            return new(name, "unavailable", "К текущей реплике уже приложено изображение или документ; используй его вместо нового снимка.",
                Data: ToData(new { status = "unavailable", code = "visual_attachment_already_present" }));
        }

        return new(name, "requested", "Запрошен один снимок экрана с системным подтверждением пользователя.",
            RequestsScreenCapture: true,
            Data: ToData(new { status = "requested", requiresUserConsent = true }));
    }

    private async Task<AssistantToolExecution> ProposeActionAsync(
        string name,
        string type,
        string? query,
        string? videoId,
        string userId,
        int sourceMessageId,
        AssistantRuntimeContext context,
        CancellationToken ct)
    {
        if (!context.SupportsExternalActions)
            return new(name, "unavailable", "Действие недоступно на этом клиенте.",
                Data: ToData(new { status = "unavailable", code = "client_capability_missing" }));

        var action = type switch
        {
            ExternalActionTypes.OpenVass => new ExternalActionCommand(type),
            ExternalActionTypes.YouTubeSearch when NormalizeQuery(query) is { } safeQuery => new ExternalActionCommand(type, Query: safeQuery),
            ExternalActionTypes.YouTubeWatch when IsVideoId(videoId) => new ExternalActionCommand(type, VideoId: videoId),
            _ => null
        };
        if (action is null)
            return new(name, "invalid", "Аргументы действия некорректны.",
                Data: ToData(new { status = "invalid", code = "invalid_arguments" }));

        var receipt = await _actionReceipts.ProposeAsync(userId, sourceMessageId, action, ct);
        return receipt is null
            ? new(name, "rejected", "Действие не разрешено политикой Vass.",
                Data: ToData(new { status = "rejected", code = "policy_rejected" }))
            : new(name, "proposed", "Команда передана клиенту для исполнения.", action, receipt.ActionId,
                Data: ToData(new
                {
                    status = "proposed",
                    actionId = receipt.ActionId,
                    actionType = action.Type,
                    clientReceiptRequired = true
                }));
    }

    private static string? GetString(JsonElement arguments, string name) =>
        arguments.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() : null;

    private static string? NormalizeQuery(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;
        var normalized = string.Join(' ', query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= ExternalActionService.MaxQueryLength ? normalized : null;
    }

    private static bool IsVideoId(string? value) => !string.IsNullOrWhiteSpace(value) &&
        value.Length == 11 && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');

    private static bool IsClientAction(string name) => name is "open_vass" or "youtube_search" or "youtube_watch";

    private static IReadOnlyList<object> ToToolItems(IEnumerable<MemoryItemResponse> items) =>
        items.Take(8).Select(item => (object)new
        {
            id = item.Id,
            text = item.Text,
            kind = item.Kind,
            revision = item.Revision,
            updatedAt = item.UpdatedAt
        }).ToArray();

    private static string FormatItems(IEnumerable<MemoryItemResponse> items)
    {
        var selected = items.Take(8).Select(item => $"[{item.Id}] {item.Text}").ToArray();
        return selected.Length == 0 ? "Подходящих записей нет." : string.Join("\n", selected);
    }

    private static Guid CreateOperationId(int sourceMessageId, AssistantToolCall call)
    {
        var material = $"{sourceMessageId}\n{call.Name}\n{call.Arguments.GetRawText()}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return new Guid(hash.AsSpan(0, 16));
    }

    private static JsonElement ToData<T>(T value) => JsonSerializer.SerializeToElement(value);
}
