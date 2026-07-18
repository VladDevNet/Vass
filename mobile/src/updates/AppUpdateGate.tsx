import Constants from 'expo-constants';
import { useCallback, useEffect, useRef, useState } from 'react';
import { AppState, Platform } from 'react-native';
import {
  api,
  isAndroidUpdateSkipped,
  skipAndroidUpdate,
  type AndroidAppUpdate,
} from '../api/client';
import { AppUpdateModal } from '../components/AppUpdateModal';
import { log } from '../logging/remoteLogger';
import {
  canRequestAndroidPackageInstalls,
  downloadAndroidUpdate,
  launchAndroidPackageInstaller,
  requestAndroidPackageInstallPermission,
} from './androidInAppInstaller';
import type { ReactNode } from 'react';

interface AppUpdateGateProps {
  children: ReactNode;
}

type InstallPhase = 'idle' | 'downloading' | 'permission' | 'installing';

interface PendingAndroidInstall {
  apkUri: string;
  latestVersionCode: number;
  sha256: string | null;
}

function getAndroidVersionCode(): number | null {
  const rawVersionCode = Constants.nativeBuildVersion ?? Constants.expoConfig?.android?.versionCode;
  const versionCode = Number(rawVersionCode);
  return Number.isInteger(versionCode) && versionCode > 0 ? versionCode : null;
}

