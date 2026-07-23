import Constants from 'expo-constants';
import * as SecureStore from 'expo-secure-store';
import { useEffect, useRef, useState } from 'react';
import { Platform } from 'react-native';
import { ReleaseCapabilitiesModal } from '../components/ReleaseCapabilitiesModal';
import { api, type CapabilityDiscoveryItem, type CapabilityRuntimeParams } from '../api/client';
import { log } from '../logging/remoteLogger';
import { VassOverlay } from '../../modules/vass-overlay';
import type { ReactNode } from 'react';

interface FeatureIntroductionGateProps {
  userId: string;
  children: ReactNode;
}

function getVersionCode(): number | null {
  const raw = Constants.nativeBuildVersion ?? Constants.expoConfig?.android?.versionCode;
  const value = Number(raw);
  return Number.isInteger(value) && value > 0 ? value : null;
}

function getRuntimeParams(): CapabilityRuntimeParams {
  const isPhone = Platform.OS !== 'web';
  return {
    supportsReminders: isPhone,
    supportsPeriodicReminders: Platform.OS === 'android',
    supportsExternalActions: Platform.OS === 'android' && VassOverlay.isAvailable(),
    supportsScreenAnalysis: Platform.OS === 'android' && VassOverlay.isAvailable(),
    supportsLibrary: isPhone,
  };
}

export function FeatureIntroductionGate({ userId, children }: FeatureIntroductionGateProps) {
  const [items, setItems] = useState<CapabilityDiscoveryItem[]>([]);
  const loadingRef = useRef(false);
  const checkedUserRef = useRef<string | null>(null);

  useEffect(() => {
    const versionCode = getVersionCode();
    if (Platform.OS !== 'android' || versionCode === null || loadingRef.current || checkedUserRef.current === userId) return;
    const storageKey = `vass_release_capabilities_${userId}_${versionCode}`;
    let cancelled = false;
    loadingRef.current = true;
    checkedUserRef.current = userId;

    void (async () => {
      try {
        const alreadyShown = await SecureStore.getItemAsync(storageKey);
        if (alreadyShown) return;
        const releaseItems = await api.presentReleaseCapabilities(getRuntimeParams());
        await SecureStore.setItemAsync(storageKey, '1');
        if (!cancelled && releaseItems.length > 0) {
          setItems(releaseItems);
          log('info', 'capabilities', 'release introduction shown', {
            versionCode,
            capabilityIds: releaseItems.map((item) => item.id),
          });
        }
      } catch (error) {
        // Leave the local marker unset. A later foreground launch can retry
        // safely; the server makes a previously presented set empty.
        checkedUserRef.current = null;
        log('warn', 'capabilities', 'release introduction check failed', {
          error: error instanceof Error ? error.message : String(error),
        });
      } finally {
        loadingRef.current = false;
      }
    })();

    return () => { cancelled = true; };
  }, [userId]);

  return (
    <>
      {children}
      <ReleaseCapabilitiesModal items={items} onDone={() => setItems([])} />
    </>
  );
}
