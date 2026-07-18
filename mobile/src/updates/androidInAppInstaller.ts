import { Directory, File, Paths } from 'expo-file-system';
import { Platform } from 'react-native';
import { VassOverlay } from '../../modules/vass-overlay';

const UPDATE_CACHE_DIRECTORY = 'vass-updates';

function ensureAndroidNativeInstaller(): void {
  if (Platform.OS !== 'android' || !VassOverlay.isAvailable()) {
    throw new Error('В этой сборке недоступна установка Android-обновлений.');
  }
}

export async function downloadAndroidUpdate(
  downloadUrl: string,
  versionCode: number,
  onProgress: (progress: number | null) => void,
): Promise<string> {
  ensureAndroidNativeInstaller();
  if (!Number.isInteger(versionCode) || versionCode < 1) {
    throw new Error('Сервер передал некорректную версию обновления.');
  }

  // The Android FileProvider exposes only this narrow cache subdirectory.
  // Keeping the APK here also makes it disposable if an install is abandoned.
  const directory = new Directory(Paths.cache, UPDATE_CACHE_DIRECTORY);
  directory.create({ idempotent: true, intermediates: true });
  const destination = new File(directory, `vass-update-${versionCode}.apk`);

  let lastProgress = -1;
  const downloaded = await File.downloadFileAsync(downloadUrl, destination, {
    idempotent: true,
    onProgress: ({ bytesWritten, totalBytes }) => {
      if (totalBytes <= 0) {
        if (lastProgress !== -2) {
          lastProgress = -2;
          onProgress(null);
        }
        return;
      }

      const next = Math.min(1, Math.max(0, bytesWritten / totalBytes));
      const rounded = Math.floor(next * 100);
      if (rounded === lastProgress) return;
      lastProgress = rounded;
      onProgress(next);
    },
  });

  if (!downloaded.exists || !downloaded.size) {
    throw new Error('Файл обновления загрузился не полностью.');
  }

  onProgress(1);
  return downloaded.uri;
}

export async function canRequestAndroidPackageInstalls(): Promise<boolean> {
  ensureAndroidNativeInstaller();
  return VassOverlay.canRequestPackageInstalls();
}

export async function requestAndroidPackageInstallPermission(): Promise<void> {
  ensureAndroidNativeInstaller();
  await VassOverlay.requestPackageInstallPermission();
}

export async function launchAndroidPackageInstaller(
  apkUri: string,
  sha256: string | null | undefined,
): Promise<void> {
  ensureAndroidNativeInstaller();
  await VassOverlay.installUpdateApk(apkUri, sha256 ?? null);
}
