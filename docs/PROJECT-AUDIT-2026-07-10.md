# Полный технический аудит Vass

**Дата:** 2026-07-10  
**Срез репозитория:** `feature/medium-term-memory`, commit `ae858be`  
**Публичный контур:** `https://vass.it-consult.services/`  
**Статус документа:** актуальный baseline для исправлений и release gate

## 1. Резюме

Vass уже является работоспособным прототипом с хорошим основным контуром: ASP.NET Core API, PostgreSQL, Gemini, собственный TTS, legacy PWA и новый Expo/React Native клиент. Backend и mobile компилируются чисто, публичный health endpoint отвечает, HTTPS настроен корректно, API запускается под непривилегированным пользователем, а mobile хранит JWT в SecureStore.

При этом проект пока нельзя считать production-ready для широкого или семейного использования. Главные причины:

1. Шестизначный device-link код можно перебирать без rate limit; успешный подбор выдает полноценный JWT аккаунта.
2. Выключенная сейчас функция speaker identification не имеет tenant isolation: голосовые профили и pending-state глобальны для всех пользователей.
3. Пользовательские API-ключи хранятся в PostgreSQL открытым текстом.
4. `AudioFileName` в `/chat/send` не проверяется как безопасное имя и не привязан к владельцу загрузки.
5. Фоновое обновление custom instructions использует тот же scoped `DbContext` параллельно с основным запросом.
6. Нет автоматических тестов и CI, а миграции применяются автоматически при старте production-контейнера.

**Итоговая оценка:** архитектурно жизнеспособный, быстро развивающийся продуктовый прототип; безопасность и эксплуатационная надежность требуют отдельного hardening-этапа до красивого публичного релиза.

## 2. Объем и методика

Проверено:

- структура репозитория, git-состояние и последние изменения;
- ASP.NET Core controllers, services, EF Core entities и migrations;
- Expo/React Native API client, auth, voice loop, VAD, TTS и UI screens;
- legacy PWA, которая сейчас отдается публичным доменом;
- Dockerfiles, Compose, nginx и публичный HTTP/HTTPS контур;
- зависимости NuGet и npm;
- существующая документация и новые design/spec документы;
- сборка backend и строгая TypeScript-проверка mobile.

Не выполнялось:

- изменение состояния VPS, базы данных или production-конфигурации;
- активный brute force, нагрузочное тестирование и destructive security testing;
- проверка содержимого production PostgreSQL, backup jobs, firewall и внешнего reverse proxy;
- физический тест APK на Android/iOS в рамках этого аудита.

## 3. Текущее устройство проекта

| Слой | Фактическое состояние |
|---|---|
| Public frontend | Legacy PWA из `frontend/`, отдается nginx |
| Новый клиент | Expo 57 / React Native 0.86 в `mobile/`; основной продуктовый UI уже здесь |
| API | ASP.NET Core 10, controllers + EF Core, без отдельного application layer |
| Auth | ASP.NET Identity + JWT на 30 дней; email/password и device-link |
| AI | Gemini REST: transcription, completion check, chat, grounding, OCR |
| TTS | Piper Flask microservice; mobile также использует системные голоса |
| Speaker ID | Код, таблица и Flask service существуют, вызов из chat закомментирован |
| Storage | PostgreSQL + Docker volume для audio |
| Deploy | Docker Compose, внутренний nginx на `127.0.0.1:4001`, внешний TLS proxy вне repo |
| Tests/CI | Автоматических тестов и CI workflow нет |

В репозитории одновременно находятся три уровня истины:

- реально работающий legacy web-клиент;
- реально разрабатываемый mobile-клиент;
- планы/specs для memory и visual capture, часть которых еще не реализована.

Это нужно явно учитывать в backlog и release notes. Например, visual capture пока является спецификацией, а medium-term memory на проверенном commit содержит только поля и миграцию.

## 4. Критические и высокие риски

### P0 / SEC-01: Device-link допускает перебор и захват аккаунта

`POST /api/v1/auth/device-link/redeem` анонимен, код состоит из 6 цифр, живет 10 минут, а rate limiting, блокировки по IP/коду и счетчика попыток нет. При успехе endpoint возвращает обычный JWT на 30 дней.

