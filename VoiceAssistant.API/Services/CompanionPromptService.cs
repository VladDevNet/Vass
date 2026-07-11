namespace VoiceAssistant.API.Services;

public class CompanionPromptService
{
    private readonly string _systemPrompt;

    public CompanionPromptService(IWebHostEnvironment env)
    {
        var promptPath = Path.Combine(env.ContentRootPath, "Prompts", "companion-system.txt");
        _systemPrompt = File.ReadAllText(promptPath);
    }

    public string GetDefaultSystemPromptText() => _systemPrompt;

    public string GetSystemPrompt(string? customSystemPrompt = null, string? userName = null, string? assistantName = null, string? mediumTermSummary = null)
        => BuildSystemPrompt(_systemPrompt, DateTime.UtcNow, customSystemPrompt, userName, assistantName, mediumTermSummary);

    public static string BuildSystemPrompt(string basePrompt, DateTime now, string? customSystemPrompt = null,
        string? userName = null, string? assistantName = null, string? mediumTermSummary = null)
    {
        var ruCulture = System.Globalization.CultureInfo.GetCultureInfo("ru-RU");
        var dateStr = now.ToString("yyyy-MM-dd (dddd, d MMMM yyyy)", ruCulture);
        var template = $"Сегодняшняя дата: {dateStr}, время {now:HH:mm} (UTC). Используй эту дату как единственный источник истины о том, какое сегодня число — не полагайся на свои внутренние знания о текущей дате.\n\n{basePrompt}";

        if (!string.IsNullOrWhiteSpace(assistantName))
        {
            template += $"\n\nТебя зовут {assistantName}. Если пользователь спросит, как тебя зовут, или упомянет тебя по имени, отзывайся на это имя.";
        }

        if (!string.IsNullOrWhiteSpace(userName))
        {
            template += $"\n\nПользователя зовут {userName}. Обращайся к нему по имени, когда это уместно — не в каждой реплике.";
        }

        if (!string.IsNullOrEmpty(customSystemPrompt))
        {
            template += $"\n\n## Дополнительные инструкции пользователя:\n{customSystemPrompt}";
        }

        if (!string.IsNullOrEmpty(mediumTermSummary))
        {
            template += $"\n\n## Память о более ранней части разговора:\n{mediumTermSummary}";
        }

        return template;
    }
}
