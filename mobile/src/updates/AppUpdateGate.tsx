import Constants from 'expo-constants';
import { useCallback, useEffect, useRef, useState } from 'react';
import { AppState, Linking, Platform } from 'react-native';
import {
  api,
  isAndroidUpdateSkipped,
  skipAndroidUpdate,
  type AndroidAppUpdate,
} from '../api/client';
import { AppUpdateModal } from '../components/AppUpdateModal';
import { log } from '../logging/remoteLogger';
import type { ReactNode } from 'react';

interface AppUpdateGateProps {
  children: ReactNode;
}

function getAndroidVersionCode(): number | null {
  const rawVersionCode = Constants.nativeBuildVersion ?? Constants.expoConfig?.android?.versionCode;
  const versionCode = Number(rawVersionCode);
  return Number.isInteger(versionCode) && versionCode > 0 ? versionCode : null;
}

export function AppUpdateGate({ children }: AppUpdateGateProps) {
  const [update, setUpdate] = useState<AndroidAppUpdate | null>(null);
  const [isLaunching, setIsLaunching] = useState(false);
  const [installError, setInstallError] = useState<string | null>(null);
  const checkingRef = useRef(false);
  const appStateRef = useRef(AppState.currentState);

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

  useEffect(() => {
    void checkForUpdate();
    const subscription = AppState.addEventListener('change', (nextState) => {
      const returnedToForeground = appStateRef.current !== 'active' && nextState === 'active';
      appStateRef.current = nextState;
      if (returnedToForeground) void checkForUpdate();
    });
    return () => subscription.remove();
  }, [checkForUpdate]);

  const handleSkip = useCallback(() => {
    if (!update || update.mandatory) return;
    void skipAndroidUpdate(update.latestVersionCode);
    setUpdate(null);
  }, [update]);

  const handleInstall = useCallback(async () => {
    if (!update?.downloadUrl || isLaunching) return;

    setInstallError(null);
    setIsLaunching(true);
    try {
      await Linking.openURL(update.downloadUrl);
      log('info', 'updates', 'Opened Android update download', {
        latestVersionCode: update.latestVersionCode,
      });
    } catch (error) {
      setInstallError('Не удалось открыть обновление. Проверьте интернет и попробуйте ещё раз.');
      log('error', 'updates', 'Unable to open Android update download', {
        error: error instanceof Error ? error.message : String(error),
      });
    } finally {
      setIsLaunching(false);
    }
  }, [isLaunching, update]);

  return (
    <>
      {children}
      <AppUpdateModal
        update={update}
        isLaunching={isLaunching}
        error={installError}
        onInstall={() => void handleInstall()}
        onSkip={handleSkip}
      />
    </>
  );
}