**Риск:** удаленный атакующий может перебирать пространство из 1 000 000 кодов и войти в чужой аккаунт. Регистрация и password login также не ограничены по частоте.

**Исправление:** до следующего публичного релиза добавить ASP.NET Core rate limiter для login/register/redeem, отдельный строгий policy для redeem, аудит попыток, временную блокировку и увеличение энтропии кода. Успешная выдача кода должна инвалидировать ранее активный код пользователя.

### P1 / SEC-02: Speaker identification не изолирован между пользователями

`SpeakerProfile` не содержит `UserId`; `SpeakerRegistryService` загружает все профили через `ToListAsync()`. `SpeakerPendingStore` зарегистрирован singleton и хранит один глобальный кластер реплик.

**Текущее влияние:** вызов `IdentifyAsync` в `ChatController.Send` закомментирован, поэтому дефект сейчас не исполняется.

**Риск при включении:** голоса, имена и реплики разных аккаунтов будут смешиваться. Это одновременно privacy defect и ошибка идентификации.

**Исправление:** не включать функцию до добавления `UserId`/household scope в `SpeakerProfile`, составного индекса, фильтрации всех запросов и user-scoped pending store. Биометрические embedding требуют отдельной retention/consent политики.

### P1 / SEC-03: BYOK API-ключи хранятся открытым текстом

`OpenAiApiKey`, `AnthropicApiKey` и `GeminiApiKey` находятся в `UserSettings` как обычный `text`. Маскирование выполняется только при HTTP-ответе и не защищает базу, backup или диагностический dump.

**Риск:** компрометация БД раскрывает платежные ключи пользователей.

**Исправление:** удалить неиспользуемые provider-поля либо шифровать значения application-level ключом из secret store; поддержать key rotation. Никогда не логировать URL Gemini с `?key=` и тела ошибок provider API без redaction.

### P1 / SEC-04: Небезопасная обработка `AudioFileName`

`/chat/upload-audio` возвращает GUID, но `/chat/send` принимает произвольную строку, делает `Path.Combine(_audioPath, req.AudioFileName)` и проверяет только существование файла. Нет проверки `Path.GetFileName(...) == value`, допустимого расширения и ownership записи upload.

**Риск:** authenticated caller может передать абсолютный/обходной путь и заставить ffmpeg обработать локальный файл контейнера. Кроме того, загруженный audio до отправки не принадлежит конкретному пользователю на уровне данных.

**Исправление:** ввести attachment/upload entity с `UserId`, opaque id и статусом pending/consumed; принимать id, а не filename. До этого минимум разрешать только GUID `.webm`, использовать `GetFullPath` + проверку нахождения под `_audioPath`.

### P1 / REL-01: Параллельное использование одного EF Core `DbContext`

`MaybeUpdateCustomInstructionsAsync` запускается без ожидания и позднее обращается к `_db`, пока основной `Send` продолжает streaming и затем вызывает `SaveChangesAsync`. Scoped `DbContext` не поддерживает параллельные операции.

**Риск:** плавающие `A second operation was started on this context`, потерянные обновления памяти и нестабильность под реальной задержкой Gemini. Ошибка фоновой задачи поглощается и для пользователя выглядит как будто команда "запомни" сработала.

**Исправление:** вынести операцию в отдельный scope через `IDbContextFactory<AppDbContext>` либо сначала завершать AI-классификацию, затем выполнять короткую DB-операцию отдельным context. Добавить integration test с контролируемыми задержками.

### P1 / REL-02: Клиент получает `[DONE]` до фиксации assistant message

`ChatController.Send` сначала отправляет `[DONE]`, затем добавляет ответ ассистента и вызывает `SaveChangesAsync`; stats отправляются еще позже. Mobile прекращает чтение stream сразу при `[DONE]`.

**Риск:** UI считает turn успешным, хотя сохранение могло упасть; stats для mobile фактически недостижимы. История после перезапуска может не совпасть с тем, что пользователь услышал.

**Исправление:** сохранить assistant message до terminal event, затем отправить stats и только последним `[DONE]`. Для сохранения user/assistant turn определить явную транзакционную семантику и поведение при disconnect.

### P1 / OPS-01: Production migrations выполняются при старте API

