using System.Text.Json;

namespace VoiceAssistant.API.Services;

public sealed record AssistantCapabilityDescriptor(
    string Id,
    string State,
    string Surface,
    string Description);

public sealed record AssistantCapabilitySnapshot(
    int Version,
    IReadOnlyList<AssistantCapabilityDescriptor> Capabilities);

public sealed record AssistantRuntimeContext(
    bool HasVisualAttachment,
    bool SupportsScreenAnalysis);

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
            new("memory.status", _memoryEnabled ? "available" : "disabled", "server", "Статус памяти"),
            new("memory.list", _memoryEnabled ? "available" : "disabled", "server", "Список сохраненной памяти"),
            new("memory.search", _memoryEnabled ? "available" : "disabled", "server", "Поиск по памяти"),
            new("memory.remember", _memoryEnabled ? "available" : "disabled", "server", "Явное сохранение после подтвержденной записи"),
            new("memory.correct", _memoryEnabled ? "available" : "disabled", "server", "Исправление сохраненной записи"),
            new("memory.forget", _memoryEnabled ? "available" : "disabled", "server", "Удаление одной сохраненной записи"),
            new("memory.clear", _memoryEnabled ? "confirmation_required" : "disabled", "server", "Очистка памяти с отдельным подтверждением"),
            new("visual.input", context.HasVisualAttachment ? "attached" : "user_control_only", "client", "Камера, галерея и share доступны только пользователю через UI"),
            new("screen.analysis", context.SupportsScreenAnalysis ? "available_with_consent" : "unavailable", "client", "Снимок экрана требует системного согласия")
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
