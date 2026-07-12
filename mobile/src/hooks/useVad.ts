import { useEffect, useRef } from 'react';
import type { AudioRecorder } from 'expo-audio';
import { log } from '../logging/remoteLogger';
import { VassOverlay } from '../../modules/vass-overlay';

const POLL_INTERVAL_MS = 50;

// Throttle for the optional onLevelSample diagnostic callback — every tick
// would be far too noisy to actually read back later.
const SAMPLE_INTERVAL_MS = 1000;

// ~250ms of sustained sound before treating it as real speech (not a click
// or door slam) — same debounce shape as the web client's proven VAD
// (frontend/js/yolo.js's START_SPEECH_FRAMES, also 5 frames at a 50ms tick).
// The caller can raise this (see UseVadOptions.startFrames) for a stricter
// bar — e.g. barge-in, where yolo.js requires 10 frames (~500ms) instead of
// 5 before treating sound during playback as a real interruption.
const DEFAULT_START_SPEECH_FRAMES = 5;

// Shorter debounce for speech RESUMING after a mid-utterance pause — yolo.js
// uses a lighter 2-frame (100ms) bar here than the 5-frame initial-onset
// bar, since by this point we already know the person is mid-thought, not
// judging whether a stray sound is speech at all.
const RESUME_SPEECH_FRAMES = 2;

// expo-audio's metering is dBFS (-160..0, see AudioRecorder.kt's
// 20*log10(amplitude/32767)) — a different scale than the web client's
// Web Audio RMS (~0..1), so this is a fresh starting guess for "quiet room
// vs. someone talking at conversational distance," not a converted
// constant. Real-device logs from the VAD-foundation checkpoint show actual
// speech reading -18 to -29dB smoothed against this threshold — a healthy
// margin, so left unchanged here.
export const DEFAULT_THRESHOLD_DB = -35;

export interface UseVadOptions {
  recorder: AudioRecorder;
  // Only polls while true — the caller arms/disarms per conversation turn
  // (idle+recording = armed, thinking+speaking = not, until barge-in lands).
  active: boolean;
  // Bump (any new value — a counter works well) to force a state reset on a
  // genuinely new turn even when `active` itself doesn't change value. This
  // matters because `active` is a boolean OR of several caller states (e.g.
  // useVoiceChat's main call: idle/recording/speaking all count) — a turn
  // that starts and ends entirely within "idle or recording" (discarded as
  // noise, no completeness-check ever succeeds) never flips `active`
  // false→true, so without this the effect below never reruns and
  // hasSpoken/speechStartAt/lastSpeechAt stay frozen from the PREVIOUS
  // turn's utterance. On the very next tick, "now - stale lastSpeechAt"
  // already exceeds any real silence ceiling, firing it again immediately
  // with the old, frozen activeSpeechMs — a tight loop confirmed on a real
  // device (identical activeSpeechMs across dozens of cycles 1ms apart from
  // the preceding "mic armed" log line). Optional and independent of
  // active/recorder/thresholdDb/startFrames below — omit if the caller's
  // `active` already goes false between every turn (e.g. shadow-capture's
  // thinking-only call doesn't need this).
  resetKey?: number | string;
  thresholdDb?: number;
  // How many consecutive loud frames before treating sound as real speech —
  // see DEFAULT_START_SPEECH_FRAMES. Raised by the caller for a stricter,
  // slower-to-trigger bar (barge-in during TTS playback).
  startFrames?: number;
  // Fires once per armed period, on the initial speech onset.
  onSpeechStart: () => void;
  // Fires each time speech resumes after a mid-utterance pause (a shorter
  // debounce than onSpeechStart — see RESUME_SPEECH_FRAMES). Turn-taking
  // policy (useVoiceChat) uses this to cancel a pending completeness-check
  // wait once the speaker is clearly still going.
  onSpeechResume: () => void;
  // Fires on EVERY tick while silent after having spoken, reporting how
  // long it's been quiet and how long the utterance-so-far has run. This
  // hook has no built-in "timeout" concept — it just reports the raw
  // ongoing silence duration every 50ms; the caller decides what to do
  // with it (a single fixed cutoff, a staged completeness-check schedule,
  // whatever turn-taking policy fits). Kept this way specifically so
  // useVoiceChat can implement real turn-taking without useVad needing to
  // know anything about completeness-checks.
  onSilenceTick: (silenceDurationMs: number, activeSpeechMs: number) => void;
  // Optional, throttled (~once/SAMPLE_INTERVAL_MS) level report, regardless
  // of threshold-crossing state — unlike every other callback above, fires
  // even while quiet the whole time. Added for barge-in diagnostics (see
  // useVoiceChat.ts): with no signal at all when nothing crosses
  // INTERRUPTION_THRESHOLD_DB, there's no way to tell "user tried to
  // interrupt but wasn't loud/sustained enough" apart from "mic heard
  // nothing at all" after the fact. Opt-in and independent of the
  // speech/silence state machine above, so existing callers are unaffected.
  // Reports the PEAK level observed since the last sample, not a point-in-
  // time reading at the sample tick — a 1-second throttle on an instant
  // value would routinely miss a loud-but-brief (<1s) sound entirely, which
  // is exactly the "heard it but not long enough" case this exists to
  // catch. Found by independent review.
  onLevelSample?: (peakSmoothedDb: number, peakRawMetering: number) => void;
}

