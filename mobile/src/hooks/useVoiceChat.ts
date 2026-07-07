import { useCallback, useEffect, useRef, useState } from 'react';
import {
  createAudioPlayer,
  RecordingPresets,
  requestRecordingPermissionsAsync,
  setAudioModeAsync,
  useAudioRecorder,
} from 'expo-audio';
import { api, API_URL, sendMessage } from '../api/client';
import { interruptSpeaking, speakToCompletion, stopSpeaking } from '../tts/systemSpeech';
import { DEFAULT_THRESHOLD_DB, useVad } from './useVad';
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
// healthy margin above the -35dB threshold. Echo cancellation specifically
// also matters for barge-in below — it's what should keep the recorder from
// hearing Olga's own TTS output as if it were the user interrupting.
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
// producing no transcription) handles short/meaningless sounds. Also reused
// by shadow-capture below — yolo.js uses the identical 450ms bar there too.
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

// Barge-in tuning — yolo.js's exact frame count (10, ~500ms — double the
// normal 5-frame/250ms onset bar) so a stray sound during playback needs to
// be clearly sustained speech, not a click, before cutting Olga off.
// yolo.js raises its threshold by a 2.5x RMS multiplier during SPEAKING;
// dBFS is already logarithmic, so the equivalent additive shift is
// 20*log10(2.5) ≈ 8dB (not a literal ports of the multiplier itself, which
// doesn't translate directly onto a log scale).
const INTERRUPTION_FRAMES = 10;
const INTERRUPTION_THRESHOLD_DB = DEFAULT_THRESHOLD_DB + 8;

// Shadow-capture tuning — yolo.js's exact SILENCE_TIMEOUT (distinct from
// turn-taking's CHECK_SILENCE_THRESHOLD_MS above — shadow-capture judges
// "did they actually keep talking" locally, no completeness-check involved,
// so it uses a shorter, simpler silence bar).
const SHADOW_SILENCE_TIMEOUT_MS = 1000;

