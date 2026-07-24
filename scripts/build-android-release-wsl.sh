#!/usr/bin/env bash
set -euo pipefail

publish_release="${VASS_ANDROID_PUBLISH:-0}"
release_notes="${VASS_ANDROID_RELEASE_NOTES:-}"

usage() {
  cat <<'USAGE'
Usage: ./scripts/build-android-release-wsl.sh [--publish] [--notes "Release notes"]

Builds an ARM64 Android release from the Linux filesystem only.
--publish uploads the verified APK to the VPS and updates the in-app update manifest.
USAGE
}

while (($# > 0)); do
  case "$1" in
    --publish)
      publish_release=1
      ;;
    --notes)
      shift
      if (($# == 0)); then
        echo "--notes requires a value." >&2
        exit 64
      fi
      release_notes="$1"
      ;;
    --help|-h)
      usage
      exit 0
      ;;
    *)
      echo "Unknown option: $1" >&2
      usage >&2
      exit 64
      ;;
  esac
  shift
done

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

read_release_metadata() {
  node - <<'NODE'
const config = JSON.parse(require('fs').readFileSync('app.json', 'utf8')).expo;
if (!/^[0-9A-Za-z._-]+$/.test(config.version ?? '')) throw new Error('Invalid Expo version.');
if (!Number.isSafeInteger(config.android?.versionCode) || config.android.versionCode < 1) {
  throw new Error('Android versionCode must be a positive integer.');
}
process.stdout.write(`${config.version}\n${config.android.versionCode}\n`);
NODE
}

mapfile -t release_metadata < <(read_release_metadata)
release_version="${release_metadata[0]}"
release_version_code="${release_metadata[1]}"

lock_stamp=".vass-build-package-lock.sha256"
lock_hash="$(sha256sum package-lock.json | awk '{print $1}')"
if [[ ! -d node_modules ]] || [[ ! -f "$lock_stamp" ]] || [[ "$(<"$lock_stamp")" != "$lock_hash" ]]; then
  npm ci --no-audit --no-fund
  printf '%s' "$lock_hash" > "$lock_stamp"
fi
npm exec patch-package --error-on-fail

native_stamp=".vass-build-native-input.sha256"
native_hash="$(
  {
    node - <<'NODE'
const fs = require('fs');
const config = JSON.parse(fs.readFileSync('app.json', 'utf8'));
delete config.expo.version;
delete config.expo.android?.versionCode;
process.stdout.write(JSON.stringify(config));
NODE
    find package-lock.json modules assets/avatar assets/icon.png assets/android-icon-foreground.png assets/android-icon-background.png assets/android-icon-monochrome.png -type f -print0 |
      sort -z |
      xargs -0 sha256sum
  } | sha256sum | awk '{print $1}'
)"

if [[ "${VASS_ANDROID_CLEAN_PREBUILD:-0}" == "1" ]] || [[ ! -d android ]] || [[ ! -f "$native_stamp" ]] || [[ "$(<"$native_stamp")" != "$native_hash" ]]; then
  npx expo prebuild --platform android --clean --no-install
  printf '%s' "$native_hash" > "$native_stamp"
else
  node - <<'NODE'
const fs = require('fs');
const config = JSON.parse(fs.readFileSync('app.json', 'utf8')).expo;
const gradlePath = 'android/app/build.gradle';
let gradle = fs.readFileSync(gradlePath, 'utf8');
if (!/versionCode \d+/.test(gradle) || !/versionName "[^"]+"/.test(gradle)) {
  throw new Error(`Unable to find Android version fields in ${gradlePath}.`);
}
const next = gradle
  .replace(/versionCode \d+/, `versionCode ${config.android.versionCode}`)
  .replace(/versionName "[^"]+"/, `versionName "${config.version}"`);
if (next !== gradle) fs.writeFileSync(gradlePath, next);
NODE
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
"$ANDROID_HOME/build-tools/36.0.0/aapt" dump badging "$apk" | sed -n '1p'
apk_sha256="$(sha256sum "$apk" | awk '{print $1}')"
printf '%s  %s\n' "$apk_sha256" "$apk"

if [[ "$publish_release" != "1" ]]; then
  exit 0
