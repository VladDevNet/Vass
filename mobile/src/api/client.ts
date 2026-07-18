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

const ONBOARDING_DISMISSED_KEY = 'vass_onboarding_dismissed';
const SKIPPED_ANDROID_UPDATE_VERSION_KEY = 'vass_skipped_android_update_version_code';

// Whether the user has ever gotten through (saved or skipped) the profile
// setup screen — separate from whether displayName is actually set, since
// skipping deliberately leaves it null. Without this, a user who skips
// would see the setup screen again on every single app launch, forever.
export async function isOnboardingDismissed(): Promise<boolean> {
  try {
    return (await SecureStore.getItemAsync(ONBOARDING_DISMISSED_KEY)) === '1';
  } catch {
    return false;
  }
}

export async function dismissOnboarding(): Promise<void> {
  try {
    await SecureStore.setItemAsync(ONBOARDING_DISMISSED_KEY, '1');
  } catch {
    // no-op on platforms without a working SecureStore (web preview only)
  }
}

export class ApiError extends Error {
  constructor(message: string, public readonly status?: number, public readonly code?: string) {
    super(message);
    this.name = 'ApiError';
  }
}

// Optional releases may be dismissed once. The stored value is the target
// versionCode, so a later release immediately becomes visible again without
// needing any migration or a "clear skipped updates" setting.
export async function isAndroidUpdateSkipped(versionCode: number): Promise<boolean> {
  try {
    return (await SecureStore.getItemAsync(SKIPPED_ANDROID_UPDATE_VERSION_KEY)) === String(versionCode);
  } catch {
    return false;
  }
}

export async function skipAndroidUpdate(versionCode: number): Promise<void> {
  try {
    await SecureStore.setItemAsync(SKIPPED_ANDROID_UPDATE_VERSION_KEY, String(versionCode));
  } catch {
    // no-op on platforms without a working SecureStore (web preview only)
  }
}

export function isApprovalRequiredError(error: unknown): boolean {
  return error instanceof ApiError && error.code === 'approval_required';
}

// A stalled request (dead wifi, unreachable server) would otherwise hang the
// UI forever — every fetch below is capped and turns a timeout into a clear
// ApiError instead of leaving the caller waiting indefinitely. Checking
// controller.signal.aborted (rather than the caught error's type/name) works
// regardless of how a given fetch implementation labels its abort error.
function timeoutSignal(ms: number): { signal: AbortSignal; cancel: () => void } {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), ms);
  return {
    signal: controller.signal,
    cancel: () => clearTimeout(timer),
  };
}

const DEFAULT_TIMEOUT_MS = 15_000;
const UPLOAD_TIMEOUT_MS = 90_000;
// Short — this gates real-time turn-taking responsiveness (a hung check
// shouldn't stall the conversation for as long as a full audio upload
// reasonably might). check-utterance is a short snippet + one fast Gemini
// side-call server-side, not the full reply.
const CHECK_UTTERANCE_TIMEOUT_MS = 10_000;
// The SSE reply streams for as long as the model takes to finish, not a fixed
// round-trip — matches useVoiceChat's MAX_PLAYBACK_MS precedent for "generous
// cap so the UI can't get stuck forever" rather than a tight request timeout.
const SEND_MESSAGE_TIMEOUT_MS = 60_000;

// Any 401 means the token is dead (expired/revoked). AuthContext registers a
// handler here on mount so it can drop back to the login screen — without
// this, an expired token mid-session left the user stuck on HomeScreen
// hitting "Unauthorized" on every action with no way back except force-quit.
let onUnauthorized: (() => void) | null = null;

export function setUnauthorizedHandler(handler: (() => void) | null): void {
  onUnauthorized = handler;
}

async function handleUnauthorized(): Promise<void> {
  await setToken(null);
  onUnauthorized?.();
}

