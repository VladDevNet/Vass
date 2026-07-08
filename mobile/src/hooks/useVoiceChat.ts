import { useCallback, useEffect, useRef, useState } from 'react';
import {
  createAudioPlayer,
  RecordingPresets,
  requestRecordingPermissionsAsync,
  setAudioModeAsync,
  useAudioRecorder,
} from 'expo-audio';
import { api, sendMessage } from '../api/client';
import { interruptSpeaking, speakBackchannel, speakToCompletion, stopSpeaking } from '../tts/systemSpeech';
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

// The ONLY silence gate left in this design — yolo.js's original value
// (frontend/js/yolo.js:52), kept even though the completeness-check it used
// to gate is gone (see the file-level comment below for why).
const CHECK_SILENCE_THRESHOLD_MS = 1200;

// Below this much active speech, treat a pause as a cough/knock/click, not a
// real utterance — matches yolo.js's submitSpeech() bar. Gates every segment
// (first AND continuations) before it's sent: without a completeness-check
// step to silently absorb noise server-side, a cough would otherwise go
// straight to the main model and come back as a spoken non-sequitur.
const MIN_SPEECH_MS = 450;

// Pause after discarding a too-short sound before re-arming, so continuous
// background noise can't retrigger in a tight loop — mirrors yolo.js's
// identical 750ms cooldown in the same spot.
const DISCARD_COOLDOWN_MS = 750;

// Barge-in tuning — yolo.js's exact frame count (10, ~500ms — double the
// normal 5-frame/250ms onset bar) so a stray sound during playback needs to
// be clearly sustained speech, not a click, before cutting Olga off.
// yolo.js raises its threshold by a 2.5x RMS multiplier during SPEAKING;
// dBFS is already logarithmic, so the equivalent additive shift is
// 20*log10(2.5) ≈ 8dB (not a literal ports of the multiplier itself, which
// doesn't translate directly onto a log scale).
const INTERRUPTION_FRAMES = 10;
const INTERRUPTION_THRESHOLD_DB = DEFAULT_THRESHOLD_DB + 8;

// Continuation-confirmation timing — yolo.js's exact SILENCE_TIMEOUT. Once
// broadly "shadow capture only during THINKING," now the single mechanism
// that confirms ANY segment's continuation (see the file-level comment).
const SHADOW_SILENCE_TIMEOUT_MS = 1000;

// Sanity floor for commitShadowContinuation — see shadowArmedAtRef's own
// comment for the production 400 this closes. A genuine continuation's real
// floor is closer to ~2s than a round number: useVad's exponential
// smoothing starts every reset at -160dB, so against continuous speech it
// takes ~8 ticks (~400ms) just to cross DEFAULT_THRESHOLD_DB, THEN 5 more
// ticks (~250ms) of onset debounce before hasSpoken flips — hasSpoken
// itself doesn't fire until ~600ms in. commitShadowContinuation is only
// ever called once activeSpeechMs has ALSO cleared MIN_SPEECH_MS (450ms),
// and only after SHADOW_SILENCE_TIMEOUT_MS (1000ms) of trailing silence on
// top of that — 600+450+1000 ≈ 2050ms minimum (independently re-derived
// and confirmed during PR #51's review). 500ms leaves wide margin below
// that real floor while safely rejecting the stale retrigger that
// motivated this constant, confirmed in production logs as firing ~25ms
// after a re-arm (armedForMs, measured at the exact point this constant
// gates — not to be confused with the ~160ms gap seen between THAT
// decision and sendSegment's own later log line, which is separately
// explained by the native recorder-stop round trip in between).
const MIN_SHADOW_ARM_MS = 500;

