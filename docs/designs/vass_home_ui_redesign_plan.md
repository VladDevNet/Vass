# Vass Home UI Redesign Plan

Цель: полностью заменить текущий технический `HomeScreen` на красивый голосовой экран-компаньон, сохранив уже работающую механику разговора, tap/long-press жесты, историю, настройки и device-link сценарий.

Финальный целевой макет:

```text
docs/designs/vass_home_ui_final_mockup_v1.png
```

## What Exists Now

Кодовая база уже содержит важную механику, которую нельзя потерять:

| Area | Current source | Keep |
| --- | --- | --- |
| Voice state machine | `mobile/src/hooks/useVoiceChat.ts` | `idle`, `recording`, `thinking`, `speaking`, `paused` |
| Main screen gestures | `mobile/src/screens/HomeScreen.tsx` | tap через `forceFinalize`, long press через `pauseConversation` |
| Current avatar fallback | `mobile/src/components/AvatarFace.tsx` | оставить как fallback/dev mode |
| Olga assets | `mobile/assets/avatar/` | использовать для layered PNG прототипа и как source для Rive |
| Settings | `mobile/src/screens/ProfileScreen.tsx` | оставить отдельным экраном |
| History | `mobile/src/screens/ChatHistoryScreen.tsx` | оставить отдельным экраном |
| Device link | `api.createDeviceLink()` в `HomeScreen` | перенести в настройки/devices, не показывать на главном экране |

## Product Direction

Главный экран больше не должен выглядеть как тестовая панель.

Пользователь должен видеть:

1. Ольгу как центрального живого персонажа.
2. Один крупный текущий статус.
3. Мягкий live transcript/reply preview.
4. Три понятных нижних действия: настройки, главное голосовое действие, история.

Пользователь не должен видеть на главном экране:

1. Технические подсказки вроде "удерживайте, чтобы поставить на паузу".
2. Device-link код и инструкции.
3. Logout.
4. Debug-like labels `Вы:` / `Ассистент:`.
5. Большие текстовые кнопки.
6. Светлые карточки на белом фоне.

## Final Screen Structure

### 1. Fullscreen AMOLED Surface

- `SafeAreaView`, не `ScrollView` для основного состояния.
- Фон `#000000`.
- Статус-бар светлый.
- Никаких белых секций.
- Все вторичные поверхности: темное стекло `rgba(255,255,255,0.06..0.10)` с тонкой границей.

### 2. Top Identity Row

Слева:

- online dot;
- имя `Ольга`;
- короткий emotional presence label: `рядом`, `слушает`, `думает`, `говорит`.

Справа:

- компактная profile/settings кнопка-иконка;
- без email/logout/device-code на главном экране.

### 3. Main State Headline

Большой текст:

| State | Headline |
| --- | --- |
| `idle` | `Слушаю вас…` |
| `recording` | `Слышу вас…` |
| `thinking` | `Думаю…` |
| `speaking` | `Отвечаю…` |
| `paused` | `На паузе` |

Подзаголовок короткий, не обучающий интерфейсу:

- `Можно говорить естественно`
- `Собираю мысль`
- `Сейчас отвечу`
- `Продолжим, когда будете готовы`

### 4. Olga Avatar Stage

Первый релиз:

- `OlgaLayeredAvatar` на базе `mobile/assets/avatar/olga_base.png`.
- Overlays:
  - `olga_eyes_closed_overlay.png`;
  - `olga_mouth_open_small_overlay.png`;
  - `olga_mouth_open_big_overlay.png`.
- Glow делаем в React Native стилями/анимацией, не отдельными PNG, если этого хватит.

Позже:

- заменить внутреннюю реализацию `OlgaLayeredAvatar` на Rive;
- внешний контракт компонента оставить тем же.

### 5. Live Conversation Preview

Вместо двух больших bubbles:

- одна темная glass-плашка под waveform;
- показывает последнюю активную строку:
  - во время `recording`: текущий transcript;
  - во время `speaking`: краткий reply;
  - если текста нет: плашка скрыта или показывает мягкую нейтральную фразу.

Не использовать labels `Вы:` и `Ассистент:` на главном экране.

### 6. Bottom Control Dock

Три зоны:

| Position | Action | Notes |
| --- | --- | --- |
| Left | Settings/Profile | icon-first, opens `ProfileScreen` when safe |
| Center | Primary voice action | same tap/long-press model as current avatar press |
| Right | History | icon-first, opens `ChatHistoryScreen` when safe |

