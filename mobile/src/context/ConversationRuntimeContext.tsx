import { createContext, useCallback, useContext, useEffect, useRef, useState, type ReactNode } from 'react';
import { AppState } from 'react-native';
import { api, type ExternalActionEvent } from '../api/client';
import { useVoiceChat, type VoiceAudioOutput, type VoiceState } from '../hooks/useVoiceChat';
import { useAuth } from './AuthContext';
import { VassOverlay, type OverlayEvent, type SharedContentResult } from '../../modules/vass-overlay';
import { log } from '../logging/remoteLogger';
import { useVisualInput } from '../hooks/useVisualInput';
import { getLibraryCatalogForAssistant, saveLibraryArtifact } from '../library/libraryStore';
import type { PendingSharedText, PendingVisualInput, StageVisualAssetInput, VisualInputStatus, VisualSource } from '../visual/types';

type LibraryActionEvent = Extract<ExternalActionEvent, { type: 'library_write' | 'library_open' }>;

export interface LibraryNavigationRequest {
  requestId: number;
  artifactId: string | null;
}

interface ConversationRuntimeValue {
  sessionId: number | null;
  sessionError: string | null;
  state: VoiceState;
  transcript: string;
  reply: string;
  error: string | null;
  ttsPlaying: boolean;
  micArmed: boolean;
  conversationEnded: boolean;
  audioOutputs: VoiceAudioOutput[];
  selectedAudioOutputId: string;
  libraryNavigation: LibraryNavigationRequest | null;
  forceFinalize: () => void;
  pauseConversation: () => Promise<void>;
  resumeConversation: () => Promise<void>;
  endConversation: () => Promise<void>;
  clearLibraryNavigation: (requestId: number) => void;
  announceSystemNotice: (text: string) => Promise<void>;
  refreshAudioOutputs: () => Promise<unknown>;
  selectAudioOutput: (outputId: string) => Promise<void>;
  prepareOverlayMode: () => Promise<void>;
  disableOverlayMode: (resumeWhenVisible: boolean) => Promise<void>;
  pendingVisual: PendingVisualInput | null;
  visualStatus: VisualInputStatus;
  visualError: string | null;
  visualUploadingUri: string | null;
  pendingSharedText: PendingSharedText | null;
  pickVisual: (source: VisualSource) => Promise<void>;
  removePendingVisual: () => Promise<void>;
  removePendingSharedText: () => void;
  stageVisualAsset: (input: StageVisualAssetInput) => Promise<PendingVisualInput | null>;
}

const ConversationRuntimeContext = createContext<ConversationRuntimeValue | null>(null);

