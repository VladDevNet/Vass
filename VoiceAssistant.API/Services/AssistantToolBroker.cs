using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VoiceAssistant.API.Data.Entities;

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
    ReminderDraft? Reminder = null,
    ReminderCancellationResult? ReminderCancellation = null);

// The only authority that turns a model proposal into a side effect. Every
// branch is owner-scoped and validates its arguments independently of Gemini.
public sealed class AssistantToolBroker
{
    public const int MaxCallsPerStep = 3;

    private readonly MemoryItemService _memory;
    private readonly ConversationSearchService _conversationSearch;
    private readonly ReminderService _reminders;
    private readonly ActionReceiptService _actionReceipts;
    private readonly AssistantCapabilityRegistry _capabilities;

    public AssistantToolBroker(
        MemoryItemService memory,
        ConversationSearchService conversationSearch,
        ReminderService reminders,
        ActionReceiptService actionReceipts,
        AssistantCapabilityRegistry capabilities)
    {
        _memory = memory;
        _conversationSearch = conversationSearch;
        _reminders = reminders;
        _actionReceipts = actionReceipts;
        _capabilities = capabilities;
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
        var hasAttemptedReminder = context.HasAttemptedReminder;
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
            else if (IsReminderTool(call.Name) && hasAttemptedReminder)
            {
                execution = new AssistantToolExecution(
                    call.Name,
                    "rejected",
                    "В одном ходе разрешено создать только одно напоминание.",
                    Data: ToData(new { status = "rejected", code = "reminder_already_created" }));
            }
            else
            {
                execution = await ExecuteOneAsync(
                    call,
                    userId,
                    sourceMessageId,
                    context,
                    CreateOperationId(sourceMessageId, call, context.ClientTurnId),
                    cancellationToken);
            }

            if (execution.ExternalAction is not null)
                hasProposedClientAction = true;
            // A failed recurring proposal must not be silently downgraded to
            // a one-shot (or retried with changed semantics) later in the same
            // turn. Any reminder tool attempt consumes the single reminder
            // side-effect slot; clarification happens in the next user turn.
            if (IsReminderTool(call.Name))
                hasAttemptedReminder = true;
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
            "memory_remember" => await RememberAsync(
                userId,
                sourceMessageId,
                GetString(call.Arguments, "text"),
                GetString(call.Arguments, "category"),
                GetBoolean(call.Arguments, "saveCurrentAttachment"),
                context,
                operationId,
                cancellationToken),
            "memory_correct" => await CorrectAsync(
                userId,
                GetString(call.Arguments, "memoryId"),
                GetString(call.Arguments, "text"),
                GetString(call.Arguments, "category"),
                operationId,
                cancellationToken),
            "memory_forget" => await ForgetAsync(userId, GetString(call.Arguments, "memoryId"), operationId, cancellationToken),
            "reminder_create" => await CreateReminderAsync(userId, sourceMessageId, GetString(call.Arguments, "text"), GetString(call.Arguments, "dueAtLocal"), context, operationId, cancellationToken),
            "periodic_reminder_create" => await CreatePeriodicReminderAsync(
                userId,
                sourceMessageId,
                GetString(call.Arguments, "text"),
                GetString(call.Arguments, "startAtLocal"),
                GetString(call.Arguments, "rrule"),
                context,
                operationId,
                cancellationToken),
            "reminder_list" => await ReminderListAsync(userId, cancellationToken),
            "reminder_cancel" => await CancelReminderAsync(
                userId,
                GetInteger(call.Arguments, "reminderId"),
                cancellationToken),
            "capability_help" => CapabilityHelp(context, GetString(call.Arguments, "topic")),
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
        string? category,
        bool saveCurrentAttachment,
        AssistantRuntimeContext context,
        Guid operationId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 1000)
            return new("memory_remember", "invalid", "Нужна одна короткая осмысленная запись памяти.",
                Data: ToData(new { status = "invalid", code = "invalid_text" }));
        if (saveCurrentAttachment && context.VisualAssetId is null)
            return new("memory_remember", "invalid", "К этой реплике не приложен документ или изображение для сохранения.",
                Data: ToData(new { status = "invalid", code = "visual_attachment_required" }));

