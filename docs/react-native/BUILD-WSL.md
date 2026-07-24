# Локальная сборка через WSL + установка на физическое устройство

> **Проверено 2026-07-06/07**: собран реальный APK (`com.vass.assistant`,
> targetSdk 36/minSdk 24) на тулчейне, уже готовом из работы над eCMRHub.
> ⚠ Первая раздача была debug-сборкой — на реальном телефоне без Metro
> получили красный экран `Unable to load script` (debug — это dev-client,
> ему нужен живой Metro, JS-бандл не встроен). Пересобрали `assembleRelease`
> — тот содержит бандл внутри, ставится и работает автономно. **Для любой
> раздачи на тест — только release**, см. раздел 4.

Применимо с начала Фазы 1 (когда появится `mobile/`). Адаптировано из опыта
проекта eCMRHub (`D:\Repos\eCMRHub\src\mobile\docs\MOBILE_BUILD_GOTCHAS.md` и
`WSL_BUILD_RECIPE.md`) — та же машина, тот же класс проблем (Windows + WSL2 +
Gradle/CMake/NDK).

**Важное отличие от eCMRHub:** там тестируют на эмуляторе через Maestro.
Здесь, пока нет мобильной CI/автотестов, — только Android, только физическое
устройство, только ручная установка через `adb`. Это на самом деле **проще**
эмуляторной истории: не нужен `google_apis` AVD, не нужен общий adb-демон
Windows↔WSL, не нужен `10.0.2.2`/`0.0.0.0` для локального API — телефон сам
ходит по Wi-Fi/мобильной сети напрямую на `https://vass.it-consult.services`,
как обычный клиент. iOS-сборка на Windows невозможна в принципе (нужен
macOS/Xcode) — не рассматривается, пока не появится Mac.

## Кардинальное правило — собирать только на нативной Linux FS

**Никогда** не запускай `gradlew`, `npm install` (для `mobile/`) или любой
`assemble*`/`bundle*` таск из `/mnt/d/...`. WSL пробрасывает `D:\` через
9P-демон — каждая файловая операция идёт через Windows ACL и антивирус.
CMake/ninja/clang делают тысячи мелких операций на сборку — под 9P это либо
виснет на `:expo-modules-core:configureCMakeRelWithDebInfo` на 30+ минут,
либо падает случайными `EACCES`/`ETIMEDOUT`, либо (хуже всего) молча собирает
битый APK, падающий на запуске.

Правило: клонируй и собирай из `~/vass` (native ext4 внутри WSL). Правки кода
делай как удобно (Windows IDE на `D:\Repos\Vass`), синхронизируй в WSL через
`git pull` перед сборкой.

## 1. Разовая настройка тулчейна (один раз на дистрибутив WSL)

```bash
# Node 22 через nvm
curl -o- https://raw.githubusercontent.com/nvm-sh/nvm/v0.40.1/install.sh | bash
. ~/.nvm/nvm.sh
nvm install 22 && nvm use 22

# JDK 17 (хватает для AGP 8.x/Kotlin, дешевле чем JDK 21 с Windows-стороны)
sudo apt update && sudo apt install -y openjdk-17-jdk

# Android SDK + platform-tools + cmdline-tools
mkdir -p ~/Android/sdk/cmdline-tools
cd ~/Android/sdk/cmdline-tools
curl -O https://dl.google.com/android/repository/commandlinetools-linux-11076708_latest.zip
unzip commandlinetools-linux-*.zip && mv cmdline-tools latest && rm commandlinetools-linux-*.zip

cat >> ~/.bashrc <<'EOF'
export ANDROID_HOME=$HOME/Android/sdk
export ANDROID_SDK_ROOT=$ANDROID_HOME
export PATH=$ANDROID_HOME/platform-tools:$ANDROID_HOME/cmdline-tools/latest/bin:$PATH
export JAVA_HOME=/usr/lib/jvm/java-17-openjdk-amd64
export PATH=$JAVA_HOME/bin:$PATH
EOF
. ~/.bashrc

sdkmanager "platform-tools" "platforms;android-34" "build-tools;34.0.0" \
           "ndk;27.1.12297006" "cmake;3.22.1"
