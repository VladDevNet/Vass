import { useEffect, useRef } from 'react';
import type { AudioRecorder } from 'expo-audio';
import { log } from '../logging/remoteLogger';

const POLL_INTERVAL_MS = 50;

// ~250ms of sustained sound before treating it as real speech (not a click
// or door slam) — same debounce shape as the web client's proven VAD
// (frontend/js/yolo.js's START_SPEECH_FRAMES, also 5 frames at a 50ms tick).
const START_SPEECH_FRAMES = 5;

// expo-audio's metering is dBFS (-160..0, see AudioRecorder.kt's
// 20*log10(amplitude/32767)) — a different scale than the web client's
// Web Audio RMS (~0..1), so this is a fresh starting guess for "quiet room
// vs. someone talking at conversational distance," not a converted
// constant. Needs real-device calibration, same as the TTS voice work
// earlier in this project — see BACKLOG.md's VAD checkpoint.
export const DEFAULT_THRESHOLD_DB = -35;

export interface UseVadOptions {
  recorder: AudioRecorder;
  // Only polls while true — the caller arms/disarms per conversation turn
  // (idle+recording = armed, thinking+speaking = not, until barge-in lands).
  active: boolean;
  thresholdDb?: number;
  onSpeechStart: () => void;
  // Fires once per armed period, `silenceMs` after the last confirmed-speech
  // frame. Passes how long the speech itself lasted so the caller can filter
  // coughs/knocks (see yolo.js's submitSpeech 450ms filter) — this hook only
  // detects, it doesn't judge what counts as a real utterance.
  onSilenceTimeout: (activeSpeechMs: number) => void;
  silenceMs: number;
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
  onSilenceTimeout,
  silenceMs,
}: UseVadOptions): void {
  const smoothedRef = useRef(-160);
  const speechFramesRef = useRef(0);
  const hasSpokenRef = useRef(false);
  const speechStartAtRef = useRef(0);
  const lastSpeechAtRef = useRef(0);
  const timedOutRef = useRef(false);

  // Latest callbacks in refs so the polling effect below only restarts when
  // active/recorder/thresholdDb/silenceMs actually change — not on every
  // render just because the caller passed fresh closures.
  const onSpeechStartRef = useRef(onSpeechStart);
  onSpeechStartRef.current = onSpeechStart;
  const onSilenceTimeoutRef = useRef(onSilenceTimeout);
  onSilenceTimeoutRef.current = onSilenceTimeout;

  useEffect(() => {
    // Fresh state every time this (re)arms — mirrors yolo.js's
    // startUserListening() resetting hasSpoken/consecutiveSpeechFrames/etc.
    // at the start of each turn.
    smoothedRef.current = -160;
    speechFramesRef.current = 0;
    hasSpokenRef.current = false;
    speechStartAtRef.current = 0;
    lastSpeechAtRef.current = 0;
    timedOutRef.current = false;

    if (!active) return;

    const interval = setInterval(() => {
      const { metering } = recorder.getStatus();
      if (metering === undefined) return; // metering not enabled, or recorder not started yet

      smoothedRef.current = smoothedRef.current * 0.75 + metering * 0.25;

      if (smoothedRef.current > thresholdDb) {
        speechFramesRef.current++;
        if (!hasSpokenRef.current) {
          if (speechFramesRef.current >= START_SPEECH_FRAMES) {
            hasSpokenRef.current = true;
            speechStartAtRef.current = Date.now();
            lastSpeechAtRef.current = Date.now();
            log('info', 'vad', 'speech detected', {
              smoothedDb: Math.round(smoothedRef.current * 10) / 10,
              thresholdDb,
              rawMetering: metering,
            });
            onSpeechStartRef.current();
          }
        } else {
          lastSpeechAtRef.current = Date.now();
        }
      } else {
        speechFramesRef.current = 0;
        if (hasSpokenRef.current && !timedOutRef.current) {
          if (Date.now() - lastSpeechAtRef.current >= silenceMs) {
            timedOutRef.current = true;
            const activeSpeechMs = lastSpeechAtRef.current - speechStartAtRef.current;
            log('info', 'vad', 'silence timeout', { activeSpeechMs, silenceMs });
            onSilenceTimeoutRef.current(activeSpeechMs);
          }
        }
      }
    }, POLL_INTERVAL_MS);

    return () => clearInterval(interval);
  }, [active, recorder, thresholdDb, silenceMs]);
}
