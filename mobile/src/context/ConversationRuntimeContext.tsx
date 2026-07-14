import { createContext, useCallback, useContext, useEffect, useRef, useState, type ReactNode } from 'react';
import { AppState } from 'react-native';
import { api } from '../api/client';
import { useVoiceChat, type VoiceState } from '../hooks/useVoiceChat';
import { useAuth } from './AuthContext';
import { VassOverlay, type OverlayEvent, type SharedImageResult } from '../../modules/vass-overlay';
import { log } from '../logging/remoteLogger';
import { useVisualInput } from '../hooks/useVisualInput';
import type { PendingVisualInput, StageVisualAssetInput, VisualInputStatus, VisualSource } from '../visual/types';

interface ConversationRuntimeValue {
  sessionId: number | null;
  sessionError: string | null;
  state: VoiceState;
  transcript: string;
  reply: string;
  error: string | null;
  micArmed: boolean;
  forceFinalize: () => void;
  pauseConversation: () => Promise<void>;
  resumeConversation: () => Promise<void>;
  prepareOverlayMode: () => Promise<void>;
  disableOverlayMode: (resumeWhenVisible: boolean) => Promise<void>;
  pendingVisual: PendingVisualInput | null;
  visualStatus: VisualInputStatus;
  visualError: string | null;
  visualUploadingUri: string | null;
  pickVisual: (source: VisualSource) => Promise<void>;
  removePendingVisual: () => Promise<void>;
  stageVisualAsset: (input: StageVisualAssetInput) => Promise<PendingVisualInput | null>;
}

const ConversationRuntimeContext = createContext<ConversationRuntimeValue | null>(null);