        var normalized = text.Trim();
        var resolvedCategory = MemoryCategories.NormalizeOrDefault(
            category,
            saveCurrentAttachment ? "documents" : MemoryCategories.Other);
        var result = await _memory.RememberAsync(
            userId,
            normalized,
            sourceMessageId,
            operationId,
            ct,
            category,
            saveCurrentAttachment ? context.VisualAssetId : null);
        var confirmed = result.Code is "remembered" or "already_known";
        return new("memory_remember", result.Code,
            confirmed ? $"Запись памяти подтверждена: {normalized}" : "Запись памяти не подтверждена.",
            Data: ToData(new
            {
                status = result.Status,
                code = result.Code,
                memoryId = result.MemoryItemId,
                text = confirmed ? normalized : null,
                category = confirmed ? resolvedCategory : null,
                savedCurrentAttachment = confirmed && saveCurrentAttachment
            }));
    }

    private async Task<AssistantToolExecution> CorrectAsync(
        string userId,
        string? id,
        string? text,
        string? category,
        Guid operationId,
        CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var memoryId) || string.IsNullOrWhiteSpace(text) || text.Length > 1000)
            return new("memory_correct", "invalid", "Нужны ID найденной записи и новая формулировка.",
                Data: ToData(new { status = "invalid", code = "invalid_arguments" }));

        var normalized = text.Trim();
        var result = await _memory.CorrectAsync(userId, memoryId, normalized, operationId, ct, category);
        return new("memory_correct", result.Code,
            result.Code == "corrected" ? $"Запись исправлена: {normalized}" : "Запись не была исправлена.",
            Data: ToData(new
            {
                status = result.Status,
                code = result.Code,
                memoryId = result.MemoryItemId,
                text = result.Code == "corrected" ? normalized : null,
                category = result.Code == "corrected" ? category : null
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
        Guid operationId,
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
            ct,
            operationId);
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

    private async Task<AssistantToolExecution> CreatePeriodicReminderAsync(
        string userId,
        int sourceMessageId,
        string? text,
        string? startAtLocal,
        string? recurrenceRule,
        AssistantRuntimeContext context,
        Guid operationId,
        CancellationToken ct)
    {
        if (!context.SupportsPeriodicReminders)
            return new("periodic_reminder_create", "unavailable",
                "Периодические напоминания требуют клиента с protocol version 2 и корректным часовым поясом.",
                Data: ToData(new { status = "unavailable", code = "periodic_reminder_client_required" }));

        var result = await _reminders.CreatePeriodicFromToolAsync(
            userId,
            sourceMessageId,
            text,
            startAtLocal,
            recurrenceRule,
            context.DeviceId,
            context.TimeZoneId,
            operationId,
            ct);
        if (result.State != ReminderInterpretationState.Created || result.Reminder is not { } reminder)
        {
            return new("periodic_reminder_create", "needs_clarification",
                "Нужны точный первый локальный запуск и поддерживаемое правило повторения.",
                Data: ToData(new { status = "needs_clarification", code = "invalid_or_unsupported_recurrence" }));
        }

        return new("periodic_reminder_create", "created",
            $"Периодическое напоминание сохранено: {reminder.Text}; {reminder.RecurrenceRule}.",
            Data: ToData(new
            {
                status = "created",
                reminderId = reminder.Id,
                text = reminder.Text,
                startAtUtc = reminder.DueAtUtc,
                timeZoneId = reminder.TimeZoneId,
                rrule = reminder.RecurrenceRule,
                clientReceiptRequired = true
            }),
            Reminder: reminder);
    }

    private async Task<AssistantToolExecution> ReminderListAsync(string userId, CancellationToken ct)
    {
        var reminders = await _reminders.ListActiveAsync(userId, ct);
        return new("reminder_list", "ok",
            reminders.Count == 0
                ? "Активных напоминаний нет."
                : string.Join("\n", reminders.Take(20).Select(reminder =>
                    $"[{reminder.Id}] {reminder.Text}; {reminder.DueAtUtc:O}; {reminder.RecurrenceRule ?? "одноразовое"}")),
            Data: ToData(new
            {
                status = "ok",
                reminders = reminders.Take(20).Select(reminder => new
                {
                    id = reminder.Id,
                    text = reminder.Text,
                    dueAtUtc = reminder.DueAtUtc,
                    timeZoneId = reminder.TimeZoneId,
                    recurrenceRule = reminder.RecurrenceRule,
                    status = reminder.Status
                })
            }));
    }

    private async Task<AssistantToolExecution> CancelReminderAsync(string userId, int? reminderId, CancellationToken ct)
    {
        if (reminderId is not > 0)
            return new("reminder_cancel", "invalid", "Нужен ID конкретного напоминания из reminder_list.",
                Data: ToData(new { status = "invalid", code = "invalid_reminder_id" }));

        var cancellation = await _reminders.CancelAsync(userId, reminderId.Value, ct);
        if (cancellation is null)
            return new("reminder_cancel", "not_found", "Активное напоминание с таким ID не найдено.",
                Data: ToData(new { status = "not_found", code = "reminder_not_found" }));

        return new("reminder_cancel", "cancelled", $"Напоминание отменено: {cancellation.Text}.",
            Data: ToData(new
            {
                status = "cancelled",
                reminderId = cancellation.ReminderId,
                text = cancellation.Text,
                deliveries = cancellation.Deliveries.Select(delivery => new
                {
                    deviceId = delivery.DeviceId,
                    localNotificationId = delivery.LocalNotificationId
                })
            }),
            ReminderCancellation: cancellation);
    }

    private AssistantToolExecution CapabilityHelp(AssistantRuntimeContext context, string? topic)
    {
        var items = _capabilities.GetHelp(context, topic).Take(8).ToArray();
        return new("capability_help", "ok",
            items.Length == 0
                ? "Сейчас нет доступных подсказок по этой теме."
                : string.Join("\n", items.Select(item => $"{item.Title}: {item.Description}")),
            Data: ToData(new
            {
                status = "ok",
                items = items.Select(item => new
                {
                    id = item.Id,
                    title = item.Title,
                    description = item.Description,
                    examples = item.Examples,
                    interfaceHint = item.InterfaceHint
                })
            }));
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

    private static bool GetBoolean(JsonElement arguments, string name) =>
        arguments.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static int? GetInteger(JsonElement arguments, string name) =>
        arguments.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var result)
            ? result
            : null;

    private static string? NormalizeQuery(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;
        var normalized = string.Join(' ', query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= ExternalActionService.MaxQueryLength ? normalized : null;
    }

    private static bool IsVideoId(string? value) => !string.IsNullOrWhiteSpace(value) &&
        value.Length == 11 && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');

    private static bool IsClientAction(string name) => name is "open_vass" or "youtube_search" or "youtube_watch";
    private static bool IsReminderTool(string name) => name is "reminder_create" or "periodic_reminder_create";

    private static IReadOnlyList<object> ToToolItems(IEnumerable<MemoryItemResponse> items) =>
        items.Take(8).Select(item => (object)new
        {
            id = item.Id,
            text = item.Text,
            kind = item.Kind,
            category = item.Category,
            hasAttachment = item.Attachment is not null,
            attachmentName = item.Attachment?.OriginalFileName,
            revision = item.Revision,
            updatedAt = item.UpdatedAt
        }).ToArray();

    private static string FormatItems(IEnumerable<MemoryItemResponse> items)
    {
        var selected = items.Take(8).Select(item =>
            $"[{item.Id}; {item.Category}] {item.Text}{(item.Attachment is null ? "" : " (сохраненное вложение)")}").ToArray();
        return selected.Length == 0 ? "Подходящих записей нет." : string.Join("\n", selected);
    }

    private static Guid CreateOperationId(int sourceMessageId, AssistantToolCall call, Guid? clientTurnId)
    {
        var turnKey = clientTurnId is { } stableId ? $"turn:{stableId:N}" : $"message:{sourceMessageId}";
        // A retry invokes the model again, so relative schedules can be
        // re-extracted with a different timestamp. Reminders therefore use
        // one stable side-effect slot per client turn; first successful write
        // wins even if a replay changes arguments or switches between the
        // one-shot and periodic tool. Other tools retain argument-sensitive
        // operation IDs so multiple distinct calls remain possible.
        var operation = IsReminderTool(call.Name)
            ? "reminder-slot"
            : $"{call.Name}\n{Canonicalize(call.Arguments)}";
        var material = $"{turnKey}\n{operation}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return new Guid(hash.AsSpan(0, 16));
    }

    private static string Canonicalize(JsonElement value)
    {
        var builder = new StringBuilder();
        AppendCanonical(builder, value);
        return builder.ToString();
    }

    private static void AppendCanonical(StringBuilder builder, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                builder.Append('{');
                var firstProperty = true;
                foreach (var property in value.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    if (!firstProperty) builder.Append(',');
                    firstProperty = false;
                    builder.Append(JsonSerializer.Serialize(property.Name)).Append(':');
                    AppendCanonical(builder, property.Value);
                }
                builder.Append('}');
                break;
            case JsonValueKind.Array:
                builder.Append('[');
                var firstItem = true;
                foreach (var item in value.EnumerateArray())
                {
                    if (!firstItem) builder.Append(',');
                    firstItem = false;
                    AppendCanonical(builder, item);
                }
                builder.Append(']');
                break;
            case JsonValueKind.String:
                builder.Append(JsonSerializer.Serialize(value.GetString()));
                break;
            case JsonValueKind.True:
                builder.Append("true");
                break;
            case JsonValueKind.False:
                builder.Append("false");
                break;
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                builder.Append("null");
                break;
            default:
                builder.Append(value.GetRawText());
                break;
        }
    }

    private static JsonElement ToData<T>(T value) => JsonSerializer.SerializeToElement(value);
}
