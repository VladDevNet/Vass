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
  | { type: 'vadTick'; timestamp: number }
  | { type: 'screenCapture'; requestId: string; status: 'ready' | 'cancelled' | 'error'; uri?: string | null; error?: string | null }
  | { type: 'sharedImage'; requestId: string; status: 'ready' | 'error'; uri?: string | null; mimeType?: string | null; originalName?: string | null; error?: string | null };

export interface ScreenCaptureResult {
  requestId: string | null;
  status: 'ready' | 'cancelled' | 'error' | null;
  uri: string | null;
  error: string | null;
}

export interface SharedImageResult {
  requestId: string | null;
  status: 'ready' | 'error' | null;
  uri: string | null;
  mimeType: string | null;
  originalName: string | null;
  error: string | null;
}

interface NativeVassOverlayModule {
  canDrawOverlays(): Promise<boolean>;
  getStatus(): Promise<OverlayStatus>;
  requestOverlayPermission(): Promise<void>;
  openAppDetails(): Promise<void>;
  openApp(): Promise<void>;
  openExternalUrl(url: string): Promise<void>;
  requestScreenCapture(requestId: string): Promise<void>;
  getScreenCaptureResult(): Promise<ScreenCaptureResult>;
  clearScreenCaptureResult(requestId: string): Promise<void>;
  getSharedImage(): Promise<SharedImageResult>;
  acknowledgeSharedImage(requestId: string): Promise<void>;
  start(snapshot: OverlaySnapshot, appVisible: boolean): Promise<void>;
  update(snapshot: OverlaySnapshot): void;
  setAppVisible(visible: boolean): void;
  suspendForExternalMedia(): Promise<void>;
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

  async openExternalUrl(url: string): Promise<void> {
    if (!nativeModule) throw new Error('Android external actions are unavailable in this build');
    await nativeModule.openExternalUrl(url);
  },

  async requestScreenCapture(requestId: string): Promise<void> {
    if (!nativeModule) throw new Error('Разбор экрана недоступен в этой сборке');
    await nativeModule.requestScreenCapture(requestId);
  },

  async getScreenCaptureResult(): Promise<ScreenCaptureResult> {
    if (!nativeModule) return { requestId: null, status: null, uri: null, error: null };
    return nativeModule.getScreenCaptureResult();
  },

  async clearScreenCaptureResult(requestId: string): Promise<void> {
    await nativeModule?.clearScreenCaptureResult(requestId);
  },

  async getSharedImage(): Promise<SharedImageResult> {
    if (!nativeModule || typeof nativeModule.getSharedImage !== 'function') {
      return { requestId: null, status: null, uri: null, mimeType: null, originalName: null, error: null };
    }
    return nativeModule.getSharedImage();
  },

  async acknowledgeSharedImage(requestId: string): Promise<void> {
    if (typeof nativeModule?.acknowledgeSharedImage === 'function') {
      await nativeModule.acknowledgeSharedImage(requestId);
    }
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

  async suspendForExternalMedia(): Promise<void> {
    await nativeModule?.suspendForExternalMedia();
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
