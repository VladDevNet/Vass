# Устранение находок PROJECT-AUDIT-2026-07-10 — план работ

> Не классический feature-план — источник требований уже есть в
> `docs/PROJECT-AUDIT-2026-07-10.md` (раздел на каждую находку содержит
> «Риск» и «Исправление»). Этот документ фиксирует только: разрешение 4
> продуктовых развилок, которые в самом аудите оставлены открытыми, и
> порядок/группировку работы по фазам.

## Разрешённые развилки (подтверждено пользователем 2026-07-11)

- **ARCH-01**: legacy PWA убирается из публичного контура сейчас, mobile —
  единственный клиент. НЕ «diagnostic-only с датой удаления» — сразу.
- **SEC-02**: speaker identification остаётся выключенной. Фикс — только
  укрепить флаг/комментарий, чтобы не включили случайно. Полная tenant
  isolation НЕ делается в рамках этой программы работ.
- **SEC-03**: шифруются все три поля (`GeminiApiKey`, `OpenAiApiKey`,
  `AnthropicApiKey`) — OpenAiApiKey/AnthropicApiKey НЕ удаляются, несмотря
  на пометку «остаток старого продукта» в разделе 6 аудита.
- **QA-01**: тестовая инфраструктура строится сейчас, не откладывается.
  Порядок: integration-тесты auth/chat/settings → GitHub Actions CI
  (restore/build/test, `tsc`, expo-doctor, dependency audit).

Не заданы явно (беру решение сам, по умолчанию — из текста аудита, без
дополнительного вопроса, раз это не продуктовая развилка, а деталь
реализации):
- **OPS-01** (миграции на старте): не убираю `db.Database.Migrate()` из
  `Program.cs` полностью — это ломало бы уже отработанный в этой сессии
  deploy-процесс. Добавляю `pg_dump` перед `docker compose up -d api` как
  явный шаг deploy-процедуры (не гейт внутри кода) + smoke test после.
- **SEC-05** (JWT без revocation): полный refresh-token redesign — это
  отдельная большая фича, не входит в эту программу работ. Делаю только
  дешёвую часть: security stamp/version claim, чтобы был хоть какой-то путь
  к принудительному revoke, без redesign auth-потока.
- **DATA-01** (retention): фиксирую политику как часть фикса (30 дней для
  client logs, audio удаляется вместе с session — уже так и работает,
  добавляю только явный scheduled cleanup для client logs).

## Фазы

### Gate A — безопасность (первым делом)
1. SEC-01 — rate limiting на login/register/device-link redeem
2. SEC-04 — безопасная обработка AudioFileName (path validation)
3. SEC-02 — укрепить флаг speaker identification (укрепление, не полный фикс)
4. SEC-03 — шифрование BYOK-ключей (все три провайдера)
5. SEC-07 — лимиты запросов и валидация входа

### Gate B — надёжность и инфраструктура
6. REL-01 — параллельный DbContext в MaybeUpdateCustomInstructionsAsync
7. REL-02 — порядок SSE terminal event / сохранения assistant message
8. REL-04 — typed error channel для AI/provider errors (тот же класс бага,
   что уже фиксился в ConversationMemoryService — здесь общий случай)
9. REL-03 — readiness endpoint (DB/storage), отдельно от liveness
10. OPS-01 — pg_dump перед деплоем + smoke test (см. развилку выше)
11. OPS-02 — убрать мёртвые ссылки на speaker-id/silero-tts из compose
12. QA-01 — тестовая инфраструктура: integration-тесты auth/chat/settings,
    затем GitHub Actions CI

### Gate C — продукт и очистка
13. ARCH-01 — убрать legacy PWA из публичного контура
14. SEC-05 — security stamp/version claim (дешёвая часть, см. развилку)
15. SEC-06 — CORS/CSP hardening
16. DATA-01 — retention для client logs
17. API-01 — PATCH-семантика для settings вместо GET+PUT всего объекта
18. MOB-02 — очистка TTS cache файлов
19. DEP-01 — обновление зависимостей (.NET patch, Npgsql, MCP; `expo install --check`)
20. DOC-01 — `docs/README.md` с картой документов и статусами
21. Раздел 6 (техдолг) — по одному PR на логически связанную группу:
    мёртвые OpenAI/Anthropic-поля остаются (см. SEC-03), но
    `ModelContextProtocol`, hard-coded dialog session, `GetSessions`
    side-effect, `.env.example` формат — отдельные мелкие PR.

MOB-01 (рефакторинг useVoiceChat) сознательно НЕ включён — аудит сам
рекомендует не делать механический рефакторинг без characterization tests
(QA-01), а строить их для файла такого размера — отдельная большая задача
за пределами этой программы работ.

## Процесс

Каждый пункт — отдельная ветка/PR, коммит-цикл как весь остаток сессии.
Мелкие механические фиксы (path validation, dead compose entries, cache
cleanup) — прямая реализация без отдельного review-раунда. Пункты с
реальной архитектурной поверхностью (SEC-01 rate limiting, SEC-03
шифрование, QA-01 CI, REL-01 DbContext) — с task-review, как в
subagent-driven-development, но без отдельного design-документа на
каждый — фикс уже специфицирован текстом аудита.
