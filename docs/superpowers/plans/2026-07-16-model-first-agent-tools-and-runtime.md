# Model-first agent tools и durable runtime - Implementation Plan

> План развивает принятую design-spec `docs/superpowers/specs/2026-07-14-assistant-capabilities-and-memory-tools-design.md`. Сначала используем нативные function calls модели в интерактивном turn, затем строим собственный устойчивый agent runtime. MCP не является предварительным условием ни для одной из фаз.

**Goal:** Vass понимает естественную пользовательскую формулировку, сама выбирает подходящий строго типизированный инструмент, получает его результат и формирует осмысленный ответ. Простые действия завершаются в текущем голосовом turn; длинные задачи могут продолжаться надёжно, показывать прогресс и возвращать итог после разрыва SSE или сворачивания приложения.

**Architecture:** Все действия остаются внутренними typed capabilities. Модель получает только JSON Schema декларации и вызывает функцию; `AssistantToolBroker` проверяет allowlist, владельца, политику и необходимость согласия, затем возвращает структурированный result обратно модели как `functionResponse`. В первой фазе цикл живёт в HTTP/SSE turn с лимитами. Во второй те же contracts исполняет durable `AgentRun` worker; mobile-клиент становится получателем статуса и выполняет только явно разрешённые device actions.

**Tech Stack:** ASP.NET Core 10, EF Core 10, PostgreSQL, Gemini native function calling, SSE, React Native/Expo Android. Внешний MCP adapter возможен позднее, но не заменяет внутренний broker.

## Current Baseline (2026-07-16)

- [x] `AssistantToolPlannerService` сохраняет полный Gemini `model.content`, включая call ID и opaque provider metadata.
- [x] `AssistantAgentTurnService` исполняет bounded native loop `functionCall -> broker -> functionResponse -> next call/final text`.
- [x] `AssistantToolBroker` возвращает typed results для memory, conversation search, reminders, screen request, Vass navigation и YouTube actions.
- [x] `ChatController` больше не вызывает legacy screen preflight или reminder keyword/parser route; эти действия выбирает модель через tools.
- [x] Existing client actions остаются на action-receipt contract и не считаются проигранными/открытыми до receipt клиента.
- [ ] Нет durable agent job, состояния ожидания клиента, cancellation/resume, истории tool steps и фонового завершения задачи.

## Product Decisions

- Модель, а не регулярные выражения, выбирает capability: «запомни», «сохрани это», «найди, о чём мы говорили позавчера», «посмотри экран», «развернись», «включи видео» ведут к типизированным вызовам.
- Брокер, а не модель, решает безопасность: owner checks, лимиты, допустимые аргументы, consent и идемпотентность.
- Модель не получает и не сохраняет chain-of-thought. В telemetry пишем только tool name, статус, latency, IDs и безопасные агрегаты.
- `screen.capture_once` всегда требует текущего явного пользовательского запроса и системного consent. Никакого continuous/background capture, даже после появления durable runtime.
- Camera, gallery и Android Share остаются user-initiated input: они создают attachment/context, но не являются model-callable tool.
- Модель не обещает, что YouTube уже играет. Результат external action означает только «команда принята/выполнена клиентом» по action receipt.
- MCP будет нужен как адаптер внешних интеграций, если они появятся. Для memory, screen и navigation он добавит транспортный слой, но не решит policy, mobile consent или durability.

---

## Phase 1: Native Model Tool Loop In The Live Turn

**Outcome:** обычный голосовой запрос запускает ограниченный агентный цикл: модель вызывает один или несколько инструментов, видит их результаты и только затем отвечает пользователю. Это серверное изменение; для уже существующих инструментов не требует нового APK-контракта.

### 1. Provider-Neutral Tool Message Contract

**Files:**
- Modify: `VoiceAssistant.API/Services/AssistantToolPlannerService.cs`
- Modify: `VoiceAssistant.API/Services/GeminiService.cs`
- Create: `VoiceAssistant.API/Services/AssistantAgentTurnService.cs`
- Modify: `VoiceAssistant.API/Models/...` or existing service contracts
- Modify: `VoiceAssistant.API.Tests/...`

- [x] Введены provider-neutral representations для assistant function call и tool result: `CallId`, `Name`, JSON args, JSON result, success/error and machine-readable code.
- [x] Сохраняется provider-required function-call metadata (включая call ID и thought signature) между model response и `functionResponse`.
- [x] `AssistantAgentTurnService` является единственной точкой цикла: model proposal -> broker -> functionResponse -> next model proposal/final answer.
- [x] Установлены лимиты: максимум 4 model steps, максимум 3 tool executions за step, общий timeout 25 seconds и bounded context/result size.
- [x] После tool round финальный model text передается одним durable SSE text event; raw protocol text, JSON функций и внутренние exceptions не попадают пользователю.
- [x] Malformed args возвращаются модели как typed tool errors; timeout/exhausted loop логируются и безопасно переходят в normal-response fallback.

