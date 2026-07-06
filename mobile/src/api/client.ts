import * as SecureStore from 'expo-secure-store';

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
};