// Direct RMS-style port of yolo.js's startVadLoop (frontend/js/yolo.js:406-506):
// exponential smoothing + frame-debounced state transitions, just fed
// expo-audio's synchronous dBFS metering instead of Web Audio API sample
// arrays. No native ML module — this is the same plain arithmetic the web
// client already proved out, not the Silero VAD earlier docs aspired to
// (see audio-and-vad.md and this feature's plan for how that was confirmed).
export function useVad({
  recorder,
  active,
  resetKey,
  thresholdDb = DEFAULT_THRESHOLD_DB,
  startFrames = DEFAULT_START_SPEECH_FRAMES,
  onSpeechStart,
  onSpeechResume,
  onSilenceTick,
  onLevelSample,
}: UseVadOptions): void {
  const smoothedRef = useRef(-160);
  const speechFramesRef = useRef(0);
  const resumeFramesRef = useRef(0);
  const hasSpokenRef = useRef(false);
  const sinceLastSampleMsRef = useRef(0);
  const peakSmoothedSinceSampleRef = useRef(-160);
  const peakRawSinceSampleRef = useRef(-160);
  // True once we've registered "currently silent" after having spoken —
  // lets onSpeechResume fire only on an actual quiet->loud transition, not
  // on every loud frame while already mid-utterance.
  const isSilentRef = useRef(false);
  const speechStartAtRef = useRef(0);
  const lastSpeechAtRef = useRef(0);

  // Latest callbacks in refs so the polling effect below only restarts when
  // active/recorder/thresholdDb actually change — not on every render just
  // because the caller passed fresh closures.
  const onSpeechStartRef = useRef(onSpeechStart);
  onSpeechStartRef.current = onSpeechStart;
  const onSpeechResumeRef = useRef(onSpeechResume);
  onSpeechResumeRef.current = onSpeechResume;
  const onSilenceTickRef = useRef(onSilenceTick);
  onSilenceTickRef.current = onSilenceTick;
  const onLevelSampleRef = useRef(onLevelSample);
  onLevelSampleRef.current = onLevelSample;

  useEffect(() => {
    // Fresh state every time this (re)arms — mirrors yolo.js's
    // startUserListening() resetting hasSpoken/consecutiveSpeechFrames/etc.
    // at the start of each turn. Does NOT re-run mid-turn just because the
    // underlying native recorder briefly stops/restarts for a
    // completeness-check snapshot (see useVoiceChat) — active/recorder stay
    // the same across that, so this state correctly persists across it. DOES
    // re-run on every genuinely new turn via resetKey, even when active
    // itself doesn't change value (see its own comment above) — required
    // for turns that start and end without active ever going false.
    smoothedRef.current = -160;
    speechFramesRef.current = 0;
    resumeFramesRef.current = 0;
    hasSpokenRef.current = false;
    isSilentRef.current = false;
    speechStartAtRef.current = 0;
    lastSpeechAtRef.current = 0;
    sinceLastSampleMsRef.current = 0;
    peakSmoothedSinceSampleRef.current = -160;
    peakRawSinceSampleRef.current = -160;

    if (!active) return;

    let lastTickAt = 0;
    const tick = () => {
      const tickAt = Date.now();
      // During the foreground/background transition a final JS timer and
      // the first native heartbeat can land almost together. Treat them as
      // one frame so debounce/silence accounting does not run twice.
      if (tickAt - lastTickAt < POLL_INTERVAL_MS / 2) return;
      lastTickAt = tickAt;
      const { metering } = recorder.getStatus();
      if (metering === undefined) return; // metering not enabled, or recorder not started yet

      smoothedRef.current = smoothedRef.current * 0.75 + metering * 0.25;
      const now = Date.now();

      if (onLevelSampleRef.current) {
        peakSmoothedSinceSampleRef.current = Math.max(peakSmoothedSinceSampleRef.current, smoothedRef.current);
        peakRawSinceSampleRef.current = Math.max(peakRawSinceSampleRef.current, metering);
        sinceLastSampleMsRef.current += POLL_INTERVAL_MS;
        if (sinceLastSampleMsRef.current >= SAMPLE_INTERVAL_MS) {
          sinceLastSampleMsRef.current = 0;
          onLevelSampleRef.current(
            Math.round(peakSmoothedSinceSampleRef.current * 10) / 10,
            Math.round(peakRawSinceSampleRef.current * 10) / 10
          );
          peakSmoothedSinceSampleRef.current = -160;
          peakRawSinceSampleRef.current = -160;
        }
      }

      if (smoothedRef.current > thresholdDb) {
        speechFramesRef.current++;
        resumeFramesRef.current++;

        if (!hasSpokenRef.current) {
          if (speechFramesRef.current >= startFrames) {
            hasSpokenRef.current = true;
            isSilentRef.current = false;
            speechStartAtRef.current = now;
            lastSpeechAtRef.current = now;
            log('info', 'vad', 'speech detected', {
              smoothedDb: Math.round(smoothedRef.current * 10) / 10,
              thresholdDb,
              rawMetering: metering,
            });
            onSpeechStartRef.current();
          }
        } else {
          lastSpeechAtRef.current = now;
          if (isSilentRef.current && resumeFramesRef.current >= RESUME_SPEECH_FRAMES) {
            isSilentRef.current = false;
            log('info', 'vad', 'speech resumed');
            onSpeechResumeRef.current();
          }
        }
      } else {
        speechFramesRef.current = 0;
        resumeFramesRef.current = 0;
        if (hasSpokenRef.current) {
          isSilentRef.current = true;
          onSilenceTickRef.current(now - lastSpeechAtRef.current, lastSpeechAtRef.current - speechStartAtRef.current);
        }
      }
    };

    const interval = setInterval(tick, POLL_INTERVAL_MS);
    // React Native pauses JavaScript timers in onHostPause. The native
    // overlay foreground service emits this heartbeat only while its window
    // is actually visible, keeping the SAME VAD state machine moving in the
    // background without creating a second recorder or JS runtime.
    const removeOverlayListener = VassOverlay.addListener((event) => {
      if (event.type === 'vadTick') tick();
    });

    return () => {
      clearInterval(interval);
      removeOverlayListener();
    };
  }, [active, recorder, thresholdDb, startFrames, resetKey]);
}