`db.Database.Migrate()` вызывается безусловно в `Program.cs`. В Compose нет отдельного migration job, pre-deploy backup или rollback gate.

**Риск:** одна несовместимая или долгая миграция делает весь API неготовым; несколько экземпляров могут конкурировать; destructive migration применяется одновременно с deploy.

**Исправление:** вынести миграции в явный deployment step, перед ним делать и проверять `pg_dump`, после него запускать smoke test. Зафиксировать restore drill и RPO/RTO.

### P1 / QA-01: Нет автоматических тестов и CI

В backend нет test project, в mobile нет test script, `.github/workflows` отсутствует. Самый сложный модуль, `useVoiceChat.ts`, имеет около 1200 строк и множество асинхронных переходов состояния, но проверяется вручную.

**Риск:** runtime regressions обнаруживаются на устройстве или production; миграции, auth ownership и SSE contract ничем не защищены.

**Исправление:** первым шагом добавить xUnit integration tests для auth/chat/settings и unit tests для memory/prompt logic; затем state-machine tests для voice loop. CI должен выполнять restore/build/test, `tsc`, Expo doctor и dependency audit.

### P1 / ARCH-01: Публично развернут не тот UI, который является продуктом

Публичный домен продолжает отдавать legacy PWA, тогда как новый avatar/UI и основные решения находятся в `mobile/`. `DEVELOPMENT.md` в начале все еще описывает продукт как web app и говорит, что React Native только планируется.

**Риск:** две реализации voice loop расходятся, исправления дублируются, acceptance criteria неясны. Пользовательский релиз и фактическая разработка выглядят как разные продукты.

**Исправление:** официально определить mobile как primary client, legacy PWA как maintenance-only diagnostic client с датой удаления либо привести web к новому UI. Обновить корневую документацию и release process.

## 5. Средние риски

### P2 / SEC-05: Долгоживущий JWT без revocation

JWT действует 30 дней; refresh-token rotation, session/device list и server-side revoke отсутствуют. Logout удаляет токен только локально.

**Рекомендация:** короткий access token + rotating refresh token, device sessions и revoke при logout/компрометации. Минимум добавить security stamp/version claim.

### P2 / SEC-06: Слишком широкая web security policy

Production API подтвердил `Access-Control-Allow-Origin: *`. Публичная PWA хранит JWT в `localStorage`; CSP отсутствует. HSTS, `X-Frame-Options: SAMEORIGIN` и `X-Content-Type-Options: nosniff` присутствуют.

**Рекомендация:** ограничить CORS доверенными origin или убрать CORS для same-origin PWA, добавить строгий CSP, Referrer-Policy и Permissions-Policy. Для web-auth предпочтительнее secure HttpOnly cookie/BFF; если PWA остается временной, минимизировать ее поверхность.

### P2 / SEC-07: Неполные лимиты и проверка входа

- TTS принимает текст без максимальной длины и может потреблять CPU.
- Client logs ограничены количеством записей, но `Message` и `Data` не имеют размера запроса/поля.
- OCR доверяет `Content-Type: image/*`, а не сигнатуре/allowlist.
- Session title, `AvatarId`, language и custom prompt почти не валидируются.
- Upload endpoints полагаются на model binding до проверки длины файла.

**Рекомендация:** централизованные DTO validators, request/body limits, MIME allowlist + magic-byte check и per-user quotas.

### P2 / DATA-01: Нет retention для audio и client logs

Audio удаляется только вместе с session; orphan uploads остаются. Client logs пишутся в PostgreSQL без TTL/cleanup и могут содержать transcript, stack trace или device metadata.

**Рекомендация:** retention policy, scheduled cleanup, явная privacy classification и redaction на клиенте/сервере. Remote logging должен быть feature-flagged для production.

### P2 / REL-03: Health endpoint проверяет только процесс

`/api/health` всегда возвращает `healthy` и не проверяет PostgreSQL, TTS, writable audio volume или AI configuration. Поэтому внешний мониторинг может видеть зеленый API при неработающем пользовательском flow.

**Рекомендация:** разделить liveness и readiness; readiness проверяет DB и storage, а TTS отражается отдельным degraded dependency status. Gemini лучше проверять конфигурационно, без платного запроса на каждый probe.

### P2 / REL-04: AI/provider errors превращаются в обычный ответ