async function request<T>(path: string, options: RequestInit = {}): Promise<T> {
  const headers: Record<string, string> = {
    'Content-Type': 'application/json',
    ...(options.headers as Record<string, string> | undefined),
  };
  if (token) headers.Authorization = `Bearer ${token}`;

  const { signal, cancel } = timeoutSignal(DEFAULT_TIMEOUT_MS);
  let res: Response;
  try {
    res = await fetch(`${API_URL}/api/v1${path}`, { ...options, headers, signal });
  } catch (err) {
    if (signal.aborted) throw new ApiError('Превышено время ожидания ответа сервера');
    throw err;
  } finally {
    cancel();
  }

  if (res.status === 401) {
    await handleUnauthorized();
    throw new ApiError('Unauthorized', res.status);
  }

  if (!res.ok) {
    const data = await res.json().catch(() => ({}));
    throw new ApiError(data.error ?? data.errors?.join(', ') ?? `HTTP ${res.status}`, res.status, data.code);
  }

  if (res.status === 204) return undefined as T;
  return res.json() as Promise<T>;
}

export interface AuthResponse {
  token?: string;
  approvalRequired?: boolean;
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

export interface ChatMessage {
  id: number;
  role: string;
  content: string;
  createdAt: string;
  audioFileName: string | null;
  attachments: ChatAttachment[];
}

export interface ChatAttachment {
  id: string;
  kind: 'image' | 'document';
  mimeType: string;
  sizeBytes: number;
  originalName: string | null;
}

export interface VisualAsset {
  id: string;
  mimeType: string;
  sizeBytes: number;
  originalFileName: string | null;
}

export interface MemoryStatus {
  availability: 'available' | 'disabled' | 'temporarily_unavailable';
  activeCount: number;
  semanticSearchAvailable: boolean;
}

export interface MemoryItem {
  id: string;
  text: string;
  kind: string;
  category: string;
  revision: number;
  status: string;
  createdAt: string;
  updatedAt: string;
  lastRecalledAt: string | null;
  embeddingState: 'pending' | 'ready' | 'failed';
  attachment: MemoryAttachment | null;
}

export interface MemoryAttachment {
  id: string;
  mimeType: string;
  sizeBytes: number;
  originalFileName: string | null;
}

export interface MemorySearchResult {
  status: 'ok' | 'not_found' | 'disabled' | 'invalid';
  retrieval: 'hybrid' | 'lexical' | 'none';
  items: MemoryItem[];
}

export interface MemoryOperationResult {
  operationId: string;
  status: string;
  code: string;
  memoryItemId: string | null;
  confirmationToken?: string | null;
  confirmationExpiresAt?: string | null;
}

export interface ManagedReminder {
  id: number;
  text: string;
  dueAtUtc: string;
  timeZoneId: string;
  recurrenceRule: string | null;
  status: string;
}

export interface CapabilityHelpItem {
  id: string;
  title: string;
  description: string;
  examples: string[];
  interfaceHint: string;
}

export interface AndroidAppUpdate {
  updateAvailable: boolean;
  mandatory: boolean;
  latestVersion: string | null;
  latestVersionCode: number;
  minimumSupportedVersionCode: number;
  downloadUrl: string | null;
  sha256: string | null;
  releaseNotes: string | null;
}

// Not `extends ChatSession` — GET /chat/sessions/{id} (unlike the list
// endpoint) doesn't return the session's own createdAt, only each
// message's (see ChatController.cs's GetSession).
export interface SessionDetail {
  id: number;
  mode: string;
  title: string;
  messages: ChatMessage[];
  hasMore: boolean;
}

export interface DeviceLink {
  code: string;
  expiresAt: string;
}

// Mirrors VoiceAssistant.API/Controllers/SettingsController.cs's
// SettingsResponse exactly.
export interface Settings {
  displayName: string | null;
  assistantName: string | null;
  avatarId: string | null;
  interfaceLanguage: string;
  openAiApiKey: string | null;
  anthropicApiKey: string | null;
  geminiApiKey: string | null;
  customSystemPrompt: string | null;
  fullTranslation: boolean;
}

// PATCH /settings (PROJECT-AUDIT-2026-07-10 API-01): every field is optional
// and independently applied server-side only if actually sent — omitting a
// field leaves it untouched, so updateNames/updateAvatarId below send only
// what they're changing instead of the old GET-then-PUT-whole-object
// round-trip (which used to silently clobber anything changed concurrently,
// e.g. a customSystemPrompt set moments earlier via the "запомни, говори
// медленнее" voice-command feature). An empty string explicitly clears a
// field to null; omitting it (or sending undefined, which JSON.stringify
// drops from the body entirely) leaves it alone.
interface SettingsPatch {
  displayName?: string;
  assistantName?: string;
  avatarId?: string;
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

