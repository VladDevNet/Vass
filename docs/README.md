# Документация Vass — карта и статусы

Этот файл — единая точка входа во всю документацию проекта. Цель — то, что
просил [аудит от 2026-07-10](PROJECT-AUDIT-2026-07-10.md) (раздел P2/DOC-01):
`DEVELOPMENT.md`, `docs/react-native/*`, дизайн-планы и superpowers-спеки
местами противоречат друг другу и не помечены единообразно, готова ли
описанная функциональность или это ещё план.

**Этот аудит — baseline.** Его находки последовательно закрываются планом
[`2026-07-11-audit-remediation.md`](superpowers/plans/2026-07-11-audit-remediation.md);
не заводите второй конкурирующий backlog для той же ревизии — новые находки
добавляются туда же или в `BACKLOG.md`, смотря к чему они относятся.

## Статусы

| Статус | Значение |
|---|---|
| `implemented` | Реализовано и работает в текущей версии продукта |
| `in progress` | Частично реализовано — документ смешивает готовое с ещё не сделанным (см. примечание в строке) |
| `approved` | Подход согласован с пользователем, реализация не начата |
| `draft` | Написано, но не согласовано и/или ещё не запланировано в BACKLOG |
| `legacy` | Устарело — заменено другим документом или другим техническим решением |
| `reference` | Живой справочник/реестр без понятия «готовности» (обновляется постоянно, не описывает одну фичу) |

## Корень репозитория

| Документ | Статус | Заметка |
|---|---|---|
| [`DEVELOPMENT.md`](../DEVELOPMENT.md) | `implemented` | Актуальная архитектура бэкенда и деплоя |

## Планирование

| Документ | Статус | Заметка |
|---|---|---|
| [`ROADMAP.md`](ROADMAP.md) | `reference` | Живой приоритетный порядок между фазами/спеками/новыми направлениями (не конкурирует с BACKLOG.md — тот держит конкретные таски внутри мобильных фаз) |

## React Native (`docs/react-native/`)

| Документ | Статус | Заметка |
|---|---|---|
| [`README.md`](react-native/README.md) | `implemented` | Индекс каталога мобильной документации |
| [`architecture.md`](react-native/architecture.md) | `in progress` | Expo/RN/TS каркас, `/api/v1`, упрощённый вход реализованы. Push-уведомления (FCM/APNs, «напоминания из памяти») описаны, но нигде не реализованы — ни бэкенда, ни `expo-notifications` в мобильном клиенте |
| [`audio-and-vad.md`](react-native/audio-and-vad.md) | `in progress` | Аудио-сессия и серверный STT актуальны. Раздел «VAD on-device: Silero VAD через onnxruntime» устарел: реально в `useVad.ts` портирован RMS/dBFS-подход с веба, а не Silero ONNX — нативный ML-модуль не понадобился |
| [`tts-and-avatar.md`](react-native/tts-and-avatar.md) | `in progress` | TTS-раздел (`expo-speech`) актуален. Avatar-раздел описывает раннюю версию `AvatarFace.tsx` (07-07) как основной аватар — с 07-09/07-10 основным стал `LayeredAvatar` (layered PNG, не Rive), см. `docs/designs/*_avatar_asset_plan.md`. `AvatarFace.tsx` не удалён: остаётся runtime fallback на случай ошибки загрузки ассетов (`HomeScreen.tsx`) |
| [`memory.md`](react-native/memory.md) | `in progress` | Кратко- и среднесрочная память реализованы. Долгосрочная память (RAG/pgvector) — следующий пункт роадмапа, ещё не начата |
| [`content-companion.md`](react-native/content-companion.md) | `draft` | YouTube/новости/сериалы/рецепты через intent-роутер — написано, ни один intent не реализован |
| [`risks.md`](react-native/risks.md) | `reference` | Живой реестр рисков и осознанных ограничений |
| [`BACKLOG.md`](react-native/BACKLOG.md) | `reference` | Живой бэклог Фаз 0-7. Фазы 0-6 в основном реализованы (см. историю PR); Фаза 7 (overlay) — `draft`-спека, ядро (7.1-7.2) пересмотрено на более ранний приоритет 2026-07-12, см. [ROADMAP.md](../ROADMAP.md) |
| [`BUILD-WSL.md`](react-native/BUILD-WSL.md) | `implemented` | Проверенный процесс локальной Android-сборки, используется регулярно |
| [`BUILD-MACOS.md`](react-native/BUILD-MACOS.md) | `approved` | Согласованный процесс iOS-сборки; ни разу не исполнялся — нет доступного Mac |

