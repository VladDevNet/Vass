import { useEffect } from 'react';
import { AppState } from 'react-native';
import { activateKeepAwakeAsync, deactivateKeepAwake } from 'expo-keep-awake';
import { log } from '../logging/remoteLogger';

const KEEP_AWAKE_TAG = 'vass-active-conversation';

export function useConversationKeepAwake(active: boolean): void {
  useEffect(() => {
    let disposed = false;

    const sync = async (refreshActivity: boolean) => {
      try {
        // Expo keeps tags across Activity replacement, but FLAG_KEEP_SCREEN_ON
        // belongs to the old window. Remove and re-add the stable tag whenever
        // Vass becomes active so the current Activity receives the flag.
        if (refreshActivity || !active) {
          await deactivateKeepAwake(KEEP_AWAKE_TAG);
        }
        if (!disposed && active && AppState.currentState === 'active') {
          await activateKeepAwakeAsync(KEEP_AWAKE_TAG);
        }
      } catch (err) {
        log('warn', 'app', 'failed to synchronize keep-awake state', {
          error: err instanceof Error ? err.message : String(err),
        });
      }
    };

    void sync(true);
    const subscription = AppState.addEventListener('change', (nextState) => {
      void sync(nextState === 'active');
    });

    return () => {
      disposed = true;
      subscription.remove();
      void deactivateKeepAwake(KEEP_AWAKE_TAG).catch(() => undefined);
    };
  }, [active]);
}
