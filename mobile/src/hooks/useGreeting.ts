import { useEffect, useRef } from 'react';
import { AppState, type AppStateStatus } from 'react-native';
import { speakGreeting } from '../tts/systemSpeech';

// Presentational-only, same architectural boundary as useSleepTimer.ts (see
// its own comment) — reacts to a `ready` signal from the caller, never
// touches useVoiceChat.ts or VoiceState directly.
//
// A mobile-native take on frontend/js/yolo.js's greeting (commit 2b28a10,
// GREETING_FILLERS played on "YOLO start" and focus-return), onto on-device
// TTS (expo-speech, via speakGreeting) instead of the web version's
// server-synthesized Piper WAV files — that server path no longer exists on
// mobile (PR #27).
//
// Two independent triggers, both calling the same speakGreeting():
// 1. Cold start — fires exactly once, the first time `ready` becomes true
//    (HomeScreen.tsx passes state === 'idle' && !!sessionId).
// 2. Focus-return — fires when AppState transitions to 'active' after
//    having genuinely left it (background/inactive) at least once SINCE
//    the cold-start greeting already happened, and only while `ready` is
//    still true at the moment of return.
//
// The guards below close real issues found in review, not hypothetical
// ones:
//
// - The CALLER's `ready` must not go true from mere UI/state defaults --
//   it must reflect the mic having genuinely been armed. The OS's own
//   mic-permission dialog (requestRecordingPermissionsAsync, in
//   useVoiceChat.ts's one-time setup effect) triggers an
//   Activity.onPause/onResume on Android -- which React Native's own
//   AppState module maps directly to a background/active transition --
//   and iOS's 'inactive' state is documented to cover exactly "asking for
//   permissions" too (see
//   node_modules/react-native/Libraries/AppState/AppState.d.ts). An
//   earlier version of this hook tried to guard against that dialog's own
//   open/close being mistaken for a real focus-return by only tracking
//   "genuinely left" AFTER hasGreetedRef was already true -- that doesn't
//   work: `state === 'idle'` (useVoiceChat.ts's useState<VoiceState>
//   initial value) is true from this hook's very FIRST render, well
//   before the permission dialog can even appear, so hasGreetedRef was
//   already true by the time that dialog-induced background event fired,
//   and the guard passed through the exact case it was meant to block.
//   The real fix lives in the caller: HomeScreen.tsx now passes
//   `micArmed && state === 'idle' && !!sessionId` as `ready`, and
//   micArmed only becomes true once armMic() has actually succeeded --
//   which itself can't happen until permission is granted (see
//   useVoiceChat.ts's own comment on micArmed). That closes the race at
//   its root: `ready` simply can't go true until well after any
//   permission dialog has already been resolved, so a background event
//   from that dialog can never land while hasGreetedRef is still false.
//
// - The focus-return greeting itself is gated on readyRef.current (kept in
//   sync with the `ready` prop via its own effect, since the AppState
//   listener's closure is otherwise fixed at mount) — NOT fired
//   unconditionally on return. The web original this takes inspiration
//   from only ever re-greeted via enterYoloMode(), which itself only
//   proceeds from a fully torn-down DISABLED state, never mid-conversation.
//   Firing unconditionally here would be a materially broader trigger than
//   that — background/foreground mic recovery isn't implemented yet (a
//   separate, larger gap — see docs/react-native/audio-and-vad.md's own
//   "Android — foreground service" future item), so greeting over a turn
//   left stuck mid-flight could talk over an active reply or assert false
//   readiness. Gating on `ready` (idle, session present) keeps this close
//   to the original's own conservative intent without requiring that
//   larger recovery work first.
export function useGreeting(ready: boolean): void {
  const hasGreetedRef = useRef(false);
  const hasBeenBackgroundedRef = useRef(false);
  const readyRef = useRef(ready);
  readyRef.current = ready;

  useEffect(() => {
    if (!ready || hasGreetedRef.current) return;
    hasGreetedRef.current = true;
    speakGreeting();
  }, [ready]);

  useEffect(() => {
    const subscription = AppState.addEventListener('change', (next: AppStateStatus) => {
      if (next === 'background' || next === 'inactive') {
        if (hasGreetedRef.current) hasBeenBackgroundedRef.current = true;
        return;
      }
      if (next === 'active' && hasBeenBackgroundedRef.current && hasGreetedRef.current && readyRef.current) {
        hasBeenBackgroundedRef.current = false;
        speakGreeting();
      }
    });
    return () => subscription.remove();
  }, []);
}