// Sixth mobile voice-loop increment: optimistic turn-taking, replacing the
// completeness-check round-trip (PR #38) with a direct send. Real-device
// latency logging (a full session of production timings) showed the
// dominant cost was never the on-device VAD/TTS — it was two sequential
// network round-trips per turn: first /chat/check-utterance (2-5.5s
// observed, just to ask "are they done?"), THEN the real /chat/send call
// (2-10s+, scaling with reply length) only after that came back "complete."
// That first round-trip bought silence-filling patience but nothing else —
// the reply itself doesn't start generating until the SECOND call anyway.
//
// New design: the instant CHECK_SILENCE_THRESHOLD_MS (1.2s) passes, stop
// the recorder and send that audio straight to /chat/send (no
// /chat/check-utterance at all) — betting optimistically that the pause
// means "done," while a backchannel filler plays immediately to fill the
// dead air. A second recorder (still named "shadow" — same mechanism as
// PR #40, just generalized: it now covers EVERY segment's continuation
// window, not only the literal LLM-thinking wait) keeps listening the whole
// time. If real speech resumes there, we do NOT abort the in-flight
// request (the abort-vs-catch-block race that shadow-capture's original
// design spent real effort routing around — see PR #40/#45's history) —
// we simply let it run to completion in the background and discard
// whatever it returns, then send the new segment as its own attempt. Which
// attempt actually gets spoken is decided by a monotonic generation counter
// (segmentGenerationRef) rather than a boolean flag: a boolean can't
// correctly track "am I still the current attempt" once more than one
// continuation happens in a row (a later attempt's reset would incorrectly
// un-cancel an earlier one still in flight) — the same class of bug this
// session's resetKey/micGeneration fix (PR #45) closed for useVad's state.
//
// Net effect: a turn that's genuinely finished after one pause now skips an
// entire network round-trip; a turn where the speaker keeps going costs an
// extra (discarded) model call instead of an extra (cheap) judgment call —
// a worthwhile trade since discarding is free from the user's perspective
// (nothing plays) while the old design's judgment call was pure latency
// with no other benefit.
//
// expo-audio still can't snapshot a recording without fully stopping it
// (AudioRecorder.kt — confirmed by direct source reading, see PR #38's
// history), so each segment is still its own separate native recording,
// same as before.
//
// Barge-in requires the mic to be live and VAD watching for the ENTIRE
// 'speaking' duration, not just after — so the recorder arms BEFORE TTS
// starts (see speakReplyAndWrapUp), not after. recorderLiveRef tracks
// whether the native recorder is currently prepared+recording so armMic/
// rearmRecorderOnly/stopRecorderIfLive can each be called unconditionally
// wherever they're needed without worrying about double-arming (which
// expo-audio throws AudioRecorderAlreadyPreparedException for — confirmed in
// AudioRecorder.kt's prepareRecording) or double-stopping.
export function useVoiceChat(sessionId: number | null) {
  const [state, setState] = useState<VoiceState>('idle');
  const [micArmed, setMicArmed] = useState(false);
  // Bumped on every successful armMic() — passed to the main useVad call as
  // resetKey (see its comment) so VAD's internal hasSpoken/speechStartAt
  // state resets on every new turn, not just when `active` itself flips
  // value. Without this, a turn that starts and ends entirely within
  // idle/recording never changes `active` (idle and recording are both "on"
  // for this useVad call), so the NEXT armMic() inherits frozen
  // speech-timing refs from the PREVIOUS turn — confirmed on a real device
  // as a tight loop, re-firing every tick with an identical stale
  // activeSpeechMs (PR #45).
  const [micGeneration, setMicGeneration] = useState(0);
  // Same idea, for the shadow (continuation) recorder's OWN useVad call —
  // bumped on every successful armShadowRecorder(), not just on the
  // 'thinking'-transition effect. Without this, a SECOND continuation
  // within the same 'thinking' phase inherits frozen hasSpoken/
  // speechStartAt/lastSpeechAt state from the FIRST continuation (state
  // never leaves 'thinking' between them, so useVad's active prop never
  // flips value — same root cause as micGeneration above, just triggered by
  // a mid-phase re-arm instead of a whole new turn). Confirmed by
  // independent review: without this, the freshly re-armed shadow recorder
  // reads quiet on its very first post-arm tick, but hasSpoken/lastSpeechAt
  // are still the FIRST continuation's stale values (already past every
  // threshold, since that's why it just committed) — onSilenceTick fires
  // immediately, re-committing again, forever, on every ordinary
  // pause-then-resume-then-pause speech pattern.
  const [shadowGeneration, setShadowGeneration] = useState(0);
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
  // Same idea, for the shadow (continuation) recorder.
  const shadowRecorderLiveRef = useRef(false);
  // Wall-clock Date.now() of the shadow recorder's last successful arm —
  // an INDEPENDENT signal from useVad's own (VAD-tick-driven) timing state,
  // used as a last-resort sanity check in commitShadowContinuation. Exists
  // because of a real production 400: a stale VAD tick (the residual,
  // reviewer-flagged race in shadowGeneration's resetKey — bounded to at
  // most one extra spurious retrigger per re-arm, not a full loop, but not
  // eliminated) fired a commit using activeSpeechMs CARRIED OVER from the
  // previous continuation, ~25ms after the recorder had just been re-armed
  // (production ClientLogEntries: "armed" then "confirmed continuation"
  // 25ms later with an IDENTICAL activeSpeechMs to the prior commit — the
  // tell that it was stale, not fresh). The resulting recording was a WebM
  // with valid container framing but zero actual audio samples — ffmpeg
  // has nothing to convert, the backend correctly 400s, and that surfaced
  // as a visible error. activeSpeechMs itself can't catch this (it's the
  // stale value CAUSING the false trigger), so this checks real elapsed
  // time since arm instead — a dimension the stale VAD tick has no way to
  // fake.
  const shadowArmedAtRef = useRef(0);
  // Set on a confirmed barge-in, checked in sendSegment's finally block so
  // it skips the normal end-of-turn armMic() — a barge-in already
  // transitioned straight to 'recording' with its own turn-taking state
  // freshly underway (see handleSpeechStart), and armMic() would incorrectly
  // reset that out from under it.
  const bargedInRef = useRef(false);
  // Guards handleSilenceTick's first-segment trigger against re-entry across
  // the few 50ms VAD ticks between "silence threshold crossed" and `state`
  // actually becoming 'thinking' (at which point the main recorder's useVad
  // call goes inactive on its own and this guard's job is done — see where
  // it's cleared). Independent of segmentGenerationRef below: this prevents
  // literally sending the SAME first segment twice, not "which reply wins."
  const firstSegmentInFlightRef = useRef(false);
  // Guards handleShadowSilenceTick's discard-as-noise branch from re-logging
  // every 50ms tick for as long as a continuation window stays silent after
  // a short blip — useVad's onSilenceTick has no "already handled" concept
  // of its own (see useVad.ts), and activeSpeechMs stays fixed once speech
  // stops. Reset on shadow speech resuming and on a fresh arm, so a
  // genuinely new short sound later gets its own log.
  const shadowDiscardLoggedRef = useRef(false);

  // Turn-taking state. Each segment's transcribed text is stored keyed by
  // its OWN generation number (see segmentGenerationRef below), not
  // appended to a running string — segment 1's transcription can resolve
  // AFTER segment 2's (network timing has no relationship to recording
  // order: segment 1's request might simply be slower), and a plain
  // accumulating string has no way to recover the right order once that
  // happens, only whichever order the network happened to resolve in.
  // Keying by generation and sorting numerically at read time
  // (pendingText() below) keeps chronological order correct regardless of
  // resolve order. Found by tracing this exact race before it ever shipped.
  //
  // Residual, accepted gap — SAME-TURN only (cross-turn writes are closed
  // off entirely by turnGenerationRef below): a segment still sends
  // whatever pendingText() returns AT THAT MOMENT — if an EARLIER
  // generation's transcription is abnormally slow (slower than this whole
  // next pause-plus-1000ms shadow cycle put together, which real timings
  // make unlikely but not impossible), the send can go out missing that
  // earlier segment's text. The data itself isn't lost (a later .set() for
  // that generation still lands in the map, gated only on the turn still
  // being the same one), only that turn's reply won't have reflected it.
  // Documented rather than engineered away — same call as this session's
  // temperature=0 tradeoff (PR #46): a real but low-probability timing
  // edge, not worth the added coupling a guaranteed fix would need.
  const pendingSegmentsRef = useRef<Map<number, string>>(new Map());
  const pendingText = useCallback(
    () =>
      Array.from(pendingSegmentsRef.current.entries())
        .sort(([a], [b]) => a - b)
        .map(([, text]) => text)
        .join(' ')
        .trim(),
    []
  );
  // Monotonic — bumped every time a NEW segment starts sending (first or
  // continuation) and captured locally as that segment's own identity. When
  // a segment's sendMessage call resolves, it compares its captured value
  // against the CURRENT counter: equal means "I'm still the latest attempt,
  // speak me"; anything else means a later segment has since superseded me,
  // discard silently. A plain boolean can't do this correctly once more
  // than one continuation happens in a row — see the file-level comment.
  // Doubles as the key into pendingSegmentsRef above, since segment order
  // and generation order are the same thing.
  const segmentGenerationRef = useRef(0);
  // A DIFFERENT axis from segmentGenerationRef above — that one answers "is
  // this the latest attempt WITHIN a turn," this one answers "has the whole
  // TURN this segment belongs to already ended." Bumped once per armMic()
  // (once per turn, not once per segment). Needed because segmentGenerationRef
  // is never reset across turns (deliberately — see its own comment), so a
  // segment from a turn that's ALREADY OVER (armMic() already ran for the
  // NEXT turn) can still have segmentGenerationRef.current unchanged if that
  // next turn discarded its own first sound as noise without ever calling
  // sendSegment (see handleSilenceTick's discard branch) — without this,
  // such a stale segment's late-arriving transcription would splice
  // unrelated old-turn text into the NEW turn's pendingSegmentsRef, or worse,
  // a stale reply could get spoken over a mic that's already listening for
  // something else entirely. Found by independent review of this PR.
  const turnGenerationRef = useRef(0);

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
      // Left un-recovered here on purpose — the next silence tick will find
      // a dead recorder, fail its own segment-send attempt, and finalize
      // with whatever text has already been accumulated rather than
      // getting stuck.
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

  // Same pair, for the shadow (continuation) recorder — armed both by the
  // 'thinking' effect below AND directly by sendSegment on every
  // continuation commit (see its own comment), unlike the main recorder
  // which only ever arms once per turn.
  const armShadowRecorder = useCallback(async () => {
    if (!mountedRef.current || shadowRecorderLiveRef.current) return;
    try {
      await shadowRecorder.prepareToRecordAsync();
      if (!mountedRef.current) return;
      shadowRecorder.record();
      shadowRecorderLiveRef.current = true;
      shadowArmedAtRef.current = Date.now();
      shadowDiscardLoggedRef.current = false;
      // Bumped on EVERY successful (re)arm, not just the first one per
      // turn — see shadowGeneration's own declaration comment for why a
      // second-or-later continuation within the same 'thinking' phase
      // needs its own reset just as much as a whole new turn does.
      setShadowGeneration((g) => g + 1);
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

  // Arms the shadow recorder for the whole 'thinking' phase, discards it (no
  // commit) the moment we leave 'thinking' any other way. Fires once per
  // TRANSITION into 'thinking' — a continuation round that sends its own
  // segment and re-enters 'thinking'-adjacent listening re-arms explicitly
  // inside sendSegment instead (state doesn't leave 'thinking' between
  // rounds, so this effect wouldn't fire again on its own).
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
    // Marks every segment still in flight from whatever turn just ended as
    // belonging to a dead turn — see turnGenerationRef's own comment.
    // Bumped unconditionally here, even if the re-arm below ends up
    // failing, since by the time armMic() is called at all, the CALLER
    // (sendSegment's finally, or the discard-cooldown timeout) already
    // considers that turn's work finished.
    turnGenerationRef.current += 1;
    pendingSegmentsRef.current.clear();
    bargedInRef.current = false;
    firstSegmentInFlightRef.current = false;
    await rearmRecorderOnly();
    if (!mountedRef.current || !recorderLiveRef.current) return;
    setMicArmed(true);
    setMicGeneration((g) => g + 1);
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

  // Shared tail: given a reply just received, speaks it and wraps up. Kept
  // separate so every caller stays in sync on the barge-in-aware TTS
  // handling.
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

  // The core of this design (see the file-level comment). `fromShadow`
  // distinguishes the very first segment of a turn (captured by the main
  // recorder, triggered by handleSilenceTick) from a continuation segment
  // (captured by the shadow recorder, triggered by commitShadowContinuation)
  // — and they genuinely need DIFFERENT handling, not just a different
  // recorder to stop: /chat/send only accepts EITHER text OR audio, never
  // both (ChatController.cs's Send action), so a continuation's audio can't
  // be combined with the first segment's already-known text in one call.
  // The first segment goes straight to /chat/send as raw audio — the
  // optimistic bet this whole design rests on. A continuation instead gets
  // a cheap transcription-only pass (checkUtteranceComplete, `.complete`
  // ignored — see its comment in client.ts) so it can be appended as TEXT
  // to whatever's already been recognized, and THAT combined text is what
  // actually goes to /chat/send — otherwise the model would answer the
  // continuation alone, with no idea what came before it.
  const sendSegment = useCallback(
    async (fromShadow: boolean) => {
      const sid = sessionIdRef.current;
      // Captured up front, before any await — identifies which TURN this
      // segment belongs to (see turnGenerationRef's own comment). Distinct
      // from myGeneration below, which is captured later and identifies
      // this segment's standing WITHIN that turn.
      const myTurn = turnGenerationRef.current;
      // Reset unconditionally, not just on a success path — this call itself
      // can be the turn AFTER a barge-in (the interrupting utterance's own
      // segment), and leaving a stale `true` here would make the finally
      // block below skip armMic() for an entirely unrelated turn — state
      // stuck with no recovery. Same defensive shape as the barge-in PR's
      // own fix for this exact class of bug.
      bargedInRef.current = false;

      const uri = fromShadow ? await stopShadowRecorderIfLive() : await stopRecorderIfLive();

      if (!fromShadow) {
        // Only the first segment's trigger needs this guard — see its own
        // comment. Release it the instant state leaves idle/recording (via
        // setState below) rather than waiting for this whole async call to
        // finish, since that's the moment the main recorder's useVad call
        // goes inactive on its own and re-entry becomes structurally
        // impossible anyway.
        firstSegmentInFlightRef.current = false;
      }

      if (!sid || !uri) {
        if (!sid) setError('Сессия ещё не готова');
        await armMic();
        return;
      }

      // Bumped once per attempt, whether this turns out to be the ONLY
      // attempt or gets superseded down the line — see the field comment.
      const myGeneration = ++segmentGenerationRef.current;

      if (!fromShadow) {
        setState('thinking');
        setTranscript('');
        setReply('');
        speakBackchannel();
      } else {
        // The 'thinking'-entry effect above only fires on a state
        // TRANSITION into 'thinking', which already happened for the first
        // segment — state hasn't left 'thinking' since, so it won't fire
        // again on its own. Re-arm explicitly so a THIRD segment can still
        // be caught if the speaker keeps going.
        void armShadowRecorder();
      }

      const turnStartedAt = Date.now();
      log('info', 'turn', 'segment send start', { fromShadow });
      try {
        let fullReply: string;

        if (fromShadow) {
          const { transcription } = await api.checkUtteranceComplete(uri);
          if (myTurn !== turnGenerationRef.current) {
            // The TURN itself is already over (armMic() already ran for
            // a next one) — pendingSegmentsRef now belongs to whatever
            // turn is current, so writing into it here would splice this
            // dead turn's text into an unrelated conversation. Distinct
            // from the ordinary supersede check below, which still wants
            // the write (same turn, just a later segment took over).
            log('info', 'turn', 'discarding continuation from an already-ended turn');
            return;
          }
          // Record BEFORE checking supersede, not after — a THIRD segment
          // can supersede this one (generation 2) while this call was still
          // in flight, but this segment's recognized text must still reach
          // whichever generation ends up sending, or a middle continuation
          // in a 2+ chain would be silently dropped rather than folded in
          // (caught by tracing the exact 2-continuation scenario by hand
          // before ever sending this for review).
          if (transcription.trim()) {
            pendingSegmentsRef.current.set(myGeneration, transcription.trim());
            if (myGeneration === segmentGenerationRef.current) setTranscript(pendingText());
          }
          if (myGeneration !== segmentGenerationRef.current) {
            log('info', 'turn', 'continuation superseded — folded into pending text, not sent standalone');
            return;
          }
          const combinedText = pendingText();
          if (!combinedText) {
            // Nothing transcribable at all across every segment so far —
            // shadow-capture already gates on MIN_SPEECH_MS before ever
            // committing a continuation, so this should be rare (a
            // transcription that came back empty despite real audio), but
            // there's nothing left to send if it happens.
            await armMic();
            return;
          }
          fullReply = await sendMessage({ sessionId: sid, message: combinedText }, {
            onChunk: (chunk) => {
              if (myGeneration === segmentGenerationRef.current) setReply((prev) => prev + chunk);
            },
          });
        } else {
          const { fileName } = await api.uploadAudio(uri);
          fullReply = await sendMessage(
            { sessionId: sid, message: '', audioFileName: fileName },
            {
              onTranscription: (text) => {
                if (!text.trim() || myTurn !== turnGenerationRef.current) return;
                pendingSegmentsRef.current.set(myGeneration, text.trim());
                if (myGeneration === segmentGenerationRef.current) setTranscript(pendingText());
              },
              onChunk: (chunk) => {
                if (myGeneration === segmentGenerationRef.current) setReply((prev) => prev + chunk);
              },
            }
          );
        }

        if (myGeneration !== segmentGenerationRef.current || myTurn !== turnGenerationRef.current) {
          // Either a continuation superseded this segment within the SAME
          // turn (its transcription was already folded into
          // pendingSegmentsRef above regardless of generation, so nothing
          // recognized is lost, only this now-incomplete-in-hindsight reply
          // itself), or the whole TURN has since ended — either way, this
          // reply must never be spoken.
          log('info', 'turn', 'discarding superseded or stale-turn reply', { replyLength: fullReply.length });
          return;
        }

        log('info', 'turn', 'reply received', { sendMs: Date.now() - turnStartedAt, replyLength: fullReply.length });
        pendingSegmentsRef.current.clear();
        await speakReplyAndWrapUp(fullReply, turnStartedAt);
      } catch (err) {
        if (myGeneration !== segmentGenerationRef.current || myTurn !== turnGenerationRef.current) {
          // Also superseded (or the whole turn already ended) — a failure
          // on a discarded attempt isn't a real error from the user's
          // perspective, don't surface it, and don't clear a map that may
          // by now belong to a different turn entirely.
          log('warn', 'turn', 'superseded or stale-turn segment failed (ignored)', {
            error: err instanceof Error ? err.message : String(err),
          });
          return;
        }
        const message = err instanceof Error ? err.message : String(err);
        setError(message);
        log('error', 'turn', 'segment send failed', { error: message, elapsedMs: Date.now() - turnStartedAt });
        pendingSegmentsRef.current.clear();
      } finally {
        // Only the segment that's still current AND whose turn is still
        // alive owns turn wrap-up — a superseded (or stale-turn) segment's
        // finally must not touch armMic()/bargedInRef, since a NEWER
        // segment (or its own eventual finally) is
        // responsible for that now.
        if (myGeneration === segmentGenerationRef.current && myTurn === turnGenerationRef.current) {
          if (!bargedInRef.current) await armMic();
        }
      }
    },
    [armMic, stopRecorderIfLive, stopShadowRecorderIfLive, armShadowRecorder, speakReplyAndWrapUp, pendingText]
  );

  // Confirmed as real, sustained continuation speech (not yet committed —
  // see the guard at the top): stop the shadow recorder and send it as the
  // next segment. Deliberately does NOT abort whatever segment is currently
  // in flight — see the file-level comment for why letting it run to
  // completion and discarding its result (via segmentGenerationRef) is
  // simpler and race-free compared to this session's earlier
  // AbortController-based design.
  const commitShadowContinuation = useCallback(
    (activeSpeechMs: number) => {
      if (!shadowRecorderLiveRef.current) return; // already committed by an earlier tick, or never armed
      const armedForMs = Date.now() - shadowArmedAtRef.current;
      if (armedForMs < MIN_SHADOW_ARM_MS) {
        // Stale VAD retrigger — see shadowArmedAtRef's own comment for the
        // real production 400 this closes. activeSpeechMs here is exactly
        // the kind of carried-over value that causes the false trigger in
        // the first place, so it can't be trusted to catch this itself;
        // wall-clock time since arm can't be faked the same way. Recorder
        // stays live and armed either way — a genuinely fresh commit later
        // is unaffected.
        log('debug', 'shadow', 'discarding stale re-arm retrigger', { activeSpeechMs, armedForMs });
        return;
      }
      log('info', 'turn', 'shadow-capture: confirmed continuation', { activeSpeechMs });
      void sendSegment(true);
    },
    [sendSegment]
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
    // Without this, a second-or-later continuation within the same
    // 'thinking' phase reruns on a recorder that was just stopped and
    // re-armed but inherits FROZEN hasSpoken/speechStartAt/lastSpeechAt
    // state from the previous continuation — since `active` itself never
    // changes value across that re-arm (state stays 'thinking' the whole
    // time). See shadowGeneration's own declaration comment; this is the
    // exact same bug class resetKey already closed for the main recorder
    // (PR #45), just triggered by a mid-phase re-arm instead of a whole new
    // turn. Found by independent review of this PR — reproduced as an
    // immediate, self-sustaining commit loop on every ordinary
    // pause-then-resume-then-pause speech pattern.
    resetKey: shadowGeneration,
    onSpeechStart: () => log('debug', 'shadow', 'shadow speech detected'),
    onSpeechResume: () => {
      shadowDiscardLoggedRef.current = false;
    },
    onSilenceTick: handleShadowSilenceTick,
  });

  // Fires on every VAD tick while silent after having spoken — the ONLY
  // trigger left for the FIRST segment of a turn (see the file-level
  // comment; there is no more recheck/ceiling schedule to manage here).
  const handleSilenceTick = useCallback(
    (silenceDurationMs: number, activeSpeechMs: number) => {
      if (silenceDurationMs < CHECK_SILENCE_THRESHOLD_MS || firstSegmentInFlightRef.current) return;
      firstSegmentInFlightRef.current = true;
      if (activeSpeechMs < MIN_SPEECH_MS) {
        log('debug', 'turn', 'discarding short sound before first segment', { activeSpeechMs });
        firstSegmentInFlightRef.current = false;
        // Re-arm after a short cooldown rather than immediately, so
        // continuous background noise can't retrigger this in a tight loop
        // — mirrors yolo.js's identical 750ms cooldown in the same spot.
        setTimeout(() => armMic(), DISCARD_COOLDOWN_MS);
        return;
      }
      void sendSegment(false);
    },
    [sendSegment, armMic]
  );

  const handleSpeechResume = useCallback(() => {
    // No staleness bookkeeping needed here anymore — segmentGenerationRef
    // already handles "was this segment superseded" at resolution time,
    // regardless of how many times speech has resumed/paused since it was
    // sent. Kept as a required useVad callback (still fires on a genuine
    // quiet->loud transition mid-utterance) even though this hook no longer
    // needs to act on it itself.
  }, []);

  // While 'speaking', a confirmed onset is a barge-in: stop Olga mid-word
  // and hand the turn straight back to the user — mirrors yolo.js's
  // triggerInterruption() going directly to LISTENING, not IDLE, since VAD
  // already confirmed real sustained speech (10 frames at the boosted
  // threshold, not just the normal 5) to get here at all. No network abort
  // needed: by 'speaking', sendMessage() has already resolved — there's
  // nothing left in flight except the TTS playback itself. Unchanged from
  // PR #39 — this design deliberately leaves barge-in-during-speaking alone.
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
  // Diagnostic only, scoped to 'speaking' (barge-in) — logs are otherwise
  // silent whenever nothing crosses INTERRUPTION_THRESHOLD_DB, so a real
  // interruption attempt that stayed just under the boosted bar leaves no
  // trace at all. Real-device testing reported barge-in "not working at
  // all" with zero corroborating log evidence either way — this exists so
  // the NEXT attempt has an actual level trace to diagnose from instead of
  // guessing at a threshold change blind.
  const handleSpeakingLevelSample = useCallback((peakSmoothedDb: number, peakRawMetering: number) => {
    log('debug', 'vad', 'speaking level sample', {
      peakSmoothedDb,
      peakRawMetering,
      thresholdDb: INTERRUPTION_THRESHOLD_DB,
    });
  }, []);
  useVad({
    recorder,
    active: micArmed && (state === 'idle' || state === 'recording' || isSpeaking),
    resetKey: micGeneration,
    thresholdDb: isSpeaking ? INTERRUPTION_THRESHOLD_DB : undefined,
    startFrames: isSpeaking ? INTERRUPTION_FRAMES : undefined,
    onSpeechStart: handleSpeechStart,
    onSpeechResume: handleSpeechResume,
    onSilenceTick: handleSilenceTick,
    onLevelSample: isSpeaking ? handleSpeakingLevelSample : undefined,
  });

  // Manual override: send whatever's been captured so far immediately,
  // regardless of the 1.2s pause timer — same shape as before, just calling
  // straight into sendSegment now that there's no separate ceiling function.
  // Also doubles as a manual interruption while speaking, mirroring
  // yolo.js's yolo-speak-now-btn ("Перебить" during SPEAKING) — a safety
  // valve for on-device VAD/turn-taking that hasn't been calibrated against
  // a real microphone yet.
  const forceFinalize = useCallback(() => {
    if (state === 'speaking') {
      bargedInRef.current = true;
      log('info', 'turn', 'barge-in: manual interruption tap');
      interruptSpeaking();
      setState('recording');
    } else if ((state === 'idle' || state === 'recording') && !firstSegmentInFlightRef.current) {
      firstSegmentInFlightRef.current = true;
      void sendSegment(false);
    }
  }, [state, sendSegment]);

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
