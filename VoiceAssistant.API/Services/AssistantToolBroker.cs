using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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

    private static readonly Regex UnsafeLibraryMarkupPattern = new(
        @"<(?:script|iframe|object|embed|frame|frameset|form|input|button|textarea|select|option|audio|video|source|track|link|base|svg|math)\b|\son[a-z]+\s*=|\s(?:href|src|action|formaction|poster|data)\s*=|javascript\s*:|@import\b|url\s*\(",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly MemoryItemService _memory;
    private readonly ConversationSearchService _conversationSearch;
    private readonly ReminderService _reminders;
    private readonly ActionReceiptService _actionReceipts;
    private readonly AssistantCapabilityRegistry _capabilities;
    private readonly CapabilityDiscoveryService _capabilityDiscovery;
    private readonly GroundedWebSearchService _webSearch;

    public AssistantToolBroker(
        MemoryItemService memory,
        ConversationSearchService conversationSearch,
        ReminderService reminders,
        ActionReceiptService actionReceipts,
        AssistantCapabilityRegistry capabilities,
        CapabilityDiscoveryService capabilityDiscovery,
        GroundedWebSearchService webSearch)
    {
        _memory = memory;
        _conversationSearch = conversationSearch;
        _reminders = reminders;
        _actionReceipts = actionReceipts;
        _capabilities = capabilities;
        _capabilityDiscovery = capabilityDiscovery;
        _webSearch = webSearch;
    }

    public async Task<IReadOnlyList<AssistantToolExecution>> ExecuteAsync(
        IEnumerable<AssistantToolCall> calls,
        string userId,
        int sourceMessageId,
        AssistantRuntimeContext context,
        string? apiKey,
        GroundedWebSearchPrefetch? webSearchPrefetch,
        CancellationToken cancellationToken)
    {
        var results = new List<AssistantToolExecution>();
        var index = 0;
        var hasProposedClientAction = context.HasProposedClientAction;
        var hasAttemptedReminder = context.HasAttemptedReminder;
        var hasAttemptedWebSearch = context.HasAttemptedWebSearch;
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
            else if (call.Name == "web_search" && hasAttemptedWebSearch)
            {
                execution = new AssistantToolExecution(
                    call.Name,
                    "rejected",
                    "В одном интерактивном ходе разрешен только один поиск в интернете.",
                    Data: ToData(new { status = "rejected", code = "web_search_already_attempted" }));
            }
            else
            {
                execution = await ExecuteOneAsync(
                    call,
                    userId,
                    sourceMessageId,
                    context,
                    apiKey,
                    webSearchPrefetch,
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
            if (call.Name == "web_search")
                hasAttemptedWebSearch = true;
            if (GetConfirmedCapabilityUse(execution) is { } usedCapability)
                await _capabilityDiscovery.MarkUsedAsync(userId, usedCapability, cancellationToken);
            results.Add(execution with { CallId = call.CallId });
        }

        return results;
    }

    private async Task<AssistantToolExecution> ExecuteOneAsync(
        AssistantToolCall call,
        string userId,
        int sourceMessageId,
        AssistantRuntimeContext context,
        string? apiKey,
        GroundedWebSearchPrefetch? webSearchPrefetch,
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
            "web_search" => await WebSearchAsync(
                GetString(call.Arguments, "query"),
                apiKey,
                webSearchPrefetch,
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
            "library_list" => LibraryList(context),
            "library_write" => await ProposeLibraryWriteAsync(
                call.Name,
                GetString(call.Arguments, "title"),
                GetString(call.Arguments, "kind"),
                GetString(call.Arguments, "html"),
                GetString(call.Arguments, "sectionTitle"),
                GetString(call.Arguments, "summary"),
                GetStringArray(call.Arguments, "sourceUrls"),
                GetString(call.Arguments, "artifactId"),
                GetString(call.Arguments, "revisionNote"),
                userId,
                sourceMessageId,
                context,
                cancellationToken),
            "library_open" => await ProposeLibraryOpenAsync(
                call.Name,
                GetString(call.Arguments, "artifactId"),
                userId,
                sourceMessageId,
                context,
                cancellationToken),
            "capability_help" => CapabilityHelp(context, GetString(call.Arguments, "topic")),
            "capability_discovery_status" => await CapabilityDiscoveryStatusAsync(userId, context, cancellationToken),
            "capability_discovery_candidates" => await CapabilityDiscoveryCandidatesAsync(userId, context, cancellationToken),
            "capability_discovery_present" => await CapabilityDiscoveryPresentAsync(
                userId,
                GetString(call.Arguments, "capabilityId"),
                context,
                cancellationToken),
            "capability_discovery_decline" => await CapabilityDiscoveryDeclineAsync(
                userId,
                GetString(call.Arguments, "capabilityId"),
                context,
                cancellationToken),
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

    private async Task<AssistantToolExecution> WebSearchAsync(
        string? query,
        string? apiKey,
        GroundedWebSearchPrefetch? prefetch,
        CancellationToken ct)
    {
        Task<GroundedWebSearchResult>? prefetchedTask = null;
        var usedPrefetch = prefetch is not null && prefetch.TryTake(out prefetchedTask);
        var result = usedPrefetch
            ? await prefetchedTask!
            : await _webSearch.SearchAsync(query, apiKey, ct);
        var resultSource = usedPrefetch ? "voice_prefetch" : "agent_tool";

        // A broad voice utterance can occasionally be too ambiguous for a
        // grounded result. In that case, retain correctness by retrying the
        // planner's more focused query instead of treating the prefetch as a
        // final answer.
        if (usedPrefetch && (result.Status is "not_grounded" or "invalid"))
        {
            result = await _webSearch.SearchAsync(query, apiKey, ct, "agent_retry_after_prefetch");
            resultSource = "agent_retry_after_prefetch";
        }

        return new("web_search", result.Status, result.Summary,
            Data: ToData(new
            {
                status = result.Status,
                summary = result.Summary,
                queryCount = result.QueryCount,
                source = resultSource,
                sources = result.Sources.Select(source => new { title = source.Title, url = source.Url })
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

    private static AssistantToolExecution LibraryList(AssistantRuntimeContext context)
    {
        if (!context.SupportsLibrary)
            return new("library_list", "unavailable", "Локальная библиотека недоступна на этом клиенте.",
                Data: ToData(new { status = "unavailable", code = "library_client_required" }));

        var items = (context.LibraryCatalog ?? []).Take(20).ToArray();
        var sections = (context.LibrarySections ?? []).Take(30).ToArray();
        return new("library_list", "ok",
            items.Length == 0 && sections.Length == 0
                ? "В локальной библиотеке пока нет разделов и книг."
                : string.Join("\n", sections.Select(section => $"[раздел: {section.Id}] {section.Title}")
                    .Concat(items.Select(item => $"[{item.Id}; раздел: {item.SectionTitle ?? "Без раздела"}; {item.Kind}; версий: {item.RevisionCount}] {item.Title}"))),
            Data: ToData(new
            {
                status = "ok",
                sections = sections.Select(section => new { id = section.Id, title = section.Title }),
                items = items.Select(item => new
                {
                    id = item.Id,
                    title = item.Title,
                    kind = item.Kind,
                    summary = item.Summary,
                    revisionCount = item.RevisionCount,
                    sectionTitle = item.SectionTitle
                })
            }));
    }

    private async Task<AssistantToolExecution> ProposeLibraryWriteAsync(
        string name,
        string? title,
        string? kind,
        string? html,
        string? sectionTitle,
        string? summary,
        IReadOnlyList<string> sourceUrls,
        string? artifactId,
        string? revisionNote,
        string userId,
        int sourceMessageId,
        AssistantRuntimeContext context,
        CancellationToken ct)
    {
        if (!context.SupportsLibrary)
            return new(name, "unavailable", "Локальная библиотека недоступна на этом клиенте.",
                Data: ToData(new { status = "unavailable", code = "library_client_required" }));

        if (!TryCreateLibraryArtifact(title, kind, html, sectionTitle, summary, sourceUrls, artifactId, revisionNote, out var artifact))
            return new(name, "invalid", "Для книги нужны корректные название, тип и статический HTML без внешнего кода.",
                Data: ToData(new { status = "invalid", code = "invalid_library_document" }));

        if (artifact.ArtifactId is not null && !(context.LibraryCatalog ?? []).Any(item => item.Id.Equals(artifact.ArtifactId, StringComparison.OrdinalIgnoreCase)))
            return new(name, "not_found", "Нельзя обновить книгу, которой нет в локальном каталоге.",
                Data: ToData(new { status = "not_found", code = "library_artifact_not_found" }));

        return await ProposeCommandAsync(
            name,
            new ExternalActionCommand(ExternalActionTypes.LibraryWrite, LibraryArtifact: artifact),
            userId,
            sourceMessageId,
            context,
            ct);
    }

    private async Task<AssistantToolExecution> ProposeLibraryOpenAsync(
        string name,
        string? artifactId,
        string userId,
        int sourceMessageId,
        AssistantRuntimeContext context,
        CancellationToken ct)
    {
        if (!context.SupportsLibrary)
            return new(name, "unavailable", "Локальная библиотека недоступна на этом клиенте.",
                Data: ToData(new { status = "unavailable", code = "library_client_required" }));
        if (!TryNormalizeLibraryId(artifactId, out var normalizedId))
            return new(name, "invalid", "Нужен корректный ID книги из локального каталога.",
                Data: ToData(new { status = "invalid", code = "invalid_library_artifact_id" }));
        if (normalizedId is not null && !(context.LibraryCatalog ?? []).Any(item => item.Id.Equals(normalizedId, StringComparison.OrdinalIgnoreCase)))
            return new(name, "not_found", "Книга не найдена в локальном каталоге.",
                Data: ToData(new { status = "not_found", code = "library_artifact_not_found" }));

        return await ProposeCommandAsync(
            name,
            new ExternalActionCommand(ExternalActionTypes.LibraryOpen, ArtifactId: normalizedId),
            userId,
            sourceMessageId,
            context,
            ct);
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

    private async Task<AssistantToolExecution> CapabilityDiscoveryCandidatesAsync(
        string userId,
        AssistantRuntimeContext context,
        CancellationToken ct)
    {
        var result = await _capabilityDiscovery.GetCandidatesAsync(userId, context, ct);
        var candidates = result.Candidates ?? [];
        return new("capability_discovery_candidates", result.Status, result.Summary,
            Data: ToData(new
            {
                status = result.Status,
                candidates = candidates.Select(candidate => new
                {
                    id = candidate.Id,
                    title = candidate.Title,
                    description = candidate.Description,
                    examples = candidate.Examples,
                    interfaceHint = candidate.InterfaceHint,
                })
            }));
    }

    private async Task<AssistantToolExecution> CapabilityDiscoveryStatusAsync(
        string userId,
        AssistantRuntimeContext context,
        CancellationToken ct)
    {
        var snapshot = await _capabilityDiscovery.GetSnapshotAsync(userId, context, ct);
        return new("capability_discovery_status", "ok",
            snapshot.Items.Count == 0
                ? "Доступных возможностей для отслеживания нет."
                : string.Join("\n", snapshot.Items.Select(item =>
                    $"{item.Title}: {item.State}; использовано: {item.UsageCount}; подсказок: {item.SuggestionCount}.")),
            Data: ToData(new
            {
                status = "ok",
                userMessageCount = snapshot.UserMessageCount,
                canSuggest = snapshot.CanSuggest,
                items = snapshot.Items.Select(item => new
                {
                    id = item.Id,
                    title = item.Title,
                    state = item.State,
                    usageCount = item.UsageCount,
                    suggestionCount = item.SuggestionCount,
                    declined = item.DeclinedAt is not null,
                })
            }));
    }

    private async Task<AssistantToolExecution> CapabilityDiscoveryPresentAsync(
        string userId,
        string? capabilityId,
        AssistantRuntimeContext context,
        CancellationToken ct)
    {
        var result = await _capabilityDiscovery.PresentAsync(userId, capabilityId, context, ct);
        return new("capability_discovery_present", result.Status, result.Summary,
            Data: ToData(new
            {
                status = result.Status,
                capability = result.Capability is null ? null : new
                {
                    id = result.Capability.Id,
                    title = result.Capability.Title,
                    description = result.Capability.Description,
                    examples = result.Capability.Examples,
                    interfaceHint = result.Capability.InterfaceHint,
                }
            }));
    }

    private async Task<AssistantToolExecution> CapabilityDiscoveryDeclineAsync(
        string userId,
        string? capabilityId,
        AssistantRuntimeContext context,
        CancellationToken ct)
    {
        var result = await _capabilityDiscovery.DeclineAsync(userId, capabilityId, context, ct);
        return new("capability_discovery_decline", result.Status, result.Summary,
            Data: ToData(new
            {
                status = result.Status,
                capability = result.Capability is null ? null : new
                {
                    id = result.Capability.Id,
                    title = result.Capability.Title,
                }
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

        return await ProposeCommandAsync(name, action, userId, sourceMessageId, context, ct);
    }

    private async Task<AssistantToolExecution> ProposeCommandAsync(
        string name,
        ExternalActionCommand action,
        string userId,
        int sourceMessageId,
        AssistantRuntimeContext context,
        CancellationToken ct)
    {
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

    private static IReadOnlyList<string> GetStringArray(JsonElement arguments, string name) =>
        arguments.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString()?.Trim())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item!)
                .Take(12)
                .ToArray()
            : [];

    private static string? NormalizeQuery(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;
        var normalized = string.Join(' ', query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= ExternalActionService.MaxQueryLength ? normalized : null;
    }

    private static bool IsVideoId(string? value) => !string.IsNullOrWhiteSpace(value) &&
        value.Length == 11 && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');

    private static bool TryCreateLibraryArtifact(
        string? title,
        string? kind,
        string? html,
        string? sectionTitle,
        string? summary,
        IReadOnlyList<string> sourceUrls,
        string? artifactId,
        string? revisionNote,
        out LibraryArtifactAction artifact)
    {
        artifact = default!;
        var normalizedTitle = NormalizeLibraryText(title, 120);
        var normalizedHtml = html?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedTitle) || string.IsNullOrWhiteSpace(normalizedHtml) || normalizedHtml.Length is < 32 or > 96_000 ||
            UnsafeLibraryMarkupPattern.IsMatch(normalizedHtml) ||
            !TryNormalizeLibraryId(artifactId, out var normalizedId))
        {
            return false;
        }

        var kindCandidate = kind?.Trim().ToLowerInvariant();
        var normalizedKind = kindCandidate switch
        {
            "recipes" or "restaurants" or "entertainment" or "guide" or "other" => kindCandidate,
            _ => "other"
        };
        var safeUrls = sourceUrls
            .Select(url => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? uri.AbsoluteUri : null)
            .Where(url => url is not null)
            .Select(url => url!)
            .Distinct(StringComparer.Ordinal)
            .Take(12)
            .ToArray();
        artifact = new LibraryArtifactAction(
            normalizedId,
            normalizedTitle!,
            normalizedKind,
            normalizedHtml,
            NormalizeLibraryText(sectionTitle, 60),
            NormalizeLibraryText(summary, 600),
            safeUrls,
            NormalizeLibraryText(revisionNote, 220));
        return true;
    }

    private static bool TryNormalizeLibraryId(string? raw, out string? id)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            id = null;
            return true;
        }
        if (!Guid.TryParse(raw, out var parsed))
        {
            id = null;
            return false;
        }
        id = parsed.ToString();
        return true;
    }

    private static string? NormalizeLibraryText(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength];
    }

    private static bool IsClientAction(string name) => name is "open_vass" or "youtube_search" or "youtube_watch" or "library_write" or "library_open";
    private static bool IsReminderTool(string name) => name is "reminder_create" or "periodic_reminder_create";

    private static string? GetConfirmedCapabilityUse(AssistantToolExecution execution) => (execution.Name, execution.Status) switch
    {
        ("memory_status", "available") or
        ("memory_list", "ok") or
        ("memory_search", "ok") or
        ("memory_search", "not_found") or
        ("memory_remember", "remembered") or
        ("memory_remember", "already_known") or
        ("memory_correct", "corrected") or
        ("memory_forget", "forgotten") => "memory",
        ("reminder_create", "created") or
        ("periodic_reminder_create", "created") or
        ("reminder_list", "ok") or
        ("reminder_cancel", "cancelled") => "reminders",
        ("library_list", "ok") => "library",
        _ => null,
    };

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
