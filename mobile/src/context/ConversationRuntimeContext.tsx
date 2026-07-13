import { createContext, useCallback, useContext, useEffect, useRef, useState, type ReactNode } from 'react';
import { AppState } from 'react-native';
import { api } from '../api/client';
import { useVoiceChat, type VoiceState } from '../hooks/useVoiceChat';
import { useAuth } from './AuthContext';
import { VassOverlay, type OverlayEvent } from '../../modules/vass-overlay';
import { log } from '../logging/remoteLogger';

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
}

const ConversationRuntimeContext = createContext<ConversationRuntimeValue | null>(null);

export function ConversationRuntimeProvider({ children }: { children: ReactNode }) {
  const { avatarId } = useAuth();
  const [sessionId, setSessionId] = useState<number | null>(null);
  const [sessionError, setSessionError] = useState<string | null>(null);
  const runtime = useVoiceChat(sessionId);
  const stateRef = useRef(runtime.state);
  const actionsRef = useRef(runtime);
  const resumeRequestedRef = useRef(false);
  const pausedForBackgroundRef = useRef(false);
  const restoredOverlayAudioRef = useRef(false);

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
