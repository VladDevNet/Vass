import * as SecureStore from 'expo-secure-store';
import { fetch as expoFetch } from 'expo/fetch';
import { File, Paths } from 'expo-file-system';
import { Blob as ExpoBlob } from 'expo-blob';

const API_URL = process.env.EXPO_PUBLIC_API_URL ?? 'https://vass.it-consult.services';
const TOKEN_KEY = 'vass_token';

let token: string | null = null;

// expo-secure-store targets the iOS/Android keychain; it has no working
// implementation on web (throws instead of falling back). Web is only used
// here for fast preview during development, never a real target platform
// (see docs/react-native/BUILD-WSL.md), so on failure we just treat it as
// "no token stored" rather than pull in a web-specific storage backend.
export async function loadStoredToken(): Promise<string | null> {
  try {
    token = await SecureStore.getItemAsync(TOKEN_KEY);
  } catch {
    token = null;
  }
  return token;
}

export function getToken(): string | null {
  return token;
}

export async function setToken(next: string | null): Promise<void> {
  token = next;
  try {
    if (next) {
      await SecureStore.setItemAsync(TOKEN_KEY, next);
    } else {
      await SecureStore.deleteItemAsync(TOKEN_KEY);
    }
  } catch {
    // no-op on platforms without a working SecureStore (web preview only)
  }
}

class ApiError extends Error {}

async function request<T>(path: string, options: RequestInit = {}): Promise<T> {
  const headers: Record<string, string> = {
    'Content-Type': 'application/json',
    ...(options.headers as Record<string, string> | undefined),
  };
  if (token) headers.Authorization = `Bearer ${token}`;

  const res = await fetch(`${API_URL}/api/v1${path}`, { ...options, headers });

  if (res.status === 401) {
    await setToken(null);
    throw new ApiError('Unauthorized');
  }

  if (!res.ok) {
    const data = await res.json().catch(() => ({}));
    throw new ApiError(data.error ?? data.errors?.join(', ') ?? `HTTP ${res.status}`);
  }

  if (res.status === 204) return undefined as T;
  return res.json() as Promise<T>;
}

export interface AuthResponse {
  token: string;
}

export interface CurrentUser {
  id: string;
  email: string;
  nativeLang: string;
  level: string;
  createdAt: string;
  lastActiveAt: string;
}

export interface ChatSession {
  id: number;
  mode: string;
  title: string;
  createdAt: string;
}

export interface DeviceLink {
  code: string;
  expiresAt: string;
}

