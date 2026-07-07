import * as Speech from 'expo-speech';

// Android's TextToSpeech.speak() hard-rejects (throws) text longer than this
// — expo-speech doesn't chunk automatically. A warm, conversational reply
// from the companion can easily run past it, so long replies are split on
// sentence boundaries and spoken as separate, explicitly sequenced chunks
// (see speakToCompletion — NOT fired all at once relying on native
// auto-queueing; see the comment there for why).
const MAX_CHUNK_LENGTH = Math.min(Speech.maxSpeechInputLength - 100, 3500);

// A stalled/never-firing onDone would otherwise leave the UI stuck in
// 'speaking' forever — same MAX_PLAYBACK_MS safety-net pattern already used
// for network TTS playback in useVoiceChat.ts.
const MAX_SPEECH_MS = 60_000;

// undefined = not looked up yet this session, null = no Russian voice found.
let cachedRussianVoice: string | null | undefined;

async function getRussianVoice(): Promise<string | null> {
  if (cachedRussianVoice !== undefined) return cachedRussianVoice;
  const voices = await Speech.getAvailableVoicesAsync();
  const russian = voices.filter((v) => v.language.toLowerCase().startsWith('ru'));
  const best = russian.find((v) => v.quality === Speech.VoiceQuality.Enhanced) ?? russian[0];
  cachedRussianVoice = best?.identifier ?? null;
  return cachedRussianVoice;
}

function splitIntoChunks(text: string, maxLength: number): string[] {
  const sentences = text.match(/[^.!?…]+[.!?…]*\s*/g) ?? [text];
  const chunks: string[] = [];
  let current = '';

  for (const sentence of sentences) {
    if (current && current.length + sentence.length > maxLength) {
      chunks.push(current);
      current = '';
    }
    if (sentence.length > maxLength) {
      // A single sentence longer than the limit (rare) — hard-split as a last resort.
      for (let i = 0; i < sentence.length; i += maxLength) {
        chunks.push(sentence.slice(i, i + maxLength));
      }
    } else {
      current += sentence;
    }
  }
  if (current) chunks.push(current);
  return chunks;
}

// Speaks one chunk and resolves once it's actually finished — via onDone
// (spoke fully) or onStopped (cut short by our own Speech.stop(), see
// below). speak() itself is fire-and-forget on the JS side: on Android it
// dispatches onto a native background thread (AppContext's own
// HandlerThread) without waiting for the utterance to actually reach the
// native queue. Awaiting THIS promise before speaking the next chunk is
// what keeps chunks strictly sequenced — speak() for chunk N+1 is never
// even called until chunk N's native callback has actually fired, so
// there's nothing left half-submitted for a stray Speech.stop() to race
// against.
function speakChunk(text: string, voice: string): Promise<void> {
  return new Promise((resolve, reject) => {
    Speech.speak(text, {
      language: 'ru-RU',
      voice,
      onDone: () => resolve(),
      onStopped: () => resolve(),
      onError: (err) => reject(err),
    });
  });
}

// Mirrors api.synthesizeSpeech's role in useVoiceChat.stopAndRespond, but
// speaks directly instead of returning a file URI to play — expo-speech has
// no file/bytes output, only start/done/error callbacks.
export async function speakToCompletion(text: string): Promise<void> {
  const voice = await getRussianVoice();
  if (!voice) throw new Error('На устройстве нет русского голоса');

  const chunks = splitIntoChunks(text, MAX_CHUNK_LENGTH);
  if (chunks.length === 0) return;

  let timedOut = false;
  // Stops (not just ignores) playback once the cap is hit — resolving
  // without calling stop() would let a long reply keep talking in the
  // background after the UI has already moved on to 'idle'.
  const timeoutId = setTimeout(() => {
    timedOut = true;
    Speech.stop();
  }, MAX_SPEECH_MS);

  try {
    for (const chunk of chunks) {
      if (timedOut) break;
      await speakChunk(chunk, voice);
    }
  } catch (err) {
    // A mid-reply failure (bad chunk, engine error) — stop rather than
    // leave a partial utterance hanging, since the caller is about to
    // start the network-fallback playback right after this rejects.
    Speech.stop();
    throw err;
  } finally {
    clearTimeout(timeoutId);
  }
}

export function stopSpeaking(): void {
  Speech.stop();
}
