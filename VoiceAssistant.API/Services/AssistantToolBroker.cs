using System.Text.Json;

namespace VoiceAssistant.API.Services;

public sealed record AssistantToolExecution(
    string Name,
    string Status,
    string Summary,
    ExternalActionCommand? ExternalAction = null,
    Guid? ActionReceiptId = null,
    bool RequestsScreenCapture = false);

// The only authority that turns a model proposal into a side effect. Every
// branch is owner-scoped and validates its arguments independently of Gemini.
public sealed class AssistantToolBroker
{
    private readonly MemoryItemService _memory;
    private readonly ActionReceiptService _actionReceipts;

    public AssistantToolBroker(MemoryItemService memory, ActionReceiptService actionReceipts)
    {
        _memory = memory;
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
        foreach (var call in calls.Take(3))
        {
            results.Add(await ExecuteOneAsync(call, userId, sourceMessageId, context, cancellationToken));
        }
        return results;
    }

    private async Task<AssistantToolExecution> ExecuteOneAsync(
        AssistantToolCall call,
        string userId,
        int sourceMessageId,
        AssistantRuntimeContext context,
        CancellationToken cancellationToken)
    {
        return call.Name switch
        {
            "memory_status" => await MemoryStatusAsync(userId, cancellationToken),
            "memory_list" => await MemoryListAsync(userId, cancellationToken),
            "memory_search" => await MemorySearchAsync(userId, GetString(call.Arguments, "query"), cancellationToken),
            "memory_remember" => await RememberAsync(userId, sourceMessageId, GetString(call.Arguments, "text"), cancellationToken),
            "memory_correct" => await CorrectAsync(userId, GetString(call.Arguments, "memoryId"), GetString(call.Arguments, "text"), cancellationToken),
            "memory_forget" => await ForgetAsync(userId, GetString(call.Arguments, "memoryId"), cancellationToken),
            "screen_capture_once" => context.SupportsScreenAnalysis
                ? new(call.Name, "requested", "Запрошен один снимок экрана с системным подтверждением пользователя.", RequestsScreenCapture: true)
                : new(call.Name, "unavailable", "Снимок экрана недоступен на этом устройстве или в этом режиме."),
            "open_vass" => await ProposeActionAsync(call.Name, ExternalActionTypes.OpenVass, null, null, userId, sourceMessageId, context, cancellationToken),
            "youtube_search" => await ProposeActionAsync(call.Name, ExternalActionTypes.YouTubeSearch, GetString(call.Arguments, "query"), null, userId, sourceMessageId, context, cancellationToken),
            "youtube_watch" => await ProposeActionAsync(call.Name, ExternalActionTypes.YouTubeWatch, null, GetString(call.Arguments, "videoId"), userId, sourceMessageId, context, cancellationToken),
            _ => new(call.Name, "rejected", "Инструмент недоступен.")
        };
    }

    private async Task<AssistantToolExecution> MemoryStatusAsync(string userId, CancellationToken ct)
    {
        var status = await _memory.GetStatusAsync(userId, ct);
        return new("memory_status", status.Availability, $"Память: {status.Availability}; записей: {status.ActiveCount}.");
    }

    private async Task<AssistantToolExecution> MemoryListAsync(string userId, CancellationToken ct)
    {
        var items = await _memory.ListAsync(userId, 20, ct);
        return new("memory_list", "ok", FormatItems(items));
    }

    private async Task<AssistantToolExecution> MemorySearchAsync(string userId, string? query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query)) return new("memory_search", "invalid", "Не указан поисковый запрос.");
        var result = await _memory.SearchAsync(userId, query, ct);
        return new("memory_search", result.Status, FormatItems(result.Items));
    }

    private async Task<AssistantToolExecution> RememberAsync(string userId, int sourceMessageId, string? text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 1000)
            return new("memory_remember", "invalid", "Нужна одна короткая осмысленная запись памяти.");
        var result = await _memory.RememberAsync(userId, text, sourceMessageId, Guid.NewGuid(), ct);
        return new("memory_remember", result.Code, result.Code is "remembered" or "already_known"
            ? $"Запись памяти подтверждена: {text.Trim()}"
            : "Запись памяти не подтверждена.");
    }

    private async Task<AssistantToolExecution> CorrectAsync(string userId, string? id, string? text, CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var memoryId) || string.IsNullOrWhiteSpace(text) || text.Length > 1000)
            return new("memory_correct", "invalid", "Нужны ID найденной записи и новая формулировка.");
        var result = await _memory.CorrectAsync(userId, memoryId, text, Guid.NewGuid(), ct);
        return new("memory_correct", result.Code, result.Code == "corrected" ? $"Запись исправлена: {text.Trim()}" : "Запись не была исправлена.");
    }

    private async Task<AssistantToolExecution> ForgetAsync(string userId, string? id, CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var memoryId)) return new("memory_forget", "invalid", "Нужен ID конкретной записи памяти.");
        var result = await _memory.ForgetAsync(userId, memoryId, Guid.NewGuid(), ct);
        return new("memory_forget", result.Code, result.Code == "forgotten" ? "Запись удалена из активной памяти." : "Запись не была удалена.");
    }

    private async Task<AssistantToolExecution> ProposeActionAsync(
        string name, string type, string? query, string? videoId, string userId, int sourceMessageId,
        AssistantRuntimeContext context, CancellationToken ct)
    {
        if (!context.SupportsExternalActions) return new(name, "unavailable", "Действие недоступно на этом клиенте.");
        var action = type switch
        {
            ExternalActionTypes.OpenVass => new ExternalActionCommand(type),
            ExternalActionTypes.YouTubeSearch when NormalizeQuery(query) is { } safeQuery => new ExternalActionCommand(type, Query: safeQuery),
            ExternalActionTypes.YouTubeWatch when IsVideoId(videoId) => new ExternalActionCommand(type, VideoId: videoId),
            _ => null
        };
        if (action is null) return new(name, "invalid", "Аргументы действия некорректны.");
        var receipt = await _actionReceipts.ProposeAsync(userId, sourceMessageId, action, ct);
        return receipt is null
            ? new(name, "rejected", "Действие не разрешено политикой Vass.")
            : new(name, "proposed", "Команда передана клиенту для исполнения.", action, receipt.ActionId);
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

    private static string FormatItems(IEnumerable<MemoryItemResponse> items)
    {
        var selected = items.Take(20).Select(item => $"[{item.Id}] {item.Text}").ToArray();
        return selected.Length == 0 ? "Подходящих записей нет." : string.Join("\n", selected);
    }
}
