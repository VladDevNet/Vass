namespace PolishTutor.Api.Data.Entities;

public class UserSettings
{
    public int Id { get; set; }
    public string UserId { get; set; } = null!;
    public User User { get; set; } = null!;

    public string? DisplayName { get; set; }
    public string InterfaceLanguage { get; set; } = "uk";
    public string? OpenAiApiKey { get; set; }
    public string? AnthropicApiKey { get; set; }
    public string? GeminiApiKey { get; set; }
    public string? CustomSystemPrompt { get; set; }
    public bool FullTranslation { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