Главная кнопка меняет иконку:

| State | Center icon | Tap behavior |
| --- | --- | --- |
| `idle` | mic | current `forceFinalize` behavior |
| `recording` | active mic / waveform | answer now via `forceFinalize` |
| `thinking` | subtle loader | no-op tap, long press still pauses |
| `speaking` | interrupt / mic | barge-in via `forceFinalize` |
| `paused` | play | resume via existing paused branch in `forceFinalize` |

Long press:

- avatar stage and center button both call `pauseConversation`;
- this keeps the current pause behavior from all active states.

## Gesture Contract

The redesigned UI must preserve the current behavior:

| Gesture | Current target | New target | Behavior |
| --- | --- | --- | --- |
| Tap while paused | avatar Pressable | avatar + center button | resume conversation |
| Tap while speaking | avatar Pressable | avatar + center button | interrupt / barge-in |
| Tap while idle/recording | avatar Pressable | avatar + center button | finalize current user turn early |
| Tap while thinking | avatar Pressable | avatar + center button | no destructive action |
| Long press while active | avatar Pressable | avatar + center button | pause conversation |

Implementation detail:

```tsx
<Pressable
  onPress={forceFinalize}
  onLongPress={() => void pauseConversation()}
  disabled={!sessionId}
>
  ...
</Pressable>
```

The component tree can change, but this contract should remain.

## Proposed Components

### `OlgaLayeredAvatar`

New component:

```text
mobile/src/components/OlgaLayeredAvatar.tsx
```

Props:

```ts
interface OlgaLayeredAvatarProps {
  state: VoiceState;
  disabled?: boolean;
}
```

Responsibilities:

- render base Olga image;
- animate breathing/head bob;
- random blink in `idle`, `recording`, `speaking`;
- closed eyes in `paused` only if we decide paused should look resting;
- mouth loop in `speaking`;
- glow color by state.

### `VoiceStatusHeader`

New component or private section in `HomeScreen`:

- maps `VoiceState` to headline/subtitle;
- no technical hints.

### `ConversationPeek`

New component:

- picks current transcript/reply;
- one-line or two-line clamp;
- hides empty state cleanly.

### `VoiceControlDock`

New component:

- settings icon;
- primary voice button;
- history icon;
- receives existing callbacks from `HomeScreen`.

## Implementation Phases

### Phase 1: Visual Shell

1. Replace `ScrollView` with fullscreen `SafeAreaView` + `View`.
2. Move current device-link/logout UI out of `HomeScreen`.
3. Add AMOLED styles and final layout.
4. Keep existing `useVoiceChat` hook untouched.

### Phase 2: Olga Layered Avatar

1. Add `OlgaLayeredAvatar`.
2. Use existing AI-sliced overlays.
3. Keep `AvatarFace` as fallback behind a feature flag or error path.
4. Validate that images render on native and web.

### Phase 3: Controls And Gestures

1. Bind avatar stage and center button to the current tap/long-press handlers.
2. Keep navigation disabled when the current state makes it unsafe.
3. Show disabled controls visually, not as technical explanatory text.
4. Make paused state visually obvious.

### Phase 4: Settings Cleanup

1. Move "device code for new device" into `ProfileScreen`.
2. Move logout into `ProfileScreen`.
3. Keep history as the right dock action.

### Phase 5: Polish

1. Add haptic feedback if available.
2. Add accessible labels for icon buttons.
3. Check small Android screens.
4. Check long Russian text in transcript/reply.
5. Confirm no text overlaps at 360px width.

## Acceptance Criteria

1. Главный экран визуально соответствует `vass_home_ui_final_mockup_v1.png`.
2. На главном экране нет device-link, logout, debug labels и технических gesture-инструкций.
3. Все существующие voice states отображаются визуально.
4. Tap/long-press поведение совпадает с текущей реализацией.
5. Настройки и история доступны, но не мешают голосовому сценарию.
6. Аватар Ольги занимает центральное место и использует текущие AI assets.
7. При отсутствии/ошибке новых assets приложение не падает и может показать `AvatarFace`.
8. TypeScript check проходит.
9. Expo web/native preview не показывает белых артефактов вокруг PNG-слоев.

## Not In This Pass

- Rive runtime integration.
- Full 3D / GLB / Three.js.
- Paid avatar SDK.
- Viseme-level lip sync.
- Multi-character avatar personalization.
