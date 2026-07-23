import { useEffect, useRef } from 'react';
import * as SecureStore from 'expo-secure-store';
import { AppState, type AppStateStatus } from 'react-native';
import { speakGreeting } from '../tts/systemSpeech';

const LONG_ABSENCE_MS = 4 * 60 * 60 * 1000;

interface GreetingIdentity {
  userId: string;
  displayName: string | null;
}

function presenceKey(userId: string): string {
  return `vass_greeting_presence_${userId}`;
}

function parsePresence(raw: string | null): number | null {
  if (!raw) return null;
  const value = Number(raw);
  return Number.isFinite(value) && value > 0 ? value : null;
}

// Greeting cadence belongs on the device: opening the app is a local UI
// event, and it should remain personal even while the device is offline.
// The timestamp is scoped by account so a shared family tablet never treats
// one person's recent visit as another person's return.
export function useGreeting(ready: boolean, identity: GreetingIdentity | null): void {
  const hasGreetedRef = useRef(false);
  const greetingInFlightRef = useRef(false);
  const hasBeenBackgroundedRef = useRef(false);
  const lastPresenceAtRef = useRef<number | null>(null);
  const readyRef = useRef(ready);
  const identityRef = useRef<GreetingIdentity | null>(identity);
  readyRef.current = ready;
  identityRef.current = identity;

  useEffect(() => {
    // HomeScreen is normally remounted on account change, but resetting here
    // makes the hook correct even if that navigation contract changes later.
    hasGreetedRef.current = false;
    greetingInFlightRef.current = false;
    hasBeenBackgroundedRef.current = false;
    lastPresenceAtRef.current = null;
  }, [identity?.userId]);

  async function recordPresence(currentIdentity: GreetingIdentity): Promise<void> {
    const now = Date.now();
    lastPresenceAtRef.current = now;
    try {
      await SecureStore.setItemAsync(presenceKey(currentIdentity.userId), String(now));
    } catch {
      // SecureStore is unavailable in web preview. The in-memory timestamp
      // still keeps foreground returns natural for the current app run.
    }
  }

  async function greet(): Promise<void> {
    const currentIdentity = identityRef.current;
    if (!currentIdentity || greetingInFlightRef.current || !readyRef.current) return;

    greetingInFlightRef.current = true;
    try {
      let previousPresence = lastPresenceAtRef.current;
      if (previousPresence === null) {
        try {
          previousPresence = parsePresence(await SecureStore.getItemAsync(presenceKey(currentIdentity.userId)));
          lastPresenceAtRef.current = previousPresence;
        } catch {
          // No persisted timestamp means this is a genuine new/long visit.
        }
      }

      // A profile may have changed while SecureStore was being read. Do not
      // speak stale account data into the newly active session.
      if (!readyRef.current || identityRef.current?.userId !== currentIdentity.userId) return;

      const gap = previousPresence === null ? Number.POSITIVE_INFINITY : Date.now() - previousPresence;
      const returning = gap >= 0 && gap < LONG_ABSENCE_MS;
      await recordPresence(currentIdentity);
      hasGreetedRef.current = true;
      speakGreeting(returning ? 'returning' : 'welcome', currentIdentity.displayName);
    } finally {
      greetingInFlightRef.current = false;
    }
  }

  useEffect(() => {
    if (!ready || !identity || hasGreetedRef.current) return;
    void greet();
  }, [identity, ready]);

  useEffect(() => {
    const subscription = AppState.addEventListener('change', (next: AppStateStatus) => {
      if (next === 'background' || next === 'inactive') {
        if (hasGreetedRef.current) {
          hasBeenBackgroundedRef.current = true;
          const currentIdentity = identityRef.current;
          if (currentIdentity) void recordPresence(currentIdentity);
        }
        return;
      }

      if (next === 'active' && hasBeenBackgroundedRef.current && hasGreetedRef.current && readyRef.current) {
        hasBeenBackgroundedRef.current = false;
        void greet();
      }
    });
    return () => subscription.remove();
  }, []);
}
