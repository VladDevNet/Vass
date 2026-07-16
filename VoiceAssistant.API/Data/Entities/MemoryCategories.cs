namespace VoiceAssistant.API.Data.Entities;

// Stable, server-owned identifiers. The client localizes labels, while the
// model can choose only one of these values when it deliberately saves memory.
public static class MemoryCategories
{
    public const string Other = "other";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        "profile", "family", "contacts", "health", "medications", "allergies", "habits",
        "work", "education", "finance", "home", "pets", "shopping", "recipes", "food",
        "travel", "transport", "events", "tasks", "projects", "hobbies", "books", "films",
        "music", "games", "technology", "links", "documents", Other
    };

    public static bool IsValid(string? value) =>
        !string.IsNullOrWhiteSpace(value) && All.Contains(value.Trim().ToLowerInvariant());

    public static string NormalizeOrDefault(string? value, string defaultValue = Other)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        return IsValid(normalized) ? normalized! : defaultValue;
    }
}
