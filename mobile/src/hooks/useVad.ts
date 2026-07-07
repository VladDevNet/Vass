import { useEffect, useRef } from 'react';
import type { AudioRecorder } from 'expo-audio';
import { log } from '../logging/remoteLogger';

const POLL_INTERVAL_MS = 50;

// ~250ms of sustained sound before treating it as real speech (not a click
// or door slam) — same debounce shape as the web client's proven VAD
// (frontend/js/yolo.js's START_SPEECH_FRAMES, also 5 frames at a 50ms tick).
const START_SPEECH_FRAMES = 5;

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
  thresholdDb?: number;
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
  thresholdDb = DEFAULT_THRESHOLD_DB,
  onSpeechStart,
  onSpeechResume,
  onSilenceTick,
}: UseVadOptions): void {
  const smoothedRef = useRef(-160);
  const speechFramesRef = useRef(0);
  const resumeFramesRef = useRef(0);
  const hasSpokenRef = useRef(false);
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

  useEffect(() => {
    // Fresh state every time this (re)arms — mirrors yolo.js's
    // startUserListening() resetting hasSpoken/consecutiveSpeechFrames/etc.
    // at the start of each turn. Does NOT re-run mid-turn just because the
    // underlying native recorder briefly stops/restarts for a
    // completeness-check snapshot (see useVoiceChat) — active/recorder stay
    // the same across that, so this state correctly persists across it.
    smoothedRef.current = -160;
    speechFramesRef.current = 0;
    resumeFramesRef.current = 0;
    hasSpokenRef.current = false;
    isSilentRef.current = false;
    speechStartAtRef.current = 0;
    lastSpeechAtRef.current = 0;

    if (!active) return;

    const interval = setInterval(() => {
      const { metering } = recorder.getStatus();
      if (metering === undefined) return; // metering not enabled, or recorder not started yet

      smoothedRef.current = smoothedRef.current * 0.75 + metering * 0.25;
      const now = Date.now();

      if (smoothedRef.current > thresholdDb) {
        speechFramesRef.current++;
        resumeFramesRef.current++;

        if (!hasSpokenRef.current) {
          if (speechFramesRef.current >= START_SPEECH_FRAMES) {
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
    }, POLL_INTERVAL_MS);

    return () => clearInterval(interval);
  }, [active, recorder, thresholdDb]);
}
