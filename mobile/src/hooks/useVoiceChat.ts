import { useCallback, useEffect, useRef, useState } from 'react';
import {
  createAudioPlayer,
  RecordingPresets,
  requestRecordingPermissionsAsync,
  setAudioModeAsync,
  useAudioRecorder,
} from 'expo-audio';
import { api, sendMessage } from '../api/client';
import { speakToCompletion, stopSpeaking } from '../tts/systemSpeech';
import { useVad } from './useVad';

export type VoiceState = 'idle' | 'recording' | 'thinking' | 'speaking';

const RECORDING_OPTIONS = { ...RecordingPresets.HIGH_QUALITY, isMeteringEnabled: true };

// Placeholder fixed pause before auto-submitting — replaced by real
// completeness-check timing (server already has /chat/check-utterance) in
// the next Phase 1 increment. Picked between yolo.js's first completeness
// check (1200ms) and its hard ceiling (7000ms) as a single-tier stand-in.
const SILENCE_TIMEOUT_MS = 1500;

// Below this much active speech, treat it as a cough/knock/click rather than
// a real utterance — same bar and reasoning as yolo.js's submitSpeech().
const MIN_SPEECH_MS = 450;

// Pause after discarding a too-short sound before re-arming, so continuous
// background noise can't retrigger in a tight loop — mirrors yolo.js's
// identical 750ms cooldown in the same spot.
const DISCARD_COOLDOWN_MS = 750;