  getAndroidAppUpdate: (currentVersionCode: number): Promise<AndroidAppUpdate> =>
    request<AndroidAppUpdate>(`/app-updates/android?currentVersionCode=${encodeURIComponent(String(currentVersionCode))}`),

  getSessions: () => request<ChatSession[]>('/chat/sessions'),

  getSession: (id: number, before?: number, limit = 30) => {
    const params = new URLSearchParams({ limit: String(limit) });
    if (before !== undefined) params.set('before', String(before));
    return request<SessionDetail>(`/chat/sessions/${id}?${params.toString()}`);
  },

  // Elderly-friendly login: generated on an already-logged-in device,
  // redeemed on a brand-new one — see VoiceAssistant.API/Controllers/AuthController.cs.
  createDeviceLink: () => request<DeviceLink>('/auth/device-link', { method: 'POST' }),

  redeemDeviceLink: (code: string) =>
    request<AuthResponse>('/auth/device-link/redeem', {
      method: 'POST',
      body: JSON.stringify({ code }),
    }),

  getSettings: () => request<Settings>('/settings'),

  // Both names save together (one screen, one button), so still a single
  // round-trip — but now sends ONLY these two fields (see SettingsPatch),
  // no preceding GET needed either.
  updateNames: (displayName: string, assistantName: string): Promise<Settings> =>
    request<Settings>('/settings', {
      method: 'PATCH',
      body: JSON.stringify({ displayName, assistantName: assistantName.trim() } satisfies SettingsPatch),
    }),

  updateAvatarId: (avatarId: string): Promise<Settings> =>
    request<Settings>('/settings', {
      method: 'PATCH',
      body: JSON.stringify({ avatarId } satisfies SettingsPatch),
    }),

  getMemoryStatus: (): Promise<MemoryStatus> => request<MemoryStatus>('/memory/status'),

  getMemoryItems: (): Promise<MemoryItem[]> => request<MemoryItem[]>('/memory/items'),

  searchMemory: (query: string): Promise<MemorySearchResult> =>
    request<MemorySearchResult>(`/memory/search?query=${encodeURIComponent(query)}`),

  remember: (text: string, category?: string, operationId?: string): Promise<MemoryOperationResult> =>
    request<MemoryOperationResult>('/memory/remember', {
      method: 'POST',
      body: JSON.stringify({ text, category, operationId }),
    }),

  correctMemory: (id: string, text: string, category?: string, operationId?: string): Promise<MemoryOperationResult> =>
    request<MemoryOperationResult>('/memory/correct', {
      method: 'POST',
      body: JSON.stringify({ id, text, category, operationId }),
    }),

  forgetMemory: (id: string, operationId?: string): Promise<MemoryOperationResult> =>
    request<MemoryOperationResult>('/memory/forget', {
      method: 'POST',
      body: JSON.stringify({ id, operationId }),
    }),

  prepareMemoryClear: (): Promise<MemoryOperationResult> =>
    request<MemoryOperationResult>('/memory/clear/prepare', { method: 'POST', body: '{}' }),

  clearMemory: (operationId: string, confirmationToken: string): Promise<MemoryOperationResult> =>
    request<MemoryOperationResult>('/memory/clear', {
      method: 'POST',
      body: JSON.stringify({ operationId, confirmationToken }),
    }),

  recordActionReceipt: (actionId: string, status: 'handler_dispatched' | 'failed' | 'cancelled', resultCode?: string): Promise<void> =>
    request<void>('/actions/receipts', {
      method: 'POST',
      body: JSON.stringify({ actionId, status, resultCode }),
    }),

  getReminders: (deviceId: string, protocolVersion = 2): Promise<ReminderSyncItem[]> =>
    request<ReminderSyncItem[]>(`/reminders?deviceId=${encodeURIComponent(deviceId)}&protocolVersion=${protocolVersion}`),

  getManagedReminders: (): Promise<ManagedReminder[]> => request<ManagedReminder[]>('/reminders/manage'),

  cancelReminder: (id: number): Promise<Array<{ deviceId: string; localNotificationId: string }>> =>
    request<Array<{ deviceId: string; localNotificationId: string }>>(`/reminders/${id}`, { method: 'DELETE' }),