```

Эмулятор/AVD не нужен — тестируем на физическом устройстве.

## 2. Синхронизация репозитория (перед каждой сборкой)

```bash
cd ~/vass   # git clone git@github.com:VladDevNet/Vass.git ~/vass — один раз
git fetch origin
git stash push -u -m "stale-local-$(date +%Y-%m-%d)-pre-build"   # no-op если чисто
git checkout main && git pull --ff-only
```

На практике `npm install` внутри `mobile/` сам переписывает
`package-lock.json` (иногда и `package.json`, если что-то донормализовалось)
даже без изменений в исходниках — следующий `git checkout`/`git pull`
откажется с «local changes would be overwritten», хотя ты ничего руками не
трогал. `git stash` выше это и ловит; если забыл его на прошлом прогоне —
`git checkout -- mobile/package.json mobile/package-lock.json` перед
`git pull` безопасно отбрасывает именно этот дрейф (сам файл в репозитории
уже актуален).

## 3. Переменные окружения сборки

```bash
export PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin
export PATH=$HOME/.nvm/versions/node/v22.18.0/bin:$PATH   # версия под nvm ls
export ANDROID_HOME=$HOME/Android/sdk
export ANDROID_SDK_ROOT=$ANDROID_HOME
export PATH=$ANDROID_HOME/platform-tools:$ANDROID_HOME/cmdline-tools/latest/bin:$PATH
export JAVA_HOME=/usr/lib/jvm/java-17-openjdk-amd64
export PATH=$JAVA_HOME/bin:$PATH
unset _JAVA_OPTIONS   # если когда-то будет установлена — ломает Gradle
```

Установка зависимостей (после клона или когда `git pull` принёс lockfile):

```bash
cd ~/vass/mobile
[ "$(ls node_modules 2>/dev/null | wc -l)" -lt 100 ] && npm install --no-audit --no-fund
```

### Генерация native-проекта перед release

`mobile/android` намеренно не хранится в git: Expo генерирует его из
`app.json` и config plugins. Перед каждым release его нужно синхронизировать
с текущим commit, иначе APK может получить старые `versionCode` и
`versionName`, даже если `app.json` уже обновлён. Скрипт различает два случая:
для обычного релиза он обновляет версию из `app.json` в сгенерированном Gradle
проекте, не удаляя Android outputs; если менялись native inputs, запускает
чистую генерацию автоматически.

```bash
cd ~/vass
./scripts/build-android-release-wsl.sh
```

Чтобы после успешной сборки сразу опубликовать APK для встроенного обновления:

```bash
cd ~/vass
./scripts/build-android-release-wsl.sh --publish --notes "Short description for testers."
```

Этот вариант использует SSH из WSL, загружает один готовый APK на VPS и
обновляет manifest обновления. Исходники, Gradle, Metro и `node_modules`
остаются только в Linux filesystem; `/mnt/d` не используется для сборки.

Скрипт проверяет Node 22, запрещает `/mnt/*`, запускает `npm ci` только при
смене lockfile, использует Gradle build cache и печатает итоговую
версию/хэш APK. Fingerprint native inputs включает `app.json` без номера
версии, lockfile, config plugin, нативный overlay и нативные avatar-ресурсы.
Не редактируй `android/app/build.gradle` вручную как источник версии:
скрипт синхронизирует её из `mobile/app.json`.

Полный clean нужен только после добавления/обновления native-зависимости,
изменения config plugin либо при восстановлении повреждённого native-проекта:

```bash
cd ~/vass
VASS_ANDROID_CLEAN_PREBUILD=1 ./scripts/build-android-release-wsl.sh
```

Это намеренно более медленный путь: он пересоздаёт `android/` и лишает Gradle
инкрементальных outputs. Обычно он не нужен, потому что fingerprint ловит
изменения native inputs. Metro cache не сбрасывается в каждом release; его
очищают только как отдельный шаг диагностики, если действительно виден
устаревший bundle.

## 4. Debug vs release — какой APK ставить руками

⚠ **Debug-APK — это dev-client, ему нужен живой Metro.** Он НЕ содержит JS
внутри себя — при запуске он пытается достучаться до `localhost:8081` (по
USB, через `adb reverse tcp:8081 tcp:8081`) или до Metro на той же Wi-Fi
сети. Установленный сам по себе, без запущенного `npx expo start` рядом, он
падает красным экраном `Unable to load script`. Это ровно то, что случилось
при первой раздаче APK на реальном тесте — пришлось пересобирать.

Для «поставь и просто пользуйся» (передать APK кому-то для теста без
подключения к твоему Metro) — **всегда `assembleRelease`**, не `assembleDebug`.
Debug имеет смысл только когда сам разработчик держит `npx expo start`
запущенным рядом с телефоном на USB/Wi-Fi ради быстрой перезагрузки JS без
пересборки — то есть практически никогда в этом проекте, пока нет второго
человека, физически сидящего рядом с разработчиком.

```bash
cd ~/vass/mobile/android
EXPO_PUBLIC_API_URL="https://vass.it-consult.services" \
  ./gradlew :app:assembleRelease -PreactNativeArchitectures=arm64-v8a --no-daemon --console=plain
# → app/build/outputs/apk/release/app-release.apk (JS-бандл уже внутри)
```

`arm64-v8a`, а не `x86_64` — реальные телефоны почти всегда ARM (эмулятора у
нас нет). `EXPO_PUBLIC_API_URL` указывает сразу на прод-бэкенд
(`vass.it-consult.services`, HTTPS через Caddy) — никакой локальной API,
никакого `10.0.2.2`, никакого cleartext-исключения: телефон общается с VPS
так же, как веб-версия. Release по умолчанию подписывается debug-ключом
(достаточно для ручной раздачи, не для стора) — Expo-шаблон настраивает это
сам, отдельный шаг с ключами не нужен.

Если меняли `EXPO_PUBLIC_API_URL` или другую `EXPO_PUBLIC_*` — она запечена в
`index.android.bundle` на этапе сборки бандла; смена без очистки бандла
ничего не даст:

```bash
find android/app/build -name "index.android.bundle" -delete
rm -rf android/app/build/generated/assets/createBundleReleaseJsAndAssets
```

Если добавляли нативный модуль (`npm install <lib-с-native-кодом>`) — сначала
codegen, иначе `:app:configureCMake*` падает с `AccessDeniedException`:

```bash
./gradlew :react-native-webview:generateCodegenArtifactsFromSchema --no-daemon
```

## 5. Установка на физическое устройство

Устройство физически подключено к Windows по USB — WSL2 его не видит
напрямую (нет USB passthrough без отдельной настройки usbipd-win). Проще:
скопировать готовый APK на Windows-видимый путь и поставить `adb.exe` с
Windows-стороны — единственная операция с одним файлом, не сборка, так что
кардинальное правило про `/mnt/d` тут не нарушается.

```bash
# WSL: скопировать готовый APK туда, откуда достанет Windows (RELEASE, не debug —
# имя файла тоже не должно говорить "debug", это уже путало один раз)
cp ~/vass/mobile/android/app/build/outputs/apk/release/app-release.apk \
   /mnt/d/Repos/Vass/mobile-builds/vass.apk
```

```powershell
# Windows: включить USB-отладку на телефоне, разрешить компьютер при первом подключении
adb devices                                  # должен показать устройство
adb install -r D:\Repos\Vass\mobile-builds\vass-debug.apk
adb logcat *:S ReactNativeJS:V ReactNative:V  # логи JS-рантайма при отладке
```

(Android SDK platform-tools для Windows — тот же установщик, что для любой
Android-разработки; `adb.exe` можно взять из Android Studio или
`platform-tools` zip с developer.android.com, независимо от Linux-копии в WSL.)

Беспроводная альтернатива (без кабеля, если оба на одной Wi-Fi сети):
`adb pair <ip>:<port>` (Android 11+, "Беспроводная отладка" в настройках
разработчика), дальше `adb connect` — та же команда `adb install` работает.

## 6. Известные грабли (актуальные для этого пути)

| Симптом | Причина | Решение |
|---|---|---|
| Сборка виснет на `configureCMakeRelWithDebInfo` 30+ мин | Сборка из `/mnt/d` вместо `~/vass` | Кардинальное правило выше |
| `ninja: error: manifest 'build.ninja' still dirty after 100 tries` | Протухшие `.cxx`-кэши + антивирус | `rm -rf android/app/.cxx node_modules/react-native-reanimated/android/build` (и другие native-модули), пересобрать с `--max-workers=2` |
| Пересобранный APK всё ещё стучится на старый URL | Закэшированный `index.android.bundle` | См. раздел 4 — удалить бандл перед пересборкой |
| `:app:configureCMake*` падает `AccessDeniedException` после установки нового модуля | Codegen для модуля ещё не прогонялся | Запустить `generateCodegenArtifactsFromSchema` для конкретного модуля |
| Красный экран `Unable to load script` / `Make sure you're running Metro` на установленном APK | Поставили **debug**-сборку — это dev-client без встроенного JS-бандла, ему нужен живой `expo start` рядом (USB или та же Wi-Fi) | Собрать и раздавать **`assembleRelease`**, не `assembleDebug` — см. раздел 4. Debug — только когда сам разработчик держит Metro запущенным рядом с телефоном |
| `assembleRelease` падает clang++ signal-11 (гипотетически, не воспроизведено на WSL) | На чистом Windows-Gradle компиляция arm64 упирается в лимит paging file — но WSL-сборки (этот документ) собирали `assembleRelease` без этой проблемы | Если всё же поймали — собрать debug для локальной проверки логики, но для раздачи на реальный тест всё равно нужен release (см. строку выше); альтернатива — EAS cloud |
| `adb devices` не видит телефон | USB-отладка не включена, либо не подтверждён компьютер на телефоне, либо не тот `adb.exe` в PATH | Проверить "Отладка по USB" в настройках разработчика, переподключить кабель, подтвердить диалог на телефоне |
| `wsl -d Ubuntu -- bash ~/script.sh` → `bash: C:Usersvkhol/script.sh: No such file or directory` | При вызове `wsl.exe` из **PowerShell** (не из самого WSL) `~` иногда разворачивается в Windows-профиль до того, как строка доходит до bash внутри WSL | Всегда используй абсолютный путь `/home/<user>/script.sh` вместо `~/script.sh`, когда запускаешь `wsl.exe` из PowerShell/Windows-стороны |
| Инлайн `wsl -d Ubuntu -- bash -c 'export PATH=...'` падает с `syntax error near unexpected token '('` | PowerShell наследует родительский Windows PATH со скобками (`Program Files (x86)`) в переменную окружения, которая протекает в инлайн-команду | Пиши команду в `.sh`-файл (через Write-инструмент по пути `\\wsl$\Ubuntu\home\...`) и запускай `wsl -d Ubuntu -- bash /home/user/script.sh` вместо длинной инлайн `-c '...'` строки |
| `TypeError: configs.toReversed is not a function` на `createBundleReleaseJsAndAssets` | `nvm` подключается только в интерактивном шелле (через `.bashrc`); `wsl -d Ubuntu -- bash script.sh` — неинтерактивный, PATH откатывается на системный `/usr/bin/node` (Node 18, без `Array.prototype.toReversed`, нужен Node 20+) вместо нужного nvm-default (Node 22) | В начале скрипта явно: `export NVM_DIR="$HOME/.nvm"; . "$NVM_DIR/nvm.sh"; nvm use default` — не полагаться на то, что `node` уже правильный в PATH |
| `wsl -d Ubuntu -- bash /home/user/script.sh` из **Git Bash** → `bash: C:/Program Files/Git/home/user/script.sh: No such file or directory` (exit 127) | Git Bash (MSYS2) сам конвертирует POSIX-похожие аргументы в Windows-пути перед вызовом нативного `wsl.exe` — переписывает уже-абсолютный `/home/user/...` в `C:/Program Files/Git/home/user/...`, ровно та же путаница, что и с `~`, но для Git Bash, а не PowerShell | Запускай `wsl.exe`-команды с абсолютным Linux-путём через **PowerShell**-инструмент, а не Git Bash — PowerShell не делает POSIX→Windows конвертацию аргументов. Альтернатива внутри Git Bash: `MSYS_NO_PATHCONV=1 wsl -d Ubuntu -- bash /home/user/script.sh` |

Полный список из 20 граблей (Maestro/emulator-специфичные тоже) — в
`D:\Repos\eCMRHub\src\mobile\docs\MOBILE_BUILD_GOTCHAS.md`, если понадобится
шире (например когда дойдём до эмулятора или CI).
