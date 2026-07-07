import { useCallback, useEffect, useRef, useState } from 'react';
import {
  createAudioPlayer,
  RecordingPresets,
  requestRecordingPermissionsAsync,
  setAudioModeAsync,
  useAudioRecorder,
} from 'expo-audio';
import { api, API_URL, sendMessage } from '../api/client';
import { speakToCompletion, stopSpeaking } from '../tts/systemSpeech';
import { useVad } from './useVad';
import { log } from '../logging/remoteLogger';

export type VoiceState = 'idle' | 'recording' | 'thinking' | 'speaking';

// android.audioSource defaults to MediaRecorder.AudioSource.MIC — raw,
// unprocessed input (verified in expo-audio's own AudioRecorder.kt) — unlike
// the web client's getUserMedia({ audio: { autoGainControl: true,
// noiseSuppression: true, echoCancellation: true } }) (frontend/js/yolo.js's
// acquireMicrophone). 'voice_communication' is the Android MediaRecorder
// source Android itself documents as taking advantage of echo cancellation
// and automatic gain control if available. Real-device logs from the VAD
// checkpoint confirm this works: speech reads -18 to -29dB smoothed, a
// healthy margin above the -35dB threshold.
const RECORDING_OPTIONS = {
  ...RecordingPresets.HIGH_QUALITY,
  isMeteringEnabled: true,
  android: { ...RecordingPresets.HIGH_QUALITY.android, audioSource: 'voice_communication' as const },
};

// Real turn-taking timings — yolo.js's exact values (frontend/js/yolo.js:52-54).
const CHECK_SILENCE_THRESHOLD_MS = 1200;
const RECHECK_INTERVAL_MS = 1800;
const MAX_SILENCE_CEILING_MS = 7000;

// Below this much active speech with no transcribable text ever produced,
// treat it as a cough/knock/click rather than a real utterance check-utterance
// just failed on — same bar as yolo.js's submitSpeech(). Only gates the
// ceiling fallback now (see finalizeAtCeiling): the normal check-based path
// doesn't need a local filter, Gemini's own completeness judgment (or simply
// producing no transcription) handles short/meaningless sounds.
const MIN_SPEECH_MS = 450;

// Pause after discarding a too-short sound before re-arming, so continuous
// background noise can't retrigger in a tight loop — mirrors yolo.js's
// identical 750ms cooldown in the same spot.
const DISCARD_COOLDOWN_MS = 750;

// Pre-synthesized "still listening" phrases — same files the web client
// plays (frontend/audio/fillers/, served as plain static files by nginx,
// no auth needed). Reassures the speaker Olga is still listening during a
// real thinking pause, without ending their turn.
const BACKCHANNEL_FILLERS = ['back-1.wav', 'back-2.wav', 'back-3.wav', 'back-4.wav', 'back-5.wav'].map(
  (f) => `${API_URL}/audio/fillers/${f}`
);

