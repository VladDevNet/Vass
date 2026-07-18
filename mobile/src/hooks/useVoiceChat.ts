import { useCallback, useEffect, useRef, useState } from 'react';
import { AppState, Platform } from 'react-native';
import * as Notifications from 'expo-notifications';
import {
  RecordingPresets,
  requestRecordingPermissionsAsync,
  setAudioModeAsync,
  useAudioRecorder,
} from 'expo-audio';
import { api, sendMessage, type ExternalActionEvent, type ScreenCaptureRequest, type ServerTurnStats } from '../api/client';
import {
  cancelLocalReminder,
  getReminderDeviceContext,
  scheduleAndAcknowledgeReminder,
} from '../reminders/localReminders';
import {
  createStreamingSpeech,
  hasSpeechToResume,
  interruptSpeaking,
  pauseSpeaking,
  resumeSpeaking,
  speakBackchannel,
  speakSystemNotice,
  stopSpeaking,
  stripMarkdownForSpeechChunk,
  stripMarkupForDisplay,
} from '../tts/systemSpeech';
import type { StreamingSpeech } from '../tts/systemSpeech';
import { DEFAULT_THRESHOLD_DB, useVad } from './useVad';
import { log } from '../logging/remoteLogger';
import { executeExternalAction, ExternalActionExecutionError } from '../actions/externalActions';
import { VassOverlay } from '../../modules/vass-overlay';
import type { PendingSharedText, PendingVisualInput, StageVisualAssetInput } from '../visual/types';

export type VoiceState = 'idle' | 'recording' | 'thinking' | 'speaking' | 'paused';

interface VisualTurnBridge {
  getPendingVisual: () => PendingVisualInput | null;
  consumePendingVisual: (assetId: string) => void;
  stageVisualAsset: (input: StageVisualAssetInput) => Promise<PendingVisualInput | null>;
  getPendingSharedText: () => PendingSharedText | null;
  consumePendingSharedText: (requestId: string) => void;
  setScreenCaptureConsentPending: (pending: boolean) => void;
}

class ScreenCaptureCancelledError extends Error {
  constructor() {
    super('Screen capture was cancelled');
  }
}

class NativeOperationTimeoutError extends Error {
  constructor(operation: string) {
    super(`${operation} timed out`);
    this.name = 'NativeOperationTimeoutError';
  }
}

function createClientTurnId(): string {
  if (typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function') return crypto.randomUUID();

  // Idempotency correlation, not a credential. Keep a valid GUID fallback
  // for runtimes that do not expose Web Crypto's randomUUID.
  const randomHex = () => Math.floor(Math.random() * 0x1_0000).toString(16).padStart(4, '0');
  return `${randomHex()}${randomHex()}-${randomHex()}-4${randomHex().slice(1)}-a${randomHex().slice(1)}-${randomHex()}${randomHex()}${randomHex()}`;
}

// Android owns the consent UI and the user may need time to choose a display.
// The turn stays correlated with this one request for the full period instead
// of treating a legitimate late consent as an unrelated future attachment.
const SCREEN_CAPTURE_RESULT_TIMEOUT_MS = 120_000;
const SCREEN_CAPTURE_RESULT_POLL_MS = 150;
const SCREEN_CAPTURE_NATIVE_REQUEST_TIMEOUT_MS = 2_000;

function waitFor(milliseconds: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, milliseconds));
}

async function withTimeout<T>(operation: Promise<T>, timeoutMs: number, name: string): Promise<T> {
  let timeoutId: ReturnType<typeof setTimeout> | undefined;
  const timeout = new Promise<never>((_, reject) => {
    timeoutId = setTimeout(() => reject(new NativeOperationTimeoutError(name)), timeoutMs);
  });

  try {
    return await Promise.race([operation, timeout]);
  } finally {
    if (timeoutId !== undefined) clearTimeout(timeoutId);
  }
}

async function captureOneShotScreenImage(requestId: string): Promise<string> {
  log('info', 'screen-capture', 'requesting Android screen capture consent', { requestId });
  await withTimeout(
    VassOverlay.requestScreenCapture(requestId),
    SCREEN_CAPTURE_NATIVE_REQUEST_TIMEOUT_MS,
    'Screen capture consent request'
  );
  const startedAt = Date.now();
  log('info', 'screen-capture', 'waiting for Android consent and screen frame', {
    requestId,
    timeoutMs: SCREEN_CAPTURE_RESULT_TIMEOUT_MS,
  });
  const deadline = Date.now() + SCREEN_CAPTURE_RESULT_TIMEOUT_MS;
  while (Date.now() < deadline) {
    const result = await VassOverlay.getScreenCaptureResult();
    if (result.requestId === requestId) {
      if (result.status === 'ready' && result.uri) {
        log('info', 'screen-capture', 'screen capture image received', {
          requestId,
          waitedMs: Date.now() - startedAt,
        });
        return result.uri;
      }
      if (result.status === 'cancelled') {
        log('info', 'screen-capture', 'screen capture consent cancelled', { requestId });
        throw new ScreenCaptureCancelledError();
      }
      if (result.status === 'error') {
        log('warn', 'screen-capture', 'screen capture service returned an error', { requestId });
        throw new Error('Не удалось получить снимок экрана. Повторите запрос.');
      }
    }
    await waitFor(SCREEN_CAPTURE_RESULT_POLL_MS);
  }
  log('warn', 'screen-capture', 'screen capture consent timed out', {
    requestId,
    timeoutMs: SCREEN_CAPTURE_RESULT_TIMEOUT_MS,
  });
  throw new Error('Не удалось получить снимок экрана вовремя. Повторите запрос.');
}

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
  // Speech does not need the old 44.1 kHz stereo music profile. Android may
  // still negotiate a nearby supported rate, but this asks for the compact
  // 16 kHz mono / 32 kbit AAC profile used by the server-side experiment.
  sampleRate: 16_000,
  numberOfChannels: 1,
  bitRate: 32_000,
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
const OVERLAY_INTERRUPTION_FRAMES = 8;
const OVERLAY_INTERRUPTION_THRESHOLD_DB = DEFAULT_THRESHOLD_DB + 4;
const EXTERNAL_MEDIA_STOP_TIMEOUT_MS = 1_500;
const SCREEN_CAPTURE_RECORDER_STOP_TIMEOUT_MS = 1_500;
const SHADOW_RECORDER_PREPARE_TIMEOUT_MS = 2_500;
const SHADOW_RECORDER_STOP_TIMEOUT_MS = 2_500;

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
// Streaming TTS (the follow-up explicitly deferred when this redesign
// first shipped) is now also in: sendSegment no longer waits for a reply
// to finish streaming ENTIRELY before speaking a word of it. Complete
// sentences are extracted from the growing SSE buffer as they arrive
// (extractCompleteSentences below) and fed to systemSpeech.ts's
// createStreamingSpeech, which speaks each one as soon as it's ready while
// later sentences are still being generated. Real production replies ran
// 2-15s+ to fully generate — that whole window used to be dead air after
// the backchannel filler ran out; now only the time to the FIRST sentence
// is on the critical path.
//
// Barge-in requires the mic to be live and VAD watching for the ENTIRE
// 'speaking' duration, not just after — so the recorder arms BEFORE the
// first sentence is spoken (see sendSegment's handleChunk/
// onBeforeFirstSpeech), not after. recorderLiveRef tracks whether the
// native recorder is currently prepared+recording so armMic/
// rearmRecorderOnly/stopRecorderIfLive can each be called unconditionally
// wherever they're needed without worrying about double-arming (which
// expo-audio throws AudioRecorderAlreadyPreparedException for — confirmed in
// AudioRecorder.kt's prepareRecording) or double-stopping.

