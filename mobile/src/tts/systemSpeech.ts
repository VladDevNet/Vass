import * as Speech from 'expo-speech';

// Android's TextToSpeech.speak() hard-rejects (throws) text longer than this
// — expo-speech doesn't chunk automatically. A warm, conversational reply
// from the companion can easily run past it, so split on sentence
// boundaries and let expo-speech's own queueing ("calling speak() while
// already speaking adds an utterance to queue") play the chunks back-to-back.
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

// Mirrors api.synthesizeSpeech's role in useVoiceChat.stopAndRespond, but
// speaks directly instead of returning a file URI to play — expo-speech has
// no file/bytes output, only start/done/error callbacks.
export async function speakToCompletion(text: string): Promise<void> {
  const voice = await getRussianVoice();
  if (!voice) throw new Error('На устройстве нет русского голоса');

  const chunks = splitIntoChunks(text, MAX_CHUNK_LENGTH);
  if (chunks.length === 0) return;

  return new Promise((resolve, reject) => {
    const timeoutId = setTimeout(resolve, MAX_SPEECH_MS);

    chunks.forEach((chunk, index) => {
      const isLast = index === chunks.length - 1;
      Speech.speak(chunk, {
        language: 'ru-RU',
        voice,
        onError: (err) => {
          clearTimeout(timeoutId);
          // Clear anything still queued behind the failed chunk so it can't
          // keep talking over the network-fallback playback the caller is
          // about to start.
          Speech.stop();
          reject(err);
        },
        ...(isLast
          ? {
              onDone: () => {
                clearTimeout(timeoutId);
                resolve();
              },
            }
          : {}),
      });
    });
  });
}

export function stopSpeaking(): void {
  Speech.stop();
}
