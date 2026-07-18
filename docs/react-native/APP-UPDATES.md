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

1. Build a signed APK with a higher Android `versionCode`.
2. Upload it as `/root/vass/releases/<filename>.apk`.
3. Calculate its SHA-256 and set the `MOBILE_UPDATE_ANDROID_*` variables in `/root/vass/.env`.
4. Recreate the API container so it reads the new manifest values.
5. Check the endpoint for both a current and an older `currentVersionCode` before notifying testers.

Do not raise `MOBILE_UPDATE_ANDROID_MIN_SUPPORTED_VERSION_CODE` until the release is known to be installable from the production download URL.
