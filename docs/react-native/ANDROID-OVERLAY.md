# Android overlay — фазы 7.1–7.3

Статус на 2026-07-12: нативная основа 7.1, voice-интеграция 7.2 и external
actions 7.3 реализованы. Физический background/foreground smoke,
YouTube/open_vass сценарии и 30-минутная сессия остаются acceptance gate.

## Что реализовано

- local Expo Module `mobile/modules/vass-overlay`, автоматически связанный Expo Autolinking;
- config plugin как источник истины для permissions, `specialUse` service и PNG resources;
- явный disclosure в настройках до перехода в системный grant flow;
- `TYPE_APPLICATION_OVERLAY` размером 84dp, выбранный avatar и halo состояния;
- drag, snap к ближайшему краю, safe insets, отдельные позиции portrait/landscape;
- foreground service `START_NOT_STICKY`: после process death скрыто не перезапускается;
- overlay скрывается при foreground Activity и появляется только после сворачивания Vass;
- постоянное notification с открытием Vass и немедленной остановкой режима;
- double tap возвращает существующий application task через launch intent;
- единый `ConversationRuntime`: fullscreen и overlay используют одну session,
  один voice loop и одну state machine;
- short tap завершает текущую фразу или перебивает озвучку, long press ставит
  разговор на паузу/возобновляет его, notification повторяет pause/continue;
- состояние и halo синхронизируются из реального voice runtime;
- background recording включается только после явного запуска overlay из
  видимой Activity и обслуживается штатным `AudioRecordingService` Expo Audio;
- pause и stop дополнительно останавливают recording service на нативной
  стороне, даже если JS runtime временно недоступен;
- после process death сервисы не перезапускаются скрыто (`START_NOT_STICKY`);
- серверный intent-router передаёт `open_vass`, `youtube_search` и
  `youtube_watch` отдельным типизированным SSE event;
- YouTube открывается universal search/watch URL через системный handler,
  без автокликов, undocumented package API и `AccessibilityService`;
- `open_vass` поднимает существующий `singleTask` MainActivity через explicit
  launch intent, не создавая второй conversation runtime;
- device action дедуплицируется внутри одного voice turn; ошибка handler
  озвучивается локально, после чего listening безопасно восстанавливается;
- Android 8+; на Android 7 приложение работает без overlay.

Screen capture пока не входит. Overlay не читает экран. При включённом режиме
тот же разговор продолжает слушать в фоне, что явно описано в disclosure и
отображается системным уведомлением Android.

## Архитектура background voice

Ответственность разделена между двумя foreground services:

- `VassOverlayService` (`specialUse`) владеет окном, жестами и notification
  управления;
- `expo.modules.audio.service.AudioRecordingService` (`microphone`) владеет
  фоновой записью. Он добавляется config plugin Expo Audio через
  `enableBackgroundRecording: true`.

Приложение включает `allowsBackgroundRecording` только при запуске overlay и
выключает при остановке режима. Возобновление после pause выводит существующий
task Vass на передний план: Android не гарантирует безопасный холодный старт
microphone FGS из произвольного background-контекста. Нативный emergency stop
привязан к внутреннему service/action contract Expo SDK 57; при обновлении Expo
его нужно перепроверить по исходникам SDK.

## Сборка

Android собирается только на native Linux filesystem в WSL, не из `/mnt/d`.
Полная инструкция и toolchain: [BUILD-WSL.md](BUILD-WSL.md).

Проверенный release command:

```bash
cd ~/vass/mobile/android
NODE_ENV=production \
EXPO_PUBLIC_API_URL=https://vass.it-consult.services \
./gradlew :app:assembleRelease \
  -PreactNativeArchitectures=arm64-v8a \
  --no-daemon --console=plain
```

Текущий локальный артефакт: `mobile-builds/vass-overlay.apk` (gitignored).

## Physical-device smoke

- установить release APK поверх текущей версии и войти в аккаунт;
- открыть Настройки → «Поверх других приложений»;
- проверить disclosure, notification permission и системный overlay grant;
- при sideload-блокировке открыть карточку Vass → меню `⋮` → «Разрешить
  ограниченные настройки», затем повторить overlay grant;
- свернуть Vass поверх браузера и YouTube: круг виден, touch вне круга проходит;
- перетащить к обоим краям, повернуть экран, вернуть portrait и проверить позиции;
- double tap открывает существующий Vass, без второго task;
- short tap во время записи отправляет фразу, во время речи перебивает ответ;
- сказать «открой в YouTube песни Высоцкого»: открывается приложение YouTube
  либо browser search, overlay остаётся видимым и разговор не дублируется;
- передать в речи конкретную YouTube-ссылку: открывается watch URL; выдуманный
  или некорректный video ID не принимается;
- сказать «вернись в Vass»: открывается существующий fullscreen task;
- повторить YouTube-команду без установленного handler и проверить локально
  озвученную ошибку с последующим восстановлением listening;
- long press останавливает микрофон и ставит runtime в `paused`; повторное
  нажатие возвращает Vass и продолжает разговор;
- проверить те же pause/continue через постоянное notification;
- свернуть/развернуть Vass несколько раз во время `recording`, `thinking` и
  `speaking`: session и voice loop не дублируются;
- провести 30-минутный разговор в фоне и проверить отсутствие зависшего
  микрофона после pause/stop;
- «Остановить» из notification сразу убирает окно и сам notification;
- отозвать overlay permission во время работы: окно исчезает, приложение не падает;
- выгрузить process: микрофон/overlay не должны скрыто рестартовать.

Физический smoke не заменяет матрицу 7.5. Для принятия 7.1 достаточно одного
реального Android 14/15 устройства; Pixel и aggressive-OEM матрица остаётся
финальным hardening gate.