export function ConversationRuntimeProvider({ children }: { children: ReactNode }) {
  const { avatarId, user } = useAuth();
  const [sessionId, setSessionId] = useState<number | null>(null);
  const [sessionError, setSessionError] = useState<string | null>(null);
  const [pendingSharedText, setPendingSharedText] = useState<PendingSharedText | null>(null);
  const [libraryNavigation, setLibraryNavigation] = useState<LibraryNavigationRequest | null>(null);
  const visual = useVisualInput();
  const pendingSharedTextRef = useRef<PendingSharedText | null>(null);
  const screenCaptureConsentPendingRef = useRef(false);
  const libraryRequestIdRef = useRef(0);
  const stageSharedText = useCallback((pending: PendingSharedText) => {
    pendingSharedTextRef.current = pending;
    setPendingSharedText(pending);
  }, []);
  const getPendingSharedText = useCallback(() => pendingSharedTextRef.current, []);
  const consumePendingSharedText = useCallback((requestId: string) => {
    if (pendingSharedTextRef.current?.requestId !== requestId) return;
    pendingSharedTextRef.current = null;
    setPendingSharedText(null);
  }, []);
  const setScreenCaptureConsentPending = useCallback((pending: boolean) => {
    if (screenCaptureConsentPendingRef.current === pending) return;
    screenCaptureConsentPendingRef.current = pending;
    log('info', 'screen-capture', pending
      ? 'screen capture consent is pending'
      : 'screen capture consent finished');
  }, []);

  const requestLibraryNavigation = useCallback((artifactId: string | null) => {
    const requestId = ++libraryRequestIdRef.current;
    setLibraryNavigation({ requestId, artifactId });
    return requestId;
  }, []);

  const applyLibraryAction = useCallback(async (action: LibraryActionEvent): Promise<{ resultCode: string }> => {
    if (action.type === 'library_write') {
      const result = await saveLibraryArtifact(action.libraryArtifact);
      requestLibraryNavigation(result.artifact.id);
      await VassOverlay.openApp().catch(() => undefined);
      log('info', 'library', 'local artifact saved from assistant action', {
        artifactId: result.artifact.id,
        revisionCount: result.artifact.revisions.length,
        created: result.created,
      });
      return { resultCode: result.created ? 'library_created' : 'library_revision_saved' };
    }

    requestLibraryNavigation(action.artifactId);
    await VassOverlay.openApp().catch(() => undefined);
    log('info', 'library', 'library navigation requested by assistant', { artifactId: action.artifactId });
    return { resultCode: action.artifactId ? 'library_artifact_opened' : 'library_catalog_opened' };
  }, [requestLibraryNavigation]);

  const clearLibraryNavigation = useCallback((requestId: number) => {
    setLibraryNavigation((current) => current?.requestId === requestId ? null : current);
  }, []);

  const runtime = useVoiceChat(sessionId, user?.id ?? null, {
    getPendingVisual: visual.getPendingVisual,
    consumePendingVisual: visual.consumePendingVisual,
    stageVisualAsset: visual.stageVisualAsset,
    getPendingSharedText,
    consumePendingSharedText,
    setScreenCaptureConsentPending,
    getLibraryCatalog: getLibraryCatalogForAssistant,
    applyLibraryAction,
  });
  const stateRef = useRef(runtime.state);
  const actionsRef = useRef(runtime);
  const resumeRequestedRef = useRef(false);
  const pausedForBackgroundRef = useRef(false);
  const restoredOverlayAudioRef = useRef(false);
  const sharedContentAttemptRef = useRef<string | null>(null);

  stateRef.current = runtime.state;
  actionsRef.current = runtime;

  const endConversation = useCallback(async () => {
    // Clear resume intents before the first await. A foreground transition
    // can arrive while native audio is still unwinding, and it must never
    // revive a conversation the person explicitly ended.
    resumeRequestedRef.current = false;
    pausedForBackgroundRef.current = false;
    restoredOverlayAudioRef.current = false;
    await runtime.endConversation();
    await VassOverlay.stop().catch((error) => {
      log('warn', 'overlay', 'could not stop overlay while ending conversation', {
        error: error instanceof Error ? error.message : String(error),
      });
    });
    await VassOverlay.finishAppTask().catch((error) => {
      log('warn', 'app', 'could not remove Android task after ending conversation', {
        error: error instanceof Error ? error.message : String(error),
      });
    });
  }, [runtime.endConversation]);

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

    const receiveSharedContent = async (sharedFromEvent?: Extract<OverlayEvent, { type: 'sharedContent' }>) => {
      // Some Android share targets deliver the URI through the native event
      // before JS can query SharedPreferences. Use that payload directly;
      // the getter remains the recovery path if the app was cold-started.
      const shared: SharedContentResult = sharedFromEvent
        ? {
            requestId: sharedFromEvent.requestId,
            status: sharedFromEvent.status,
            kind: sharedFromEvent.kind ?? null,
            text: sharedFromEvent.text ?? null,
            uri: sharedFromEvent.uri ?? null,
            mimeType: sharedFromEvent.mimeType ?? null,
            originalName: sharedFromEvent.originalName ?? null,
            error: sharedFromEvent.error ?? null,
          }
        : await VassOverlay.getSharedContent();
      if (cancelled || !shared.requestId || !shared.status) return;
      if (sharedContentAttemptRef.current === shared.requestId) return;
      sharedContentAttemptRef.current = shared.requestId;

      if (shared.status === 'ready' && shared.kind === 'text' && shared.text?.trim()) {
        stageSharedText({ requestId: shared.requestId, content: shared.text.trim() });
        await VassOverlay.acknowledgeSharedContent(shared.requestId);
        await VassOverlay.openApp().catch(() => undefined);
        await actionsRef.current.announceSystemNotice(
          /(?:https?:\/\/|www\.)/i.test(shared.text) ? 'Получена ссылка.' : 'Получен текст.'
        );
        void api.recordCapabilityUsage('share').catch((err) => {
          log('warn', 'share', 'could not record successful shared content usage', {
            error: err instanceof Error ? err.message : String(err),
          });
        });
        log('info', 'share', 'shared text staged for next voice turn', { length: shared.text.length });
        return;
      }

      if (shared.status === 'error' || shared.kind !== 'attachment' || !shared.uri || !shared.mimeType) {
        visual.reportVisualError(
          'Не удалось получить вложение. Размер файла не должен превышать 50 МБ.',
        );
        await VassOverlay.acknowledgeSharedContent(shared.requestId);
        return;
      }

      const staged = await visual.stageVisualAsset({
        uri: shared.uri,
        mimeType: shared.mimeType,
        originalName: shared.originalName,
      });
      if (cancelled) return;
      if (!staged) {
        sharedContentAttemptRef.current = null;
        return;
      }
      await VassOverlay.acknowledgeSharedContent(shared.requestId);
      await VassOverlay.openApp().catch(() => undefined);
      await actionsRef.current.announceSystemNotice(
        shared.mimeType.startsWith('image/') ? 'Получено изображение.' : 'Получен документ.'
      );
      void api.recordCapabilityUsage('share').catch((err) => {
        log('warn', 'share', 'could not record successful shared attachment usage', {
          error: err instanceof Error ? err.message : String(err),
        });
      });
      log('info', 'visual', 'shared attachment staged for next voice turn', { mimeType: shared.mimeType });
    };

    void receiveSharedContent().catch((err) => {
      sharedContentAttemptRef.current = null;
      log('error', 'share', 'failed to receive shared content', {
        error: err instanceof Error ? err.message : String(err),
      });
    });
    const removeListener = VassOverlay.addListener((event) => {
      if (event.type !== 'sharedContent') return;
      sharedContentAttemptRef.current = null;
      void receiveSharedContent(event);
    });
    const appStateSubscription = AppState.addEventListener('change', (nextState) => {
      if (nextState !== 'active') return;
      sharedContentAttemptRef.current = null;
      void receiveSharedContent();
    });
    return () => {
      cancelled = true;
      removeListener();
      appStateSubscription.remove();
    };
  }, [user?.id, stageSharedText, visual.reportVisualError, visual.stageVisualAsset]);

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
          void endConversation();
          break;
        case 'openApp':
          requestResume();
          break;
      }
    });
  }, [requestResume, endConversation]);

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
        if (actionsRef.current.isConversationEnded()) return;
        // Android's MediaProjection consent is a system activity. It makes
        // React Native report "background", but the active voice turn must
        // remain alive until the user accepts or cancels that dialog.
        if (screenCaptureConsentPendingRef.current) {
          log('info', 'screen-capture', 'ignoring background transition for system consent');
          return;
        }
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
        if (actionsRef.current.isConversationEnded()) {
          void actionsRef.current.startConversationAfterExplicitLaunch();
          return;
        }
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
        ttsPlaying: runtime.ttsPlaying,
        micArmed: runtime.micArmed,
        conversationEnded: runtime.conversationEnded,
        audioOutputs: runtime.audioOutputs,
        selectedAudioOutputId: runtime.selectedAudioOutputId,
        libraryNavigation,
        forceFinalize: runtime.forceFinalize,
        pauseConversation: runtime.pauseConversation,
        resumeConversation: runtime.resumeConversation,
        endConversation,
        clearLibraryNavigation,
        announceSystemNotice: runtime.announceSystemNotice,
        refreshAudioOutputs: runtime.refreshAudioOutputs,
        selectAudioOutput: runtime.selectAudioOutput,
        prepareOverlayMode,
        disableOverlayMode,
        pendingVisual: visual.pendingVisual,
        visualStatus: visual.status,
        visualError: visual.error,
        visualUploadingUri: visual.uploadingUri,
        pendingSharedText,
        pickVisual: visual.pickVisual,
        removePendingVisual: visual.removePendingVisual,
        removePendingSharedText: () => consumePendingSharedText(pendingSharedText?.requestId ?? ''),
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