**Acceptance:** модель может выполнить `memory_search`, увидеть найденные записи с provenance, затем вызвать `memory_remember` с собственной связной формулировкой; final answer подтверждает именно реально сохранённую запись.

### 2. Complete Typed Capability Surface

**Files:**
- Modify: `VoiceAssistant.API/Services/AssistantToolBroker.cs`
- Modify: existing memory, reminder, external action and conversation services
- Create/modify focused unit and integration tests

**First capability set:**

```text
memory.status
memory.list
memory.search
memory.remember
memory.correct
memory.forget
memory.clear
conversation.search
reminder.create
screen.capture_once
open_vass
youtube.search
youtube.watch
```

- [ ] Дать `memory.remember` модели явные поля `content`, `category`, optional source/reference and deduplication intent. Сохранять именно сформированную моделью связную запись, а не обрывок пользовательской фразы.
- [x] Добавлен `conversation.search` по owner-scoped истории: query, ограничение на 8 коротких excerpts и provenance message IDs/timestamps. Текущая user message исключается из выдачи; optional inclusive date range ограничен 31 днем.
- [x] Для «позавчера» и других относительных дат передаётся текущая timezone/date в system context; broker принимает только explicit `yyyy-MM-dd` range и нормализует его в UTC.
- [x] Reminders переведены на `reminder_create`: модель извлекает intent/time, сервер валидирует timezone/due time и возвращает receipt. Старый parser остается compatibility code, но ChatController его не вызывает.
- [x] Existing action receipts сохранены для `open_vass`, `youtube.search`, `youtube.watch`; tool result разделяет `proposed`, `rejected`, `unavailable` и клиентское completion остается отдельным receipt.
- [x] `screen_capture_once` переводит turn в mediated waiting state: provisional user message удаляется, а retry с OS-approved image владеет реальным ответом.
- [ ] Для write tools использовать idempotency key, привязанный к agent turn/step, чтобы SSE retry не создавал вторую память или второе напоминание.

**Acceptance:** просьба «вот ссылка, сохрани её в память» сохраняет смысловую карточку со ссылкой; «запомни, о чём мы говорили позавчера» сначала ищет разговор, а при недостатке данных задаёт уточняющий вопрос вместо выдумывания записи.

### 3. Retire Legacy Dispatch Safely

**Files:**
- Modify: `VoiceAssistant.API/Controllers/ChatController.cs`
- Modify/remove: legacy screen intent, reminder classification and explicit-memory extraction paths
- Modify: `VoiceAssistant.API/Services/ExternalActionService.cs` where needed
- Modify: integration tests and fake Gemini handler

- [x] Agent turn стал primary path для exposed memory, conversation, reminder, screen и client action capabilities. `memory.clear` намеренно не exposed до отдельного confirmation flow.
- [x] Direct screen/reminder dispatch удален из ChatController; `ScreenAnalysisIntentService` и `ExternalActionService` больше не зарегистрированы для runtime. У `ReminderService` сохранен compatibility parser, но ChatController его не вызывает.
- [x] Passive memory extraction оставлена отдельно от explicit `memory.remember`, но запрещена в turn с explicit memory/reminder tool call.
- [ ] Передавать shared URL, image/PDF/document attachment и screen image как структурированный turn context, а не как текстовую подстановку. Проверка ownership и размерные пределы остаются на сервере.
- [ ] Не добавлять в allowlist raw SQL, filesystem, generic HTTP fetch, shell или произвольный URL execution.

**Acceptance:** один и тот же смысл, выраженный разными словами, приходит к одинаковому typed tool intent; на один запрос создаётся максимум одно side effect.

### 4. Observability, Tests And Physical Verification

**Files:**
- Create: agent turn / tool execution telemetry service or persistence abstraction
- Modify: `VoiceAssistant.API.Tests/...`
- Modify: `VoiceAssistant.API.IntegrationTests/FakeGeminiHandler.cs`
- Modify: `docs/...` only where a public API contract changes

- [ ] Записывать `agentTurnId`, step number, capability name, success/error code, latency, request/result byte counts and action receipt state. Не логировать hidden reasoning, raw attachment bytes или private memory content.
- [x] Fake Gemini handler покрывает one tool, search-then-write, conversation-search-then-write, screen request, reminder и client actions; он требует точный `callId` в `functionResponse`.
- [x] Добавлены unit/integration tests для полного `functionCall -> functionResponse -> final text` loop, последовательных calls и conversation recall. Cross-user isolation, malformed/timeout/load scenarios остаются follow-up.
- [ ] Выполнить физический Android smoke: memory from link, memory from past conversation, reminder, YouTube exact video, open from overlay, one-shot screen capture, Share link/PDF/image, interruption and reconnect.
- [ ] Провести один 30-minute continuous scenario с зафиксированным agent/tool audit: overlay, YouTube, memory, share, screen capture, return to full screen, TTS/STT continuity.

