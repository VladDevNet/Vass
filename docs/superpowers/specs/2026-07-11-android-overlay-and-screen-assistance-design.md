# Android overlay и помощь поверх других приложений — design

## Статус и место в плане

**Статус:** принято как Android-only направление, реализация отложена в последнюю фазу общего mobile-плана.  
**Общий план:** [BACKLOG.md — Фаза 7](../../react-native/BACKLOG.md#фаза-7--android-overlay-и-помощь-поверх-других-приложений).  
**Security prerequisite:** Gate A/B из [audit remediation plan](../plans/2026-07-11-audit-remediation.md), особенно attachment ownership, request limits, foreground readiness и тестовая инфраструктура.  
**Платформа:** Android 8.0+ (API 26+). На Android 7.x основное приложение продолжает работать, но overlay недоступен.  
**Почему последняя фаза:** функция зависит от стабильного фонового voice runtime, intent-роутера, безопасных image attachments и результатов пилота. Она не должна создавать второй разговорный pipeline поверх еще меняющегося основного.

На момент добавления этой спецификации последней фазой общего плана была **Фаза 6 — пилот**. Overlay добавлен следующей **Фазой 7**, после пилота и решения о способе распространения приложения.

## Контекст

Пользователь должен иметь возможность свернуть Vass в небольшой круглый аватар поверх YouTube, браузера, настроек и других Android-приложений, продолжить голосовой разговор, попросить объяснить текущий экран, запустить внешний контент и вернуться в полноэкранный Vass.

Это **не Picture-in-Picture**. PiP рассчитан прежде всего на видео и видеозвонки, а элементы управления показываются системным меню после нажатия на окно. Vass нужен постоянно доступный интерактивный control, поэтому используется `WindowManager` с `TYPE_APPLICATION_OVERLAY` и разрешением `SYSTEM_ALERT_WINDOW`.

## Product Goals

1. Один узнаваемый avatar-control поверх других приложений.
2. Сохранение существующих жестов разговора: короткий тап, пауза/продолжение, перебивание.
3. Голосовые команды для открытия YouTube/URL и возврата в Vass.
4. Одноразовый разбор текущего экрана только по явному запросу пользователя.
5. Видимый и честный privacy contract: никакого скрытого захвата экрана или микрофона.
6. Один conversation runtime для HomeScreen и overlay, без дублирования state machine.

## Не цели

- iOS overlay: iOS не разрешает произвольное окно поверх других приложений.
- постоянная трансляция экрана на сервер;
- скрытый screenshot без системного consent;
- `AccessibilityService`, чтение accessibility tree, автоклики и управление чужим UI;
- автоматический старт overlay/микрофона после перезагрузки телефона;
- обход `FLAG_SECURE`, DRM или защищенных банковских экранов;
- встраивание YouTube-видео в overlay;
- второй экземпляр React Native application/root view внутри Android service.

## UX

### Включение

Overlay включается осознанно из настроек Vass переключателем **«Плавающий помощник»**.

Первое включение:

1. Vass показывает собственный экран с коротким объяснением: аватар будет виден поверх других приложений; микрофон и захват экрана работают только в явно обозначенных режимах.
2. После подтверждения открывается системный экран `ACTION_MANAGE_OVERLAY_PERMISSION` для package `com.vass.assistant`.
3. После возврата приложение проверяет `Settings.canDrawOverlays(context)`.
4. Если permission выдан, foreground service стартует пока Activity еще видима, затем пользователь может свернуть приложение.
5. Если permission не выдан, переключатель возвращается в off и приложение остается полностью рабочим без overlay.

Нельзя отправлять пользователя в Settings сразу при запуске приложения. Permission запрашивается только после явного включения функции.

### Внешний вид

- круг `72dp`, touch target не меньше `72dp`;
- base-портрет выбранного avatar (`olga` или `male`), без текста;
- AMOLED-черная внешняя обводка для отделения от светлого фона;
- halo состояния вокруг аватара;
- положение свободно меняется drag-жестом;
- после отпускания круг прилипает к ближайшему краю экрана;
- координаты сохраняются локально отдельно для portrait/landscape;
- учитываются status/navigation bars, cutouts и gesture insets;
- overlay не должен закрывать системные permission dialogs и keyboard controls.

Overlay рисуется нативным Android `View`, используя существующие PNG assets. В первой версии не требуется перенос всей layered-анимации: base image + halo + простой blink/speaking pulse достаточно. Это сохраняет один маленький и устойчивый native view вместо второго RN renderer.

### Состояния

| Conversation state | Overlay |
|---|---|
| `idle` | спокойный avatar, нейтральный halo |
| `recording` | яркий listening halo, мягкий pulse |
| `thinking` | медленный теплый pulse |
| `speaking` | speaking halo + простая mouth/pulse animation |
| `paused` | приглушенный avatar, halo без анимации |
| `error` | короткая красная обводка, затем возврат в предыдущее устойчивое состояние |

`sleeping` остается презентационным состоянием полноэкранного HomeScreen и не используется в overlay: плавающий control должен однозначно выглядеть доступным.

### Жесты одной кнопки

| Жест | Действие |
|---|---|
| Drag | переместить overlay, не запускать voice action |
| Short tap | тот же manual control, что центральная кнопка HomeScreen: finalize/interrupt/resume в зависимости от состояния |
| Long press, 600ms | pause conversation; повторный long press — resume |
| Double tap | открыть полноэкранный Vass |

Жесты реализуются через Android `GestureDetector`. Drag отменяет tap после превышения touch slop. Double tap имеет системное окно распознавания; short tap исполняется через `onSingleTapConfirmed`, чтобы один физический жест никогда не выполнял одновременно control и open-app.

### Постоянное уведомление

Пока overlay service активен, Android показывает foreground notification:

- title: `Vass работает поверх приложений`;
- текущее состояние: `Слушает`, `Думает`, `Говорит`, `На паузе`;
- action `Открыть`;
- action `Пауза` / `Продолжить`;
- action `Остановить`.

`Остановить` немедленно убирает overlay, прекращает background microphone/audio, освобождает projection resources и завершает service.

## Conversation Runtime

### Один runtime, два UI-клиента

`HomeScreen` и overlay не должны владеть отдельными voice loops. До реализации overlay существующий `useVoiceChat` требуется отделить от жизненного цикла конкретного React component:

```text
ConversationRuntime
  ├─ state/transcript/reply/error stream
  ├─ forceFinalize()
  ├─ pause()
  ├─ resume()
  ├─ requestScreenAnalysis(prompt)
  ├─ openExternalIntent(intent)
  └─ stop()

HomeScreen UI ───────┐
                     ├── один ConversationRuntime
Android Overlay ─────┘
```

Конкретное внутреннее размещение runtime выбирается implementation plan после отдельного spike, но должны выполняться инварианты:

- уход Activity в background не обрывает активный разговор;
- foreground service является владельцем долгоживущего background lifecycle;
- React screen может размонтироваться и вернуться без создания второго turn;
- native overlay получает только минимальное состояние и команды;
- networking/session/auth остаются общими с основным приложением;
- service не хранит JWT в notification, Intent extras или обычных preferences.

Простой вариант «оставим hook смонтированным и надеемся, что JS process не будет приостановлен» не принимается как готовая реализация. Допустим spike, но acceptance требует реального background test, process pressure и возврата Activity.

### Background microphone

На Android 14+ microphone foreground service требует:

- `RECORD_AUDIO` runtime permission;
- `FOREGROUND_SERVICE`;
- `FOREGROUND_SERVICE_MICROPHONE`;
- `android:foregroundServiceType="microphone"`;
- запуск service, пока Vass Activity видима и while-in-use permission активен.

Поэтому режим разговора подготавливается **до** ухода приложения в background. Overlay не должен пытаться холодно стартовать microphone service из невидимого process. Если service/recorder был остановлен системой, tap на overlay открывает полноэкранный Vass для безопасного восстановления.

На Android 15 наличие `SYSTEM_ALERT_WINDOW` само по себе недостаточно для произвольного background-start: exemption действует, только когда overlay реально видим. Это дополнительная страховка, а не основной lifecycle design.

## Native Architecture

### Local Expo Module

Функция реализуется local Expo Module, например:

```text
mobile/modules/vass-overlay/
  android/src/main/java/com/vass/overlay/
    VassOverlayModule.kt
    VassOverlayService.kt
    OverlayAvatarView.kt
    OverlayNotification.kt
    OverlayPositionStore.kt
    ScreenCapturePermissionActivity.kt
    VassMediaProjectionService.kt
  app.plugin.js
  expo-module.config.json
  src/index.ts
```

Прямые ручные изменения сгенерированной `mobile/android/` не являются источником истины. Permissions, services, activities и manifest properties добавляются config plugin, чтобы `expo prebuild --clean` воспроизводил проект.

### JS/Native Contract

Предлагаемый TypeScript facade:

```ts
export type OverlayState =
  | 'idle'
  | 'recording'
  | 'thinking'
  | 'speaking'
  | 'paused'
  | 'error';

export interface OverlaySnapshot {
  state: OverlayState;
  avatarId: 'olga' | 'male';
  enabled: boolean;
}

export interface VassOverlayApi {
  canDrawOverlays(): Promise<boolean>;
  requestOverlayPermission(): Promise<boolean>;
  start(snapshot: OverlaySnapshot): Promise<void>;
  update(snapshot: OverlaySnapshot): void;
  stop(): Promise<void>;
  addListener(listener: (event: OverlayEvent) => void): () => void;
}

export type OverlayEvent =
  | { type: 'controlPress' }
  | { type: 'pauseToggle' }
  | { type: 'openApp' }
  | { type: 'stopRequested' };
```

State updates должны быть idempotent. Service хранит последний snapshot, чтобы пережить recreation Android component, но разговорное содержимое не записывается в обычные preferences.

### Foreground Service Types

Рекомендуется разделить ответственности:

1. `VassOverlayService`: окно и persistent notification; на API 34+ тип `specialUse` с понятным `PROPERTY_SPECIAL_USE_FGS_SUBTYPE` для floating voice-assistant control. Этот use case подлежит review в Google Play.
2. Conversation/audio service: `microphone` только когда микрофон реально используется.
3. `VassMediaProjectionService`: `mediaProjection` только на время consented screen capture.

Не объявлять один постоянно работающий service сразу со всеми типами. Android и Play должны видеть фактическое назначение и время использования каждого sensitive capability.

## External Intents

### YouTube

Intent-роутер Фазы 5 определяет `youtube_play` или `youtube_search`. Overlay только инициирует/отображает разговор, а открытие выполняется общим Android intent adapter:

- конкретное видео: `Intent.ACTION_VIEW` с `https://www.youtube.com/watch?v=...`;
- поиск: `Intent.ACTION_VIEW` с `https://www.youtube.com/results?search_query=...`;
- если YouTube установлен, universal/web URL обычно открывается в нем;
- иначе используется browser;
- если intent не разрешается, Vass озвучивает ошибку и остается активным.

Не привязываться к undocumented YouTube package API и не пытаться управлять интерфейсом YouTube через Accessibility Service.

### Возврат в полный экран

Три поддерживаемых пути:

1. double tap по overlay;
2. notification action `Открыть`;
3. распознанный intent `open_vass` для фраз «вернись в Vass», «откройся полностью».

Открытие использует explicit Intent на `MainActivity` с существующим task, без создания второго экземпляра Activity. После возврата overlay может остаться видимым или скрыться согласно настройке `Скрывать в Vass`; default — скрывать, пока Activity foreground.

## Screen Analysis

### Зависимость

Overlay не создает отдельный image API. Он использует attachment model и multimodal send из [спецификации visual capture](./2026-07-10-visual-capture-and-image-tasks-design.md): приватный upload, ownership, image attachment и Gemini multimodal turn.

До закрытия attachment security из аудита проекта screen analysis через overlay не реализуется.

### Пользовательский сценарий

```text
Пользователь: «Объясни, что сейчас на экране»
  → intent `screen_analyze`
  → Vass озвучивает короткое подтверждение
  → открывается системный MediaProjection consent
  → пользователь выбирает весь экран или одно приложение (Android 14+)
  → overlay временно скрывается
  → захватывается один кадр
  → MediaProjection немедленно останавливается
  → кадр показывается в notification/fullscreen preview при необходимости
  → image + исходная голосовая просьба отправляются как один multimodal turn
  → ответ озвучивается; overlay возвращается
```

### Правила захвата

- Consent запрашивается для каждой новой MediaProjection session.
- Один token используется один раз для одного `createVirtualDisplay()`.
- Первая версия делает **один кадр**, а не stream/video.
- Перед кадром overlay получает `visibility=gone`; capture ждет минимум два frame callbacks, чтобы кнопка не попала в изображение.
- После получения первого валидного `Image` virtual display, image reader и projection освобождаются в `finally`.
- Локальный screenshot хранится в cache, удаляется после успешного upload или отмены.
- На сервер передается исходный user prompt; OCR выполняется только если этого требует задача.
- `FLAG_SECURE`/DRM может дать черный кадр. Vass объясняет ограничение и предлагает обычный screenshot/share flow.
- При lock screen, отзыве consent или `MediaProjection.Callback.onStop()` захват прекращается без retry loop.

### Background Activity Restriction

Для системного consent нужна Activity. Если Android разрешает открыть прозрачную `ScreenCapturePermissionActivity` из текущего visible-overlay/user-request flow, используем ее. Если OEM/background restrictions блокируют запуск, foreground notification показывает action **«Разрешить разбор экрана»**, и пользовательским tap открывает consent flow.

Голосовая фраза не считается разрешением на скрытый screen capture: системное подтверждение не обходится.

## Permissions And Manifest

Config plugin добавляет только необходимые declarations:

```xml
<uses-permission android:name="android.permission.SYSTEM_ALERT_WINDOW" />
<uses-permission android:name="android.permission.FOREGROUND_SERVICE" />
<uses-permission android:name="android.permission.FOREGROUND_SERVICE_SPECIAL_USE" />
<uses-permission android:name="android.permission.FOREGROUND_SERVICE_MICROPHONE" />
<uses-permission android:name="android.permission.FOREGROUND_SERVICE_MEDIA_PROJECTION" />
<uses-permission android:name="android.permission.RECORD_AUDIO" />
```

Services/permission Activity обязательно `android:exported="false"`. PendingIntent получает explicit component и `FLAG_IMMUTABLE`, кроме случаев, где Android API требует mutable PendingIntent.

Overlay window:

- `TYPE_APPLICATION_OVERLAY`;
- `FLAG_NOT_FOCUSABLE` по умолчанию;
- не перехватывает touch вне собственных bounds;
- не рисуется поверх lock screen;
- не маскирует системные permission/security dialogs.

## Privacy And Store Compliance

Перед grant overlay permission и первым screen capture нужен prominent disclosure на русском языке:

- что именно видно поверх приложений;
- когда микрофон активен;
- что screenshot отправляется на сервер/Gemini только после запроса;
- как немедленно остановить режим;
- где удалить сохраненные изображения/историю.

Требования:

- постоянное notification во время service;
- Android microphone/privacy indicator остается системным;
- никакого screen capture telemetry или thumbnail в remote logs;
- логировать только state transitions, размеры и error codes;
- обновить privacy policy и Data Safety declaration до store release;
- `specialUse` foreground service subtype описать конкретно для Play review;
- функция должна полностью работать при sideload, но store publication считается отдельным acceptance gate.

## Failure And Recovery

| Ситуация | Поведение |
|---|---|
| Overlay permission отозван | service убирает view, notification предлагает открыть настройки |
| Process/service убит | не рестартовать микрофон скрыто; при следующем открытии показать понятный status |
| Auth token истек | остановить listening, показать error halo/notification, открыть login по tap |
| Нет сети | сохранить conversation state без screenshot upload; предложить повтор после восстановления |
| Screen consent отменен | вернуться в overlay без error banner и без upload |
| Черный/пустой capture | объяснить защищенный экран, предложить screenshot + share |
| External intent не найден | озвучить, что приложение/ссылка не открылись |
| Rotation/display change | пересчитать bounds и восстановить edge position |
| MainActivity foreground | скрыть overlay view, service может продолжить runtime/notification |
| MainActivity background | показать overlay только если setting включен и permission остается выданным |

## Implementation Stages Inside Phase 7

### 7.1 Native Overlay Foundation

- local Expo Module + config plugin;
- permission onboarding;
- native avatar view, drag/snap, saved position;
- special-use foreground service + notification;
- start/stop из настроек;
- без фонового voice control на этом подэтапе.

### 7.2 Shared Conversation Runtime

- отделить conversation lifecycle от `HomeScreen`;
- связать state с overlay;
- short tap, long press, interruption и pause/resume;
- надежное продолжение active turn при background/foreground transition;
- real-device soak test минимум 30 минут.

### 7.3 External Actions And Fullscreen Return

- intent `open_vass`;
- explicit MainActivity restore;
- YouTube search/watch URLs через общий intent adapter;
- notification actions;
- ошибки отсутствующего handler.

### 7.4 One-shot Screen Analysis

- MediaProjection consent Activity/service;
- one-frame capture без overlay в кадре;
- visual attachment upload + multimodal send;
- cleanup и protected-screen fallback.

### 7.5 Hardening And Distribution Gate

- Android 8/10/12/14/15+ matrix;
- Samsung/Xiaomi/Pixel physical tests;
- permission revoke, battery saver, process pressure, rotation, lock screen;
- privacy disclosure/Data Safety;
- Google Play foreground-service/permission review либо документированное решение о managed sideload.

## Acceptance Criteria

1. Пользователь включает overlay только после собственного объясняющего экрана и system grant.
2. Overlay виден поверх браузера и YouTube, свободно двигается и не блокирует touch вне круга.
3. Выбранный avatar и conversation state синхронизируются с полноэкранным Vass.
4. Tap/long press/double tap выполняют ровно одно ожидаемое действие.
5. Активный разговор не дублируется и не теряется при Home → overlay → Home.
6. Foreground notification всегда присутствует и позволяет немедленно остановить режим.
7. Команда YouTube открывает результат через поддерживаемый Intent без Accessibility automation.
8. Команда возврата открывает существующий MainActivity task, не второй экземпляр.
9. Screen analysis всегда показывает системный consent для новой session и захватывает ровно один кадр.
10. Overlay не попадает в screenshot; cache-файл удаляется после отправки/отмены.
11. Защищенный экран приводит к понятному fallback, а не к выдуманному AI-ответу.
12. Permission revoke, process death, network loss и истекший JWT не оставляют невидимый микрофон/service.
13. `npx tsc --noEmit`, Android native build и существующие backend tests проходят.
14. Физический тест пройден минимум на одном Pixel/чистом Android и одном устройстве с агрессивным OEM power management.

## Source References

- Android overlay window types: https://developer.android.com/reference/android/view/WindowManager.LayoutParams
- Android foreground-service types: https://developer.android.com/develop/background-work/services/fgs/service-types
- Android foreground-service restrictions: https://developer.android.com/develop/background-work/services/fgs/restrictions-bg-start
- Android 15 foreground-service changes: https://developer.android.com/develop/background-work/services/fgs/changes
- Android MediaProjection: https://developer.android.com/media/grow/media-projection
- Android common intents: https://developer.android.com/guide/components/intents-common.html
- Expo custom native code: https://docs.expo.dev/workflow/customizing/
- Google Play prominent disclosure and consent: https://support.google.com/googleplay/android-developer/answer/11150561
