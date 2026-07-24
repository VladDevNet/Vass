# Android beta updates

Vass beta builds are installed outside Google Play. On every Android foreground transition, the client asks the anonymous `GET /api/v1/app-updates/android?currentVersionCode=<n>` endpoint for a release manifest.

The server publishes an update only when all of the following VPS `.env` values are valid:

- `MOBILE_UPDATE_ANDROID_LATEST_VERSION`
- `MOBILE_UPDATE_ANDROID_LATEST_VERSION_CODE`
- `MOBILE_UPDATE_ANDROID_DOWNLOAD_URL` (HTTPS)

`MOBILE_UPDATE_ANDROID_MIN_SUPPORTED_VERSION_CODE` controls compatibility:

- `0` or a value at or below the installed `versionCode`: update is optional and can be dismissed for that target version.
- Above the installed `versionCode`: update is mandatory; the modal cannot be dismissed.

APK files live on the VPS in `releases/` and nginx exposes them read-only at `/downloads/`. The mobile client opens the HTTPS APK URL; Android's installer verifies the application id and signing certificate and requires the person's confirmation. The client never silently installs an APK.

## Publishing a beta

Build and publish in one command from the WSL-native clone:

```bash
cd ~/vass
./scripts/build-android-release-wsl.sh --publish --notes "Short description for testers."
```

The script builds an ARM64 APK, uploads it to `/root/vass/releases/`, verifies
its SHA-256 on the VPS, updates the five `MOBILE_UPDATE_ANDROID_*` manifest
fields atomically, recreates only `api`, and verifies the endpoint for both
the preceding and new `versionCode`. It refuses to overwrite a published
release with an equal or lower `versionCode`.

Use `VASS_ANDROID_PUBLISH=1` instead of `--publish` for automation. A build
without either option remains local and never changes the production manifest.

Do not raise `MOBILE_UPDATE_ANDROID_MIN_SUPPORTED_VERSION_CODE` until the release is known to be installable from the production download URL.