// Third mobile voice-loop increment (docs/react-native/BACKLOG.md Phase 1):
// real turn-taking via the existing /chat/check-utterance endpoint, on top
// of the VAD foundation. Not a literal port of yolo.js's snapshot-based
// design — expo-audio's AudioRecorder has no way to read a recording's
// bytes without stopping it first (unlike Web MediaRecorder's incremental
// chunk delivery, confirmed by reading AudioRecorder.kt directly: the only
// way to get a valid, parseable file is stopRecording(), which fully
// finalizes and releases the native recorder). So each completeness check
// here stops the current segment, immediately re-arms a fresh one for the
// continuation (useVad's own state persists across this, since
// active/recorder don't change — see useVad.ts), and accumulates each
// segment's transcription as TEXT rather than trying to stitch raw audio
// together. This actually matches yolo.js's own submitKnownText more
// closely than its snapshot mechanism does: once a completeness check has
// transcribed anything, the reference implementation sends that text
// directly too, with no separate audio upload for that turn.
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
  // Guards the various finalize/discard paths against overlapping — several
  // independent triggers (the silence ceiling, a completeness check saying
  // "complete", the manual override tap) can each try to finalize, so a
  // race between them needs an explicit lock rather than relying on one
  // gated button.
  const finalizingRef = useRef(false);
  // Unlike the mount effect below (which guards its own single run with a
  // local `cancelled` flag), armMic()/rearmRecorderOnly() are called from
  // several places with no cleanup of their own, so they need their own
  // unmount guard rather than relying on each caller to remember one.
  // Without this, a pending re-arm can call
  // prepareToRecordAsync()/record() on a recorder already released by
  // useReleasingSharedObject, or setState on an unmounted component.
  const mountedRef = useRef(true);

  // Turn-taking state — see triggerCompletenessCheck/finalizeWithText below.
  const pendingTextRef = useRef(''); // confirmed transcript accumulated across check cycles this turn
  const nextCheckAtRef = useRef(CHECK_SILENCE_THRESHOLD_MS); // silenceDurationMs threshold for the next check
  const checkInFlightRef = useRef(false);
  // True if speech resumed while a check was in flight — matches yolo.js's
  // own staleness guard ("only act on complete if nothing new was said
  // since this snapshot"), since by the time a stale check's answer
  // arrives the transcription it judged no longer covers the whole thought.
  const speechResumedSinceCheckRef = useRef(false);

  useEffect(() => {
    return () => {
      mountedRef.current = false;
      playerRef.current?.release();
      stopSpeaking();
    };
  }, []);

  // (Re)arms the mic for a brand-new turn: resets turn-taking state and
  // starts a fresh recording file so VAD can watch it from the moment
  // "idle" begins — matches yolo.js starting a brand-new MediaRecorder in
  // startUserListening() on every re-entry to IDLE, rather than only
  // starting to capture once speech is already detected.
  const armMic = useCallback(async () => {
    if (!mountedRef.current) return;
    pendingTextRef.current = '';
    nextCheckAtRef.current = CHECK_SILENCE_THRESHOLD_MS;
    checkInFlightRef.current = false;
    speechResumedSinceCheckRef.current = false;
    try {
      await recorder.prepareToRecordAsync();
      if (!mountedRef.current) return; // unmounted while awaiting prepare
      recorder.record();
      setMicArmed(true);
      setState('idle');
      log('debug', 'mic', 'armed');
    } catch (err) {
      if (!mountedRef.current) return;
      setMicArmed(false);
      const message = err instanceof Error ? err.message : String(err);
      setError(message);
      log('error', 'mic', 'arm failed', { error: message });
    }
  }, [recorder]);

  // Re-arms the underlying native recorder for a continuation segment
  // WITHOUT touching state/turn-taking accumulators — used mid-turn by
  // triggerCompletenessCheck, where we're still in the same conversational
  // turn, just starting a fresh file because expo-audio can't snapshot the
  // current one without stopping it.
  const rearmRecorderOnly = useCallback(async () => {
    if (!mountedRef.current) return;
    try {
      await recorder.prepareToRecordAsync();
      if (!mountedRef.current) return;
      recorder.record();
    } catch (err) {
      if (!mountedRef.current) return;
      log('error', 'mic', 'mid-turn rearm failed', { error: err instanceof Error ? err.message : String(err) });
      // Left un-recovered here on purpose — the next silence tick (or the
      // hard ceiling) will find a dead recorder, fail its own check
      // attempt, and finalize with whatever text has already been
      // accumulated rather than getting stuck.
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
          log('error', 'mic', 'permission denied');
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

  // Sends the accumulated transcript directly as text (no audio upload —
  // matches yolo.js's submitKnownText, which never persists audio for a
  // turn a completeness check already transcribed) and speaks the reply.
  const finalizeWithText = useCallback(async () => {
    if (finalizingRef.current) return;
    finalizingRef.current = true;
    const sid = sessionIdRef.current;
    const text = pendingTextRef.current.trim();
    pendingTextRef.current = '';

    if (!sid || !text) {
      finalizingRef.current = false;
      if (!sid) setError('Сессия ещё не готова');
      await armMic();
      return;
    }

    setState('thinking');
    setTranscript(text);
    setReply('');
    const turnStartedAt = Date.now();
    log('info', 'turn', 'finalize (text) start', { textLength: text.length });
    try {
      // Whatever mid-turn continuation recording is currently running gets
      // discarded here — same as yolo.js's submitKnownText clearing
      // currentRecordingChunks with no further use of them.
      try {
        await recorder.stop();
      } catch {
        // already stopped/inactive — nothing to clean up
      }

      const fullReply = await sendMessage(
        { sessionId: sid, message: text },
        { onChunk: (chunk) => setReply((prev) => prev + chunk) }
      );
      log('info', 'turn', 'reply received', { sendMs: Date.now() - turnStartedAt, replyLength: fullReply.length });

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
        } catch (err) {
          log('warn', 'tts', 'on-device speech failed, falling back to network TTS', {
            error: err instanceof Error ? err.message : String(err),
          });
          const audioUri = await api.synthesizeSpeech(fullReply);
          await playToCompletion(audioUri, playerRef);
        }
      }
      log('info', 'turn', 'finalize done', { totalMs: Date.now() - turnStartedAt });
    } catch (err) {
      const message = err instanceof Error ? err.message : String(err);
      setError(message);
      log('error', 'turn', 'finalize failed', { error: message, elapsedMs: Date.now() - turnStartedAt });
    } finally {
      finalizingRef.current = false;
      await armMic();
    }
  }, [recorder, armMic]);

  // Plays a random "still listening" phrase — fire-and-forget, doesn't
  // block or need awaiting, matches yolo.js's playStaticClip. Only called
  // when the speaker is confirmed still mid-pause (not stale — see caller).
  const playBackchannelFiller = useCallback(() => {
    const url = BACKCHANNEL_FILLERS[Math.floor(Math.random() * BACKCHANNEL_FILLERS.length)];
    const player = createAudioPlayer(url);
    const subscription = player.addListener('playbackStatusUpdate', (status) => {
      if (status.didJustFinish || status.error) {
        subscription.remove();
        player.release();
      }
    });
    player.play();
  }, []);

  // Snapshots the in-progress utterance (by necessity: stops the current
  // recording segment, then immediately starts a new one for the
  // continuation — see the file-level comment for why expo-audio can't do
  // this non-destructively) and asks the server whether the speaker sounds
  // done or is likely still talking/pausing to think.
  const triggerCompletenessCheck = useCallback(async () => {
    if (checkInFlightRef.current) return;
    checkInFlightRef.current = true;
    speechResumedSinceCheckRef.current = false;

    try {
      let uri: string | null = null;
      try {
        await recorder.stop();
        uri = recorder.uri;
      } catch (err) {
        log('warn', 'turn', 'stop for check failed', { error: err instanceof Error ? err.message : String(err) });
      }
      // Re-arm immediately so the mic keeps listening for a continuation
      // with minimal gap — this becomes the new "current" segment. Doesn't
      // await: no reason to delay the network check on the re-arm finishing.
      void rearmRecorderOnly();

      if (!uri) return;

      log('info', 'turn', 'completeness check start');
      const result = await api.checkUtteranceComplete(uri);
      const stale = speechResumedSinceCheckRef.current;
      log('info', 'turn', 'completeness check result', {
        complete: result.complete,
        transcriptionLength: result.transcription.length,
        stale,
      });

      if (result.transcription.trim()) {
        pendingTextRef.current = pendingTextRef.current
          ? `${pendingTextRef.current} ${result.transcription.trim()}`
          : result.transcription.trim();
      }

      if (stale) return; // they were already talking again by the time this resolved

      if (result.complete) {
        await finalizeWithText();
      } else {
        playBackchannelFiller();
      }
    } catch (err) {
      log('warn', 'turn', 'completeness check failed', { error: err instanceof Error ? err.message : String(err) });
      // Swallowed — the next recheck cycle (nextCheckAtRef already bumped
      // forward by the caller) or the hard ceiling will retry/finalize.
    } finally {
      checkInFlightRef.current = false;
    }
  }, [recorder, rearmRecorderOnly, finalizeWithText, playBackchannelFiller]);

  // Hard-ceiling fallback: one last transcribe attempt on whatever's
  // currently in flight (so the final segment isn't silently dropped just
  // because the ceiling landed between recheck cycles), then finalize
  // regardless of what that check judges.
  const finalizeAtCeiling = useCallback(
    async (activeSpeechMs: number) => {
      if (finalizingRef.current || checkInFlightRef.current) return;
      log('warn', 'turn', 'silence ceiling reached', {
        activeSpeechMs,
        pendingTextSoFar: pendingTextRef.current.length,
      });

      try {
        await recorder.stop();
        const uri = recorder.uri;
        if (uri) {
          try {
            const result = await api.checkUtteranceComplete(uri);
            if (result.transcription.trim()) {
              pendingTextRef.current = pendingTextRef.current
                ? `${pendingTextRef.current} ${result.transcription.trim()}`
                : result.transcription.trim();
            }
          } catch (err) {
            log('warn', 'turn', 'final check at ceiling failed', {
              error: err instanceof Error ? err.message : String(err),
            });
          }
        }
      } catch {
        // already stopped/inactive — nothing to clean up
      }

      if (pendingTextRef.current.trim()) {
        await finalizeWithText();
        return;
      }

      log('info', 'turn', activeSpeechMs < MIN_SPEECH_MS ? 'discarding as noise at ceiling' : 'discarding — no transcribable text', {
        activeSpeechMs,
      });
      pendingTextRef.current = '';
      nextCheckAtRef.current = CHECK_SILENCE_THRESHOLD_MS;
      setTimeout(() => armMic(), DISCARD_COOLDOWN_MS);
    },
    [recorder, finalizeWithText, armMic]
  );

  const handleSilenceTick = useCallback(
    (silenceDurationMs: number, activeSpeechMs: number) => {
      if (silenceDurationMs >= MAX_SILENCE_CEILING_MS) {
        finalizeAtCeiling(activeSpeechMs);
      } else if (silenceDurationMs >= nextCheckAtRef.current && !checkInFlightRef.current) {
        nextCheckAtRef.current = silenceDurationMs + RECHECK_INTERVAL_MS;
        triggerCompletenessCheck();
      }
    },
    [finalizeAtCeiling, triggerCompletenessCheck]
  );

  const handleSpeechResume = useCallback(() => {
    speechResumedSinceCheckRef.current = true;
  }, []);

  useVad({
    recorder,
    active: micArmed && (state === 'idle' || state === 'recording'),
    onSpeechStart: () => setState('recording'),
    onSpeechResume: handleSpeechResume,
    onSilenceTick: handleSilenceTick,
  });

  // Manual override: force-finalize whatever's been captured so far,
  // regardless of what VAD/turn-taking currently think — same "one more
  // check, then finalize regardless" shape as the hard ceiling, just
  // triggered by a tap instead of 7 seconds of silence. A safety valve for
  // on-device VAD/turn-taking that hasn't been calibrated against a real
  // microphone yet — mirrors yolo.js's force-submit affordance on its
  // yolo-speak-now-btn, scoped down to what this increment covers (no
  // barge-in tap yet — that's a later Phase 1 item).
  const forceFinalize = useCallback(() => {
    if (state === 'idle' || state === 'recording') finalizeAtCeiling(0);
  }, [state, finalizeAtCeiling]);

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