export const api = {
  register: (email: string, password: string) =>
    request<AuthResponse>('/auth/register', {
      method: 'POST',
      body: JSON.stringify({ email, password }),
    }),

  login: (email: string, password: string) =>
    request<AuthResponse>('/auth/login', {
      method: 'POST',
      body: JSON.stringify({ email, password }),
    }),

  me: () => request<CurrentUser>('/auth/me'),

  getSessions: () => request<ChatSession[]>('/chat/sessions'),

  // Elderly-friendly login: generated on an already-logged-in device,
  // redeemed on a brand-new one — see VoiceAssistant.API/Controllers/AuthController.cs.
  createDeviceLink: () => request<DeviceLink>('/auth/device-link', { method: 'POST' }),

  redeemDeviceLink: (code: string) =>
    request<AuthResponse>('/auth/device-link/redeem', {
      method: 'POST',
      body: JSON.stringify({ code }),
    }),

  // Uploads a just-recorded clip (from expo-audio's recorder.uri) ahead of
  // sendMessage. Multipart, not JSON — bypasses request()'s Content-Type.
  //
  // expo/fetch's FormData does NOT accept React Native's classic
  // {uri, name, type} file-object shape (that only works with the legacy
  // RN fetch/XHR) — its convertFormDataAsync only handles strings, real
  // Blobs, or objects with .bytes(). Read the recording into an actual
  // Blob first so it matches what expo/fetch's multipart encoder expects.
  uploadAudio: async (uri: string): Promise<{ fileName: string }> => {
    const extension = uri.split('.').pop() ?? 'm4a';
    const bytes = await new File(uri).bytes();
    const blob = new ExpoBlob([bytes], { type: `audio/${extension}` });

    const form = new FormData();
    // expo-blob's Blob is structurally incompatible with lib.dom's Blob type
    // purely over an ArrayBuffer-vs-ArrayBufferLike generic in bytes()'s
    // signature — not a real runtime difference. FormData.append() (and
    // expo/fetch's own encoder, which is what actually reads this at
    // runtime) only care that it duck-types as a Blob.
    form.append('file', blob as unknown as Blob, `recording.${extension}`);

    const res = await expoFetch(`${API_URL}/api/v1/chat/upload-audio`, {
      method: 'POST',
      headers: token ? { Authorization: `Bearer ${token}` } : undefined,
      body: form,
    });
    if (!res.ok) throw new ApiError(`Upload failed: ${res.status}`);
    return res.json();
  },

  // Synthesizes the full reply as one WAV clip (buffered, not the sentence-by-
  // sentence PCM stream the web client uses — see docs/react-native/tts-and-avatar.md;
  // that optimization matters less once TTS moves on-device in Phase 2).
  // Returns a local file:// URI ready for createAudioPlayer().
  synthesizeSpeech: async (text: string): Promise<string> => {
    const res = await expoFetch(`${API_URL}/api/v1/chat/tts`, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        ...(token ? { Authorization: `Bearer ${token}` } : {}),
      },
      body: JSON.stringify({ text }),
    });
    if (!res.ok) throw new ApiError(`TTS failed: ${res.status}`);

    const file = new File(Paths.cache, `tts-${Date.now()}.wav`);
    await file.write(await res.bytes());
    return file.uri;
  },
};

export interface SendMessageParams {
  sessionId: number;
  message: string;
  audioFileName?: string;
}

export interface SendMessageCallbacks {
  onTranscription?: (text: string) => void;
  onChunk?: (text: string) => void;
}

// Streams POST /chat/send (SSE-style `data: {...}` lines) and resolves with
// the full concatenated reply once `[DONE]` arrives. Mirrors the parsing in
// frontend/js/api.js's streamChat — same wire format, same backend endpoint.
// Needs expo/fetch specifically: it's the fetch implementation Expo documents
// for real streaming response bodies (see docs.expo.dev/versions/unversioned/sdk/filesystem
// "Downloading Files with expo/fetch" and the Streams API page).
export async function sendMessage(
  params: SendMessageParams,
  callbacks: SendMessageCallbacks = {}
): Promise<string> {
  const res = await expoFetch(`${API_URL}/api/v1/chat/send`, {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
      ...(token ? { Authorization: `Bearer ${token}` } : {}),
    },
    body: JSON.stringify(params),
  });

  if (!res.ok || !res.body) {
    throw new ApiError(`Send failed: ${res.status}`);
  }

  const reader = res.body.getReader();
  const decoder = new TextDecoder();
  let buffer = '';
  let fullText = '';

  while (true) {
    const { done, value } = await reader.read();
    if (done) break;

    buffer += decoder.decode(value, { stream: true });
    const lines = buffer.split('\n');
    buffer = lines.pop() ?? '';

    for (const line of lines) {
      if (!line.startsWith('data: ')) continue;
      const data = line.slice(6).trim();
      if (data === '[DONE]') return fullText;

      try {
        const parsed = JSON.parse(data);
        if (parsed.transcription) {
          callbacks.onTranscription?.(parsed.transcription);
        } else if (typeof parsed.text === 'string') {
          fullText += parsed.text;
          callbacks.onChunk?.(parsed.text);
        }
        // parsed.preamble / parsed.stats: not consumed yet in this first
        // mobile voice-loop increment — see docs/react-native/BACKLOG.md Phase 1.
      } catch {
        // ignore malformed lines, matches the web client's behavior
      }
    }
  }

  return fullText;
}