// Second mobile voice-loop increment (docs/react-native/BACKLOG.md Phase 1):
// on-device VAD replaces tap-to-record — the mic arms once and stays
// recording continuously, the same way yolo.js keeps a live MediaRecorder
// running from IDLE onward so the first ~250ms of speech is never missed.
// Turn-taking here is still a single fixed silence timeout, not yet the
// real completeness-check/backchannel logic yolo.js uses — that's the next
// increment, layered on top of this once this foundation is confirmed
// working on a real device (see BUILD-WSL.md — I can build and type-check
// this, but not exercise a live microphone myself).
export function useVoiceChat(sessionId: number | null) {
  const [state, setState] = useState<VoiceState>('idle');
  const [micArmed, setMicArmed] = useState(false);
  const [transcript, setTranscript] = useState('');
  const [reply, setReply] = useState('');
  const [error, setError] = useState<string | null>(null);
  const recorder = useAudioRecorder(RECORDING_OPTIONS);
  const playerRef = useRef<ReturnType<typeof createAudioPlayer> | null>(null);
  const sessionIdRef = useRef(sessionId);
  sessionIdRef.current = sessionId;
  // Guards finalizeTurn/discardAndRearm against overlapping — unlike the old
  // single-button tap flow, either the VAD timer or the manual override tap
  // can now trigger a finalize, so a race between the two needs an explicit
  // lock rather than relying on one gated button.
  const finalizingRef = useRef(false);

  useEffect(() => {
    return () => {
      playerRef.current?.release();
      stopSpeaking();
    };
  }, []);

  // (Re)arms the mic for the next turn: starts a fresh recording file so VAD
  // can watch it from the moment "idle" begins — matches yolo.js starting a
  // brand-new MediaRecorder in startUserListening() on every re-entry to
  // IDLE, rather than only starting to capture once speech is already
  // detected.
  const armMic = useCallback(async () => {
    try {
      await recorder.prepareToRecordAsync();
      recorder.record();
      setMicArmed(true);
      setState('idle');
    } catch (err) {
      setMicArmed(false);
      setError(err instanceof Error ? err.message : String(err));
    }
  }, [recorder]);

  // One-time setup: mic permission + audio mode, then arm for the first turn.
  useEffect(() => {
    if (!sessionId) return;
    let cancelled = false;
    (async () => {
      setError(null);
      try {
        const permission = await requestRecordingPermissionsAsync();
        if (!permission.granted) {
          setError('Нет доступа к микрофону');
          return;
        }
        await setAudioModeAsync({ playsInSilentMode: true, allowsRecording: true, interruptionMode: 'doNotMix' });
        if (cancelled) return;
        await armMic();
      } catch (err) {
        if (!cancelled) setError(err instanceof Error ? err.message : String(err));
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [sessionId, armMic]);

  const finalizeTurn = useCallback(async () => {
    if (finalizingRef.current) return;
    finalizingRef.current = true;
    const sid = sessionIdRef.current;
    if (!sid) {
      setError('Сессия ещё не готова');
      finalizingRef.current = false;
      return;
    }
    setState('thinking');
    setTranscript('');
    setReply('');
    try {
      await recorder.stop();
      const uri = recorder.uri;
      if (!uri) throw new Error('Запись не найдена');

      const { fileName } = await api.uploadAudio(uri);
      const fullReply = await sendMessage(
        { sessionId: sid, message: '', audioFileName: fileName },
        {
          onTranscription: setTranscript,
          onChunk: (chunk) => setReply((prev) => prev + chunk),
        }
      );

      if (fullReply.trim()) {
        setState('speaking');
        // System TTS (expo-speech) is the primary path — instant, no network
        // hop. Its failure modes are all recoverable in JS (missing Russian
        // voice, native "text too long" rejection despite our own chunking,
        // a transient engine error) so any of them falls back to the
        // existing buffered server-Piper path rather than the reply going
        // silent.
        try {
          await speakToCompletion(fullReply);
        } catch {
          const audioUri = await api.synthesizeSpeech(fullReply);
          await playToCompletion(audioUri, playerRef);
        }
      }
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      finalizingRef.current = false;
      await armMic();
    }
  }, [recorder, armMic]);

  const discardAndRearm = useCallback(async () => {
    if (finalizingRef.current) return;
    finalizingRef.current = true;
    try {
      await recorder.stop();
    } catch {
      // already stopped/never started — nothing to clean up
    }
    setTimeout(() => {
      finalizingRef.current = false;
      armMic();
    }, DISCARD_COOLDOWN_MS);
  }, [recorder, armMic]);

  const handleSilenceTimeout = useCallback(
    (activeSpeechMs: number) => {
      if (activeSpeechMs < MIN_SPEECH_MS) {
        discardAndRearm();
      } else {
        finalizeTurn();
      }
    },
    [discardAndRearm, finalizeTurn]
  );

  useVad({
    recorder,
    active: micArmed && (state === 'idle' || state === 'recording'),
    onSpeechStart: () => setState('recording'),
    onSilenceTimeout: handleSilenceTimeout,
    silenceMs: SILENCE_TIMEOUT_MS,
  });

  // Manual override: force-finalize whatever's been captured so far,
  // regardless of what VAD currently thinks. A safety valve for on-device
  // VAD sensitivity that hasn't been calibrated against a real microphone
  // yet (see BACKLOG.md's VAD checkpoint) — mirrors yolo.js's force-submit
  // affordance on its yolo-speak-now-btn, scoped down to what this
  // increment covers (no barge-in tap yet — that's a later Phase 1 item).
  const forceFinalize = useCallback(() => {
    if (state === 'idle' || state === 'recording') finalizeTurn();
  }, [state, finalizeTurn]);

  return { state, transcript, reply, error, forceFinalize };
}

// Generous cap on how long a synthesized reply could plausibly run — a
// safety net so a broken TTS file (bad decode, no didJustFinish event)
// can't leave the UI stuck in 'speaking' forever with the mic disabled.
const MAX_PLAYBACK_MS = 60_000;

function playToCompletion(
  uri: string,
  playerRef: React.MutableRefObject<ReturnType<typeof createAudioPlayer> | null>
): Promise<void> {
  return new Promise((resolve) => {
    const player = createAudioPlayer(uri);
    playerRef.current = player;

    const finish = () => {
      clearTimeout(timeoutId);
      subscription.remove();
      player.release();
      if (playerRef.current === player) playerRef.current = null;
      resolve();
    };

    const timeoutId = setTimeout(finish, MAX_PLAYBACK_MS);
    const subscription = player.addListener('playbackStatusUpdate', (status) => {
      if (status.didJustFinish || status.error) finish();
    });
    player.play();
  });
}
