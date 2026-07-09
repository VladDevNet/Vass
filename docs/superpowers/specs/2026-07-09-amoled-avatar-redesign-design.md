# AMOLED-редизайн главного экрана + layered-аватар Ольги (PR1)

## Контекст

Три документа в `docs/designs/` уже фиксируют направление редизайна:

- [`implementation_plan.md`](../../designs/implementation_plan.md) — общее решение: AMOLED Black UI + Rive-аватар в перспективе, но НЕ в этом релизе (нужен внешний .riv-инструментарий, недоступный в этой среде).
- [`olga_avatar_asset_plan.md`](../../designs/olga_avatar_asset_plan.md) — MVP-путь без Rive: layered PNG-аватар (`mobile/assets/avatar/`, ассеты уже готовы).
- [`vass_home_ui_redesign_plan.md`](../../designs/vass_home_ui_redesign_plan.md) — детальный план экрана в 5 фаз, компонентная разбивка, gesture-контракт.

Этот документ — не замена тем трём, а исполняемый spec конкретно для **первого PR** этого редизайна: фиксирует решения по местам, где три исходных дока сами себе противоречили или оставляли открытые вопросы, плюс согласованную визуальную палитру (сессия брейншторма с визуальным companion, 2026-07-09).

`mobile/AGENTS.md` требует свериться с https://docs.expo.dev/versions/v57.0.0/ перед правкой кода — актуально для `Image`/`Animated` API, которые здесь используются.

## Scope PR1

Из пяти фаз `vass_home_ui_redesign_plan.md`, PR1 объединяет **Phase 1 (визуальная оболочка) + Phase 2 (layered-аватар)** — раздельно они дали бы разнобойный промежуточный экран (чёрный фон + старый светлый аватар).

**Разрешённые противоречия исходных доков:**

