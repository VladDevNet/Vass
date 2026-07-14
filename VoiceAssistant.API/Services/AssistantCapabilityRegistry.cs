using System.Text.Json;

namespace VoiceAssistant.API.Services;

public sealed record AssistantCapabilityDescriptor(
    string Id,
    string State,
    string Surface,
    string Taxonomy,
    string Description);

public sealed record AssistantCapabilitySnapshot(
    int Version,
    IReadOnlyList<AssistantCapabilityDescriptor> Capabilities);

public sealed record AssistantRuntimeContext(
    bool HasVisualAttachment,
    bool SupportsScreenAnalysis,
    bool SupportsExternalActions,
    bool SupportsReminders);

// This registry is intentionally declarative. It describes what this turn can
// actually do; it never turns UI-only controls into model-callable actions.
public sealed class AssistantCapabilityRegistry
{
    public const int SnapshotVersion = 1;
    private readonly bool _memoryEnabled;

    public AssistantCapabilityRegistry(IConfiguration configuration)
    {
        _memoryEnabled = configuration.GetValue("Features:LongTermMemoryEnabled", true);
    }

    public AssistantCapabilitySnapshot GetSnapshot(AssistantRuntimeContext context) => new(
        SnapshotVersion,
        [
            new("memory.status", _memoryEnabled ? "available" : "disabled", "server", AssistantActionTaxonomies.ServerLocal, "Статус памяти"),
            new("memory.list", _memoryEnabled ? "available" : "disabled", "server", AssistantActionTaxonomies.ServerLocal, "Список сохраненной памяти"),
            new("memory.search", _memoryEnabled ? "available" : "disabled", "server", AssistantActionTaxonomies.ServerLocal, "Поиск по памяти"),
            new("memory.remember", _memoryEnabled ? "available" : "disabled", "server", AssistantActionTaxonomies.ServerLocal, "Явное сохранение после подтвержденной записи"),
            new("memory.correct", _memoryEnabled ? "available" : "disabled", "server", AssistantActionTaxonomies.ServerLocal, "Исправление сохраненной записи"),
            new("memory.forget", _memoryEnabled ? "available" : "disabled", "server", AssistantActionTaxonomies.ServerLocal, "Удаление одной сохраненной записи"),
            new("memory.clear", _memoryEnabled ? "confirmation_required" : "disabled", "server", AssistantActionTaxonomies.ServerLocal, "Очистка памяти с отдельным подтверждением"),
            new("reminder.schedule", context.SupportsReminders ? "available" : "device_context_required", "server", AssistantActionTaxonomies.ServerLocal, "Локальное напоминание требует связанный телефон и его подтверждение"),
            new("navigation.open_vass", context.SupportsExternalActions ? "available" : "unavailable", "client", AssistantActionTaxonomies.Navigation, "Развернуть Vass; клиент подтверждает только передачу команды в handler"),
            new("youtube.search", context.SupportsExternalActions ? "available" : "unavailable", "client", AssistantActionTaxonomies.External, "Открыть разрешенный поиск YouTube; не подтверждает воспроизведение"),
            new("youtube.watch", context.SupportsExternalActions ? "available" : "unavailable", "client", AssistantActionTaxonomies.External, "Открыть конкретный валидный ролик YouTube; не подтверждает воспроизведение"),
            new("provider.web_search", "available", "provider", AssistantActionTaxonomies.ProviderHosted, "Провайдер может использовать web search внутри ответа; это не действие на устройстве"),
            new("visual.input", context.HasVisualAttachment ? "attached" : "user_control_only", "client", AssistantActionTaxonomies.UserControl, "Камера, галерея и share доступны только пользователю через UI"),
            new("screen.analysis", context.SupportsScreenAnalysis ? "available_with_consent" : "unavailable", "client", AssistantActionTaxonomies.UserControl, "Снимок экрана требует системного согласия")
        ]);

    public string BuildPromptManifest(AssistantRuntimeContext context)
    {
        var snapshot = GetSnapshot(context);
        var lines = snapshot.Capabilities
            .Select(item => $"- {item.Id}: {item.State}. {item.Description}");
        return "## Возможности этого хода\n" + string.Join('\n', lines) +
               "\nНе обещай действие, которого нет в manifest. Камера, галерея, файлы и screen capture запускаются только пользователем в UI. " +
               "Говори «я запомнила», «исправила» или «забыла» только когда в системном контексте есть подтвержденный receipt операции памяти.";
    }

    public static string SerializeContentFreeSnapshot(AssistantCapabilitySnapshot snapshot) =>
        JsonSerializer.Serialize(snapshot);
}