  getCapabilityHelp: (params: {
    supportsReminders: boolean;
    supportsPeriodicReminders: boolean;
    supportsExternalActions: boolean;
    supportsScreenAnalysis: boolean;
  }): Promise<CapabilityHelpItem[]> => {
    const query = new URLSearchParams({
      supportsReminders: String(params.supportsReminders),
      supportsPeriodicReminders: String(params.supportsPeriodicReminders),
      supportsExternalActions: String(params.supportsExternalActions),
      supportsScreenAnalysis: String(params.supportsScreenAnalysis),
    });
    return request<CapabilityHelpItem[]>(`/capabilities/help?${query.toString()}`);
  },

  markReminderScheduled: (id: number, deviceId: string, localNotificationId: string): Promise<void> =>
    request<void>(`/reminders/${id}/scheduled`, {
      method: 'POST',
      body: JSON.stringify({ deviceId, localNotificationId }),
    }),

  markReminderFailed: (id: number, deviceId: string, error: string): Promise<void> =>
    request<void>(`/reminders/${id}/failed`, {
      method: 'POST',
      body: JSON.stringify({ deviceId, error }),
    }),

  markReminderCancelled: (id: number, deviceId: string): Promise<void> =>
    request<void>(`/reminders/${id}/cancelled`, {
      method: 'POST',
      body: JSON.stringify({ deviceId }),
    }),

  // Uploads a just-recorded clip (from expo-audio's recorder.uri) ahead of
  // sendMessage. Multipart, not JSON — bypasses request()'s Content-Type.
  //
  // expo/fetch's FormData does NOT accept React Native's classic
  // {uri, name, type} file-object shape (that only works with the legacy
  // RN fetch/XHR) — its convertFormDataAsync only handles strings, real
  // Blobs, or objects with .bytes(). Read the recording into an actual
  // Blob first so it matches what expo/fetch's multipart encoder expects.
  uploadAudio: async (uri: string): Promise<{ fileName: string; sizeBytes?: number }> => {
    const extension = uri.split('.').pop() ?? 'm4a';
    const bytes = await new File(uri).bytes();
    const blob = new ExpoBlob([bytes], { type: `audio/${extension}` });

    // expo's convertFormData.ts derives the multipart filename from
    // `part.name` (a property read directly off the appended value), NOT
    // from form.append()'s 3rd argument the web FormData spec uses — see
    // node_modules/expo/src/winter/fetch/convertFormData.ts,
    // getFormDataPartHeaders(). A plain Blob has no .name, so without this
    // the part goes out as `Content-Disposition: form-data; name="file"`
    // with no `filename=` — ASP.NET Core's [ApiController] then can't bind
    // it as IFormFile at all and auto-400s before the action method (and
    // its logging) ever runs. This is exactly the "Upload failed: 400"
    // with nothing in the server logs seen on a real device.
    Object.defineProperty(blob, 'name', { value: `recording.${extension}`, configurable: true });

    const form = new FormData();
    // expo-blob's Blob is structurally incompatible with lib.dom's Blob type
    // purely over an ArrayBuffer-vs-ArrayBufferLike generic in bytes()'s
    // signature — not a real runtime difference. FormData.append() (and
    // expo/fetch's own encoder, which is what actually reads this at
    // runtime) only care that it duck-types as a Blob.
    form.append('file', blob as unknown as Blob, `recording.${extension}`);

    const { signal, cancel } = timeoutSignal(UPLOAD_TIMEOUT_MS);
    let res: Awaited<ReturnType<typeof expoFetch>>;
    try {
      res = await expoFetch(`${API_URL}/api/v1/chat/upload-audio`, {
        method: 'POST',
        headers: token ? { Authorization: `Bearer ${token}` } : undefined,
        body: form,
        signal,
      });
    } catch (err) {
      if (signal.aborted) throw new ApiError('Превышено время ожидания загрузки');
      throw err;
    } finally {
      cancel();
    }
    if (res.status === 401) {
      await handleUnauthorized();
      throw new ApiError('Unauthorized');
    }
    if (!res.ok) throw new ApiError(`Upload failed: ${res.status}`);
    return res.json();
  },

