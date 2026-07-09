# Красивый релиз Vass: AMOLED UI и Rive-аватар Ольги

Этот план фиксирует выбранное направление для ближайшего визуального релиза мобильного приложения **Vass**:

1. Делаем премиальный AMOLED Black интерфейс.
2. Заменяем текущий простой кодовый `AvatarFace` на красивый **Rive-аватар Ольги**.
3. Не делаем Three.js / GLB / full 3D в этом релизе.

Цель: получить эмоционально теплый, живой, визуально дорогой экран голосового ассистента без риска тяжелого 3D-пайплайна.

---

## Решение

### Принято

- Первый красивый релиз строим на **Rive**.
- Аватар Ольги делаем стилизованным, близким по ощущению к mockup `amoled_black_3d_avatar_mockup.png`: добрая девушка-ассистент, мягкое свечение, темный фон, живые глаза, улыбка.
- Rive asset (`.riv`) создается отдельно дизайнером/аниматором или через промежуточный AI concept art -> ручная векторная/риг-анимация.
- В приложении React Native подключаем готовый `.riv` и управляем его state machine из текущего voice state.

### Не делаем сейчас

- Не внедряем `three`, `expo-gl`, `GLTFLoader`, `olga.glb`.
- Не покупаем/не подключаем avatar-from-selfie SDK на этом этапе.
- Не делаем фотореалистичный talking head.
- Не делаем персональные аватары под конкретного пользователя в первом релизе.

Причина: сейчас нужен красивый стабильный релиз, а не R&D с тяжелым 3D.

---

## Визуальное Направление

### AMOLED Black UI

Фон приложения:

- Основной фон: `#000000`.
- Вся композиция должна выглядеть хорошо на OLED/AMOLED.
- Минимум крупных светлых поверхностей.
- Светящиеся акценты только вокруг аватара, микрофона и активного состояния.

Общее ощущение:

- Спокойно.
- Тепло.
- Немного “premium assistant”.
- Без перегруженного dashboard-интерфейса.

### Аватар Ольги

Референс:

- `docs/designs/amoled_black_3d_avatar_mockup.png`

Желаемый стиль:

- Stylized 2.5D / 3D-like illustration.
- Не реалистичная фотография.
- Не cartoon toy слишком детский.
- Добрая взрослая девушка, мягкая улыбка.
- Темные или каштановые волосы.
- Мягкий золотистый rim glow.
- Хорошая читаемость лица на черном фоне.

---

## Rive Asset Requirements

Файл:

```text
mobile/assets/olga.riv
```

Минимальная state machine:

```text
State machine: Olga

Inputs:
- state_idle: boolean
- state_listening: boolean
- state_thinking: boolean
- state_speaking: boolean
- state_paused: boolean
- state_sleeping: boolean
- tap: trigger
- pause: trigger
- resume: trigger
```

Минимальные анимации:

| State | Визуальное поведение |
| --- | --- |
| `idle` | Спокойное дыхание, редкое моргание, легкая улыбка |
| `listening` / `recording` | Более внимательный взгляд, мягкое синее/бирюзовое свечение |
| `thinking` | Микродвижение глаз, легкая задумчивость, фиолетовый акцент |
| `speaking` | Рот открывается/закрывается циклом, теплый золотой акцент |
| `paused` | Нейтральное ожидание, приглушенное свечение |
| `sleeping` | Закрытые глаза, медленное дыхание |

Nice-to-have:

- Отдельный `smile` input.
- Отдельный `mouthAmount` number input, если позже появится амплитуда речи.
- Небольшие sparkles/glow частицы вокруг аватара, но без шума и перегруза.

---

## Proposed Changes

Detailed Home UI redesign plan and target mockup:

```text
docs/designs/vass_home_ui_redesign_plan.md
docs/designs/vass_home_ui_final_mockup_v1.png
```

### [Component] AMOLED Black Home Screen

#### [MODIFY] `mobile/src/screens/HomeScreen.tsx`

- Перевести экран на черный фон `#000000`.
- Убрать светлую карточную эстетику.
- Сделать центральную композицию:
  - статус сверху: “Слушаю вас…”, “Думаю…”, “Отвечаю…”;
  - аватар Ольги в центре;
  - минимальная строка transcript/reply ниже;
  - нижние controls: настройки, микрофон/пауза, история.
