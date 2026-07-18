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
  | { type: 'sharedContent'; requestId: string; status: 'ready' | 'error'; kind?: 'text' | 'attachment' | null; text?: string | null; uri?: string | null; mimeType?: string | null; originalName?: string | null; error?: string | null };

export interface ScreenCaptureResult {
  requestId: string | null;
  status: 'ready' | 'cancelled' | 'error' | null;
  uri: string | null;
  error: string | null;
}

export interface SharedContentResult {
  requestId: string | null;
  status: 'ready' | 'error' | null;
  kind: 'text' | 'attachment' | null;
  text: string | null;
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
  canRequestPackageInstalls(): Promise<boolean>;
  requestPackageInstallPermission(): Promise<void>;
  installUpdateApk(uri: string, expectedSha256: string | null): Promise<void>;
  requestScreenCapture(requestId: string): Promise<void>;
  getScreenCaptureResult(): Promise<ScreenCaptureResult>;
  clearScreenCaptureResult(requestId: string): Promise<void>;
  getSharedContent(): Promise<SharedContentResult>;
  acknowledgeSharedContent(requestId: string): Promise<void>;
  start(snapshot: OverlaySnapshot, appVisible: boolean): Promise<void>;
  update(snapshot: OverlaySnapshot): void;
  setAppVisible(visible: boolean): void;
  suspendForExternalMedia(): Promise<void>;
  stop(): Promise<void>;
  finishAppTask(): Promise<void>;
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

  async canRequestPackageInstalls(): Promise<boolean> {
    if (!nativeModule || typeof nativeModule.canRequestPackageInstalls !== 'function') return false;
    return nativeModule.canRequestPackageInstalls();
  },

  async requestPackageInstallPermission(): Promise<void> {
    if (!nativeModule || typeof nativeModule.requestPackageInstallPermission !== 'function') {
      throw new Error('В этой версии Vass ещё нет встроенной установки обновлений.');
    }
    await nativeModule.requestPackageInstallPermission();
  },

  async installUpdateApk(uri: string, expectedSha256: string | null): Promise<void> {
    if (!nativeModule || typeof nativeModule.installUpdateApk !== 'function') {
      throw new Error('В этой версии Vass ещё нет встроенной установки обновлений.');
    }
    await nativeModule.installUpdateApk(uri, expectedSha256);
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

  async getSharedContent(): Promise<SharedContentResult> {
    if (!nativeModule || typeof nativeModule.getSharedContent !== 'function') {
      return { requestId: null, status: null, kind: null, text: null, uri: null, mimeType: null, originalName: null, error: null };
    }
    return nativeModule.getSharedContent();
  },

  async acknowledgeSharedContent(requestId: string): Promise<void> {
    if (typeof nativeModule?.acknowledgeSharedContent === 'function') {
      await nativeModule.acknowledgeSharedContent(requestId);
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

  async finishAppTask(): Promise<void> {
    await nativeModule?.finishAppTask();
  },

  addListener(listener: (event: OverlayEvent) => void): () => void {
    if (!nativeModule) return () => undefined;
    const subscription = nativeModule.addListener('onOverlayEvent', listener);
    return () => subscription.remove();
  },
};