  uploadVisual: async (uri: string, mimeType: string, originalName?: string): Promise<VisualAsset> => {
    const bytes = await new File(uri).bytes();
    const blob = new ExpoBlob([bytes], { type: mimeType });
    const extension = mimeType === 'image/jpeg'
      ? 'jpg'
      : mimeType.split('/')[1]?.replace(/[^a-z0-9]/gi, '') || 'bin';
    const name = originalName?.trim() || `attachment.${extension}`;
    Object.defineProperty(blob, 'name', { value: name, configurable: true });

    const form = new FormData();
    form.append('file', blob as unknown as Blob, name);
    const { signal, cancel } = timeoutSignal(UPLOAD_TIMEOUT_MS);
    let res: Awaited<ReturnType<typeof expoFetch>>;
    try {
      res = await expoFetch(`${API_URL}/api/v1/chat/visual-assets`, {
        method: 'POST',
        headers: token ? { Authorization: `Bearer ${token}` } : undefined,
        body: form,
        signal,
      });
    } catch (err) {
      if (signal.aborted) throw new ApiError('Превышено время ожидания загрузки вложения');
      throw err;
    } finally {
      cancel();
    }
    if (res.status === 401) {
      await handleUnauthorized();
      throw new ApiError('Unauthorized');
    }
    if (!res.ok) {
      const body = await res.json().catch(() => ({}));
      throw new ApiError(body?.error ?? `Не удалось загрузить вложение: ${res.status}`);
    }
    return res.json() as Promise<VisualAsset>;
  },

  deletePendingVisual: async (id: string): Promise<void> => {
    await request<void>(`/chat/visual-assets/${encodeURIComponent(id)}`, { method: 'DELETE' });
  },

  visualAssetContentSource: (id: string): { uri: string; headers?: Record<string, string> } => ({
    uri: `${API_URL}/api/v1/chat/visual-assets/${encodeURIComponent(id)}/content`,
    ...(token ? { headers: { Authorization: `Bearer ${token}` } } : {}),
  }),

  downloadVisualAsset: async (id: string, originalFileName?: string | null): Promise<File> => {
    const safeName = (originalFileName?.replace(/[^A-Za-z0-9._-]/g, '_') || 'attachment.bin').slice(-120);
    const target = new File(Paths.cache, `vass-memory-${id}-${safeName}`);
    return File.downloadFileAsync(
      `${API_URL}/api/v1/chat/visual-assets/${encodeURIComponent(id)}/content`,
      target,
      {
        headers: token ? { Authorization: `Bearer ${token}` } : {},
        idempotent: true,
      },
    );
  },