- Пузырьки сообщений заменить на темные translucent панели:
  - background: `rgba(255,255,255,0.06)`;
  - border: `rgba(255,255,255,0.12)`;
  - text: `#F8FAFC`;
  - secondary text: `#94A3B8`.
- Кнопки сделать круглыми icon-first, без больших текстовых прямоугольников.

### [Component] Rive Avatar

#### [NEW] `mobile/src/components/OlgaRiveAvatar.tsx`

Новый компонент-обертка над Rive runtime.

Ответственность:

- Загружает `mobile/assets/olga.riv`.
- Прокидывает текущий `VoiceState` в Rive state machine.
- Обрабатывает tap/long press через внешние callbacks.
- Держит fallback на текущий `AvatarFace`, если `.riv` не загружен или runtime падает.

Примерный API:

```ts
interface OlgaRiveAvatarProps {
  state: VoiceState;
  onPress?: () => void;
  onLongPress?: () => void;
}
```

### [Component] Existing Avatar Fallback

#### [KEEP] `mobile/src/components/AvatarFace.tsx`

Текущий 2D-аватар не удаляем сразу.

Он остается:

- fallback при ошибке Rive;
- dev-заглушкой до появления финального `olga.riv`;
- быстрым тестовым компонентом для web preview.

### [Dependency] Rive Runtime

#### [ADD] mobile dependency

Добавить React Native runtime для Rive.

Перед внедрением проверить актуальную документацию и совместимость с текущими версиями:

- Expo SDK 57
- React Native 0.86
- React 19

Если runtime требует prebuild/native linking, фиксируем это в `docs/react-native/BUILD-WSL.md`.

---

## AI Concept Art Workflow

До заказа/создания `.riv` делаем несколько AI concept вариантов Ольги.

Цель генерации:

- найти лицо, настроение и силуэт;
- выбрать hair/eyes/clothes/glow;
- получить reference board для Rive-дизайнера;
- не использовать generated PNG напрямую как production runtime asset.

Промпт-направление:

```text
Stylized warm female AI voice assistant named Olga, friendly adult woman,
soft brown bob haircut, expressive kind eyes, gentle smile,
premium mobile assistant character, 2.5D / Rive-ready style,
black AMOLED background, subtle golden rim light, soft glow,
centered bust portrait, clean silhouette, no text, no logo, no watermark.
```

Нужно получить минимум 3 направления:

1. Теплая домашняя Ольга.
2. Более premium/tech assistant Ольга.
3. Более минималистичная calm companion Ольга.

После выбора:

- делаем финальный style sheet;
- отдельно готовим neutral/speaking/thinking/sleeping references;
- можно временно собрать layered PNG-прототип прямо в приложении, чтобы проверить эмоции и голосовые состояния;
- переносим выбранные состояния в Rive для полированного релиза.

---

## Verification Plan

### Automated

- `npm` install в `mobile/` проходит без конфликтов.
- TypeScript проверка проходит.
- Android release build не ломается.
- Если Rive требует native build, проверить Android build через существующий WSL workflow.

### Manual

Проверить на устройстве:

1. Экран запускается на AMOLED Black UI.
2. Аватар отображается без белых артефактов/рамок.
3. `idle`, `recording`, `thinking`, `speaking`, `paused`, `sleeping` визуально отличаются.
4. Tap по аватару сохраняет текущую механику start/finalize/resume.
5. Long press, если используется pause/resume, не конфликтует с обычным tap.
6. Анимация не дергается и не блокирует голосовой цикл.
7. Если Rive asset не загрузился, приложение не падает и показывает `AvatarFace`.

---

## Acceptance Criteria

- В приложении есть красивый центральный аватар Ольги.
- Общий экран визуально близок к `amoled_black_3d_avatar_mockup.png` по настроению, но реализован через легкий Rive-подход.
- Пользователь видит не техническую заглушку, а законченный companion experience.
- 3D/GLB/Three.js не добавлены в runtime.
- Текущая голосовая логика не ухудшена.

---

## Later, Not Now

Вернуться позже, если Rive-релиз зайдет:

- персональные аватары под пользователя;
- Avaturn / MetaPerson R&D;
- GLB avatar prototype;
- viseme-based lip sync;
- real amplitude-driven mouth input;
- paid talking-head experiments.