export function AppUpdateGate({ children }: AppUpdateGateProps) {
  const [update, setUpdate] = useState<AndroidAppUpdate | null>(null);
  const [installPhase, setInstallPhase] = useState<InstallPhase>('idle');
  const [downloadProgress, setDownloadProgress] = useState<number | null>(null);
  const [installError, setInstallError] = useState<string | null>(null);
  const checkingRef = useRef(false);
  const installerHandoffRef = useRef(false);
  const pendingInstallRef = useRef<PendingAndroidInstall | null>(null);
  const installPhaseRef = useRef<InstallPhase>('idle');
  const appStateRef = useRef(AppState.currentState);

  installPhaseRef.current = installPhase;

  const checkForUpdate = useCallback(async () => {
    if (Platform.OS !== 'android' || checkingRef.current) return;

    const currentVersionCode = getAndroidVersionCode();
    if (currentVersionCode === null) {
      log('warn', 'updates', 'Android versionCode is unavailable; skipped update check');
      return;
    }

    checkingRef.current = true;
    try {
      const result = await api.getAndroidAppUpdate(currentVersionCode);
      if (!result.updateAvailable) {
        setUpdate(null);
        return;
      }

      if (!result.mandatory && await isAndroidUpdateSkipped(result.latestVersionCode)) {
        setUpdate(null);
        return;
      }

      setInstallError(null);
      setUpdate(result);
      log('info', 'updates', 'Android update is available', {
        currentVersionCode,
        latestVersionCode: result.latestVersionCode,
        mandatory: result.mandatory,
      });
    } catch (error) {
      // A check is best-effort. Voice and chat must stay usable while offline
      // or when the release service itself is temporarily unavailable.
      log('warn', 'updates', 'Android update check failed', {
        error: error instanceof Error ? error.message : String(error),
      });
    } finally {
      checkingRef.current = false;
    }
  }, []);

  const handOffToSystemInstaller = useCallback(async (pending: PendingAndroidInstall) => {
    if (installerHandoffRef.current) return 'busy' as const;
    installerHandoffRef.current = true;
    try {
      if (!await canRequestAndroidPackageInstalls()) {
        setInstallPhase('permission');
        return 'permission-required' as const;
      }

      setInstallPhase('installing');
      setInstallError(null);
      await launchAndroidPackageInstaller(pending.apkUri, pending.sha256);
      log('info', 'updates', 'Handed APK to Android package installer', {
        latestVersionCode: pending.latestVersionCode,
      });
      return 'launched' as const;
    } catch (error) {
      setInstallPhase('idle');
      setInstallError('Не удалось передать обновление Android. Попробуйте скачать его ещё раз.');
      log('error', 'updates', 'Unable to hand APK to Android package installer', {
        latestVersionCode: pending.latestVersionCode,
        error: error instanceof Error ? error.message : String(error),
      });
      return 'failed' as const;
    } finally {
      installerHandoffRef.current = false;
    }
  }, []);

  const openInstallPermission = useCallback(async () => {
    try {
      await requestAndroidPackageInstallPermission();
      log('info', 'updates', 'Opened Android update-install permission settings');
    } catch (error) {
      setInstallPhase('idle');
      setInstallError('Не удалось открыть настройки установки Android.');
      log('error', 'updates', 'Unable to open Android update-install permission settings', {
        error: error instanceof Error ? error.message : String(error),
      });
    }
  }, []);

  const continuePendingInstall = useCallback(async () => {
    const pending = pendingInstallRef.current;
    if (!pending) return;
    const result = await handOffToSystemInstaller(pending);
    if (result === 'launched') {
      pendingInstallRef.current = null;
      return;
    }
    if (result === 'permission-required') {
      setInstallError('Разрешите Vass устанавливать обновления в настройках Android.');
    }
  }, [handOffToSystemInstaller]);

  useEffect(() => {
    void checkForUpdate();
    const subscription = AppState.addEventListener('change', (nextState) => {
      const returnedToForeground = appStateRef.current !== 'active' && nextState === 'active';
      appStateRef.current = nextState;
      if (returnedToForeground) {
        // The package installer does not report a result to React Native. If
        // the person cancels it, make the modal actionable again; a completed
        // install normally restarts into the new binary before this matters.
        if (installPhaseRef.current === 'installing') {
          setInstallPhase('idle');
          setDownloadProgress(null);
        }
        void checkForUpdate();
        void continuePendingInstall();
      }
    });
    return () => subscription.remove();
  }, [checkForUpdate, continuePendingInstall]);

  const handleSkip = useCallback(() => {
    if (!update || update.mandatory || installPhase === 'downloading' || installPhase === 'installing') return;
    void skipAndroidUpdate(update.latestVersionCode);
    pendingInstallRef.current = null;
    setInstallPhase('idle');
    setDownloadProgress(null);
    setUpdate(null);
  }, [installPhase, update]);

  const handleInstall = useCallback(async () => {
    if (installPhase === 'downloading' || installPhase === 'installing') return;

    if (installPhase === 'permission' && pendingInstallRef.current) {
      const result = await handOffToSystemInstaller(pendingInstallRef.current);
      if (result === 'launched') {
        pendingInstallRef.current = null;
      } else if (result === 'permission-required') {
        await openInstallPermission();
      }
      return;
    }

    if (!update?.downloadUrl || Platform.OS !== 'android') return;

    setInstallError(null);
    setInstallPhase('downloading');
    setDownloadProgress(0);
    try {
      const apkUri = await downloadAndroidUpdate(
        update.downloadUrl,
        update.latestVersionCode,
        setDownloadProgress,
      );
      const pending: PendingAndroidInstall = {
        apkUri,
        latestVersionCode: update.latestVersionCode,
        sha256: update.sha256,
      };
      pendingInstallRef.current = pending;
      const result = await handOffToSystemInstaller(pending);
      if (result === 'launched') {
        pendingInstallRef.current = null;
      } else if (result === 'permission-required') {
        await openInstallPermission();
      }
    } catch (error) {
      setInstallPhase('idle');
      setDownloadProgress(null);
      setInstallError('Не удалось скачать обновление. Проверьте интернет и попробуйте ещё раз.');
      log('error', 'updates', 'Unable to download Android update', {
        latestVersionCode: update.latestVersionCode,
        error: error instanceof Error ? error.message : String(error),
      });
    }
  }, [handOffToSystemInstaller, installPhase, openInstallPermission, update]);

  return (
    <>
      {children}
      <AppUpdateModal
        update={update}
        installPhase={installPhase}
        downloadProgress={downloadProgress}
        error={installError}
        onInstall={() => void handleInstall()}
        onSkip={handleSkip}
      />
    </>
  );
}
