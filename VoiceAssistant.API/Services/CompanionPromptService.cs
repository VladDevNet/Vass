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

    public string GetSystemPrompt(string? customSystemPrompt = null, string? userName = null)
    {
        var now = DateTime.UtcNow;
        var ruCulture = System.Globalization.CultureInfo.GetCultureInfo("ru-RU");
        var dateStr = now.ToString("yyyy-MM-dd (dddd, d MMMM yyyy)", ruCulture);
        var template = $"Сегодняшняя дата: {dateStr}, время {now:HH:mm} (UTC). Используй эту дату как единственный источник истины о том, какое сегодня число — не полагайся на свои внутренние знания о текущей дате.\n\n{_systemPrompt}";

        if (!string.IsNullOrWhiteSpace(userName))
        {
            template += $"\n\nПользователя зовут {userName}. Обращайся к нему по имени, когда это уместно — не в каждой реплике.";
        }

        if (!string.IsNullOrEmpty(customSystemPrompt))
        {
            template += $"\n\n## Дополнительные инструкции пользователя:\n{customSystemPrompt}";
        }

        return template;
    }
}
