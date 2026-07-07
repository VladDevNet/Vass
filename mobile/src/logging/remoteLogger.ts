import { getToken } from '../api/client';

const API_URL = process.env.EXPO_PUBLIC_API_URL ?? 'https://vass.it-consult.services';

// Groups every log line from one app launch together server-side, so a
// debugging session can be reviewed as "what happened during this run"
// rather than an undifferentiated stream. Doesn't need cryptographic
// quality, just uniqueness — avoids pulling in expo-crypto for one string.
const RUN_ID = `${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 8)}`;

export type LogLevel = 'debug' | 'info' | 'warn' | 'error';

interface BufferedEntry {
  clientTimestamp: string;
  runId: string;
  level: LogLevel;
  category: string;
  message: string;
  data?: string;
}

// Bounded so a long offline stretch can't grow this without limit — drops
// the OLDEST entries first, since for debugging a live session, what just
// happened matters more than what happened many minutes ago and never sent.
const MAX_BUFFER = 500;
const FLUSH_INTERVAL_MS = 5000;
const FLUSH_THRESHOLD = 20;

const buffer: BufferedEntry[] = [];
let flushing = false;

function safeStringify(data: unknown): string | undefined {
  if (data === undefined) return undefined;
  try {
    return JSON.stringify(data);
  } catch {
    return String(data);
  }
}

// Synchronous and non-blocking by design — every call site in the voice
// loop needs to log without ever awaiting or risking a network call slow
// down a VAD tick or TTS callback. Also mirrors to console so real-time
// adb logcat viewing stays useful alongside the persisted remote copy.
export function log(level: LogLevel, category: string, message: string, data?: unknown): void {
  const consoleFn = level === 'error' ? console.error : level === 'warn' ? console.warn : console.log;
  consoleFn(`[${category}] ${message}`, data ?? '');

  buffer.push({
    clientTimestamp: new Date().toISOString(),
    runId: RUN_ID,
    level,
    category,
    message,
    data: safeStringify(data),
  });
  if (buffer.length > MAX_BUFFER) buffer.shift();
  if (buffer.length >= FLUSH_THRESHOLD) void flush();
}

// Best-effort: failures just put the entries back for the next attempt
// (capped by MAX_BUFFER same as above) rather than throwing — a logging
// pipeline must never be the thing that breaks the voice loop it's meant to
// diagnose.
export async function flush(): Promise<void> {
  if (flushing || buffer.length === 0) return;
  const token = getToken();
  if (!token) return; // not logged in yet/anymore — nothing to attribute these to

  flushing = true;
  const entries = buffer.splice(0, buffer.length);
  try {
    await fetch(`${API_URL}/api/v1/client-logs/batch`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${token}` },
      body: JSON.stringify({ entries }),
    });
  } catch {
    buffer.unshift(...entries);
    while (buffer.length > MAX_BUFFER) buffer.shift();
  } finally {
    flushing = false;
  }
}

setInterval(() => void flush(), FLUSH_INTERVAL_MS);