`GeminiService.StreamResponseAsync` при части ошибок yield-ит русскую строку ошибки. `ChatController` собирает ее как normal assistant content и сохраняет в историю.

**Рекомендация:** typed result/error channel; не сохранять infrastructure errors как реплики ассистента. SSE должен иметь отдельное `error` event и retryability code.

### P2 / API-01: Settings API имеет lost-update контракт

Mobile делает GET всего settings object, затем PUT всего объекта ради изменения одного поля. Конкурирующее изменение между GET/PUT будет затерто. Проверка masked key через `Contains("...")` неоднозначна.

**Рекомендация:** PATCH-команды по конкретным полям, server-side merge, allowlist для `AvatarId`/language и concurrency token при необходимости. Не возвращать provider key даже в маскированном виде, если UI его не использует.

### P2 / OPS-02: Compose содержит рассинхронизированную инфраструктуру

API настроен на `http://speaker-id:5003`, но сервиса `speaker-id` в Compose нет. Сейчас это не ломает chat только потому, что вызов выключен. `silero-tts` также лежит в repo, но не участвует в deploy.

**Рекомендация:** либо удалить dead deployment path, либо оформить feature profile с service/healthcheck/depends_on. Не держать конфигурацию, которая становится runtime bug одним uncomment.

### P2 / MOB-01: Voice loop слишком концентрирован

`useVoiceChat.ts` объединяет recorder lifecycle, VAD, continuation, SSE, TTS, barge-in, pause/resume и UI state. Код тщательно прокомментирован, но изменение любой фазы имеет большой blast radius.

**Рекомендация:** после появления characterization tests выделить reducer/state machine и adapters для recorder/network/TTS. Не начинать с механического refactor без тестового baseline.

### P2 / MOB-02: TTS cache не очищается явно

Каждый server TTS response записывается как новый `tts-{timestamp}.wav`; после playback файл не удаляется. OS cache когда-нибудь очищается, но длинные сессии могут накапливать файлы.

**Рекомендация:** удалить файл в `finally` после release player или использовать один ротационный cache file.

### P2 / DEP-01: Dependency baseline требует обслуживания

- NuGet: известных уязвимостей не найдено; доступны patch updates .NET 10.0.3 -> 10.0.9, Npgsql 10.0.0 -> 10.0.3 и MCP 1.0.0 -> 1.4.1.
- npm: 10 moderate advisories в транзитивной Expo toolchain, high/critical нет. Предложенный npm auto-fix некорректно ведет на старый major Expo и не должен применяться вслепую.
- Expo doctor: `expo` 57.0.3 не совпадает с ожидаемым `~57.0.4`; остальные 19 проверок прошли.

**Рекомендация:** отдельный dependency PR: сначала `npx expo install --check`, затем smoke build; .NET patch packages обновлять согласованным набором.

### P2 / DOC-01: Документация не имеет единого статуса

`DEVELOPMENT.md`, `docs/react-native/*`, design plans и superpowers specs частично противоречат друг другу. Завершенные, запланированные и экспериментальные функции не помечены единообразно.

**Рекомендация:** ввести `docs/README.md` с картой документов и status labels: `implemented`, `in progress`, `approved`, `draft`, `legacy`. Этот аудит использовать как baseline, а не создавать второй конкурирующий backlog.

## 6. Низкие риски и технический долг

- `ModelContextProtocol` подключен, но в коде не используется.
- OpenAI/Anthropic settings и `FullTranslation` выглядят остатками старого продукта.
- `CreateSessionRequest.Mode/Title` принимаются, но controller всегда создает один hard-coded dialog session.
- `GetSessions` создает данные при GET, что усложняет семантику, кэширование и тестирование.
- API состоит из крупных controllers; `ChatController` несет chat, audio, OCR, TTS и speaker orchestration.
- Root `App.tsx` задает светлый loading screen/status bar, Home использует AMOLED, а profile/history/login остаются в старой светлой визуальной системе.
- `.env.example` использует слишком реалистичный формат Gemini key; лучше оставить очевидный placeholder без узнаваемого префикса полного ключа.
- Локальный `.env` содержит development-grade секреты. Их значения в отчет не включены; перед любым совместным окружением их следует заменить.