1. **Device-link / logout.** Phase 1 говорит «убрать из HomeScreen», Phase 4 — «перенести в ProfileScreen». Если просто скрыть без переноса в PR1 — пользователь на весь цикл до Phase 4 теряет возможность выйти из аккаунта или привязать новое устройство. **Решение: PR1 сразу делает базовый перенос** (простая новая секция в `ProfileScreen.tsx`), Phase 4 (отдельный будущий PR) полирует это место, не переносит с нуля.
2. **Dock и жесты.** Формально это Phase 3, отдельный PR. Но кнопка-микрофон в доке без реакции на тап — регресс, не редизайн, и `forceFinalize`/`pauseConversation` уже реализованы и покрыты тестами (см. PR #59). **Решение: PR1 сразу навешивает существующие обработчики на dock-кнопку и аватар** (проброс пропсов, не новая логика). В Phase 3 остаётся полировка: блокировка навигации в небезопасных состояниях, визуальное (не текстовое) отображение disabled-контролов.

**В PR1:**
- AMOLED-фон, identity row, headline/subtitle, avatar stage, conversation peek, bottom dock — по `vass_home_ui_redesign_plan.md`.
- `OlgaLayeredAvatar` на реальных ассетах из `mobile/assets/avatar/`.
- Sleeping-таймер (новое состояние, не описанное в текущем `VoiceState` — см. ниже).
- Dock + аватар работают на существующих `forceFinalize`/`pauseConversation` сразу.
- Device-link + logout переезжают в `ProfileScreen`.
- `AvatarFace.tsx` остаётся как fallback при ошибке загрузки ассетов.

**Не в PR1** (последующие отдельные PR по плану): Rive, полировка disabled-состояний навигации (Phase 3 остаток), Phase 4 (полировка настроек), Phase 5 (haptics, a11y-лейблы, проверка мелких экранов).

## `sleeping` — архитектурное решение

`asset_plan.md`/`implementation_plan.md` упоминают состояние `sleeping` (закрытые глаза, медленное дыхание), которого нет в реальном `VoiceState` (`useVoiceChat.ts`). `VoiceState` глубоко завязан на живую логику VAD/микрофона/генераций — редизайн pause/resume (PR #59) потребовал 4 раунда независимого ревью именно из-за гонок вокруг этого состояния.

**Решение: `sleeping` — чисто визуальный, локальный флаг в `HomeScreen.tsx`, не новое значение `VoiceState`, `useVoiceChat.ts` не меняется.**

```ts
const SLEEP_AFTER_MS = 90_000; // отправная точка, уточнить после физического теста

const [sleeping, setSleeping] = useState(false);
useEffect(() => {
  setSleeping(false); // любая смена state — мгновенное "пробуждение"
  if (state !== 'idle') return;
  const timer = setTimeout(() => setSleeping(true), SLEEP_AFTER_MS);
  return () => clearTimeout(timer);
}, [state]);
```

Осмысленно только при `state === 'idle'`. Любой переход состояния сбрасывает таймер и будит аватар немедленно.

## Компоненты

### `mobile/src/components/OlgaLayeredAvatar.tsx` (новый)

```ts
interface OlgaLayeredAvatarProps {
  state: VoiceState;
  sleeping: boolean;
  disabled?: boolean;
  onLoadError?: () => void;
}
```

Base-портрет (`olga_base.png`) + абсолютно спозиционированные оверлеи (`olga_eyes_closed_overlay.png`, `olga_mouth_open_small_overlay.png`, `olga_mouth_open_big_overlay.png`, `olga_brows_thinking_overlay.png`), кросс-фейд через `Animated.Value` — тот же тайминг моргания/цикла рта, что уже проверен в `AvatarFace.tsx`, просто вместо нарисованных фигур картинки. Halo — не PNG, а `View` с radial-gradient/shadow ЗА портретом (см. «Визуальная палитра» ниже); собственное золотое свечение портрета не перекрашивается.

`onLoadError` зовётся при ошибке загрузки любого `Image` — используется `HomeScreen` для fallback (см. «Обработка ошибок»).

### `mobile/src/components/ConversationPeek.tsx` (новый)

```ts
interface ConversationPeekProps {
  transcript: string;
  reply: string;
  state: VoiceState;
}
```

Показывает `transcript` во время `recording`, `reply` во время `speaking`. Если оба пусты — компонент ничего не рендерит (не placeholder-фраза): минимализм важнее заполненности, отражает общий тон дизайна («не обучающий интерфейсу»).

### `mobile/src/components/VoiceControlDock.tsx` (новый)

```ts
interface VoiceControlDockProps {
  state: VoiceState;
  onSettingsPress: () => void;
  onHistoryPress: () => void;
  onMicPress: () => void;       // forceFinalize
  onMicLongPress: () => void;   // pauseConversation
  navigationDisabled: boolean;  // как сейчас: settings/history только при state === 'idle'
}
```

Три иконки: settings (лево) / mic с состоянием-зависимой иконкой (центр) / history (право). Центральная иконка меняется по `state` (mic / active mic / loader / interrupt / play) — таблица ниже.

### `VoiceStatusHeader` — НЕ отдельный файл

Приватная секция/функция прямо в `HomeScreen.tsx`: `state → {headline, subtitle}` маппинг + identity row («Ольга» / presence-лейбл). Простой lookup на десяток строк — отдельный компонент был бы лишней абстракцией.

### `mobile/src/theme/amoled.ts` (новый)

Цветовые константы: `#000000` фон, `rgba(255,255,255,0.06..0.12)` стеклянные поверхности, `#F8FAFC`/`#94A3B8` текст, halo-цвета из палитры ниже. Экономит от дублирования magic strings по 4-5 новым компонентам — не полноценная дизайн-система (см. отклонённый вариант 2 в разделе «Рассмотренные подходы»).

### Изменённые файлы

- **`HomeScreen.tsx`** — полная перестройка layout'а (fullscreen `SafeAreaView`, не `ScrollView`), собирает всё выше + sleeping-таймер + fallback-логику.
- **`ProfileScreen.tsx`** — новая секция: device-link код + logout.

`AvatarFace.tsx` и вся голосовая логика (`useVoiceChat.ts`, `useVad.ts`, `systemSpeech.ts`) не меняются.

## Визуальная палитра (согласовано через визуальный companion)

Halo — `View` с radial-gradient ЗА портретом, не перекраска самого изображения (перекраска через hue-rotate даёт неестественный цвет кожи и требует SVG-фильтр — RN не имеет built-in hue-rotate).

| `state` | halo | headline | dock-иконка |
|---|---|---|---|
| `idle` (не спит) | золотой, мягкий (совпадает с уже «запечённым» в портрете) | Слушаю вас… | mic |
| `idle` + `sleeping` | выключен; портрет затемнён (`brightness(0.45) saturate(0.5)`) + eyes-closed overlay | *(headline/subtitle скрыты полностью)* | mic |
| `recording` | синий (`rgba(59,130,246,…)`) | Слышу вас… | active mic |
| `thinking` | фиолетовый (`rgba(168,85,247,…)`) | Думаю… | loader, тап — no-op |
| `speaking` | золотой, шире/ярче + пульс | Отвечаю… | interrupt/mic |
| `paused` | приглушённый серый (`rgba(148,163,184,…)`), портрет чуть темнее (`brightness(0.75) saturate(0.7)`) | На паузе | play |

## Обработка ошибок

Если `Image` для base или любого оверлея не загрузился — `OlgaLayeredAvatar` зовёт `onLoadError`. `HomeScreen` держит `assetsFailed: boolean`; при `true` рендерит `<AvatarFace state={state} />` вместо `<OlgaLayeredAvatar />` на остаток сессии (без retry-петли). Отражает acceptance criteria исходного плана: «при отсутствии/ошибке ассетов приложение не падает».

## Gesture-контракт

Без изменений от уже реализованного (PR #59): тап = `forceFinalize` (send-now / barge-in / resume при паузе), long-press = `pauseConversation`. Оба навешены и на аватар, и на центральную dock-кнопку — не два разных источника логики, один и тот же коллбэк из `useVoiceChat`.

Открытый, осознанно отложенный риск: подсказка «удерживайте, чтобы поставить на паузу» убирается с экрана вместе с остальными техническими текстами — long-press как способ поставить паузу становится недокументированным в UI жестом. Аудитория проекта включает пожилых пользователей (см. историю упрощённого входа в BACKLOG.md). Решение отложено до физического теста новой сборки — если обнаружится, что жест не находят, добавить одноразовую подсказку при первом запуске экрана (не постоянный текст).

## Обработка ошибок и проверка

**Проверка:**
- `npx tsc --noEmit`.
- Веб-превью — в отличие от голосового цикла, здесь реально можно проверить визуально: изображения грузятся и в Expo web (в отличие от нативного mic/audio). Проверить рендер всех состояний, halo, sleeping, компоновку dock — до сборки APK.
- Физический тест на устройстве: AMOLED без белых артефактов, все состояния визуально различимы, тап/long-press не сломаны, анимация не блокирует голосовой цикл (мимика на отдельном `Animated`-треке, не блокирующем JS-поток), sleeping-таймер ощущается вовремя, fallback на `AvatarFace` при повреждённых ассетах не роняет приложение.

## Рассмотренные подходы (для памяти)

1. **Выбран.** Прямое исполнение уже написанного плана + маленький файл цветовых констант.
2. **Отклонён.** Полноценный `theme.ts` + переиспользуемые `GlassPanel`/`IconButton` для ВСЕХ будущих экранов сразу. Полезно, если AMOLED дальше поедет в `ProfileScreen`/`ChatHistoryScreen`, но больше работы сейчас ради пользы позже — YAGNI на этом этапе.
3. **Отклонён.** Всё inline в `HomeScreen.tsx` без новых файлов компонентов. Быстрее написать, но противоречит и собственной разбивке в исходном плане, и практике этого проекта — маленькие фокусированные компоненты вместо разбухающего экрана.

## Не в этом PR (из исходных доков, подтверждено)

Rive runtime, полный 3D/GLB/Three.js, платный avatar SDK, viseme lip sync, персональные аватары под пользователя, haptics/a11y-полировка (Phase 5), полировка disabled-состояний навигации (остаток Phase 3), финальная полировка настроек (Phase 4).
