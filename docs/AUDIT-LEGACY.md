# Аудит: хвосты родительского проекта Polish Tutor

> Vass склонирован из Polish Tutor (langtutor). Этот файл — полный каталог
> наследия с планом зачистки. Классификация: **DELETE** — чистый туторский
> артефакт, не используется в живом потоке; **REWORK** — нужен, но содержит
> туторский контент/именование; **KEEP** — актуален как есть.
>
> Живой поток для справки (всё, чего нет в этой цепочке — кандидат на вылет):
> `ChatController.Send` → `AudioAnalysisService` (Gemini-транскрипция) →
> `MaybeUpdateCustomInstructionsAsync` → `TutorService.GetSystemPrompt` →
> `GeminiService` (стрим + Search grounding) → `PiperTtsService` (стрим PCM).

## 1. DELETE — бэкенд

### Контроллеры
- [ ] `Controllers/OnboardingController.cs` — только выбор CEFR-уровня
      (level test) для тьютора.
- [ ] `Controllers/VocabularyController.cs` — CRUD польского словарика +
      грамматический разбор польских слов (использует AnthropicService).

### Сервисы (зарегистрированы, но НЕ вызываются в живом потоке)
- [ ] `Services/TutorTools.cs` — описания Anthropic-tool'ов (tool-calling
      поток умер при миграции на Gemini).
- [ ] `Services/TutorToolExecutor.cs` — исполнитель тех же tool'ов.
- [ ] `Services/NightlyAnalysisJob.cs` — ночной анализ ученика: промпт
      украинский, зовёт Anthropic (ключ не задан на VPS → молча падает
      каждую ночь), пишет в TutorInstructions (таблица пуста). Мёртв.
- [ ] `Services/AnthropicService.cs` — после удаления NightlyAnalysisJob и
      VocabularyController не остаётся ни одного вызова.
- [ ] `Services/OpenAiChatService.cs` — эксперимент с GPT-5.5, откатились
      на Gemini. (Если жалко — ветка в git-истории, коммит 1cc87b9.)
- [ ] `Services/OpenAiTtsService.cs` — заменён Piper'ом, никем не
      инъектируется.
- [ ] `Services/PronunciationService.cs` — оценка ПОЛЬСКОГО произношения
      (промпт по-польски), не зарегистрирован в DI.
- [ ] `Services/WhisperService.cs` — не зарегистрирован, транскрипцию
      делает Gemini.
- [ ] Почистить `Program.cs` (регистрации строк 54-66) и конструктор
      `ChatController` от инъекций удалённых сервисов
      (`_anthropic`, `_openAiChat`, `_tutorTools`, `_toolExecutor`).

### Промпты
- [ ] `Prompts/level-test.txt` — польский level test (CEFR A1-B2).
- [ ] `Prompts/conductor-analysis.txt` — украинский промпт ночного анализа.

### Сущности БД (+ одна миграция на все удаления)
- [ ] `Data/Entities/Lesson.cs` — не используется нигде.
- [ ] `Data/Entities/Exercise.cs` — не используется нигде.
- [ ] `Data/Entities/LearningPlan.cs` — чистая заглушка.
- [ ] `Data/Entities/TestResult.cs` — писался только из
      OnboardingController (удаляется вместе с ним).
- [ ] `Data/Entities/UserWord.cs` — только VocabularyController /
      TutorToolExecutor / NightlyAnalysisJob (все удаляются).
- [ ] `Data/Entities/LearnerError.cs` — то же самое.
- [ ] `Data/Entities/TutorInstruction.cs` — таблица ПУСТА, писал её только
      мёртвый NightlyAnalysisJob; чтение в ChatController (строки ~322-323)
      всегда даёт null. Роль «поведенческих инструкций» уже выполняет
      `UserSettings.CustomSystemPrompt`. Удалить чтение + сущность.
- [ ] Убрать соответствующие `DbSet` из `AppDbContext` + миграция
      `DropTutorLegacyTables` (история старых миграций остаётся — норма).

## 2. DELETE — фронтенд
- [ ] `frontend/js/vocabulary.js` — весь словарик (украинский UI,
      placeholder «Слово польською»).
- [ ] `frontend/js/onboarding.js` — level test, включая польскую
      фразу-триггер `'Cześć! Chcę sprawdzić swój poziom polskiego.'`.
- [ ] `frontend/app.html` — панель словарика (#vocabulary-panel, ~строки
      16-34), кнопка словарика, фильтры «Всі/Нові/Вчу/Знаю», бейдж
      CEFR-уровня пользователя.
- [ ] `frontend/sw.js` — убрать `/js/vocabulary.js` и `/js/onboarding.js`
      из STATIC_ASSETS (и добавить туда `/js/yolo.js`, которого там нет).
