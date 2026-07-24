#!/usr/bin/env bash
set -euo pipefail

# This script must run from a clone stored in the Linux filesystem, for example
# /home/vlad/vass. Building from /mnt/<drive> makes Metro, Gradle and CMake
# cross the Windows/WSL filesystem boundary for thousands of small operations.
project_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
if [[ "$project_root" == /mnt/* ]]; then
  echo "Refusing Android release build from $project_root." >&2
  echo "Use a WSL-native clone such as /home/vlad/vass." >&2
  exit 2
fi

export NVM_DIR="${NVM_DIR:-$HOME/.nvm}"
if [[ -s "$NVM_DIR/nvm.sh" ]]; then
  # shellcheck source=/dev/null
  . "$NVM_DIR/nvm.sh"
  nvm use --silent default
fi

if [[ "$(node --version)" != v22.* ]]; then
  echo "Node 22 is required; found $(node --version)." >&2
  exit 3
fi

export ANDROID_HOME="${ANDROID_HOME:-$HOME/Android/sdk}"
export ANDROID_SDK_ROOT="$ANDROID_HOME"
export JAVA_HOME="${JAVA_HOME:-/usr/lib/jvm/java-17-openjdk-amd64}"
export PATH="$JAVA_HOME/bin:$ANDROID_HOME/platform-tools:$ANDROID_HOME/cmdline-tools/latest/bin:$PATH"

mobile_dir="$project_root/mobile"
cd "$mobile_dir"

lock_stamp=".vass-build-package-lock.sha256"
lock_hash="$(sha256sum package-lock.json | awk '{print $1}')"
if [[ ! -d node_modules ]] || [[ ! -f "$lock_stamp" ]] || [[ "$(<"$lock_stamp")" != "$lock_hash" ]]; then
  npm ci --no-audit --no-fund
  printf '%s' "$lock_hash" > "$lock_stamp"
fi
npm exec patch-package --error-on-fail

if [[ "${VASS_ANDROID_CLEAN_PREBUILD:-0}" == "1" ]]; then
  npx expo prebuild --platform android --clean --no-install
else
  npx expo prebuild --platform android --no-install
fi

if ! grep -q '^org.gradle.caching=true$' android/gradle.properties; then
  printf '\n# Vass local release-build cache\norg.gradle.caching=true\n' >> android/gradle.properties
fi
printf 'sdk.dir=%s\n' "$ANDROID_HOME" > android/local.properties
(
  cd android
  ./gradlew :app:assembleRelease -PreactNativeArchitectures=arm64-v8a --build-cache --console=plain
)

apk="android/app/build/outputs/apk/release/app-release.apk"
test -f "$apk"
"$ANDROID_HOME/build-tools/36.0.0/aapt" dump badging "$apk" | head -n 1
sha256sum "$apk"
