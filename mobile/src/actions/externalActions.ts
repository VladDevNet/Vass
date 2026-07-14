import { AppState, Linking, Platform } from 'react-native';
import { VassOverlay } from '../../modules/vass-overlay';
import type { ExternalActionEvent } from '../api/client';

const YOUTUBE_VIDEO_ID = /^[A-Za-z0-9_-]{11}$/;
const MAX_QUERY_LENGTH = 200;

export class ExternalActionExecutionError extends Error {
  constructor(public readonly userMessage: string, public readonly resultCode: string, options?: ErrorOptions) {
    super(userMessage, options);
    this.name = 'ExternalActionExecutionError';
  }
}

export interface LocalActionReceipt {
  status: 'handler_dispatched';
  resultCode: 'navigation_handler_dispatched' | 'external_handler_dispatched';
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

export async function executeExternalAction(action: ExternalActionEvent): Promise<LocalActionReceipt> {
  if (action.type === 'open_vass') {
    if (Platform.OS === 'android' && VassOverlay.isAvailable()) {
      try {
        await VassOverlay.openApp();
        return { status: 'handler_dispatched', resultCode: 'navigation_handler_dispatched' };
      } catch (error) {
        throw new ExternalActionExecutionError('Не удалось открыть Vass полностью.', 'navigation_handler_failed', { cause: error });
      }
    }

    if (AppState.currentState === 'active') {
      return { status: 'handler_dispatched', resultCode: 'navigation_handler_dispatched' };
    }
    throw new ExternalActionExecutionError('Не удалось вернуться в Vass на этом устройстве.', 'navigation_handler_failed');
  }

  const url = buildExternalActionUrl(action);
  if (!url) throw new ExternalActionExecutionError('Не удалось распознать, что открыть в YouTube.', 'invalid_action');

  try {
    if (Platform.OS === 'android' && VassOverlay.isAvailable()) {
      await VassOverlay.openExternalUrl(url);
    } else {
      await Linking.openURL(url);
    }
    // Android/Linking can only confirm hand-off to a handler. It cannot know
    // whether YouTube played the selected item, so never upgrade this receipt.
    return { status: 'handler_dispatched', resultCode: 'external_handler_dispatched' };
  } catch (error) {
    throw new ExternalActionExecutionError('Не удалось открыть YouTube или браузер.', 'external_handler_failed', { cause: error });
  }
}
