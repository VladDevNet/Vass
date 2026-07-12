import { Platform } from 'react-native';
import { requireNativeModule, type EventSubscription } from 'expo-modules-core';

export type OverlayState = 'idle' | 'recording' | 'thinking' | 'speaking' | 'paused' | 'error';
export type OverlayAvatarId = 'olga' | 'male';

export interface OverlaySnapshot {
  state: OverlayState;
  avatarId: OverlayAvatarId;
  enabled: boolean;
}

export interface OverlayStatus {
  available: boolean;
  permissionGranted: boolean;
  enabled: boolean;
  running: boolean;
}

export type OverlayEvent =
  | { type: 'controlPress' }
  | { type: 'pauseToggle'; paused: boolean }
  | { type: 'openApp' }
  | { type: 'stopRequested' }
  | { type: 'vadTick'; timestamp: number };

interface NativeVassOverlayModule {
  canDrawOverlays(): Promise<boolean>;
  getStatus(): Promise<OverlayStatus>;
  requestOverlayPermission(): Promise<void>;
  openAppDetails(): Promise<void>;
  openApp(): Promise<void>;
  start(snapshot: OverlaySnapshot, appVisible: boolean): Promise<void>;
  update(snapshot: OverlaySnapshot): void;
  setAppVisible(visible: boolean): void;
  stop(): Promise<void>;
  addListener(eventName: 'onOverlayEvent', listener: (event: OverlayEvent) => void): EventSubscription;
}

let nativeModule: NativeVassOverlayModule | null = null;

if (Platform.OS === 'android') {
  try {
    nativeModule = requireNativeModule<NativeVassOverlayModule>('VassOverlay');
  } catch {
    // Expo Go cannot contain this local native module. The UI treats that
    // build exactly like an unsupported platform and keeps the rest working.
  }
}

export const VassOverlay = {
  isAvailable: () => nativeModule !== null,

  async canDrawOverlays(): Promise<boolean> {
    return nativeModule ? nativeModule.canDrawOverlays() : false;
  },

  async getStatus(): Promise<OverlayStatus> {
    if (!nativeModule) {
      return { available: false, permissionGranted: false, enabled: false, running: false };
    }
    return nativeModule.getStatus();
  },

  async requestOverlayPermission(): Promise<void> {
    await nativeModule?.requestOverlayPermission();
  },

  async openAppDetails(): Promise<void> {
    await nativeModule?.openAppDetails();
  },

  async openApp(): Promise<void> {
    if (!nativeModule) throw new Error('Android overlay недоступен в этой сборке');
    await nativeModule.openApp();
  },

  async start(snapshot: OverlaySnapshot, appVisible = true): Promise<void> {
    if (!nativeModule) throw new Error('Android overlay недоступен в этой сборке');
    await nativeModule.start(snapshot, appVisible);
  },

  update(snapshot: OverlaySnapshot): void {
    nativeModule?.update(snapshot);
  },

  setAppVisible(visible: boolean): void {
    nativeModule?.setAppVisible(visible);
  },

  async stop(): Promise<void> {
    await nativeModule?.stop();
  },

  addListener(listener: (event: OverlayEvent) => void): () => void {
    if (!nativeModule) return () => undefined;
    const subscription = nativeModule.addListener('onOverlayEvent', listener);
    return () => subscription.remove();
  },
};