// Fifth mobile voice-loop increment (docs/react-native/BACKLOG.md Phase 1,
// the last one): shadow-capture. While THINKING (waiting on the LLM), a
// SECOND independent AudioRecorder listens in parallel without touching the
// in-flight request — confirmed safe to build via expo-audio's own
// AudioModule.kt: recorders live in a ConcurrentHashMap with no singleton
// constraint, and the module's own lifecycle hooks
// (OnActivityEntersBackground/Foreground) already iterate and manage
// multiple concurrent instances. Only once the shadow recording is
// confirmed as real, sustained speech (the same 450ms bar as everywhere
// else in this file) does anything happen: the in-flight response is
// aborted and the fuller, combined utterance is sent instead — so a knock
// or background noise during THINKING never disturbs a perfectly good
// answer.
//
// Third and fourth mobile voice-loop increments: real turn-taking via the
// existing /chat/check-utterance endpoint, and barge-in on top of it. Not a
// literal port of yolo.js's snapshot-based design — expo-audio's
// AudioRecorder has no way to read a recording's bytes without stopping it
// first (unlike Web MediaRecorder's incremental chunk delivery, confirmed
// by reading AudioRecorder.kt directly: the only way to get a valid file is
// stopRecording(), which fully finalizes and releases the native recorder).
// So each completeness check here stops the current segment, immediately
// re-arms a fresh one for the continuation (useVad's own state persists
// across this, since active/recorder don't change — see useVad.ts), and
// accumulates each segment's transcription as TEXT rather than trying to
// stitch raw audio together. This actually matches yolo.js's own
// submitKnownText more closely than its snapshot mechanism does: once a
// completeness check has transcribed anything, the reference implementation
// sends that text directly too, with no separate audio upload for that turn.
//
// Barge-in requires the mic to be live and VAD watching for the ENTIRE
// 'speaking' duration, not just after — so the recorder now arms BEFORE TTS
// starts (see finalizeWithText), not after. recorderLiveRef tracks whether
// the native recorder is currently prepared+recording so armMic/
// rearmRecorderOnly/stopRecorderIfLive can each be called unconditionally
// wherever they're needed without worrying about double-arming (which
// expo-audio throws AudioRecorderAlreadyPreparedException for — confirmed in
// AudioRecorder.kt's prepareRecording) or double-stopping.
export function useVoiceChat(sessionId: number | null) {
  const [state, setState] = useState<VoiceState>('idle');
  const [micArmed, setMicArmed] = useState(false);
  const [transcript, setTranscript] = useState('');
  const [reply, setReply] = useState('');
  const [error, setError] = useState<string | null>(null);
  const recorder = useAudioRecorder(RECORDING_OPTIONS);
  const shadowRecorder = useAudioRecorder(RECORDING_OPTIONS);
  const playerRef = useRef<ReturnType<typeof createAudioPlayer> | null>(null);
  const sessionIdRef = useRef(sessionId);
  sessionIdRef.current = sessionId;
  // Mirrors `state` into a ref so useVad's stable onSpeechStart callback can
  // read the CURRENT state (barge-in only applies during 'speaking') without
  // needing to be recreated — and therefore without needing to be in
  // useVad's effect dependency array — on every state change.
  const stateRef = useRef(state);
  stateRef.current = state;
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
  // Whether the native recorder is currently prepared+recording — see the
  // file-level comment. Set by armMic/rearmRecorderOnly, cleared by
  // stopRecorderIfLive.
  const recorderLiveRef = useRef(false);
  // Same idea, for the shadow recorder.
  const shadowRecorderLiveRef = useRef(false);
  // Set on a confirmed barge-in, checked in finalizeWithText's finally block
  // so it skips the normal end-of-turn armMic() — a barge-in already
  // transitioned straight to 'recording' with its own turn-taking state
  // freshly underway (see handleSpeechStart), and armMic() would incorrectly
  // reset that out from under it.
  const bargedInRef = useRef(false);
  // The in-flight sendMessage's abort handle, live only while finalizeWithText
  // is actually awaiting a response — commitShadowContinuation (below) calls
  // .abort() on it once a shadow recording is confirmed as a real
  // continuation, so finalizeWithText's own catch block can pick up and send
  // the fuller, combined utterance instead of answering the incomplete first
  // half. null whenever nothing is in flight, so a stray abort() is a no-op.
  const activeSendAbortControllerRef = useRef<AbortController | null>(null);
  // Set by commitShadowContinuation right before it aborts, read (and
  // cleared) by finalizeWithText's own catch block — see the comment there
  // for why the continuation is handled INSIDE that catch rather than by a
  // separate function racing for finalizingRef.
  const shadowContinuationUriRef = useRef<string | null>(null);
  // Guards handleShadowSilenceTick's discard-as-noise branch from re-logging
  // every 50ms tick for as long as 'thinking' stays silent after a short
  // blip — useVad's onSilenceTick has no "already handled" concept of its
  // own (see useVad.ts), and activeSpeechMs stays fixed once speech stops.
  // Reset on shadow speech resuming and on a fresh arm, so a genuinely new
  // short sound later gets its own log.
  const shadowDiscardLoggedRef = useRef(false);

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

  // Re-arms the underlying native recorder — a no-op if it's already live
  // (e.g. armed ahead of TTS playback for barge-in, then reached here again
  // via the normal end-of-turn path) so callers never need to know whether
  // someone else already armed it first.
  const rearmRecorderOnly = useCallback(async () => {
    if (!mountedRef.current || recorderLiveRef.current) return;
    try {
      await recorder.prepareToRecordAsync();
      if (!mountedRef.current) return;
      recorder.record();
      recorderLiveRef.current = true;
    } catch (err) {
      if (!mountedRef.current) return;
      log('error', 'mic', 'rearm failed', { error: err instanceof Error ? err.message : String(err) });
      // Left un-recovered here on purpose — the next silence tick (or the
      // hard ceiling) will find a dead recorder, fail its own check
      // attempt, and finalize with whatever text has already been
      // accumulated rather than getting stuck.
    }
  }, [recorder]);

  // Stops the native recorder if it's currently live, returning its uri (or
  // null if it wasn't live / stop failed) — a no-op-safe counterpart to
  // rearmRecorderOnly so callers don't need to track native state themselves.
  const stopRecorderIfLive = useCallback(async (): Promise<string | null> => {
    if (!recorderLiveRef.current) return null;
    recorderLiveRef.current = false;
    try {
      await recorder.stop();
      return recorder.uri || null;
    } catch (err) {
      log('warn', 'mic', 'stop failed', { error: err instanceof Error ? err.message : String(err) });
      return null;
    }
  }, [recorder]);

  // Same pair, for the shadow recorder — armed/disarmed by the 'thinking'
  // effect below instead of by turn-taking logic.
  const armShadowRecorder = useCallback(async () => {
    if (!mountedRef.current || shadowRecorderLiveRef.current) return;
    try {
      await shadowRecorder.prepareToRecordAsync();
      if (!mountedRef.current) return;
      shadowRecorder.record();
      shadowRecorderLiveRef.current = true;
      shadowDiscardLoggedRef.current = false;
      log('debug', 'shadow', 'armed');
    } catch (err) {
      if (!mountedRef.current) return;
      log('error', 'shadow', 'arm failed', { error: err instanceof Error ? err.message : String(err) });
    }
  }, [shadowRecorder]);

  const stopShadowRecorderIfLive = useCallback(async (): Promise<string | null> => {
    if (!shadowRecorderLiveRef.current) return null;
    shadowRecorderLiveRef.current = false;
    try {
      await shadowRecorder.stop();
      return shadowRecorder.uri || null;
    } catch (err) {
      log('warn', 'shadow', 'stop failed', { error: err instanceof Error ? err.message : String(err) });
      return null;
    }
  }, [shadowRecorder]);

  // Arms the shadow recorder for the whole 'thinking' phase, discards it
  // (no commit) the moment we leave 'thinking' any other way — matches
  // yolo.js's startVadLoop calling stopShadowCapture() as soon as
  // currentState stops being THINKING.
  useEffect(() => {
    if (state === 'thinking') {
      void armShadowRecorder();
    } else {
      void stopShadowRecorderIfLive();
    }
  }, [state, armShadowRecorder, stopShadowRecorderIfLive]);

  // (Re)arms for a brand-new turn: resets turn-taking state and starts a
  // fresh recording file so VAD can watch it from the moment "idle" begins
  // — matches yolo.js starting a brand-new MediaRecorder in
  // startUserListening() on every re-entry to IDLE, rather than only
  // starting to capture once speech is already detected.
  const armMic = useCallback(async () => {
    if (!mountedRef.current) return;
    pendingTextRef.current = '';
    nextCheckAtRef.current = CHECK_SILENCE_THRESHOLD_MS;
    checkInFlightRef.current = false;
    speechResumedSinceCheckRef.current = false;
    bargedInRef.current = false;
    shadowContinuationUriRef.current = null;
    await rearmRecorderOnly();
    if (!mountedRef.current || !recorderLiveRef.current) return;
    setMicArmed(true);
    setState('idle');
    log('debug', 'mic', 'armed');
  }, [rearmRecorderOnly]);

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

  // Shared tail: given a reply just received (however it arrived — text-
  // based turn-taking, shadow-capture's audio continuation, or the hard
  // ceiling), speaks it and wraps up. Kept separate so all of those callers
  // can't drift out of sync on the barge-in-aware TTS handling.
  const speakReplyAndWrapUp = useCallback(
    async (fullReply: string, turnStartedAt: number) => {
      if (fullReply.trim()) {
        // Arm BEFORE speaking, not after — barge-in needs VAD watching for
        // the entire playback, not just once it's done. useVad's `active`
        // below already includes 'speaking', so as soon as setState fires
        // the tick loop starts watching this live recorder.
        await rearmRecorderOnly();
        setState('speaking');
        // System TTS (expo-speech) is the primary path — instant, no network
        // hop. A barge-in interruption (interruptSpeaking(), see
        // handleSpeechStart/forceFinalize below) makes speakToCompletion
        // return normally, not throw — systemSpeech.ts's own
        // interruptRequested flag breaks its chunk loop cleanly, so this
        // catch never sees a barge-in at all, only genuine on-device TTS
        // failures (missing Russian voice, native "text too long" rejection
        // despite our own chunking, a transient engine error) — all of
        // which still fall back to the existing buffered server-Piper path
        // rather than the reply going silent.
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
    },
    [rearmRecorderOnly]
  );

  // Sends the accumulated transcript directly as text (no audio upload —
  // matches yolo.js's submitKnownText, which never persists audio for a
  // turn a completeness check already transcribed) and speaks the reply.
  const finalizeWithText = useCallback(async () => {
    if (finalizingRef.current) return;
    finalizingRef.current = true;
    const sid = sessionIdRef.current;
    const text = pendingTextRef.current.trim();
    pendingTextRef.current = '';
    // Reset unconditionally, not just on a success path — this call itself
    // can be the turn AFTER a barge-in (the interrupting utterance's own
    // finalize), and leaving a stale `true` here would make the finally
    // block below skip armMic() for an entirely unrelated turn — state
    // stuck with no recovery. Caught by independent review of the barge-in PR.
    bargedInRef.current = false;

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
    const abortController = new AbortController();
    activeSendAbortControllerRef.current = abortController;
    try {
      // Whatever mid-turn continuation recording is currently running gets
      // discarded here — same as yolo.js's submitKnownText clearing
      // currentRecordingChunks with no further use of them.
      await stopRecorderIfLive();

      const fullReply = await sendMessage(
        { sessionId: sid, message: text },
        { onChunk: (chunk) => setReply((prev) => prev + chunk) },
        abortController.signal
      );
      log('info', 'turn', 'reply received', { sendMs: Date.now() - turnStartedAt, replyLength: fullReply.length });
      await speakReplyAndWrapUp(fullReply, turnStartedAt);
    } catch (err) {
      // A shadow-capture commit aborts this exact request on purpose (see
      // commitShadowContinuation) and stashes the confirmed continuation's
      // uri here — pick it up and send the fuller, combined utterance
      // ourselves rather than treating this as a failure. Handled inline
      // (not as a separate function) specifically so it runs under THIS
      // call's already-held finalizingRef lock instead of racing a second
      // function for it — the abort() call doesn't synchronously reject
      // this await, so a separate function trying to acquire the lock
      // itself could easily run before this catch block does.
      const shadowUri = shadowContinuationUriRef.current;
      shadowContinuationUriRef.current = null;
      if (shadowUri) {
        log('info', 'turn', 'aborted for shadow-capture continuation, sending combined utterance');
        const continuationStartedAt = Date.now();
        try {
          setTranscript('');
          setReply('');
          const { fileName } = await api.uploadAudio(shadowUri);
          const fullReply = await sendMessage(
            { sessionId: sid, message: '', audioFileName: fileName },
            { onTranscription: setTranscript, onChunk: (chunk) => setReply((prev) => prev + chunk) }
          );
          log('info', 'turn', 'reply received (shadow continuation)', {
            sendMs: Date.now() - continuationStartedAt,
            replyLength: fullReply.length,
          });
          await speakReplyAndWrapUp(fullReply, continuationStartedAt);
        } catch (err2) {
          const message = err2 instanceof Error ? err2.message : String(err2);
          setError(message);
          log('error', 'turn', 'shadow continuation failed', { error: message });
        }
      } else {
        const message = err instanceof Error ? err.message : String(err);
        setError(message);
        log('error', 'turn', 'finalize failed', { error: message, elapsedMs: Date.now() - turnStartedAt });
      }
    } finally {
      activeSendAbortControllerRef.current = null;
      finalizingRef.current = false;
      // Unconditional, not just on the catch path above: a shadow commit
      // can land after this turn's sendMessage already resolved
      // successfully (the abort() it fires is then a no-op on an
      // already-settled request) — in that case the catch block above
      // never runs and never reads/clears this ref. Left alone, a stale
      // URI from THIS turn would get picked up by some unrelated LATER
      // turn's catch block (e.g. a plain network error), sending that
      // turn's reply against old, irrelevant audio. Found by independent
      // review of the shadow-capture PR.
      shadowContinuationUriRef.current = null;
      // A confirmed barge-in already transitioned to 'recording' with the
      // interrupting speech's own onset already detected (see
      // handleSpeechStart) — armMic() here would incorrectly reset that
      // in-progress turn's state (pendingText/nextCheckAt/etc.) out from
      // under it. Only re-arm normally when this turn actually finished on
      // its own.
      if (!bargedInRef.current) await armMic();
    }
  }, [armMic, stopRecorderIfLive, speakReplyAndWrapUp]);

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
      const uri = await stopRecorderIfLive();
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
  }, [rearmRecorderOnly, stopRecorderIfLive, finalizeWithText, playBackchannelFiller]);

  // Guards finalizeAtCeiling against re-entry — independent of finalizingRef
  // (owned by finalizeWithText's own lifecycle; pre-setting it here would
  // make finalizeWithText's own guard check reject the call finalizeAtCeiling
  // makes into it below) and independent of checkInFlightRef (a different
  // concern — triggerCompletenessCheck's periodic rechecks). Without this,
  // nothing stops a second VAD tick 50ms later — while the first call is
  // still awaiting recorder.stop()/checkUtteranceComplete — from re-entering
  // (the ceiling condition stays true every tick past 7000ms), duplicating
  // both the network call and the accumulated text. Caught by independent
  // review of the turn-taking PR.
  const ceilingFinalizeInFlightRef = useRef(false);

  // Hard-ceiling fallback: one last transcribe attempt on whatever's
  // currently in flight (so the final segment isn't silently dropped just
  // because the ceiling landed between recheck cycles), then finalize
  // regardless of what that check judges.
  const finalizeAtCeiling = useCallback(
    async (activeSpeechMs: number) => {
      if (finalizingRef.current || checkInFlightRef.current || ceilingFinalizeInFlightRef.current) {
        log('debug', 'turn', 'finalizeAtCeiling skipped — already in flight');
        return;
      }
      ceilingFinalizeInFlightRef.current = true;
      try {
        log('warn', 'turn', 'silence ceiling reached', {
          activeSpeechMs,
          pendingTextSoFar: pendingTextRef.current.length,
        });

        const uri = await stopRecorderIfLive();
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
      } finally {
        ceilingFinalizeInFlightRef.current = false;
      }
    },
    [stopRecorderIfLive, finalizeWithText, armMic]
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

  // While 'speaking', a confirmed onset is a barge-in: stop Olga mid-word
  // and hand the turn straight back to the user — mirrors yolo.js's
  // triggerInterruption() going directly to LISTENING, not IDLE, since VAD
  // already confirmed real sustained speech (10 frames at the boosted
  // threshold, not just the normal 5) to get here at all. No network abort
  // needed: by 'speaking', sendMessage() has already resolved — there's
  // nothing left in flight except the TTS playback itself.
  const handleSpeechStart = useCallback(() => {
    if (stateRef.current === 'speaking') {
      bargedInRef.current = true;
      log('info', 'turn', 'barge-in: interrupted assistant');
      interruptSpeaking();
      setState('recording');
    } else {
      setState('recording');
    }
  }, []);

  const isSpeaking = state === 'speaking';
  useVad({
    recorder,
    active: micArmed && (state === 'idle' || state === 'recording' || isSpeaking),
    thresholdDb: isSpeaking ? INTERRUPTION_THRESHOLD_DB : undefined,
    startFrames: isSpeaking ? INTERRUPTION_FRAMES : undefined,
    onSpeechStart: handleSpeechStart,
    onSpeechResume: handleSpeechResume,
    onSilenceTick: handleSilenceTick,
  });

  // Confirmed as a real continuation (not yet committed — see the guard at
  // the top): abort the in-flight response and stash this recording's uri
  // for finalizeWithText's own catch block to pick up. Deliberately does
  // NOT try to acquire finalizingRef itself — see finalizeWithText's catch
  // comment for why that would race against the abort actually propagating.
  const commitShadowContinuation = useCallback(
    async (activeSpeechMs: number) => {
      if (!shadowRecorderLiveRef.current) return; // already committed by an earlier tick, or never armed
      log('info', 'turn', 'shadow-capture: confirmed continuation', { activeSpeechMs });
      const uri = await stopShadowRecorderIfLive();
      if (!uri) return;
      shadowContinuationUriRef.current = uri;
      activeSendAbortControllerRef.current?.abort();
    },
    [stopShadowRecorderIfLive]
  );

  const handleShadowSilenceTick = useCallback(
    (silenceDurationMs: number, activeSpeechMs: number) => {
      if (silenceDurationMs < SHADOW_SILENCE_TIMEOUT_MS) return;
      if (activeSpeechMs >= MIN_SPEECH_MS) {
        commitShadowContinuation(activeSpeechMs);
      } else if (!shadowDiscardLoggedRef.current) {
        // Too short to be real speech (knock/click) — matches yolo.js's
        // tickShadowCapture discarding and continuing to shadow rather than
        // tearing the recorder down. No state reset beyond the log guard
        // above: useVad's own debounce state naturally keeps tracking (this
        // hook has no built-in "already handled this tick" concept beyond
        // what the caller — us — does), so a genuinely new sound right
        // after a discarded blip is still correctly picked up by a later
        // tick.
        shadowDiscardLoggedRef.current = true;
        log('debug', 'shadow', 'discarding short shadow sound', { activeSpeechMs });
      }
    },
    [commitShadowContinuation]
  );

  useVad({
    recorder: shadowRecorder,
    active: state === 'thinking',
    onSpeechStart: () => log('debug', 'shadow', 'shadow speech detected'),
    onSpeechResume: () => {
      shadowDiscardLoggedRef.current = false;
    },
    onSilenceTick: handleShadowSilenceTick,
  });

  // Manual override: force-finalize whatever's been captured so far,
  // regardless of what VAD/turn-taking currently think — same "one more
  // check, then finalize regardless" shape as the hard ceiling, just
  // triggered by a tap instead of 7 seconds of silence. Also doubles as a
  // manual interruption while speaking, mirroring yolo.js's
  // yolo-speak-now-btn ("Перебить" during SPEAKING) — a safety valve for
  // on-device VAD/turn-taking that hasn't been calibrated against a real
  // microphone yet.
  const forceFinalize = useCallback(() => {
    if (state === 'speaking') {
      bargedInRef.current = true;
      log('info', 'turn', 'barge-in: manual interruption tap');
      interruptSpeaking();
      setState('recording');
    } else if (state === 'idle' || state === 'recording') {
      finalizeAtCeiling(0);
    }
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
