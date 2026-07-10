# Среднесрочная память — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Складывать выпадающую из краткосрочного окна историю разговора в сводку (`ChatSession.MediumTermSummary`), которая читается в системный промпт со следующего хода после срабатывания порога.

**Architecture:** Side-call внутри `ChatController.Send()`, по образцу уже трижды использованного паттерна (`GeminiService.StreamResponseAsync` с одним `"user"`-сообщением, без grounding). Новый `ConversationMemoryService` инкапсулирует пороговую логику (два независимых порога — накопление и пересборка) и side-call'ы. Никакого фонового воркера.

**Tech Stack:** ASP.NET Core 10 / EF Core 10 / Npgsql / Gemini API (`gemini-3.5-flash`). Новый тестовый проект: xUnit (`Microsoft.NET.Test.Sdk` + `xunit.runner.visualstudio`), первый тестовый проект в решении.

## Global Constraints

- Спека: `docs/superpowers/specs/2026-07-10-medium-term-memory-design.md` — читать перед реализацией, это источник истины по требованиям.
- Пороги — константы в коде, не выносятся в конфиг: `SummarizationThresholdChars = 200_000`, `CompactionThresholdChars = 100_000`.
- Целевой размер добавляемого фрагмента сводки: ~2000–4000 символов. Целевой размер после пересборки: ~10 000 символов.
- Side-call'ы: `model: "gemini-3.5-flash"`, `enableGrounding: false`, `systemPrompt: ""` (все инструкции — в одном `"user"`-сообщении), апеллировать к уже проверенному паттерну `MaybeUpdateCustomInstructionsAsync` (`ChatController.cs:706-759`).
- Триггер — строго ПОСЛЕ сохранения ОБЕИХ реплик хода (`ChatController.cs:443-445`), не раньше.
- Тайминг вступления в силу — с хода N+1: `session.MediumTermSummary`, прочитанный при сборке системного промпта ТЕКУЩЕГО хода (до вызова `CheckAndUpdateAsync`), уже отражает состояние ДО этого хода — новых side-call'ов этого же хода в промпте текущего хода не будет.
- Ошибка side-call'а → логируем (`LogWarning`), `MediumTermSummary`/`LastSummarizedMessageId` не трогаем — самовосстановление на следующем ходе.
- `TargetFramework net10.0`, `Nullable enable`, `ImplicitUsings enable` — как в `VoiceAssistant.API.csproj`, тестовый проект следует тем же настройкам.
- Никакого фонового воркера/`IHostedService` — сознательно вне рамок (см. спеку, раздел «Не в этой фиче»).
- Новый тестовый проект живёт в `VoiceAssistant.API.Tests/`, соседней с `VoiceAssistant.API/` директорией. Docker-сборка (`docker-compose.yml`: `build: ./VoiceAssistant.API`) использует ТОЛЬКО поддиректорию `VoiceAssistant.API/` как build context — новый проект вне её, деплой не затрагивает.

---

## Task 1: Модель данных — поля ChatSession + миграция

**Files:**
- Modify: `VoiceAssistant.API/Data/Entities/ChatSession.cs`
- Create: `VoiceAssistant.API/Migrations/<timestamp>_AddMediumTermMemoryFields.cs` (генерируется инструментом)
- Create: `VoiceAssistant.API/Migrations/<timestamp>_AddMediumTermMemoryFields.Designer.cs` (генерируется инструментом)
- Modify: `VoiceAssistant.API/Migrations/AppDbContextModelSnapshot.cs` (обновляется инструментом)

**Interfaces:**
- Produces: `ChatSession.MediumTermSummary` (`string?`), `ChatSession.LastSummarizedMessageId` (`int?`) — читаются/пишутся в Task 2 и Task 3.

Никакого Fluent-конфига в `AppDbContext.cs` не требуется — оба поля простые nullable без ограничений длины, ровно как `ChatSession.Title` (`string?`, без `HasMaxLength`) и `Message.Content` (`string`, без `HasMaxLength` — маппится Npgsql в неограниченный `text`).

- [ ] **Step 1: Добавить поля в сущность**

Открыть `VoiceAssistant.API/Data/Entities/ChatSession.cs`, заменить содержимое на:

