using PolishTutor.Api.Data.Entities;

namespace PolishTutor.Api.Services;

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
        var template = mode == "level_test" ? _levelTestPrompt : _systemPrompt;

        // Add vocabulary marking instruction for regular chat
        if (mode != "level_test")
        {
            template += "\n\n" +
                "Kiedy wprowadzasz nowe lub ważne słowo, oznacz je w formacie [słowo|tłumaczenie], " +
                "np. [przystojny|гарний]. Uczeń zobaczy przycisk dodania do słownika.";

            template += "\n\n" +
                "Masz dostępne narzędzia (tools) do zarządzania słownikiem ucznia i śledzenia błędów. " +
                "Na początku rozmowy użyj get_learner_context aby poznać kontekst ucznia. " +
                "Gdy uczeń popełnia błąd, użyj record_error. " +
                "Gdy wprowadzasz nowe słowo, użyj save_word. " +
                "Używaj lookup_vocabulary aby sprawdzić czy uczeń zna dane słowo.";
        }

        if (fullTranslation && mode != "level_test")
        {
            template += "\n\n" +
                "WAŻNE: NIE dodawaj tłumaczeń słowo po słowie, tłumaczeń w nawiasach ani ze strzałkami → w swoich odpowiedziach. " +
                "Tłumaczenie jest generowane automatycznie osobno. " +
                "Pisz TYLKO swoją odpowiedź po polsku. Możesz wyjaśnić trudne słowo, ale NIE tłumacz każdego słowa.";
        }

        if (!string.IsNullOrEmpty(customSystemPrompt))
        {
            template += $"\n\n## Додаткові інструкції від учня:\n{customSystemPrompt}";
        }

        if (!string.IsNullOrEmpty(conductorInstructions))
        {
            template += $"\n\n## Інструкції від координатора навчання:\n{conductorInstructions}";
        }

        return template
            .Replace("{LEVEL}", user.Level)
            .Replace("{NATIVE_LANG}", user.NativeLang switch
            {
                "ru" => "rosyjski",
                _ => "ukraiński"
            });
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
