import { AppState, Linking, Platform } from 'react-native';
import { VassOverlay } from '../../modules/vass-overlay';
import type { ExternalActionEvent } from '../api/client';

const YOUTUBE_VIDEO_ID = /^[A-Za-z0-9_-]{11}$/;
const MAX_QUERY_LENGTH = 200;

export class ExternalActionExecutionError extends Error {
  constructor(public readonly userMessage: string, options?: ErrorOptions) {
    super(userMessage, options);
    this.name = 'ExternalActionExecutionError';
  }
}

export function buildExternalActionUrl(action: ExternalActionEvent): string | null {
  if (action.type === 'youtube_watch') {
    if (!action.videoId || !YOUTUBE_VIDEO_ID.test(action.videoId)) return null;
    return `https://www.youtube.com/watch?v=${action.videoId}`;
  }

  if (action.type === 'youtube_search') {
    const query = action.query?.trim();
    if (!query || query.length > MAX_QUERY_LENGTH) return null;
    return `https://www.youtube.com/results?search_query=${encodeURIComponent(query)}`;
  }

  return null;
}

export async function executeExternalAction(action: ExternalActionEvent): Promise<void> {
  if (action.type === 'open_vass') {
    if (Platform.OS === 'android' && VassOverlay.isAvailable()) {
      try {
        await VassOverlay.openApp();
        return;
      } catch (error) {
        throw new ExternalActionExecutionError('Не удалось открыть Vass полностью.', { cause: error });
      }
    }

    if (AppState.currentState === 'active') return;
    throw new ExternalActionExecutionError('Не удалось вернуться в Vass на этом устройстве.');
  }

  const url = buildExternalActionUrl(action);
  if (!url) throw new ExternalActionExecutionError('Не удалось распознать, что открыть в YouTube.');

  try {
    await Linking.openURL(url);
  } catch (error) {
    throw new ExternalActionExecutionError('Не удалось открыть YouTube или браузер.', { cause: error });
  }
}