```csharp
namespace VoiceAssistant.API.Data.Entities;

public class ChatSession
{
    public int Id { get; set; }
    public string UserId { get; set; } = null!;
    public User User { get; set; } = null!;
    public string Mode { get; set; } = "dialog"; // dialog, lesson, situation
    public string? Title { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string? MediumTermSummary { get; set; }
    public int? LastSummarizedMessageId { get; set; }

    public ICollection<Message> Messages { get; set; } = [];
}
```

- [ ] **Step 2: Проверить сборку**

Run: `dotnet build` (из директории `VoiceAssistant.API/`)
Expected: `Build succeeded.`

- [ ] **Step 3: Сгенерировать миграцию**

Run (из директории `VoiceAssistant.API/`): `dotnet ef migrations add AddMediumTermMemoryFields`

Expected: команда создаёт `Migrations/<timestamp>_AddMediumTermMemoryFields.cs`, `.Designer.cs`, обновляет `AppDbContextModelSnapshot.cs`.

- [ ] **Step 4: Проверить содержимое сгенерированной миграции**

Открыть `Migrations/<timestamp>_AddMediumTermMemoryFields.cs` — метод `Up` должен содержать ровно два `AddColumn`, метод `Down` — два `DropColumn`, в том же порядке (или обратном) на таблицу `ChatSessions`:

```csharp
protected override void Up(MigrationBuilder migrationBuilder)
{
    migrationBuilder.AddColumn<string>(
        name: "MediumTermSummary",
        table: "ChatSessions",
        type: "text",
        nullable: true);

    migrationBuilder.AddColumn<int>(
        name: "LastSummarizedMessageId",
        table: "ChatSessions",
        type: "integer",
        nullable: true);
}

protected override void Down(MigrationBuilder migrationBuilder)
{
    migrationBuilder.DropColumn(
        name: "LastSummarizedMessageId",
        table: "ChatSessions");

    migrationBuilder.DropColumn(
        name: "MediumTermSummary",
        table: "ChatSessions");
}
```

Если сгенерированный тип/имя таблицы отличается (например, EF выбрал другой маппинг) — остановиться и разобраться, не подгонять вручную без понимания причины (см. `superpowers:systematic-debugging`, если расхождение необъяснимо).

- [ ] **Step 5: Commit**

```bash
git add VoiceAssistant.API/Data/Entities/ChatSession.cs VoiceAssistant.API/Migrations/
git commit -m "Add MediumTermSummary/LastSummarizedMessageId fields to ChatSession"
```

---

## Task 2: ConversationMemoryService — пороговая логика + side-call'ы

**Files:**
- Create: `VoiceAssistant.API.Tests/VoiceAssistant.API.Tests.csproj` (новый тестовый проект — первый в решении)
- Create: `VoiceAssistant.API.Tests/ConversationMemoryServiceTests.cs`
- Create: `VoiceAssistant.API/Services/ConversationMemoryService.cs`
- Modify: `VoiceAssistant.API/Program.cs` — регистрация DI

**Interfaces:**
- Consumes: `GeminiService.StreamResponseAsync(string systemPrompt, List<GeminiMessage> messages, string model, int maxTokens, string? apiKey, bool enableGrounding, CancellationToken ct)` (существующий), `AppDbContext` (существующий), `ChatSession`/`Message` из Task 1.
- Produces: `ConversationMemoryService.CheckAndUpdateAsync(ChatSession session, string? geminiApiKey, CancellationToken ct): Task` — вызывается в Task 3 из `ChatController.Send()`. Публичные статические `ShouldSummarize`, `UnsummarizedCharCount`, `ShouldCompact`, константы `SummarizationThresholdChars`/`CompactionThresholdChars` — используются только внутри этого файла и тестами, но объявлены `public` для прямой проверки в тестах без рефлексии.

### Bootstrap тестового проекта

- [ ] **Step 1: Создать проект**

Run (из корня репозитория `D:\Repos\Vass`): `dotnet new xunit -n VoiceAssistant.API.Tests -o VoiceAssistant.API.Tests`

Expected: создана директория `VoiceAssistant.API.Tests/` с `VoiceAssistant.API.Tests.csproj`, `UnitTest1.cs`, `GlobalUsings.cs` (или аналог, в зависимости от версии шаблона).

- [ ] **Step 2: Проверить/поправить TargetFramework**