## Дизайн-планы ассетов (`docs/designs/`)

| Документ | Статус | Заметка |
|---|---|---|
| [`implementation_plan.md`](designs/implementation_plan.md) | `legacy` | Ранний черновик решения «строим на Rive» для AMOLED-релиза — заменён формальной спекой/планом от 2026-07-09 и фактической layered-PNG реализацией |
| [`vass_home_ui_redesign_plan.md`](designs/vass_home_ui_redesign_plan.md) | `implemented` | Инвентаризация HomeScreen под AMOLED-редизайн — выполнено |
| [`olga_avatar_asset_plan.md`](designs/olga_avatar_asset_plan.md) | `implemented` | Ассеты Ольги нарезаны, используются в `LayeredAvatar` |
| [`male_avatar_asset_plan.md`](designs/male_avatar_asset_plan.md) | `implemented` | Ассеты мужского аватара нарезаны, используются в `LayeredAvatar` |

## Superpowers — спеки (`docs/superpowers/specs/`)

| Документ | Статус | Заметка |
|---|---|---|
| [`2026-07-09-amoled-avatar-redesign-design.md`](superpowers/specs/2026-07-09-amoled-avatar-redesign-design.md) | `implemented` | AMOLED-редизайн HomeScreen — смёрджено |
| [`2026-07-10-male-avatar-and-voice-gender-design.md`](superpowers/specs/2026-07-10-male-avatar-and-voice-gender-design.md) | `implemented` | Мужской аватар + пол голоса — смёрджено |
| [`2026-07-10-medium-term-memory-design.md`](superpowers/specs/2026-07-10-medium-term-memory-design.md) | `implemented` | Среднесрочная память (резюме сессии) — смёрджено |
| [`2026-07-10-visual-capture-and-image-tasks-design.md`](superpowers/specs/2026-07-10-visual-capture-and-image-tasks-design.md) | `draft` | Спека написана; соответствующего implementation-плана ещё нет |
| [`2026-07-11-android-overlay-and-screen-assistance-design.md`](superpowers/specs/2026-07-11-android-overlay-and-screen-assistance-design.md) | `draft` | Спека Фазы 7. Gate A/B аудита закрыты 2026-07-12; ядро (7.1-7.2) пересмотрено на более ранний приоритет, см. [ROADMAP.md](../ROADMAP.md) — implementation-план ещё не написан |

## Superpowers — планы (`docs/superpowers/plans/`)

| Документ | Статус | Заметка |
|---|---|---|
| [`2026-07-09-amoled-avatar-redesign.md`](superpowers/plans/2026-07-09-amoled-avatar-redesign.md) | `implemented` | Смёрджено |
| [`2026-07-10-male-avatar-and-voice-gender.md`](superpowers/plans/2026-07-10-male-avatar-and-voice-gender.md) | `implemented` | Смёрджено |
| [`2026-07-10-medium-term-memory.md`](superpowers/plans/2026-07-10-medium-term-memory.md) | `implemented` | Смёрджено |
| [`2026-07-11-audit-remediation.md`](superpowers/plans/2026-07-11-audit-remediation.md) | `implemented` | Вся программа (Gate A/B/C, 21 пункт) завершена 2026-07-12, включая QA-01 (CI) |

## Аудит

| Документ | Статус | Заметка |
|---|---|---|
| [`PROJECT-AUDIT-2026-07-10.md`](PROJECT-AUDIT-2026-07-10.md) | `reference` | Baseline-аудит проекта. Все находки Gate A/B/C закрыты планом remediation выше (2026-07-12) |
| [`AUDIT-LEGACY.md`](AUDIT-LEGACY.md) | `legacy` | Каталог хвостов родительского проекта Polish Tutor. DELETE-раздел выполнен; REWORK сознательно отложен, см. `react-native/README.md` → «Статус» |

## Инструкции для агентов

Не документация продукта — обязательные к прочтению правила для AI-агентов
перед правками кода в соответствующей директории.

| Документ | Статус | Заметка |
|---|---|---|
| [`mobile/AGENTS.md`](../mobile/AGENTS.md) | `reference` | Expo API версионируется между релизами — читать точную версию доков перед правками |
| [`mobile/CLAUDE.md`](../mobile/CLAUDE.md) | `reference` | `@AGENTS.md` — тот же файл для Claude Code |

## Как поддерживать актуальность

Когда спека/план из таблиц выше доходит до merge — обновите её строку на
`implemented` в том же PR, где меняется код. Когда фича сознательно
откладывается или заменяется другим подходом — `draft`/`legacy` с одной
строкой причины, а не молчаливое расхождение с кодом.