**Phase 1 Exit Criteria:** backend unit and integration suites green; no legacy dispatcher triggers a duplicate action; all capability calls are auditable by run/step; physical-device smoke and 30-minute scenario complete without a crash or lost final result.

---

## Phase 2: Durable Internal Agent Runtime

**Outcome:** задачи, которые занимают время или ожидают внешний результат, не зависят от одного HTTP/SSE connection. При этом краткие voice actions остаются быстрыми и не превращаются в очередь.

### 5. Persisted AgentRun Domain

**Files:**
- Create: `VoiceAssistant.API/Data/Entities/AgentRun.cs`
- Create: `VoiceAssistant.API/Data/Entities/AgentStep.cs`
- Modify: `VoiceAssistant.API/Data/AppDbContext.cs`
- Create: EF migration
- Create: `VoiceAssistant.API/Services/AgentRunService.cs`
- Create: `VoiceAssistant.API/Services/AgentRunWorker.cs`

**State machine:**

```text
queued -> planning -> executing -> waiting_for_client -> planning
                       |                 |
                       v                 v
                   waiting_for_user   cancelled
                       |
                       v
                    planning

planning/executing/waiting_* -> completed | failed | cancelled
```

- [ ] `AgentRun` хранит owner, session, originating message, user-visible task title/status, idempotency key, timestamps and final result reference.
- [ ] `AgentStep` хранит typed request/result summaries, state, attempt number and timing. Sensitive content is redacted/minimized.
- [ ] Сделать transitions transactional and idempotent; worker lease предотвращает двойное выполнение при нескольких instances приложения.
- [ ] Добавить explicit cancel и bounded retry policy. Не ретраить user-visible writes без idempotency key.
- [ ] Использовать database-backed queue/polling worker в первой реализации; Redis/externally managed queue рассматривать только при реальной нагрузке.

### 6. Client Handoff, Progress And Resume

**Files:**
- Create/modify agent runs API and SSE events
- Modify: React Native runtime/store and relevant screens
- Modify: action receipt endpoints

- [ ] При длительной задаче assistant сразу говорит коротко, что начал работу, и возвращает `agentRunId`; это не является скрытым chain-of-thought.
- [ ] Отправлять bounded user-facing progress: `started`, `waiting for your confirmation`, `completed`, `needs clarification`, `failed`. Не транслировать внутренние reasoning tokens.
- [ ] После reconnect/fullscreen/overlay client запрашивает active runs и восстанавливает visible state.
- [ ] Device actions (`screen.capture_once`, open/navigation, YouTube) завершаются только после receipt от активного authorized client. Worker не имитирует их completion и не включает микрофон/экран в фоне.
- [ ] Для user clarification сохранять `waiting_for_user` run и связывать следующий ответ пользователя с ним, пока не истёк TTL.

### 7. Runtime Safety And Operations

- [ ] Добавить per-user limits: active runs, daily tool budget, execution time, retries and result/context sizes.
- [ ] Добавить health/readiness проверки worker queue and stale-run watchdog, плюс admin visibility для counts/errors без private tool payloads.
- [ ] Добавить retention policy для step metadata and отмену orphan runs.
- [ ] Нагрузочно проверить reconnect, server restart during execution, stale action receipt, duplicate mobile callback, user cancellation and concurrent sessions.

**Phase 2 Exit Criteria:** server restart не теряет accepted durable task; duplicate callbacks не дублируют side effects; user видит status/final result после return from overlay or app restart; sensitive device actions never execute without fresh consent/receipt.

---

## Deferred Until After Both Phases

- MCP server/client adapter for external SaaS integrations. Он должен адаптировать уже стабильный internal capability contract, а не становиться вторым execution path.
- Arbitrary autonomous browsing, email sending, purchases, shell/filesystem control and other high-impact operations.
- Continuous screen observation, background microphone capture or background navigation.
- Storage/exposure of hidden reasoning or «агентских мыслей».

## Delivery Order

1. Finish Phase 1.1 and 1.2 with provider-native `functionResponse` loop and memory/conversation tools.
2. Migrate reminders, screen and existing external actions; remove duplicate legacy dispatch.
3. Complete automated tests and physical 30-minute scenario; release a test APK only if the mobile event contract changes.
4. Design/migrate `AgentRun` persistence and worker, then implement reconnect/progress flow.
5. Run durable-runtime recovery tests before enabling it for normal users.

## First Implementation Slice

Начинаем с `AssistantAgentTurnService`: функция `memory_search` возвращает модели provenance-rich result, после чего модель может вызвать `memory_remember` с нормальной сформулированной записью. Это непосредственно исправляет текущую проблему памяти и подтверждает весь agent loop без опасных device permissions.