export function ConversationRuntimeProvider({ children }: { children: ReactNode }) {
  const { avatarId, user } = useAuth();
  const [sessionId, setSessionId] = useState<number | null>(null);
  const [sessionError, setSessionError] = useState<string | null>(null);
  const visual = useVisualInput();
  const runtime = useVoiceChat(sessionId, {
    getPendingVisual: visual.getPendingVisual,
    consumePendingVisual: visual.consumePendingVisual,
    stageVisualAsset: visual.stageVisualAsset,
  });
  const stateRef = useRef(runtime.state);
  const actionsRef = useRef(runtime);
  const resumeRequestedRef = useRef(false);
  const pausedForBackgroundRef = useRef(false);
  const restoredOverlayAudioRef = useRef(false);
  const sharedImageAttemptRef = useRef<string | null>(null);

  stateRef.current = runtime.state;
  actionsRef.current = runtime;

  useEffect(() => {
    let cancelled = false;
    api
      .getSessions()
      .then((sessions) => {
        if (!cancelled && sessions.length > 0) setSessionId(sessions[0].id);
      })
      .catch((err) => {
        if (!cancelled) setSessionError(err instanceof Error ? err.message : String(err));
      });
    return () => {
      cancelled = true;
    };
  }, []);

  useEffect(() => {
    if (!user || !VassOverlay.isAvailable()) return;
    let cancelled = false;

    const receiveSharedImage = async (sharedFromEvent?: Extract<OverlayEvent, { type: 'sharedImage' }>) => {
      // Some Android share targets deliver the URI through the native event
      // before JS can query SharedPreferences. Use that payload directly;
      // the getter remains the recovery path if the app was cold-started.
      const shared: SharedImageResult = sharedFromEvent
        ? {
            requestId: sharedFromEvent.requestId,
            status: sharedFromEvent.status,
            uri: sharedFromEvent.uri ?? null,
            mimeType: sharedFromEvent.mimeType ?? null,
            originalName: sharedFromEvent.originalName ?? null,
            error: sharedFromEvent.error ?? null,
          }
        : await VassOverlay.getSharedImage();
      if (cancelled || !shared.requestId || !shared.status) return;
      if (sharedImageAttemptRef.current === shared.requestId) return;
      sharedImageAttemptRef.current = shared.requestId;

      if (shared.status === 'error' || !shared.uri || !shared.mimeType) {
        visual.reportVisualError(
          'Не удалось получить вложение. Размер файла не должен превышать 50 МБ.',
        );
        await VassOverlay.acknowledgeSharedImage(shared.requestId);
        return;
      }

      const staged = await visual.stageVisualAsset({
        uri: shared.uri,
        mimeType: shared.mimeType,
        originalName: shared.originalName,
      });
      if (cancelled) return;
      if (!staged) {
        sharedImageAttemptRef.current = null;
        return;
      }
      await VassOverlay.acknowledgeSharedImage(shared.requestId);
      log('info', 'visual', 'shared attachment staged for next voice turn', { mimeType: shared.mimeType });
    };

    void receiveSharedImage().catch((err) => {
      sharedImageAttemptRef.current = null;
      log('error', 'visual', 'failed to receive shared attachment', {
        error: err instanceof Error ? err.message : String(err),
      });
    });
    const removeListener = VassOverlay.addListener((event) => {
      if (event.type !== 'sharedImage') return;
      sharedImageAttemptRef.current = null;
      void receiveSharedImage(event);
    });
    const appStateSubscription = AppState.addEventListener('change', (nextState) => {
      if (nextState !== 'active') return;
      sharedImageAttemptRef.current = null;
      void receiveSharedImage();
    });
    return () => {
      cancelled = true;
      removeListener();
      appStateSubscription.remove();
    };
  }, [user?.id, visual.reportVisualError, visual.stageVisualAsset]);

  const requestResume = useCallback(() => {
    if (AppState.currentState === 'active') {
      void actionsRef.current.resumeConversation();
    } else {
      resumeRequestedRef.current = true;
    }
  }, []);

  useEffect(() => {
    return VassOverlay.addListener((event: OverlayEvent) => {
      switch (event.type) {
        case 'controlPress':
          if (stateRef.current === 'paused' && AppState.currentState !== 'active') {
            resumeRequestedRef.current = true;
          } else {
            actionsRef.current.forceFinalize();
          }
          break;
        case 'pauseToggle':
          if (event.paused) void actionsRef.current.pauseConversation();
          else requestResume();
          break;
        case 'stopRequested':
          void actionsRef.current.configureBackgroundRecording(false, false);
          break;
        case 'openApp':
          requestResume();
          break;
      }
    });
  }, [requestResume]);

  useEffect(() => {
    VassOverlay.update({
      state: runtime.state,
      avatarId: avatarId === 'male' ? 'male' : 'olga',
      enabled: true,
    });
  }, [avatarId, runtime.state]);

  useEffect(() => {
    if (!runtime.micArmed || restoredOverlayAudioRef.current) return;
    restoredOverlayAudioRef.current = true;
    void (async () => {
      const status = await VassOverlay.getStatus();
      if (status.running && status.enabled && status.permissionGranted) {
        await actionsRef.current.configureBackgroundRecording(true, true);
      }
    })().catch((err) => {
      log('error', 'overlay', 'failed to restore background conversation mode', {
        error: err instanceof Error ? err.message : String(err),
      });
    });
  }, [runtime.micArmed]);

  useEffect(() => {
    const subscription = AppState.addEventListener('change', (nextState) => {
      if (nextState === 'background') {
        void (async () => {
          const status = await VassOverlay.getStatus();
          if (AppState.currentState !== 'background') return;

          const overlayCanOwnBackground = status.running && status.enabled && status.permissionGranted;
          if (overlayCanOwnBackground) return;

          pausedForBackgroundRef.current = stateRef.current !== 'paused';
          await actionsRef.current.configureBackgroundRecording(false, false);
          if (status.running) await VassOverlay.stop();
          log('info', 'overlay', 'conversation paused because background overlay is unavailable');
        })();
        return;
      }

      if (nextState === 'active') {
        if (resumeRequestedRef.current) {
          resumeRequestedRef.current = false;
          void actionsRef.current.resumeConversation();
        } else if (pausedForBackgroundRef.current) {
          pausedForBackgroundRef.current = false;
          void actionsRef.current.resumeConversation();
        }
      }
    });
    return () => subscription.remove();
  }, []);

  const prepareOverlayMode = useCallback(
    () => runtime.configureBackgroundRecording(true, true),
    [runtime.configureBackgroundRecording],
  );

  const disableOverlayMode = useCallback(
    (resumeWhenVisible: boolean) => runtime.configureBackgroundRecording(false, resumeWhenVisible),
    [runtime.configureBackgroundRecording],
  );

  return (
    <ConversationRuntimeContext.Provider
      value={{
        sessionId,
        sessionError,
        state: runtime.state,
        transcript: runtime.transcript,
        reply: runtime.reply,
        error: runtime.error,
        micArmed: runtime.micArmed,
        forceFinalize: runtime.forceFinalize,
        pauseConversation: runtime.pauseConversation,
        resumeConversation: runtime.resumeConversation,
        prepareOverlayMode,
        disableOverlayMode,
        pendingVisual: visual.pendingVisual,
        visualStatus: visual.status,
        visualError: visual.error,
        visualUploadingUri: visual.uploadingUri,
        pickVisual: visual.pickVisual,
        removePendingVisual: visual.removePendingVisual,
        stageVisualAsset: visual.stageVisualAsset,
      }}
    >
      {children}
    </ConversationRuntimeContext.Provider>
  );
}

export function useConversationRuntime(): ConversationRuntimeValue {
  const context = useContext(ConversationRuntimeContext);
  if (!context) throw new Error('useConversationRuntime must be used within ConversationRuntimeProvider');
  return context;
}