- [ ] `frontend/css/styles.css` — CSS словарика/онбординга.
- [ ] Отображение произношения в `yolo.js` (звёзды «Вимова: N/10») —
      сервер больше не шлёт `pronunciation`-событие, обработчик мёртв.

## 3. REWORK — нужно, но переделать

| Что | Где | Действие |
|---|---|---|
| JWT Issuer/Audience = "PolishTutor" | `appsettings.json:14-15`, `docker-compose.yml:13-14` | Сменить на "Vass". ⚠ Инвалидирует выданные токены — оба пользователя перелогинятся один раз |
| Linux-пользователь `tutor` | `VoiceAssistant.API/Dockerfile:12` | Переименовать в `app` (косметика, безопасно) |
| `TutorService` | `Services/TutorService.cs` | Переименовать в `CompanionPromptService`; выкинуть польский default-промпт (GetDefaultPrompt) и параметры mode/conductorInstructions |
| `Prompts/tutor-system.txt` | содержимое уже про Ольгу | Переименовать в `companion-system.txt`; главный REWORK: переписать промпт под компаньона для пожилых (тон, сценарии из презентации), а не «ученик/уровень» |
| `SettingsController` | строка ~107 | Обновить вызов после переименования TutorService |
| `User.Level` (CEFR) + `User.NativeLang` | `Data/Entities/User.cs` | Колонки оставить (не стоят миграционной возни), убрать из UI (`chat.js:21` показывает уровень) и из промптов |
| `UserSettings.FullTranslation` | `settings.html:40-43`, сущность | Туторская фича «полный перевод» — убрать чекбокс из UI; колонку можно оставить |
| Украинские UI-строки | `app.html` («+ Нова розмова»), `yolo.js` («Слухаю вас (перебито)», «Мікрофон вимкнено», «Приховати текст», alert), `chat.js` (ошибки) | Перевести на русский — приложение русскоязычное |
| `Anthropic__ApiKey` env | `docker-compose.yml:8` | Убрать после удаления AnthropicService — заодно исчезнет warning «variable is not set» при каждом compose-вызове |
| `OPENAI_API_KEY` env | compose/.env | Оставить (запас на будущее), но пометить как неиспользуемый после удаления OpenAI-сервисов |
| `DEVELOPMENT.md` | секция Known Legacy Areas | Обновить после зачистки |

## 4. Решения по контейнерам (инфраструктура)

| Сервис | Статус | Рекомендация |
|---|---|---|
| `tts` (Piper) | Активен, боевой TTS | **KEEP** |
| `tts-silero` | Standalone-оценка качества, к API не подключён, ~470MB RAM вхолостую | Убрать из docker-compose (папку `silero-tts/` оставить в репо на случай возврата); `docker compose rm -sf tts-silero` |
| `speaker-id` | Фича приостановлена (вызов закомментирован), ~590MB RAM вхолостую | Убрать из compose ВМЕСТЕ с `depends_on: speaker-id` у api (иначе api не стартанёт); код и таблица SpeakerProfiles остаются |

## 5. KEEP — подтверждённо актуальное
- `GeminiService`, `PiperTtsService`, `AudioAnalysisService` — ядро.
- `SpeakerIdService`, `SpeakerRegistryService`, `SpeakerProfile` — код
  приостановленной фичи, осознанно храним (коммит 0ecb2b9).
- `ChatController`, `AuthController`, `SettingsController` (после REWORK).
- `frontend`: `chat.js`, `voice.js`, `yolo.js`, `auth.js`, `api.js`,
  `settings.js`, manifest, иконки.
- Все существующие миграции (история неприкосновенна).
- `docs/PLAN-REACT-NATIVE.md` — план Этапа 2.

## 6. Порядок выполнения (безопасная последовательность)

1. Фронтенд-удаления (п.2) — независимы, деплой = git pull.
2. Контроллеры Onboarding/Vocabulary + их маршруты.
3. Сервисы из п.1 + чистка Program.cs/конструктора ChatController.
   Сборка обязана остаться зелёной после каждого шага.
4. Промпты level-test/conductor-analysis.
5. Сущности + DbContext + одна миграция `DropTutorLegacyTables`.
   ⚠ Перед этим — бэкап БД (`pg_dump`), таблицы UserWords/LearnerErrors
   могут содержать исторические данные тестов.
6. Переименования (TutorService, промпт-файл, JWT, Dockerfile user).
   ⚠ JWT-смена — предупредить о перелогине.
7. Compose-чистка (Anthropic env, tts-silero, speaker-id + depends_on).
8. Переписать системный промпт под компаньона (главный содержательный
   пункт — влияет на «личность» Ольги).
9. Обновить DEVELOPMENT.md.

**Итог по объёму**: ~19 файлов под удаление, ~15 точек переработки,
1 миграция БД. Оценка: 3-5 часов аккуратной работы с проверкой сборки
и деплоем после каждого блока.