Открыть `VoiceAssistant.API.Tests/VoiceAssistant.API.Tests.csproj`. Если `<TargetFramework>` не `net10.0` — исправить на `net10.0`, чтобы совпадать с `VoiceAssistant.API.csproj`. Убедиться, что `<Nullable>enable</Nullable>` и `<ImplicitUsings>enable</ImplicitUsings>` присутствуют (шаблон обычно добавляет их сам).

- [ ] **Step 3: Добавить ссылку на основной проект**

Run (из корня репозитория): `dotnet add VoiceAssistant.API.Tests/VoiceAssistant.API.Tests.csproj reference VoiceAssistant.API/VoiceAssistant.API.csproj`

- [ ] **Step 4: Удалить шаблонный placeholder-тест**

Удалить `VoiceAssistant.API.Tests/UnitTest1.cs` — реальные тесты идут ниже, в `ConversationMemoryServiceTests.cs`.

- [ ] **Step 5: Проверить, что пустой проект собирается**

Run: `dotnet build VoiceAssistant.API.Tests/VoiceAssistant.API.Tests.csproj`
Expected: `Build succeeded.`

### TDD: пороговая логика

- [ ] **Step 6: Написать падающие тесты**

Создать `VoiceAssistant.API.Tests/ConversationMemoryServiceTests.cs`:

```csharp
using VoiceAssistant.API.Data.Entities;
using VoiceAssistant.API.Services;
using Xunit;

namespace VoiceAssistant.API.Tests;

public class ConversationMemoryServiceTests
{
    private static Message Msg(int id, string content) =>
        new() { Id = id, Content = content, Role = "user", ChatSessionId = 1 };

    [Fact]
    public void UnsummarizedCharCount_SumsOnlyMessagesAfterCursor()
    {
        var messages = new List<Message>
        {
            Msg(1, new string('a', 100)),
            Msg(2, new string('b', 200)),
            Msg(3, new string('c', 300)),
        };

        var count = ConversationMemoryService.UnsummarizedCharCount(messages, lastSummarizedMessageId: 1);

        Assert.Equal(500, count);
    }

    [Fact]
    public void UnsummarizedCharCount_NullCursor_CountsAllMessages()
    {
        var messages = new List<Message> { Msg(1, new string('a', 100)), Msg(2, new string('b', 200)) };

        var count = ConversationMemoryService.UnsummarizedCharCount(messages, lastSummarizedMessageId: null);

        Assert.Equal(300, count);
    }

    [Fact]
    public void ShouldSummarize_BelowThreshold_ReturnsFalse()
    {
        var messages = new List<Message> { Msg(1, new string('a', ConversationMemoryService.SummarizationThresholdChars - 1)) };

        Assert.False(ConversationMemoryService.ShouldSummarize(messages, lastSummarizedMessageId: null));
    }

    [Fact]
    public void ShouldSummarize_AtThreshold_ReturnsTrue()
    {
        var messages = new List<Message> { Msg(1, new string('a', ConversationMemoryService.SummarizationThresholdChars)) };

        Assert.True(ConversationMemoryService.ShouldSummarize(messages, lastSummarizedMessageId: null));
    }

    [Fact]
    public void ShouldCompact_AtThreshold_ReturnsFalse()
    {
        var summary = new string('a', ConversationMemoryService.CompactionThresholdChars);

        Assert.False(ConversationMemoryService.ShouldCompact(summary));
    }

    [Fact]
    public void ShouldCompact_AboveThreshold_ReturnsTrue()
    {
        var summary = new string('a', ConversationMemoryService.CompactionThresholdChars + 1);

        Assert.True(ConversationMemoryService.ShouldCompact(summary));
    }

    [Fact]
    public void ShouldCompact_NullSummary_ReturnsFalse()
    {
        Assert.False(ConversationMemoryService.ShouldCompact(null));
    }
}
```

- [ ] **Step 7: Запустить тесты, убедиться что не компилируется**

Run: `dotnet test VoiceAssistant.API.Tests/VoiceAssistant.API.Tests.csproj`
Expected: ошибка сборки — тип `ConversationMemoryService` не существует.

- [ ] **Step 8: Реализовать пороговую логику**

Создать `VoiceAssistant.API/Services/ConversationMemoryService.cs`:

```csharp
using VoiceAssistant.API.Data;
using VoiceAssistant.API.Data.Entities;

namespace VoiceAssistant.API.Services;

public class ConversationMemoryService
{
    public const int SummarizationThresholdChars = 200_000;
    public const int CompactionThresholdChars = 100_000;

    private readonly AppDbContext _db;
    private readonly GeminiService _gemini;
    private readonly ILogger<ConversationMemoryService> _logger;

    public ConversationMemoryService(AppDbContext db, GeminiService gemini, ILogger<ConversationMemoryService> logger)
    {
        _db = db;
        _gemini = gemini;
        _logger = logger;
    }

    public static int UnsummarizedCharCount(IReadOnlyList<Message> messages, int? lastSummarizedMessageId)
    {
        var cursor = lastSummarizedMessageId ?? 0;
        return messages.Where(m => m.Id > cursor).Sum(m => m.Content.Length);
    }

    public static bool ShouldSummarize(IReadOnlyList<Message> messages, int? lastSummarizedMessageId)
        => UnsummarizedCharCount(messages, lastSummarizedMessageId) >= SummarizationThresholdChars;

    public static bool ShouldCompact(string? mediumTermSummary)
        => (mediumTermSummary?.Length ?? 0) > CompactionThresholdChars;
}
```

- [ ] **Step 9: Запустить тесты, убедиться что проходят**

Run: `dotnet test VoiceAssistant.API.Tests/VoiceAssistant.API.Tests.csproj`
Expected: `Passed! - Failed: 0, Passed: 7`

- [ ] **Step 10: Commit**

```bash
git add VoiceAssistant.API.Tests/ VoiceAssistant.API/Services/ConversationMemoryService.cs
git commit -m "Add ConversationMemoryService threshold logic with tests"
```

### Side-call'ы: суммаризация и пересборка

Эта часть НЕ покрывается unit-тестами (нужен реальный вызов Gemini) — проверяется сборкой и живой проверкой в Task 3.

- [ ] **Step 11: Добавить оркестрирующие методы**

В `VoiceAssistant.API/Services/ConversationMemoryService.cs`, добавить в класс `ConversationMemoryService` (после `ShouldCompact`):

```csharp
    // Вызывается из ChatController.Send() после сохранения ОБЕИХ реплик хода.
    // Никогда не бросает — упавший side-call оставляет MediumTermSummary/
    // LastSummarizedMessageId нетронутыми, следующий ход попробует снова.
    public async Task CheckAndUpdateAsync(ChatSession session, string? geminiApiKey, CancellationToken ct)
    {
        try
        {
            var messages = session.Messages.ToList();
            if (ShouldSummarize(messages, session.LastSummarizedMessageId))
            {
                await SummarizeNewMessagesAsync(session, messages, geminiApiKey, ct);
            }

            if (ShouldCompact(session.MediumTermSummary))
            {
                await CompactSummaryAsync(session, geminiApiKey, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Клиент отключился — ничего ещё не сохранено, следующий ход попробует снова.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Medium-term memory update failed for session {SessionId}", session.Id);
        }
    }

    private async Task SummarizeNewMessagesAsync(ChatSession session, List<Message> messages, string? geminiApiKey, CancellationToken ct)
    {
        var cursor = session.LastSummarizedMessageId ?? 0;
        var newMessages = messages.Where(m => m.Id > cursor).OrderBy(m => m.Id).ToList();

        var transcript = string.Join("\n", newMessages.Select(m =>
            $"{(m.Role == "user" ? "Пользователь" : "Ассистент")}: {m.Content}"));

        var prompt = $$"""
            Ниже — фрагмент разговора пользователя с голосовым ассистентом-компаньоном.
            Составь короткую сводку (2000-4000 символов) главных фактов и идей из этого
            фрагмента — то, что стоит помнить в дальнейшем разговоре. Пиши по-русски,
            связным текстом, без markdown-разметки и заголовков.

            {{transcript}}
            """;

        var geminiMessages = new List<GeminiMessage> { new("user", prompt) };
        var sb = new System.Text.StringBuilder();
        await foreach (var chunk in _gemini.StreamResponseAsync("", geminiMessages, model: "gemini-3.5-flash",
            maxTokens: 2000, apiKey: geminiApiKey, enableGrounding: false, cancellationToken: ct))
        {
            sb.Append(chunk);
        }

        var newChunk = sb.ToString().Trim();
        if (string.IsNullOrEmpty(newChunk)) return;

        session.MediumTermSummary = string.IsNullOrEmpty(session.MediumTermSummary)
            ? newChunk
            : session.MediumTermSummary + "\n\n" + newChunk;
        session.LastSummarizedMessageId = newMessages[^1].Id;

        await _db.SaveChangesAsync();
        _logger.LogInformation("Medium-term summary extended for session {SessionId}, cursor now {MessageId}",
            session.Id, session.LastSummarizedMessageId);
    }

    private async Task CompactSummaryAsync(ChatSession session, string? geminiApiKey, CancellationToken ct)
    {
        var prompt = $$"""
            Ниже — накопленная сводка более ранней части разговора с пользователем. Она
            стала слишком длинной. Пересобери её заново, сохранив главные факты и идеи,
            но уложись примерно в 10000 символов. Пиши по-русски, связным текстом, без
            markdown-разметки и заголовков.

            {{session.MediumTermSummary}}
            """;

        var geminiMessages = new List<GeminiMessage> { new("user", prompt) };
        var sb = new System.Text.StringBuilder();
        await foreach (var chunk in _gemini.StreamResponseAsync("", geminiMessages, model: "gemini-3.5-flash",
            maxTokens: 4000, apiKey: geminiApiKey, enableGrounding: false, cancellationToken: ct))
        {
            sb.Append(chunk);
        }

        var compacted = sb.ToString().Trim();
        if (string.IsNullOrEmpty(compacted)) return;

        session.MediumTermSummary = compacted;
        await _db.SaveChangesAsync();
        _logger.LogInformation("Medium-term summary compacted for session {SessionId}, new length {Length}",
            session.Id, compacted.Length);
    }
```