## 7. Что сделано хорошо

- Session CRUD и чтение сохраненного audio фильтруются по `UserId`.
- Mobile JWT хранится в SecureStore, а 401 централизованно возвращает пользователя на login.
- Network calls имеют timeout/cancellation; SSE и barge-in учитывают disconnect.
- API container после подготовки volume запускает приложение non-root пользователем.
- Audio volume вынесен из image; nginx корректно отключает buffering для SSE.
- HTTP перенаправляется на HTTPS; HSTS включен.
- TypeScript работает в strict mode и проходит без ошибок.
- Backend Release build проходит с нулем warnings/errors.
- Legacy PWA экранирует chat text и session title перед `innerHTML`.
- Voice loop содержит много комментариев о реально воспроизведенных race conditions; это полезная инженерная память, которую теперь нужно закрепить тестами.
- UI mobile уже разложен на `LayeredAvatar`, `ConversationPeek`, `VoiceControlDock`, theme и отдельные hooks.

## 8. Проверки и результаты

| Проверка | Результат |
|---|---|
| `dotnet build VoiceAssistant.API/VoiceAssistant.API.csproj -c Release` | Успешно, 0 warnings, 0 errors |
| `npx tsc --noEmit` | Успешно |
| `dotnet list package --vulnerable --include-transitive` | Уязвимых NuGet packages не найдено |
| `npm audit --omit=dev` | 10 moderate, 0 high, 0 critical |
| `npx expo-doctor` | 19/20; только Expo patch mismatch |
| `docker compose config` | Валиден; Gemini key в локальном env пуст, speaker-id service отсутствует |
| `GET https://vass.it-consult.services/api/health` | `200`, body `"healthy"` |
| `GET https://vass.it-consult.services/` | `200`, legacy PWA title |
| `http://...` | `308` на HTTPS |
| CORS preflight с постороннего origin | `204`, `Access-Control-Allow-Origin: *` |
| Security headers | HSTS, SAMEORIGIN, nosniff есть; CSP нет |

После смены ветки на `feature/medium-term-memory` backend build следует повторять после каждого следующего commit реализации memory, а не считать ранний зеленый build доказательством готовности всей planned feature.

## 9. Рекомендуемый порядок работ

### Release gate A: безопасность аккаунта и данных

1. Закрыть SEC-01: rate limit и hardening device-link/login/register.
2. Закрыть SEC-04: безопасный attachment id/ownership и path validation.
3. Зафиксировать speaker identification выключенным feature flag; доработать tenant isolation до включения.
4. Зашифровать/удалить BYOK keys и ротировать development/VPS secrets.
5. Добавить request limits и quotas для TTS, logs, audio, OCR.

### Release gate B: предсказуемый deploy

1. Добавить tests + CI.
2. Вынести migrations в deploy step с backup и smoke test.
3. Исправить parallel DbContext и порядок SSE terminal event/save.
4. Добавить readiness, retention cleanup и минимальный monitoring/alerting.

### Product gate C: один продуктовый клиент

1. Назначить mobile primary client и определить судьбу legacy PWA.
2. Завершить medium-term memory с тестами и privacy boundaries.
3. Реализовать visual capture по утвержденной спецификации только после attachment security foundation.
4. Провести физический Android test pass: permissions, VAD, pause/resume, barge-in, offline/error states, длинная сессия.
5. Собрать release APK/AAB воспроизводимым pipeline, а не ручной последовательностью из локального WSL.

## 10. Критерий готовности красивого первого релиза

Релиз можно считать технически готовым, когда:

- отсутствуют открытые P0/P1 из этого документа либо для них письменно принят риск;
- auth endpoints ограничены и наблюдаемы;
- все attachment доступны только владельцу и не допускают path traversal;
- database backup/restore реально проверен;
- backend integration tests и mobile state tests выполняются в CI;
- production readiness отражает DB/storage/TTS, а не только живой процесс;
- публичный UI соответствует выбранному primary client;
- privacy/retention для audio, images, logs, memory и speaker embeddings задокументированы;
- физический test pass пройден на целевом Android устройстве.

Этот список важнее количества новых функций: после его выполнения avatar, memory и visual tasks будут опираться на надежную основу, а не увеличивать уже существующую поверхность риска.
