import { useEffect, useRef } from 'react';
import { AppState, type AppStateStatus } from 'react-native';
import { speakGreeting } from '../tts/systemSpeech';

// Presentational-only, same architectural boundary as useSleepTimer.ts (see
// its own comment) — reacts to a `ready` signal from the caller, never
// touches useVoiceChat.ts or VoiceState directly.
//
// PROJECT-AUDIT-2026-07-10 section 6 ("Приветствие при открытии/возврате
// фокуса приложения"): a direct port of frontend/js/yolo.js's greeting
// (commit 2b28a10, GREETING_FILLERS played on "YOLO start" and
// focus-return), onto on-device TTS (expo-speech, via speakGreeting) instead
// of the web version's server-synthesized Piper WAV files — that server path
// no longer exists on mobile (PR #27).
//
// Two independent triggers, both calling the same speakGreeting():
// 1. Cold start — fires exactly once, the first time `ready` becomes true
//    (session established, mic armed, resting in 'idle').
// 2. Focus-return — fires when AppState transitions to 'active' after having
//    genuinely left it (background/inactive) at least once since the last
//    greeting. Deliberately independent of `ready`/the voice loop's own
//    state, not gated on it: background/foreground mic recovery isn't
//    implemented yet (docs/react-native/audio-and-vad.md's own "Android —
//    foreground service" future item) — a one-shot greeting doesn't need to
//    wait on that separate, larger gap. Guarded on hasGreetedRef so this
//    can't fire before the cold-start greeting ever has (e.g. the user
//    backgrounds the app before mic permission/setup even finishes).
export function useGreeting(ready: boolean): void {
  const hasGreetedRef = useRef(false);
  const hasBeenBackgroundedRef = useRef(false);

  useEffect(() => {
    if (!ready || hasGreetedRef.current) return;
    hasGreetedRef.current = true;
    speakGreeting();
  }, [ready]);

  useEffect(() => {
    const subscription = AppState.addEventListener('change', (next: AppStateStatus) => {
      if (next === 'background' || next === 'inactive') {
        hasBeenBackgroundedRef.current = true;
        return;
      }
      if (next === 'active' && hasBeenBackgroundedRef.current && hasGreetedRef.current) {
        hasBeenBackgroundedRef.current = false;
        speakGreeting();
      }
    });
    return () => subscription.remove();
  }, []);
}