// Extracts as many COMPLETE sentences as currently exist at the front of a
// streaming reply buffer, returning them plus whatever incomplete tail
// remains (held back until a later chunk completes it, or force-flushed
// once the stream itself ends — see sendSegment). Same "some non-
// terminator text then 1+ terminator(s)" heuristic systemSpeech.ts's own
// splitIntoChunks already uses for the non-streaming case (imperfect for
// things like "3.14" or "т.е." — an accepted, pre-existing limitation, not
// a new one introduced here).
//
// The leading part REQUIRES at least one real character (+, not *) — the
// same leading quantifier splitIntoChunks already uses (its trailing
// quantifier stays *, deliberately: unlike this function, it must still
// capture a final unterminated fragment since there's no more text coming).
// Real-device feedback ("TTS проговаривает 'точка'") traced to a confirmed,
// reproduced mechanism: stripMarkdownForSpeechChunk's \n{2,} -> '. '
// conversion synthesizes a fresh terminator, and when a chunk boundary
// lands right after it (e.g. buffer drains to "" after "Раз.", next chunk
// arrives as "\n\nДва." and strips to ". Два."), the old * (zero-or-more)
// leading quantifier let this regex match that leftover ". " as its own
// zero-real-content "sentence" — which then reaches Speech.speak()
// completely unfiltered (neither stripMarkdownForSpeech nor
// splitIntoChunks treats bare punctuation as empty) and gets read aloud
// as its own word. See also the pump loop's own content check in
// systemSpeech.ts, added as a second, independent layer catching the same
// class of input regardless of how it got queued.
function extractCompleteSentences(buffer: string): { sentences: string[]; rest: string } {
  const sentences: string[] = [];
  const re = /[^.!?…]+[.!?…]+\s*/g;
  let consumed = 0;
  let match: RegExpExecArray | null;
  while ((match = re.exec(buffer)) !== null) {
    const sentence = match[0].trim();
    if (sentence) sentences.push(sentence);
    consumed = re.lastIndex;
  }
  return { sentences, rest: buffer.slice(consumed) };
}