- [ ] **Step 12: Зарегистрировать в DI**

В `VoiceAssistant.API/Program.cs`, сразу после строки `builder.Services.AddScoped<SpeakerRegistryService>();`, добавить:

```csharp
builder.Services.AddScoped<ConversationMemoryService>();
```

(`AddScoped`, не `AddSingleton` — сервис держит `AppDbContext`, который сам scoped-per-request, ровно как `SpeakerRegistryService`.)

- [ ] **Step 13: Проверить сборку всего решения**

Run: `dotnet build VoiceAssistant.API/VoiceAssistant.API.csproj && dotnet test VoiceAssistant.API.Tests/VoiceAssistant.API.Tests.csproj`
Expected: оба успешны, тесты по-прежнему `Passed: 7`.

- [ ] **Step 14: Commit**

```bash
git add VoiceAssistant.API/Services/ConversationMemoryService.cs VoiceAssistant.API/Program.cs
git commit -m "Add summarization/compaction side-calls to ConversationMemoryService"
```

---

## Task 3: Интеграция в CompanionPromptService + ChatController

**Files:**
- Modify: `VoiceAssistant.API/Services/CompanionPromptService.cs`
- Create: `VoiceAssistant.API.Tests/CompanionPromptServiceTests.cs`
- Modify: `VoiceAssistant.API/Controllers/ChatController.cs:31-45` (конструктор), `:353` (вызов `GetSystemPrompt`), после `:447` (вызов `CheckAndUpdateAsync`)

**Interfaces:**
- Consumes: `ConversationMemoryService.CheckAndUpdateAsync` (Task 2), `ChatSession.MediumTermSummary` (Task 1).
- Produces: `CompanionPromptService.GetSystemPrompt(string? customSystemPrompt, string? userName, string? assistantName, string? mediumTermSummary)` — новый 4-й параметр, конец списка (существующие позиционные вызовы не ломаются). `CompanionPromptService.BuildSystemPrompt(string basePrompt, DateTime now, string? customSystemPrompt, string? userName, string? assistantName, string? mediumTermSummary)` — новый публичный статический метод, тестируется напрямую без `IWebHostEnvironment`.

### TDD: CompanionPromptService

- [ ] **Step 1: Написать падающие тесты**

Создать `VoiceAssistant.API.Tests/CompanionPromptServiceTests.cs`:

```csharp
using VoiceAssistant.API.Services;
using Xunit;

namespace VoiceAssistant.API.Tests;

public class CompanionPromptServiceTests
{
    private static readonly DateTime FixedNow = new(2026, 7, 10, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void BuildSystemPrompt_WithoutSummary_OmitsMemoryBlock()
    {
        var result = CompanionPromptService.BuildSystemPrompt("base", FixedNow);

        Assert.DoesNotContain("Память о более ранней части разговора", result);
    }

    [Fact]
    public void BuildSystemPrompt_WithSummary_AppendsMemoryBlock()
    {
        var result = CompanionPromptService.BuildSystemPrompt("base", FixedNow, mediumTermSummary: "пользователь любит чай");

        Assert.Contains("## Память о более ранней части разговора:\nпользователь любит чай", result);
    }

    [Fact]
    public void BuildSystemPrompt_IncludesBasePromptAndDate()
    {
        var result = CompanionPromptService.BuildSystemPrompt("BASE_MARKER", FixedNow);

        Assert.Contains("BASE_MARKER", result);
        Assert.Contains("2026-07-10", result);
    }

    [Fact]
    public void BuildSystemPrompt_EmptySummary_OmitsMemoryBlock()
    {
        var result = CompanionPromptService.BuildSystemPrompt("base", FixedNow, mediumTermSummary: "");

        Assert.DoesNotContain("Память о более ранней части разговора", result);
    }
}
```

- [ ] **Step 2: Запустить тесты, убедиться что не компилируется**

Run: `dotnet test VoiceAssistant.API.Tests/VoiceAssistant.API.Tests.csproj`
Expected: ошибка сборки — метод `CompanionPromptService.BuildSystemPrompt` не существует.

- [ ] **Step 3: Реализовать — извлечь чистую функцию + добавить блок сводки**

Заменить содержимое `VoiceAssistant.API/Services/CompanionPromptService.cs` на:

```csharp
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
```

- [ ] **Step 4: Запустить тесты, убедиться что проходят**

Run: `dotnet test VoiceAssistant.API.Tests/VoiceAssistant.API.Tests.csproj`
Expected: `Passed! - Failed: 0, Passed: 11`

- [ ] **Step 5: Commit**

```bash
git add VoiceAssistant.API/Services/CompanionPromptService.cs VoiceAssistant.API.Tests/CompanionPromptServiceTests.cs
git commit -m "Extract CompanionPromptService.BuildSystemPrompt, add mediumTermSummary block"
```

### Wiring: ChatController

- [ ] **Step 6: Внедрить ConversationMemoryService в конструктор**

В `VoiceAssistant.API/Controllers/ChatController.cs`, заменить (строки 19-45):

```csharp
    private readonly AppDbContext _db;
    private readonly UserManager<User> _userManager;
    private readonly GeminiService _gemini;
    private readonly CompanionPromptService _tutor;
    private readonly AudioAnalysisService _audioAnalysis;
    private readonly SpeakerRegistryService _speakerRegistry;
    private readonly IConfiguration _config;
    private readonly ILogger<ChatController> _logger;
    private readonly string _audioPath;

    private readonly PiperTtsService _ttsService;

    public ChatController(AppDbContext db, UserManager<User> userManager,
        GeminiService gemini, CompanionPromptService tutor,
        AudioAnalysisService audioAnalysis, SpeakerRegistryService speakerRegistry, PiperTtsService ttsService,
        IConfiguration config, IWebHostEnvironment env, ILogger<ChatController> logger)
    {
        _db = db;
        _userManager = userManager;
        _gemini = gemini;
        _tutor = tutor;
        _audioAnalysis = audioAnalysis;
        _speakerRegistry = speakerRegistry;
        _ttsService = ttsService;
        _config = config;
        _logger = logger;
```

на:

```csharp
    private readonly AppDbContext _db;
    private readonly UserManager<User> _userManager;
    private readonly GeminiService _gemini;
    private readonly CompanionPromptService _tutor;
    private readonly AudioAnalysisService _audioAnalysis;
    private readonly SpeakerRegistryService _speakerRegistry;
    private readonly ConversationMemoryService _conversationMemory;
    private readonly IConfiguration _config;
    private readonly ILogger<ChatController> _logger;
    private readonly string _audioPath;

    private readonly PiperTtsService _ttsService;

    public ChatController(AppDbContext db, UserManager<User> userManager,
        GeminiService gemini, CompanionPromptService tutor,
        AudioAnalysisService audioAnalysis, SpeakerRegistryService speakerRegistry, ConversationMemoryService conversationMemory,
        PiperTtsService ttsService,
        IConfiguration config, IWebHostEnvironment env, ILogger<ChatController> logger)
    {
        _db = db;
        _userManager = userManager;
        _gemini = gemini;
        _tutor = tutor;
        _audioAnalysis = audioAnalysis;
        _speakerRegistry = speakerRegistry;
        _conversationMemory = conversationMemory;
        _ttsService = ttsService;
        _config = config;
        _logger = logger;
```