fi

case "${VASS_DEPLOY_DIR:-}" in
  '') publish_dir='/root/vass' ;;
  /*) publish_dir="$VASS_DEPLOY_DIR" ;;
  *)
    echo "VASS_DEPLOY_DIR must be an absolute path." >&2
    exit 65
    ;;
esac
case "$publish_dir" in
  *[\'\"\$\`]* )
    echo "VASS_DEPLOY_DIR must not contain shell metacharacters." >&2
    exit 65
    ;;
esac

publish_host="${VASS_DEPLOY_HOST:-root@vass.it-consult.services}"
release_date="$(date -u +%Y%m%d)"
release_filename="vass-${release_version}-arm64-${release_date}.apk"
download_url="https://vass.it-consult.services/downloads/${release_filename}"
release_notes="${release_notes//$'\n'/ }"
release_notes="${release_notes//$'\r'/ }"
release_notes="${release_notes:-Vass ${release_version} beta update.}"
release_notes_b64="$(printf '%s' "$release_notes" | base64 -w 0)"

echo "==> Publishing ${release_filename} to ${publish_host}:${publish_dir}/releases/..."
scp -- "$apk" "${publish_host}:${publish_dir}/releases/${release_filename}"

ssh "$publish_host" bash -s -- "$publish_dir" "$release_filename" "$release_version" "$release_version_code" "$download_url" "$apk_sha256" "$release_notes_b64" <<'REMOTE'
set -euo pipefail

publish_dir="$1"
release_filename="$2"
release_version="$3"
release_version_code="$4"
download_url="$5"
expected_sha256="$6"
release_notes_b64="$7"

cd "$publish_dir"
actual_sha256="$(sha256sum "releases/${release_filename}" | awk '{print $1}')"
if [[ "$actual_sha256" != "$expected_sha256" ]]; then
  echo "Uploaded APK checksum mismatch." >&2
  exit 1
fi

published_code="$(sed -n 's/^MOBILE_UPDATE_ANDROID_LATEST_VERSION_CODE=//p' .env | tail -n 1)"
published_code="${published_code:-0}"
if ! [[ "$published_code" =~ ^[0-9]+$ ]] || (( release_version_code <= published_code )); then
  echo "Android versionCode ${release_version_code} must be higher than published ${published_code}." >&2
  exit 1
fi

release_notes="$(printf '%s' "$release_notes_b64" | base64 -d)"
tmp_env="$(mktemp .env.vass-release.XXXXXX)"
awk -v version="$release_version" -v version_code="$release_version_code" -v url="$download_url" -v sha256="$expected_sha256" -v notes="$release_notes" '
  BEGIN {
    values["MOBILE_UPDATE_ANDROID_LATEST_VERSION"] = version
    values["MOBILE_UPDATE_ANDROID_LATEST_VERSION_CODE"] = version_code
    values["MOBILE_UPDATE_ANDROID_DOWNLOAD_URL"] = url
    values["MOBILE_UPDATE_ANDROID_SHA256"] = sha256
    values["MOBILE_UPDATE_ANDROID_RELEASE_NOTES"] = notes
  }
  {
    matched = 0
    for (key in values) {
      if (index($0, key "=") == 1) {
        print key "=" values[key]
        seen[key] = 1
        matched = 1
        break
      }
    }
    if (!matched) print
  }
  END {
    for (key in values) if (!(key in seen)) print key "=" values[key]
  }' .env > "$tmp_env"
mv "$tmp_env" .env

docker compose up -d --force-recreate api
for _ in $(seq 1 30); do
  if docker compose exec -T api curl -fsS http://localhost:5000/api/health/ready >/dev/null; then
    break
  fi
  sleep 2
done
docker compose exec -T api curl -fsS "http://localhost:5000/api/v1/app-updates/android?currentVersionCode=$((release_version_code - 1))" | grep -F '"updateAvailable":true' >/dev/null
docker compose exec -T api curl -fsS "http://localhost:5000/api/v1/app-updates/android?currentVersionCode=${release_version_code}" | grep -F '"updateAvailable":false' >/dev/null
REMOTE

echo "==> Published: ${download_url}"
