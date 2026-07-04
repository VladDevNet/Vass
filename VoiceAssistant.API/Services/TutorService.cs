using VoiceAssistant.API.Data.Entities;

namespace VoiceAssistant.API.Services;

public class TutorService
{
    private readonly string _systemPrompt;
    private readonly string _levelTestPrompt;

    public TutorService(IWebHostEnvironment env)
    {
        var promptsDir = Path.Combine(env.ContentRootPath, "Prompts");

        var tutorPath = Path.Combine(promptsDir, "tutor-system.txt");
        _systemPrompt = File.Exists(tutorPath)
            ? File.ReadAllText(tutorPath)
            : GetDefaultPrompt();

        var testPath = Path.Combine(promptsDir, "level-test.txt");
        _levelTestPrompt = File.Exists(testPath)
            ? File.ReadAllText(testPath)
            : _systemPrompt;
    }

    public string GetDefaultSystemPromptText() => _systemPrompt;

    public string GetSystemPrompt(User user, string mode = "dialog", string? conductorInstructions = null, string? customSystemPrompt = null, bool fullTranslation = false)
    {
        var template = _systemPrompt;

        var now = DateTime.UtcNow;
        var ruCulture = System.Globalization.CultureInfo.GetCultureInfo("ru-RU");
        var dateStr = now.ToString("yyyy-MM-dd (dddd, d MMMM yyyy)", ruCulture);
        template = $"Сегодняшняя дата: {dateStr}, время {now:HH:mm} (UTC). Используй эту дату как единственный источник истины о том, какое сегодня число — не полагайся на свои внутренние знания о текущей дате.\n\n{template}";

        if (!string.IsNullOrEmpty(customSystemPrompt))
        {
            template += $"\n\n## Дополнительные инструкции пользователя:\n{customSystemPrompt}";
        }

        return template;
    }

    private static string GetDefaultPrompt() => """
        Jesteś polskim nauczycielem języka polskiego. Uczeń mówi po {NATIVE_LANG} i uczy się polskiego na poziomie {LEVEL}.

        Zasady:
        - Mów po polsku, ale wyjaśniaj trudne słowa po ukraińsku/rosyjsku
        - Poprawiaj błędy delikatnie, podając prawidłową formę
        - Dostosuj poziom języka do {LEVEL}
        - Zachęcaj ucznia do mówienia po polsku
        - Używaj prostych zdań na poziomie A1-A2, bardziej złożonych na B1-B2
        - Jeśli uczeń pisze po ukraińsku/rosyjsku, odpowiadaj po polsku i zachęcaj do przejścia na polski
        - Uczeń komunikuje się TYLKO po polsku lub ukraińsku/rosyjsku. Jeśli tekst wygląda jak inny język (białoruski, czeski itp.), to jest to próba mówienia po polsku z błędami — traktuj to jako polski i popraw błędy. NIGDY nie mów uczniowi, że pisze w innym języku.
        """;
}