(Остальные строки конструктора — за пределами показанного диапазона — не трогать; проверить, что `_audioPath` и любые другие присвоения ниже строки 45 остаются как есть.)

- [ ] **Step 7: Передать сводку в GetSystemPrompt**

В том же файле, найти (около строки 353):

```csharp
        var systemPrompt = _tutor.GetSystemPrompt(settings?.CustomSystemPrompt, settings?.DisplayName, settings?.AssistantName);
```

Заменить на:

```csharp
        var systemPrompt = _tutor.GetSystemPrompt(settings?.CustomSystemPrompt, settings?.DisplayName, settings?.AssistantName, session.MediumTermSummary);
```

- [ ] **Step 8: Вызвать CheckAndUpdateAsync после сохранения ответа**

В том же файле, найти (около строки 447):

```csharp
        await AwaitInstructionUpdateAsync(instructionUpdateTask);
```

Заменить на:

```csharp
        await AwaitInstructionUpdateAsync(instructionUpdateTask);
        await _conversationMemory.CheckAndUpdateAsync(session, geminiKey, HttpContext.RequestAborted);
```

- [ ] **Step 9: Проверить сборку**

Run: `dotnet build VoiceAssistant.API/VoiceAssistant.API.csproj && dotnet test VoiceAssistant.API.Tests/VoiceAssistant.API.Tests.csproj`
Expected: оба успешны, тесты `Passed: 11`.

- [ ] **Step 10: Живая проверка — порог реально срабатывает и сводка доходит до промпта со следующего хода**

Обычная короткая переписка не достигает 200 000 символов — чтобы проверить реальное срабатывание, порог засеивается напрямую через SQL (спека явно допускает это: «прямой запрос к БД или через API»).

1. Через приложение (или напрямую) авторизоваться тестовым аккаунтом, отправить одно любое сообщение — это гарантирует, что `ChatSession` для этого пользователя существует. Узнать его `Id`:

```sql
SELECT "Id" FROM "ChatSessions" WHERE "UserId" = '<Id тестового пользователя>';
```

(Подключение к БД — `docker-compose exec db psql -U app -d vass`, либо через любой Postgres-клиент с теми же credentials, что в `docker-compose.yml`.)

2. Засеять историю сообщений этой сессии заведомо выше порога — содержимое намеренно бессмысленное (`repeat('a', ...)`), проверяется механизм срабатывания, а не качество сводки:

```sql
INSERT INTO "Messages" ("ChatSessionId", "Role", "Content", "CreatedAt")
VALUES (<Id сессии>, 'user', repeat('a', 200000), now());
```

3. Отправить ещё один `/chat/send` на эту же сессию (любой короткий текст). Дождаться ответа.

4. Проверить, что сработала суммаризация:

```sql
SELECT "LastSummarizedMessageId", length("MediumTermSummary") FROM "ChatSessions" WHERE "Id" = <Id сессии>;
```

Expected: `LastSummarizedMessageId` не `NULL` и равен `Id` последнего засеянного/отправленного сообщения; `length("MediumTermSummary")` — несколько тысяч символов (в районе 2000-4000, см. Global Constraints), не `NULL` и не 0.

5. Отправить ЕЩЁ один `/chat/send` на ту же сессию с текстом вроде «Что мы обсуждали раньше?». Так как сводка была записана на шаге 3, а `GetSystemPrompt` в этом, СЛЕДУЮЩЕМ вызове читает уже обновлённое поле — ответ должен так или иначе сослаться на то, что ассистент «помнит» более ранний контекст (конкретная формулировка не важна — важно, что модель не отвечает как с чистого листа). Это подтверждает тайминг N+1: сводка, записанная на шаге 3, не могла попасть в промпт ТОГО ЖЕ хода (код физически читает `session.MediumTermSummary` в `GetSystemPrompt`, строка 353, ДО вызова `CheckAndUpdateAsync`, который идёт после строки 447) — только следующего.

Если что-то из этого не подтверждается — не переходить к Step 11, разобраться (`superpowers:systematic-debugging`).

- [ ] **Step 11: Commit**

```bash
git add VoiceAssistant.API/Controllers/ChatController.cs
git commit -m "Wire ConversationMemoryService into ChatController.Send()"
```
