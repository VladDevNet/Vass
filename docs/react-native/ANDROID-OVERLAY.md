# Android overlay — фаза 7.1

Статус на 2026-07-12: нативная основа реализована и собрана в release APK.
Следующий подэтап — 7.2, общий `ConversationRuntime` и реальные voice-жесты.

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
- Android 8+; на Android 7 приложение работает без overlay.

Screen capture, YouTube/intents и background voice в этот инкремент не входят.
Окно не читает экран и не запускает отдельную запись микрофона.

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
- свернуть Vass поверх браузера и YouTube: круг виден, touch вне круга проходит;
- перетащить к обоим краям, повернуть экран, вернуть portrait и проверить позиции;
- double tap открывает существующий Vass, без второго task;
- «Остановить» из notification сразу убирает окно и сам notification;
- отозвать overlay permission во время работы: окно исчезает, приложение не падает;
- выгрузить process: микрофон/overlay не должны скрыто рестартовать.

Физический smoke не заменяет матрицу 7.5. Для принятия 7.1 достаточно одного
реального Android 14/15 устройства; Pixel и aggressive-OEM матрица остаётся
финальным hardening gate.