  // Transcribes a continuation segment WITHOUT triggering a full model
  // reply — POST /chat/check-utterance, already used by the web client
  // (frontend/js/api.js's checkUtteranceComplete) and needing no backend
  // changes. Same Blob-multipart pattern as uploadAudio, but the field name
  // here is `audio` (IFormFile audio in ChatController), not `file`.
  //
  // Repurposed from this project's original turn-taking design (which used
  // it, plus its `complete` judgment, as a preliminary check before every
  // segment — see git history around PR #38): useVoiceChat.ts now sends the
  // FIRST segment of a turn straight to /chat/send instead, optimistically
  // betting a pause means "done." This endpoint's completeness judgment is
  // no longer read at all — only its transcription is used, and only for
  // segments AFTER that optimistic bet turns out wrong (shadow-capture
  // confirms the speaker kept going). /chat/send only accepts EITHER text
  // OR audio, never both (see ChatController.cs's Send action) — an audio
  // continuation can't be combined with the FIRST segment's already-known
  // text in one call, so the continuation needs a separate, cheap
  // transcription-only step before the (now combined) text can be resent.
  checkUtteranceComplete: async (uri: string): Promise<{ transcription: string; complete: boolean }> => {
    const extension = uri.split('.').pop() ?? 'm4a';
    const bytes = await new File(uri).bytes();
    const blob = new ExpoBlob([bytes], { type: `audio/${extension}` });
    Object.defineProperty(blob, 'name', { value: `snapshot.${extension}`, configurable: true });

    const form = new FormData();
    form.append('audio', blob as unknown as Blob, `snapshot.${extension}`);

    const { signal, cancel } = timeoutSignal(CHECK_UTTERANCE_TIMEOUT_MS);
    let res: Awaited<ReturnType<typeof expoFetch>>;
    try {
      res = await expoFetch(`${API_URL}/api/v1/chat/check-utterance`, {
        method: 'POST',
        headers: token ? { Authorization: `Bearer ${token}` } : undefined,
        body: form,
        signal,
      });
    } catch (err) {
      if (signal.aborted) throw new ApiError('Превышено время ожидания проверки');
      throw err;
    } finally {
      cancel();
    }
    if (res.status === 401) {
      await handleUnauthorized();
      throw new ApiError('Unauthorized');
    }
    if (!res.ok) throw new ApiError(`Check failed: ${res.status}`);
    return res.json();
  },

};

export interface SendMessageParams {
  sessionId: number;
  message: string;
  audioFileName?: string;
  deviceId?: string;
  timeZoneId?: string;
  reminderProtocolVersion?: number;
  clientTurnId?: string;
  supportsExternalActions?: boolean;
  supportsScreenAnalysis?: boolean;
  visualAssetId?: string;
  sharedContent?: string;
}

export interface ScreenCaptureRequest {
  prompt: string;
}

export type ActionTaxonomy = 'navigation' | 'external';

type ExternalActionBase = {
  actionId: string;
  taxonomy: ActionTaxonomy;
};

export type ExternalActionEvent =
  | (ExternalActionBase & { type: 'open_vass'; taxonomy: 'navigation'; query?: null; videoId?: null })
  | (ExternalActionBase & { type: 'youtube_search'; taxonomy: 'external'; query: string; videoId?: null })
  | (ExternalActionBase & { type: 'youtube_watch'; taxonomy: 'external'; query?: null; videoId: string });

export interface ReminderEvent {
  id: number;
  text: string;
  dueAtUtc: string;
  timeZoneId: string;
  localNotificationId?: string | null;
}

export interface PeriodicReminderEvent {
  contractVersion: 2;
  id: number;
  text: string;
  startAtUtc: string;
  timeZoneId: string;
  rrule: string;
  localNotificationId?: string | null;
}

export interface ReminderCancelledEvent {
  id: number;
  text: string;
  deliveries: Array<{ deviceId: string; localNotificationId: string | null }>;
}

export interface ReminderSyncItem {
  id: number;
  text: string;
  dueAtUtc: string;
  timeZoneId: string;
  recurrenceRule: string | null;
  status: string;
  deliveryStatus: string | null;
  localNotificationId: string | null;
}

export interface SendMessageCallbacks {
  onTranscription?: (text: string) => void;
  onPreamble?: (text: string) => void;
  onChunk?: (text: string) => void;
  onStats?: (stats: ServerTurnStats) => void;
  onReminder?: (reminder: ReminderEvent) => Promise<void>;
  onPeriodicReminder?: (reminder: PeriodicReminderEvent) => Promise<void>;
  onReminderCancelled?: (reminder: ReminderCancelledEvent) => Promise<void>;
  onExternalAction?: (action: ExternalActionEvent) => Promise<void>;
  onScreenCapture?: (request: ScreenCaptureRequest) => void;
}

export interface ServerTurnStats {
  audioCoreMs?: number;
  audioCoreUsed?: boolean;
  audioCoreFallback?: boolean;
  memoryRecallMs?: number;
  agentMs?: number;
  agentSkipped?: boolean;
  preambleSent?: boolean;
  convertMs?: number;
  transcribeMs?: number;
  speakerIdMs?: number;
  llmFirstTokenMs?: number;
  llmTotalMs?: number;
  translationMs?: number;
}

function parseTurnStats(value: unknown): ServerTurnStats | null {
  if (!value || typeof value !== 'object') return null;
  const candidate = value as Record<string, unknown>;
  const numberValue = (name: keyof ServerTurnStats) =>
    typeof candidate[name] === 'number' && Number.isFinite(candidate[name])
      ? candidate[name] as number
      : undefined;

  return {
    audioCoreMs: numberValue('audioCoreMs'),
    audioCoreUsed: typeof candidate.audioCoreUsed === 'boolean' ? candidate.audioCoreUsed : undefined,
    audioCoreFallback: typeof candidate.audioCoreFallback === 'boolean' ? candidate.audioCoreFallback : undefined,
    memoryRecallMs: numberValue('memoryRecallMs'),
    agentMs: numberValue('agentMs'),
    agentSkipped: typeof candidate.agentSkipped === 'boolean' ? candidate.agentSkipped : undefined,
    preambleSent: typeof candidate.preambleSent === 'boolean' ? candidate.preambleSent : undefined,
    convertMs: numberValue('convertMs'),
    transcribeMs: numberValue('transcribeMs'),
    speakerIdMs: numberValue('speakerIdMs'),
    llmFirstTokenMs: numberValue('llmFirstTokenMs'),
    llmTotalMs: numberValue('llmTotalMs'),
    translationMs: numberValue('translationMs'),
  };
}

function parseExternalAction(value: unknown): ExternalActionEvent | null {
  if (!value || typeof value !== 'object') return null;
  const candidate = value as Record<string, unknown>;
  if (typeof candidate.actionId !== 'string') return null;
  if (candidate.type === 'open_vass' && candidate.taxonomy === 'navigation') {
    return { actionId: candidate.actionId, type: 'open_vass', taxonomy: 'navigation' };
  }
  if (candidate.type === 'youtube_search' && candidate.taxonomy === 'external' && typeof candidate.query === 'string') {
    return { actionId: candidate.actionId, type: 'youtube_search', taxonomy: 'external', query: candidate.query };
  }
  if (candidate.type === 'youtube_watch' && candidate.taxonomy === 'external' && typeof candidate.videoId === 'string') {
    return { actionId: candidate.actionId, type: 'youtube_watch', taxonomy: 'external', videoId: candidate.videoId };
  }
  return null;
}

function parseScreenCapture(value: unknown): ScreenCaptureRequest | null {
  if (!value || typeof value !== 'object') return null;
  const prompt = (value as Record<string, unknown>).prompt;
  return typeof prompt === 'string' && prompt.trim() ? { prompt } : null;
}

function parsePeriodicReminder(value: unknown): PeriodicReminderEvent | null {
  if (!value || typeof value !== 'object') return null;
  const candidate = value as Record<string, unknown>;
  if (candidate.contractVersion !== 2 || typeof candidate.id !== 'number' ||
      typeof candidate.text !== 'string' || typeof candidate.startAtUtc !== 'string' ||
      typeof candidate.timeZoneId !== 'string' || typeof candidate.rrule !== 'string') {
    return null;
  }

  return {
    contractVersion: 2,
    id: candidate.id,
    text: candidate.text,
    startAtUtc: candidate.startAtUtc,
    timeZoneId: candidate.timeZoneId,
    rrule: candidate.rrule,
    localNotificationId: typeof candidate.localNotificationId === 'string'
      ? candidate.localNotificationId
      : null,
  };
}

function parseReminderCancelled(value: unknown): ReminderCancelledEvent | null {
  if (!value || typeof value !== 'object') return null;
  const candidate = value as Record<string, unknown>;
  if (typeof candidate.id !== 'number' || !Number.isSafeInteger(candidate.id) || candidate.id <= 0 ||
      typeof candidate.text !== 'string' || !Array.isArray(candidate.deliveries)) {
    return null;
  }
  const deliveries = candidate.deliveries
    .filter((delivery): delivery is Record<string, unknown> => !!delivery && typeof delivery === 'object')
    .filter(delivery => typeof delivery.deviceId === 'string')
    .map(delivery => ({
      deviceId: delivery.deviceId as string,
      localNotificationId: typeof delivery.localNotificationId === 'string' ? delivery.localNotificationId : null,
    }));
  return { id: candidate.id, text: candidate.text, deliveries };
}

// Streams POST /chat/send (SSE-style `data: {...}` lines) and resolves with
// the full concatenated reply once `[DONE]` arrives. Mirrors the parsing in
// frontend/js/api.js's streamChat — same wire format, same backend endpoint.
// Needs expo/fetch specifically: it's the fetch implementation Expo documents
// for real streaming response bodies (see docs.expo.dev/versions/unversioned/sdk/filesystem
// "Downloading Files with expo/fetch" and the Streams API page).
export async function sendMessage(params: SendMessageParams, callbacks: SendMessageCallbacks = {}): Promise<string> {
  // One timeout spans the initial connection AND the full read loop below —
  // cancel() only runs once the reply is fully streamed (or the attempt has
  // failed), not right after the fetch call resolves.
  const { signal, cancel } = timeoutSignal(SEND_MESSAGE_TIMEOUT_MS);
  try {
    let res: Awaited<ReturnType<typeof expoFetch>>;
    try {
      res = await expoFetch(`${API_URL}/api/v1/chat/send`, {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          ...(token ? { Authorization: `Bearer ${token}` } : {}),
        },
        body: JSON.stringify(params),
        signal,
      });
    } catch (err) {
      if (signal.aborted) throw new ApiError('Превышено время ожидания ответа');
      throw err;
    }

    if (res.status === 401) {
      await handleUnauthorized();
      throw new ApiError('Unauthorized');
    }
    if (res.status === 400) {
      // ChatController.cs distinguishes "nothing intelligible was said"
      // (genuine silence/noise, or the backend's own anti-leak/
      // anti-hallucination guards neutralizing a bad transcription — a
      // normal, expected outcome in a live conversation) from a genuinely
      // malformed request via this body shape. Resolving with '' instead
      // of throwing lets speakReplyAndWrapUp's existing
      // `if (fullReply.trim())` guard handle it exactly like any other
      // no-op turn — silent re-arm, no error banner for what amounts to
      // "didn't quite catch that."
      let noSpeech = false;
      try {
        const body = await res.json();
        noSpeech = body?.error === 'no_speech';
      } catch {
        // not JSON (or already consumed) — fall through to the generic error
      }
      if (noSpeech) return '';
    }
    if (!res.ok || !res.body) {
      throw new ApiError(`Send failed: ${res.status}`);
    }

    const reader = res.body.getReader();
    const decoder = new TextDecoder();
    let buffer = '';
    let fullText = '';

    while (true) {
      let done: boolean, value: Uint8Array | undefined;
      try {
        ({ done, value } = await reader.read());
      } catch (err) {
        if (signal.aborted) throw new ApiError('Превышено время ожидания ответа');
        throw err;
      }
      if (done) break;

      buffer += decoder.decode(value, { stream: true });
      const lines = buffer.split('\n');
      buffer = lines.pop() ?? '';

      for (const line of lines) {
        if (!line.startsWith('data: ')) continue;
        const data = line.slice(6).trim();
        if (data === '[DONE]') return fullText;

        let parsed: Record<string, unknown>;
        try {
          parsed = JSON.parse(data) as Record<string, unknown>;
        } catch {
          // ignore malformed lines, matches the web client's behavior
          continue;
        }

        if (typeof parsed.error === 'string') {
          throw new ApiError(parsed.error);
        } else if (typeof parsed.transcription === 'string') {
          callbacks.onTranscription?.(parsed.transcription);
        } else if (typeof parsed.preamble === 'string') {
          callbacks.onPreamble?.(parsed.preamble);
        } else if (typeof parsed.text === 'string') {
          fullText += parsed.text;
          callbacks.onChunk?.(parsed.text);
        } else if (parsed.reminder && callbacks.onReminder) {
          await callbacks.onReminder(parsed.reminder as ReminderEvent);
        } else if (parsed.periodicReminder && callbacks.onPeriodicReminder) {
          const reminder = parsePeriodicReminder(parsed.periodicReminder);
          if (reminder) await callbacks.onPeriodicReminder(reminder);
        } else if (parsed.reminderCancelled && callbacks.onReminderCancelled) {
          const reminder = parseReminderCancelled(parsed.reminderCancelled);
          if (reminder) await callbacks.onReminderCancelled(reminder);
        } else if (parsed.externalAction && callbacks.onExternalAction) {
          const action = parseExternalAction(parsed.externalAction);
          if (action) await callbacks.onExternalAction(action);
        } else if (parsed.screenCapture && callbacks.onScreenCapture) {
          const request = parseScreenCapture(parsed.screenCapture);
          if (request) callbacks.onScreenCapture(request);
        } else if (parsed.stats && callbacks.onStats) {
          const stats = parseTurnStats(parsed.stats);
          if (stats) callbacks.onStats(stats);
        }
      }
    }

    throw new ApiError('Ответ сервера прервался до завершения. Попробуйте еще раз.');
  } finally {
    cancel();
  }
}