export function useVoiceChat(
  sessionId: number | null,
  ownerId: string | null,
  visualBridge?: VisualTurnBridge,
) {
  const [state, setState] = useState<VoiceState>('idle');
  const [appState, setAppState] = useState(AppState.currentState);
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

  useEffect(() => {
    const subscription = AppState.addEventListener('change', setAppState);
    return () => subscription.remove();
  }, []);
  const sessionIdRef = useRef(sessionId);
  sessionIdRef.current = sessionId;
  const visualBridgeRef = useRef(visualBridge);
  visualBridgeRef.current = visualBridge;
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
  // A timed-out native recorder operation cannot be cancelled safely from
  // JavaScript. Keep the primary voice path alive, but stop using this
  // optional continuation recorder until the app is restarted.
  const shadowRecorderDisabledRef = useRef(false);
  // Native prepare/stop are asynchronous and must never overlap. Production
  // logs confirmed the exact race: stop observed live=false while prepare
  // was still in flight, then prepare completed and left the native recorder
  // prepared forever; every later prepare failed with "already prepared".
  // Chain both operations so a stop requested during prepare runs immediately
  // after it and sees the committed live flag.
  const shadowOperationRef = useRef<Promise<void>>(Promise.resolve());
  const enqueueShadowOperation = useCallback(<T,>(operation: () => Promise<T>): Promise<T> => {
    const result = shadowOperationRef.current.then(operation, operation);
    shadowOperationRef.current = result.then(() => undefined, () => undefined);
    return result;
  }, []);
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
  // Whether the conversation is explicitly paused — mutated SYNCHRONOUSLY at
  // the exact call site in pauseConversation/resumeConversation, unlike
  // stateRef (which mirrors `state` but only updates once a render actually
  // runs). onBeforeFirstSpeech below awaits a native rearm that can easily
  // still be in flight when pauseConversation fires — checking stateRef
  // afterward isn't reliably fresh enough there (confirmed by independent
  // review: the render carrying 'paused' isn't guaranteed to have flushed
  // by the time that await resolves), which is exactly the class of gap
  // bargedInRef/firstSegmentInFlightRef already exist to close for their
  // own races. This is the same fix, for pause.
  const pausedRef = useRef(false);
  // A successful YouTube launch deliberately suspends listening until the
  // user taps the paused overlay and Vass returns to the foreground. This
  // prevents phone-speaker audio from becoming a giant user utterance.
  const externalMediaWaitingRef = useRef(false);
  // Background recording is enabled only after the user explicitly turns
  // on Android overlay mode. Keeping it off by default avoids a microphone
  // foreground-service notification during ordinary fullscreen use.
  const backgroundRecordingEnabledRef = useRef(false);
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
  // A continuation re-sends the combined text through /chat/send, so the
  // router may legitimately emit the same action more than once within one
  // turn. Keep device side effects exactly-once until armMic starts the next
  // turn; otherwise one long YouTube request could open two Activities/tabs.
  const executedExternalActionsRef = useRef<Set<string>>(new Set());

  useEffect(() => {
    return () => {
      mountedRef.current = false;
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
  //
  const armShadowRecorder = useCallback(() => enqueueShadowOperation(async () => {
    if (
      !mountedRef.current ||
      shadowRecorderDisabledRef.current ||
      shadowRecorderLiveRef.current ||
      stateRef.current !== 'thinking' ||
      pausedRef.current
    ) return;
    try {
      const preparation = shadowRecorder.prepareToRecordAsync();
      try {
        await withTimeout(preparation, SHADOW_RECORDER_PREPARE_TIMEOUT_MS, 'Shadow recorder preparation');
      } catch (err) {
        if (err instanceof NativeOperationTimeoutError) {
          shadowRecorderDisabledRef.current = true;
          // The late native preparation may still finish. Release it without
          // allowing it to block the serialized shadow operation queue.
          void preparation.then(() => shadowRecorder.stop()).catch(() => undefined);
          log('error', 'shadow', 'prepare timed out; continuation listening disabled until restart', {
            timeoutMs: SHADOW_RECORDER_PREPARE_TIMEOUT_MS,
          });
          return;
        }
        throw err;
      }
      if (!mountedRef.current || stateRef.current !== 'thinking' || pausedRef.current) {
        await shadowRecorder.stop();
        return;
      }
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
  }), [enqueueShadowOperation, shadowRecorder]);

  const stopShadowRecorderIfLive = useCallback((): Promise<string | null> =>
    enqueueShadowOperation(async () => {
      if (!shadowRecorderLiveRef.current) return null;
      shadowRecorderLiveRef.current = false;
      try {
        await withTimeout(shadowRecorder.stop(), SHADOW_RECORDER_STOP_TIMEOUT_MS, 'Shadow recorder stop');
        return shadowRecorder.uri || null;
      } catch (err) {
        if (err instanceof NativeOperationTimeoutError) {
          shadowRecorderDisabledRef.current = true;
          log('error', 'shadow', 'stop timed out; continuation listening disabled until restart', {
            timeoutMs: SHADOW_RECORDER_STOP_TIMEOUT_MS,
          });
          return null;
        }
        log('warn', 'shadow', 'stop failed', { error: err instanceof Error ? err.message : String(err) });
        return null;
      }
    }), [enqueueShadowOperation, shadowRecorder]);

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
    executedExternalActionsRef.current.clear();
    bargedInRef.current = false;
    firstSegmentInFlightRef.current = false;
    await rearmRecorderOnly();
    if (!mountedRef.current || !recorderLiveRef.current) return;
    // Re-check AFTER that (possibly slow, native) rearm — the same race
    // shape as onBeforeFirstSpeech's and resumeConversation's own (see
    // pausedRef's comment): a long-press can land while THIS rearm is
    // still in flight, and committing to 'idle' here regardless would
    // silently overwrite 'paused' and leave the mic live/listening. Every
    // caller of armMic() (armMicUnlessPaused's guarded call sites, and
    // resumeConversation's own direct one) already ensures pausedRef was
    // false at the moment THEY called in — this guards the same window
    // one level deeper, where a pause can still race the await armMic()
    // itself just did. Found by auditing every rearmRecorderOnly() call
    // site after independent review caught the same shape twice already.
    if (pausedRef.current) {
      await stopRecorderIfLive();
      return;
    }
    setMicArmed(true);
    setMicGeneration((g) => g + 1);
    setState('idle');
    log('debug', 'mic', 'armed');
  }, [rearmRecorderOnly, stopRecorderIfLive]);

  // Same as armMic(), except a no-op while the user has the conversation
  // explicitly paused. Several call sites below reach armMic() after a turn
  // ends or fails for reasons that have nothing to do with pausing (a reply
  // finishing normally, an empty continuation, a network error, a
  // discarded-noise cooldown) — none of them should silently un-pause the
  // app out from under someone who stepped away mid-call. resumeConversation
  // is the ONE place allowed to call armMic() directly while paused — that's
  // the whole point of resuming when there's nothing left to resume speaking.
  const armMicUnlessPaused = useCallback(async () => {
    if (pausedRef.current) return;
    await armMic();
  }, [armMic]);

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
        await setAudioModeAsync({
          playsInSilentMode: true,
          allowsRecording: true,
          interruptionMode: 'doNotMix',
          shouldPlayInBackground: false,
          allowsBackgroundRecording: false,
        });
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

      // Re-check AFTER that (possibly slow, native) stop — the same race
      // shape as armMic()/onBeforeFirstSpeech/resumeConversation, a fourth
      // instance independent review found by systematically sweeping every
      // setState() in this file rather than just rearmRecorderOnly() call
      // sites. VAD firing this via handleSilenceTick/commitShadowContinuation
      // and a long-press landing are two independent event sources that can
      // genuinely coincide — the natural result of one interruption (trail
      // off mid-sentence AND reach for the avatar at the same moment), not
      // a contrived double-gesture. If paused now, discard whatever was
      // just captured — audio already stopped above regardless — instead
      // of committing to 'thinking' and sending it; matches
      // pauseConversation's own "discard in-progress recording" decision
      // for a plain pause-from-'recording'. Not routed through
      // armMicUnlessPaused: armMic() would bump turnGenerationRef/clear
      // pendingSegmentsRef for a turn resumeConversation may still need to
      // treat as current once the user comes back — that reset already
      // happens naturally, either via resumeConversation's own armMic()
      // fallback (nothing to resume) or the next real turn's armMic().
      if (pausedRef.current) {
        log('info', 'turn', 'discarding captured segment — paused before it could be sent', { fromShadow });
        return;
      }

      if (!sid || !uri) {
        if (!sid) setError('Сессия ещё не готова');
        await armMicUnlessPaused();
        return;
      }

      // Bumped once per attempt, whether this turns out to be the ONLY
      // attempt or gets superseded down the line — see the field comment.
      const myGeneration = ++segmentGenerationRef.current;
      // Capture the asset for this attempt. A new image selected while Vass
      // speaks belongs to the following turn and must not be consumed here.
      let visualAssetId = visualBridgeRef.current?.getPendingVisual()?.assetId;
      const pendingSharedText = visualBridgeRef.current?.getPendingSharedText();
      const sharedContent = pendingSharedText?.content;

      if (!fromShadow) {
        setState('thinking');
        setTranscript('');
        setReply('');
        // A stale error from a previous, unrelated failed turn would
        // otherwise sit visible forever — nothing else ever clears it
        // (see setError's other call sites: only the one-time mount
        // effect resets it, no successful turn ever did). Found alongside
        // the bargedInRef gap below: a barge-in used to be able to
        // surface one via this exact path.
        setError(null);
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
      let firstReplyChunkAt: number | null = null;

      // Sentences are spoken as the reply streams in, not after the whole
      // thing arrives — see createStreamingSpeech's own comment. Scoped to
      // THIS attempt: only ever created lazily on the first real chunk (see
      // handleChunk), so an empty/no_speech reply (see PR #54) never starts
      // one at all, and a fresh instance is created per attempt, never
      // reused across segments. A plain mutable holder rather than a `let`
      // — TS narrows a `let` union to `never` at read sites outside the
      // closure that reassigns it, since it can't prove whether that
      // closure ran by then; a property access doesn't have that problem.
      const streamingHolder: { current: StreamingSpeech | null } = { current: null };
      let sentenceBuffer = '';
      let visibleReplyBuffer = '';
      const pendingExternalActionHolder: { current: ExternalActionEvent | null } = { current: null };
      const screenCaptureHolder: { current: ScreenCaptureRequest | null } = { current: null };
      let nativeScreenCaptureRequestId: string | null = null;

      const ensureStreamingSpeech = (): StreamingSpeech => {
        if (!streamingHolder.current) {
          streamingHolder.current = createStreamingSpeech(async () => {
            // Arm BEFORE speaking, not after — barge-in needs VAD watching
            // for the entire playback, including an early voice preamble.
            await rearmRecorderOnly();
            // Re-check pause AFTER that (possibly slow, native) rearm —
            // pauseConversation's own stopRecorderIfLive() call can run
            // BEFORE this rearm has actually made the recorder live (both
            // race in the background from the same 'thinking'->'speaking'
            // transition), missing it entirely. Left unchecked, the
            // recorder would end up live — and the UI would read
            // 'speaking' — during what the user believes is a silent
            // pause. Checked via pausedRef, not stateRef: independent
            // review found stateRef isn't reliably fresh enough here, since
            // it only updates once a render actually runs, not
            // synchronously at the moment pauseConversation ran.
            if (pausedRef.current) {
              await stopRecorderIfLive();
              return;
            }
            if (myGeneration === segmentGenerationRef.current) setState('speaking');
          }, pausedRef.current);
          // A preamble or a reply's first chunk can arrive while the user
          // already paused during 'thinking'. Create the pipeline anyway;
          // resumeConversation later releases it without losing the phrase.
        }
        return streamingHolder.current;
      };

      const handlePreamble = (preamble: string) => {
        if (myGeneration !== segmentGenerationRef.current || bargedInRef.current) return;
        const speech = stripMarkdownForSpeechChunk(preamble).trim();
        if (!speech) return;
        log('info', 'turn', 'voice preamble received', { elapsedMs: Date.now() - turnStartedAt, length: speech.length });
        ensureStreamingSpeech().push(speech);
      };

      const handleChunk = (chunk: string) => {
        if (myGeneration !== segmentGenerationRef.current || bargedInRef.current) {
          // Two distinct reasons to stop here, same response to both:
          // (1) a THIRD segment superseded this one while its SSE stream is
          // still delivering chunks (narrow but real: the window between a
          // sentence being queued and setState('speaking') actually
          // committing — see onBeforeFirstSpeech below — briefly overlaps
          // with 'thinking', during which shadow-capture can still fire).
          // (2) a barge-in interrupted THIS reply mid-stream — streaming
          // TTS means 'speaking' (and therefore a barge-in) can now start
          // well before sendMessage()'s network call finishes, unlike the
          // old design handleSpeechStart's own comment used to describe;
          // bargedInRef is checked directly (not just via the generation
          // counter) because barge-in alone doesn't bump
          // segmentGenerationRef — only the interrupting utterance's OWN
          // later segment does — so without this, remaining chunks would
          // keep growing the visible reply bubble for a reply nobody's
          // listening to anymore. Idempotent to call repeatedly, since
          // every remaining chunk reaches here for the rest of the stream.
          streamingHolder.current?.abort();
          return;
        }
        if (firstReplyChunkAt === null) {
          firstReplyChunkAt = Date.now();
          log('info', 'turn', 'reply first text chunk', { elapsedMs: firstReplyChunkAt - turnStartedAt });
        }
        visibleReplyBuffer += chunk;
        setReply(stripMarkupForDisplay(visibleReplyBuffer));

        const streamingSpeech = ensureStreamingSpeech();

        // The CHUNK (non-trimming) variant specifically — see its own
        // comment in systemSpeech.ts for why: this buffer can legitimately
        // end in one meaningful trailing space mid-stream (a chunk boundary
        // landing right after a word, with the next word still to come),
        // and the trimming stripMarkdownForSpeech would eat that space,
        // gluing the next chunk's word onto this one with no space between
        // them.
        sentenceBuffer = stripMarkdownForSpeechChunk(sentenceBuffer + chunk);
        const { sentences, rest } = extractCompleteSentences(sentenceBuffer);
        sentenceBuffer = rest;
        for (const sentence of sentences) streamingSpeech.push(sentence);
      };

      const handleStats = (stats: ServerTurnStats) => {
        log('info', 'turn', 'server turn stats', { ...stats, elapsedMs: Date.now() - turnStartedAt });
      };

      try {
        let fullReply: string;
        const reminderDevice = await getReminderDeviceContext();
        const supportsLocalReminders = Platform.OS === 'android' || Platform.OS === 'ios';
        const clientTurnId = createClientTurnId();
        const handleReminder = async (reminder: Parameters<typeof scheduleAndAcknowledgeReminder>[0]) => {
          const result = mountedRef.current && ownerId
            ? await scheduleAndAcknowledgeReminder(reminder, reminderDevice.deviceId, ownerId)
            : { success: false, error: 'Сессия владельца локального напоминания уже завершена' };
          if (!result.success && myTurn === turnGenerationRef.current) {
            setError(result.error ?? 'Не удалось установить локальное напоминание');
          }
        };
        const handleReminderCancelled = async (reminder: { id: number }) => {
          if (!mountedRef.current || !ownerId) return;
          await cancelLocalReminder(reminder.id, ownerId);
        };
        const recordActionStatus = (
          action: Parameters<typeof executeExternalAction>[0],
          status: 'handler_dispatched' | 'failed' | 'cancelled',
          resultCode: string,
        ) => {
          void api.recordActionReceipt(action.actionId, status, resultCode)
            .catch((error) => log('warn', 'external-action', 'could not record action receipt', {
              type: action.type,
              taxonomy: action.taxonomy,
              status,
              error: error instanceof Error ? error.message : String(error),
            }));
        };
        const handleExternalAction = async (action: Parameters<typeof executeExternalAction>[0]) => {
          if (myGeneration !== segmentGenerationRef.current || myTurn !== turnGenerationRef.current) {
            recordActionStatus(action, 'cancelled', 'turn_cancelled');
            return;
          }
          const actionKey = action.actionId;
          if (executedExternalActionsRef.current.has(actionKey)) {
            log('debug', 'external-action', 'duplicate action ignored', { type: action.type, taxonomy: action.taxonomy });
            return;
          }
          executedExternalActionsRef.current.add(actionKey);
          pendingExternalActionHolder.current = action;
          log('info', 'external-action', 'action queued until speech completes', { type: action.type, taxonomy: action.taxonomy });
        };
        const handleScreenCapture = (request: ScreenCaptureRequest) => {
          if (myGeneration !== segmentGenerationRef.current || myTurn !== turnGenerationRef.current) return;
          screenCaptureHolder.current = request;
          log('info', 'screen-capture', 'server requested one-shot screen frame');
        };
        const supportsScreenAnalysis = Platform.OS === 'android' && VassOverlay.isAvailable() && !visualAssetId;

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
            await armMicUnlessPaused();
            return;
          }
          fullReply = await sendMessage({
            sessionId: sid,
            message: combinedText,
            deviceId: supportsLocalReminders ? reminderDevice.deviceId : undefined,
            timeZoneId: reminderDevice.timeZoneId,
            reminderProtocolVersion: supportsLocalReminders ? 2 : 0,
            clientTurnId,
            supportsExternalActions: true,
            supportsScreenAnalysis,
            visualAssetId,
            sharedContent,
          }, {
            onPreamble: handlePreamble,
            onChunk: handleChunk,
            onStats: handleStats,
            onReminder: handleReminder,
            onPeriodicReminder: handleReminder,
            onReminderCancelled: handleReminderCancelled,
            onExternalAction: handleExternalAction,
            onScreenCapture: handleScreenCapture,
          });
        } else {
          const uploadStartedAt = Date.now();
          log('info', 'turn', 'audio upload start');
          const { fileName, sizeBytes } = await api.uploadAudio(uri);
          log('info', 'turn', 'audio upload completed', {
            elapsedMs: Date.now() - uploadStartedAt,
            ...(typeof sizeBytes === 'number' ? { sizeBytes } : {}),
          });
          fullReply = await sendMessage(
            {
              sessionId: sid,
              message: '',
              audioFileName: fileName,
              deviceId: supportsLocalReminders ? reminderDevice.deviceId : undefined,
              timeZoneId: reminderDevice.timeZoneId,
              reminderProtocolVersion: supportsLocalReminders ? 2 : 0,
              clientTurnId,
              supportsExternalActions: true,
              supportsScreenAnalysis,
              visualAssetId,
              sharedContent,
            },
            {
              onTranscription: (text) => {
                if (!text.trim() || myTurn !== turnGenerationRef.current) return;
                pendingSegmentsRef.current.set(myGeneration, text.trim());
                if (myGeneration === segmentGenerationRef.current) setTranscript(pendingText());
              },
              onPreamble: handlePreamble,
              onChunk: handleChunk,
              onStats: handleStats,
              onReminder: handleReminder,
              onPeriodicReminder: handleReminder,
              onReminderCancelled: handleReminderCancelled,
              onExternalAction: handleExternalAction,
              onScreenCapture: handleScreenCapture,
            }
          );
        }

        // A screen-capture preflight deliberately returns no assistant text.
        // It preserves the recognized request, obtains explicit Android
        // consent, uploads the frame through the ordinary visual path, then
        // retries the request with that frame attached.
        if (screenCaptureHolder.current) {
          const capturePrompt = screenCaptureHolder.current.prompt;
          setReply('Жду подтверждения снимка экрана…');
          log('info', 'screen-capture', 'stopping recorders before Android consent');
          let recorderStopTimedOut = false;
          await Promise.race([
            Promise.all([stopRecorderIfLive(), stopShadowRecorderIfLive()]),
            new Promise<void>((resolve) => setTimeout(() => {
              recorderStopTimedOut = true;
              resolve();
            }, SCREEN_CAPTURE_RECORDER_STOP_TIMEOUT_MS)),
          ]);
          log('info', 'screen-capture', 'recorder stop phase completed', { recorderStopTimedOut });
          nativeScreenCaptureRequestId = `screen-${Date.now()}-${Math.random().toString(36).slice(2)}`;
          let screenUri: string;
          visualBridgeRef.current?.setScreenCaptureConsentPending(true);
          try {
            screenUri = await captureOneShotScreenImage(nativeScreenCaptureRequestId);
          } finally {
            visualBridgeRef.current?.setScreenCaptureConsentPending(false);
          }
          if (myGeneration !== segmentGenerationRef.current || myTurn !== turnGenerationRef.current) return;

          // The native consent activity already returns to Vass. Keep this
          // best-effort call as a JS-level recovery path for devices that
          // resume a different task after the system picker.
          try {
            await VassOverlay.openApp();
            log('info', 'screen-capture', 'returned Vass to foreground after capture');
          } catch (err) {
            log('warn', 'screen-capture', 'could not explicitly return Vass to foreground', {
              error: err instanceof Error ? err.message : String(err),
            });
          }
          await speakSystemNotice('Снимок экрана получен.');
          setReply('Снимок получен. Отправляю его на разбор…');
          log('info', 'screen-capture', 'staging captured screen image');
          const staged = await visualBridgeRef.current?.stageVisualAsset({
            uri: screenUri,
            mimeType: 'image/jpeg',
            originalName: 'vass-screen.jpg',
          });
          if (!staged) throw new Error('Не удалось подготовить снимок экрана для разбора.');
          visualAssetId = staged.assetId;
          log('info', 'screen-capture', 'captured screen image staged for retry', {
            assetId: staged.assetId,
            sizeBytes: staged.sizeBytes,
          });

          fullReply = await sendMessage({
            sessionId: sid,
            message: capturePrompt,
            deviceId: supportsLocalReminders ? reminderDevice.deviceId : undefined,
            timeZoneId: reminderDevice.timeZoneId,
            reminderProtocolVersion: supportsLocalReminders ? 2 : 0,
            clientTurnId,
            supportsExternalActions: true,
            supportsScreenAnalysis: false,
            visualAssetId,
          }, {
            onPreamble: handlePreamble,
            onChunk: handleChunk,
            onStats: handleStats,
            onReminder: handleReminder,
            onPeriodicReminder: handleReminder,
            onReminderCancelled: handleReminderCancelled,
            onExternalAction: handleExternalAction,
          });
        }

        if (myGeneration !== segmentGenerationRef.current || myTurn !== turnGenerationRef.current || bargedInRef.current) {
          // Either a continuation superseded this segment within the SAME
          // turn (its transcription was already folded into
          // pendingSegmentsRef above regardless of generation, so nothing
          // recognized is lost, only this now-incomplete-in-hindsight reply
          // itself), the whole TURN has since ended, or a barge-in already
          // cut this reply off (bargedInRef checked directly — barge-in
          // alone doesn't bump segmentGenerationRef, only the interrupting
          // utterance's OWN later segment does, and with streaming TTS
          // sendMessage() is routinely still in flight when that happens —
          // see handleSpeechStart's comment. Without this, resolving here
          // would setError()/log "reply received" for a turn the user
          // already walked away from, found by independent review). Either
          // way, this reply must never be spoken. Also abort any streaming
          // speech handleChunk already started for it — its own supersede
          // check would catch this on the NEXT chunk, but this was the
          // LAST one (sendMessage already resolved), so nothing will
          // trigger that check again on its own.
          log('info', 'turn', 'discarding superseded, stale-turn, or barged-in-over reply', {
            replyLength: fullReply.length,
          });
          if (pendingExternalActionHolder.current) {
            recordActionStatus(pendingExternalActionHolder.current, 'cancelled', 'turn_cancelled');
          }
          streamingHolder.current?.abort();
          return;
        }

        log('info', 'turn', 'reply received', { sendMs: Date.now() - turnStartedAt, replyLength: fullReply.length });
        pendingSegmentsRef.current.clear();
        if (visualAssetId) visualBridgeRef.current?.consumePendingVisual(visualAssetId);
        if (pendingSharedText) visualBridgeRef.current?.consumePendingSharedText(pendingSharedText.requestId);

        if (streamingHolder.current) {
          // Flush whatever's left in the buffer even if it never reached
          // terminating punctuation — nothing more is coming now, so this
          // IS the final piece regardless of how it ends.
          if (sentenceBuffer.trim()) streamingHolder.current.push(sentenceBuffer);
          try {
            await streamingHolder.current.finish();
          } catch (err) {
            // Local speech is the only runtime TTS path. The former Piper
            // fallback could add 10-80 seconds of silence after a local
            // engine failure and kept the turn busy throughout. Keep the
            // answer visible and finish the turn instead.
            log('warn', 'tts', 'on-device speech failed; reply left as text', {
              error: err instanceof Error ? err.message : String(err),
            });
          }
        }
        const pendingExternalAction = pendingExternalActionHolder.current;
        if (pendingExternalAction &&
            myGeneration === segmentGenerationRef.current &&
            myTurn === turnGenerationRef.current &&
            !bargedInRef.current) {
          const opensExternalMedia = pendingExternalAction.type === 'youtube_search' ||
            pendingExternalAction.type === 'youtube_watch';
          if (opensExternalMedia) {
            externalMediaWaitingRef.current = true;
            pausedRef.current = true;
            setState('paused');
            await VassOverlay.suspendForExternalMedia();
            const stopOperations = Promise.all([
              stopRecorderIfLive(),
              stopShadowRecorderIfLive(),
            ]);
            let stopTimedOut = false;
            await Promise.race([
              stopOperations,
              new Promise<void>((resolve) => setTimeout(() => {
                stopTimedOut = true;
                resolve();
              }, EXTERNAL_MEDIA_STOP_TIMEOUT_MS)),
            ]);
            log('info', 'external-action', 'listening suspended for external media', { stopTimedOut });
          }
          try {
            const receipt = await executeExternalAction(pendingExternalAction);
            recordActionStatus(pendingExternalAction, receipt.status, receipt.resultCode);
          } catch (err) {
            const resultCode = err instanceof ExternalActionExecutionError ? err.resultCode : 'handler_failed';
            recordActionStatus(pendingExternalAction, 'failed', resultCode);
            if (opensExternalMedia) {
              externalMediaWaitingRef.current = false;
              pausedRef.current = false;
              await armMic();
            }
            throw err;
          }
          log('info', 'external-action', 'handler dispatched after speech', {
            type: pendingExternalAction.type,
            taxonomy: pendingExternalAction.taxonomy,
          });
        }
        log('info', 'turn', 'finalize done', { totalMs: Date.now() - turnStartedAt });
      } catch (err) {
        // Unconditional, even for a superseded/stale-turn segment below —
        // a stray chunk that already made it into handleChunk before the
        // network call itself failed may have started a streaming instance
        // that would otherwise be left dangling in the background.
        streamingHolder.current?.abort();
        if (myGeneration !== segmentGenerationRef.current || myTurn !== turnGenerationRef.current || bargedInRef.current) {
          // Also superseded, stale-turn, or already barged-in-over (see
          // the matching comment on the success-path check above) — a
          // failure on a discarded attempt isn't a real error from the
          // user's perspective, don't surface it, and don't clear a map
          // that may by now belong to a different turn entirely.
          log('warn', 'turn', 'superseded, stale-turn, or barged-in-over segment failed (ignored)', {
            error: err instanceof Error ? err.message : String(err),
          });
          return;
        }
        if (err instanceof ScreenCaptureCancelledError) {
          log('info', 'screen-capture', 'screen capture cancelled by user');
          pendingSegmentsRef.current.clear();
          return;
        }
        const message = err instanceof ExternalActionExecutionError
          ? err.userMessage
          : err instanceof Error ? err.message : String(err);
        setError(message);
        log('error', 'turn', 'segment send failed', { error: message, elapsedMs: Date.now() - turnStartedAt });
        pendingSegmentsRef.current.clear();
        if (err instanceof ExternalActionExecutionError) {
          await stopRecorderIfLive();
          await stopShadowRecorderIfLive();
          await speakSystemNotice(message);
        }
      } finally {
        if (nativeScreenCaptureRequestId) {
          await VassOverlay.clearScreenCaptureResult(nativeScreenCaptureRequestId).catch(() => undefined);
        }
        // Only the segment that's still current AND whose turn is still
        // alive owns turn wrap-up — a superseded (or stale-turn) segment's
        // finally must not touch armMic()/bargedInRef, since a NEWER
        // segment (or its own eventual finally) is
        // responsible for that now.
        if (myGeneration === segmentGenerationRef.current && myTurn === turnGenerationRef.current) {
          if (!bargedInRef.current) await armMicUnlessPaused();
        }
      }
    },
    [armMicUnlessPaused, armMic, stopRecorderIfLive, stopShadowRecorderIfLive, armShadowRecorder, rearmRecorderOnly, pendingText]
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
      // A stray VAD tick can still fire after pausedRef flips true but
      // before React has actually torn down this hook's polling interval
      // (useVad's active prop only takes effect on its next render) — this
      // is transitively safe via sendSegment's own pausedRef check now
      // (see its comment), but bailing out here too avoids uselessly
      // starting a stopRecorderIfLive() call at all. Defense in depth,
      // same reasoning as handleSpeechStart's own guard below.
      if (pausedRef.current) return;
      if (silenceDurationMs < CHECK_SILENCE_THRESHOLD_MS || firstSegmentInFlightRef.current) return;
      firstSegmentInFlightRef.current = true;
      if (activeSpeechMs < MIN_SPEECH_MS) {
        log('debug', 'turn', 'discarding short sound before first segment', { activeSpeechMs });
        // Keep the guard raised for the entire cooldown. Clearing it here
        // used to let every 50ms overlay heartbeat schedule another re-arm,
        // producing dozens of concurrent recorder operations at once.
        setTimeout(() => {
          firstSegmentInFlightRef.current = false;
          void armMicUnlessPaused();
        }, DISCARD_COOLDOWN_MS);
        return;
      }
      void sendSegment(false);
    },
    [sendSegment, armMicUnlessPaused]
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
  // needed — deliberately unchanged from PR #39's original barge-in design
  // — but NOT because sendMessage() is guaranteed done by now: streaming
  // TTS (see sendSegment's handleChunk) can enter 'speaking' well before
  // the SSE stream finishes, so the network call may well still be running
  // here. Left running anyway, same philosophy as a superseded segment
  // elsewhere in this file — bargedInRef (checked directly in handleChunk,
  // not just inferred from the generation counter) stops it from having
  // any further visible effect once it does resolve.
  const handleSpeechStart = useCallback(() => {
    // Same defense-in-depth reasoning as handleSilenceTick's own guard —
    // a stray VAD tick landing in the brief window between pausedRef
    // flipping true and this hook's polling interval actually tearing
    // down would otherwise overwrite 'paused' with 'recording' here
    // directly (this function has no await of its own for a later
    // recheck to hook into, unlike sendSegment/armMic/onBeforeFirstSpeech/
    // resumeConversation — the whole body runs synchronously once
    // invoked, so the ONLY place to guard is the entry point).
    if (pausedRef.current) return;
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
  const usesOverlayBargeIn = isSpeaking && appState !== 'active';
  const interruptionThresholdDb = usesOverlayBargeIn
    ? OVERLAY_INTERRUPTION_THRESHOLD_DB
    : INTERRUPTION_THRESHOLD_DB;
  const interruptionFrames = usesOverlayBargeIn
    ? OVERLAY_INTERRUPTION_FRAMES
    : INTERRUPTION_FRAMES;
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
      thresholdDb: interruptionThresholdDb,
      overlay: usesOverlayBargeIn,
    });
  }, [interruptionThresholdDb, usesOverlayBargeIn]);
  useVad({
    recorder,
    active: micArmed && (state === 'idle' || state === 'recording' || isSpeaking),
    resetKey: micGeneration,
    thresholdDb: isSpeaking ? interruptionThresholdDb : undefined,
    startFrames: isSpeaking ? interruptionFrames : undefined,
    onSpeechStart: handleSpeechStart,
    onSpeechResume: handleSpeechResume,
    onSilenceTick: handleSilenceTick,
    onLevelSample: isSpeaking ? handleSpeakingLevelSample : undefined,
  });

  // Long-press on the avatar — a real-world interruption (a phone call, a
  // knock at the door), not a barge-in: the user wants to step away and
  // pick the conversation back up, not hand the turn to themselves. Stops
  // BOTH playback and listening in place rather than ending the turn, and
  // is deliberately available from every active state, not just 'speaking':
  // - 'speaking': pauses on-device TTS mid-sentence (see pauseSpeaking's own
  //   comment) — the only case with something to actually resume speaking.
  // - 'thinking': stops shadow-capture so the interruption itself (or
  //   silence on the call) is never misheard as a continuation. If the
  //   reply's first sentence hasn't arrived yet, handleChunk's
  //   createStreamingSpeech(..., startPaused) call picks this up once it
  //   does, holding it silently until resumeConversation.
  // - 'recording': discards whatever was captured so far — the user is
  //   leaving mid-thought, not finishing it, so there's nothing to resume
  //   there either.
  // - 'idle': nothing in flight; just stops listening until resumed.
  // Both recorders are stopped unconditionally so a phone call itself can
  // never be captured as a continuation or a barge-in attempt while paused.
  // Gated on pausedRef, not state — set SYNCHRONOUSLY, before any await, so
  // a second long-press landing before this call's own awaits resolve
  // bails out immediately instead of running the stop/pause sequence twice
  // (the same re-entrancy shape firstSegmentInFlightRef already guards
  // elsewhere in this file). See pausedRef's own comment for why state/
  // stateRef aren't reliably fresh enough for this — independent review
  // found the exact gap this closes.
  const pauseConversation = useCallback(async () => {
    if (pausedRef.current) return;
    pausedRef.current = true;
    log('info', 'turn', 'paused by user', { fromState: state });
    pauseSpeaking();
    await stopRecorderIfLive();
    await stopShadowRecorderIfLive();
    setState('paused');
  }, [state, stopRecorderIfLive, stopShadowRecorderIfLive]);

  // Tap on the avatar while paused. If a StreamingSpeech instance is still
  // alive (hasSpeechToResume — mid-reply when paused, or a reply that
  // arrived WHILE paused), pick it back up: rearm the recorder and enter
  // 'speaking' ourselves first (barge-in needs the mic live for the WHOLE
  // resumed playback, same requirement as the original first utterance —
  // see rearmRecorderOnly's call sites elsewhere in this file), then let
  // resumeSpeaking() unblock the pump loop, which repeats whichever
  // sentence was interrupted in full (see createStreamingSpeech's own
  // comment). Nothing else is needed for the rest of that reply — the
  // original sendSegment call is still alive, just suspended, for the
  // entire pause; its own finally block re-arms the mic once the resumed
  // speech actually finishes, exactly as it would for a reply that was
  // never paused at all. Otherwise (paused from idle/recording, or a turn
  // that failed/produced nothing speakable while paused) there's nothing to
  // resume speaking — just go back to fresh listening. Gated on pausedRef,
  // cleared SYNCHRONOUSLY before any await for the same double-tap
  // re-entrancy reason as pauseConversation above — independent review
  // found a rapid double-tap could otherwise both pass a state-based guard
  // and both call rearmRecorderOnly(), which throws on a genuine
  // concurrent double-prepare.
  const resumeConversation = useCallback(async () => {
    if (!pausedRef.current) return;
    pausedRef.current = false;
    if (externalMediaWaitingRef.current) {
      externalMediaWaitingRef.current = false;
      await setAudioModeAsync({
        playsInSilentMode: true,
        allowsRecording: true,
        interruptionMode: 'doNotMix',
        shouldPlayInBackground: backgroundRecordingEnabledRef.current,
        allowsBackgroundRecording: backgroundRecordingEnabledRef.current,
      });
      log('info', 'external-action', 'external media wait ended, audio session restored');
      await armMic();
      return;
    }
    if (hasSpeechToResume()) {
      log('info', 'turn', 'resumed by user — continuing paused speech');
      await rearmRecorderOnly();
      // Re-check AFTER that (possibly slow, native) rearm — the exact same
      // shape as onBeforeFirstSpeech's own race, just one call site over:
      // a fresh long-press can land while THIS rearm is still in flight,
      // setting pausedRef back to true — committing to 'speaking'/
      // resumeSpeaking() here regardless would silently un-pause audio the
      // user just re-paused, AND (worse) leave pausedRef stuck true with
      // nothing left to ever clear it (this is the only place that does),
      // permanently deadlocking every later turn's createStreamingSpeech
      // call into startPaused. Found by independent review, empirically
      // reproduced. Back off instead: stop the recorder this rearm just
      // armed (pauseConversation's own stop attempt can miss it for the
      // same reason it can in onBeforeFirstSpeech) and leave the
      // instance's own internal pause alone (resumeSpeaking() was never
      // called, so it's genuinely still paused) — a LATER resume tap goes
      // through this same function fresh and completes normally.
      if (pausedRef.current) {
        await stopRecorderIfLive();
        return;
      }
      setState('speaking');
      resumeSpeaking();
    } else {
      log('info', 'turn', 'resumed by user — nothing to continue, listening again');
      await armMic();
    }
  }, [rearmRecorderOnly, armMic, stopRecorderIfLive]);

  // A shared item or accepted screen frame is a device event, not a model
  // reply. Pause the live recorder around its local spoken acknowledgement so
  // Vass never transcribes its own phrase, then restore the prior listening
  // state. A user-selected pause remains a pause.
  const announceSystemNotice = useCallback(async (text: string) => {
    const wasPaused = pausedRef.current;
    if (!wasPaused) await pauseConversation();
    await speakSystemNotice(text);
    if (!wasPaused && mountedRef.current) await resumeConversation();
  }, [pauseConversation, resumeConversation]);

  // Reconfiguring expo-audio's recorder foreground-service mode requires a
  // fresh prepare. Pause first (synchronously guarded by pausedRef), switch
  // the mode while the Activity is visible, then resume through the normal
  // state machine so no second recorder or turn is created.
  const configureBackgroundRecording = useCallback(
    async (enabled: boolean, resumeAfterConfiguration: boolean) => {
      const previous = backgroundRecordingEnabledRef.current;
      if (previous === enabled) {
        if (!enabled && !resumeAfterConfiguration && !pausedRef.current) {
          await pauseConversation();
        }
        return;
      }

      if (enabled && Platform.OS === 'android') {
        let permission = await Notifications.getPermissionsAsync();
        if (!permission.granted) permission = await Notifications.requestPermissionsAsync();
        if (!permission.granted) {
          throw new Error('Разрешите уведомления для фонового голосового режима');
        }
      }

      const wasPaused = pausedRef.current;
      backgroundRecordingEnabledRef.current = enabled;
      try {
        if (!wasPaused) await pauseConversation();
        await setAudioModeAsync({
          playsInSilentMode: true,
          allowsRecording: true,
          interruptionMode: 'doNotMix',
          shouldPlayInBackground: enabled,
          allowsBackgroundRecording: enabled,
        });
        if (!wasPaused && resumeAfterConfiguration) await resumeConversation();
        log('info', 'overlay', 'background recording mode changed', { enabled });
      } catch (err) {
        backgroundRecordingEnabledRef.current = previous;
        if (!wasPaused && resumeAfterConfiguration && pausedRef.current) {
          await resumeConversation();
        }
        throw err;
      }
    },
    [pauseConversation, resumeConversation],
  );

  // Manual override: send whatever's been captured so far immediately,
  // regardless of the 1.2s pause timer — same shape as before, just calling
  // straight into sendSegment now that there's no separate ceiling function.
  // Also doubles as a manual interruption while speaking, mirroring
  // yolo.js's yolo-speak-now-btn ("Перебить" during SPEAKING) — a safety
  // valve for on-device VAD/turn-taking that hasn't been calibrated against
  // a real microphone yet. A tap while paused resumes instead — see
  // resumeConversation; pauseConversation itself is triggered by a
  // long-press, not this short-tap handler, so the two gestures don't fight
  // over the same touch.
  const forceFinalize = useCallback(() => {
    if (state === 'paused') {
      void resumeConversation();
    } else if (state === 'speaking') {
      bargedInRef.current = true;
      log('info', 'turn', 'barge-in: manual interruption tap');
      interruptSpeaking();
      setState('recording');
    } else if ((state === 'idle' || state === 'recording') && !firstSegmentInFlightRef.current) {
      firstSegmentInFlightRef.current = true;
      void sendSegment(false);
    } else {
      // Reached during 'thinking' (nothing to interrupt yet — no TTS has
      // started, and shadow-capture already listens for a continuation on
      // its own) or a firstSegmentInFlightRef re-entry. Previously a
      // completely silent no-op with zero trace either way — a real-device
      // report of "tapped and nothing happened" during this session was
      // impossible to distinguish from the tap never reaching this handler
      // at all. Logged now so the next occurrence is diagnosable.
      log('debug', 'turn', 'tap ignored — nothing to do in this state', {
        state,
        firstSegmentInFlight: firstSegmentInFlightRef.current,
      });
    }
  }, [state, sendSegment, resumeConversation]);

  // Exposed for useGreeting.ts: true only once armMic() has actually
  // succeeded (rearmRecorderOnly() → recorder.prepareToRecordAsync() +
  // record()), which itself only completes after mic permission is
  // granted -- unlike `state === 'idle'`, which is true from this hook's
  // very first render (useState<VoiceState>('idle')'s initial value),
  // well before the permission dialog has even been shown. Monotonic
  // (never reset back to false once armed), matching what a caller
  // actually wants to know: "has the mic genuinely been armed at least
  // once this session."
  return {
    state,
    transcript,
    reply,
    error,
    forceFinalize,
    pauseConversation,
    resumeConversation,
    announceSystemNotice,
    configureBackgroundRecording,
    micArmed,
  };
}
