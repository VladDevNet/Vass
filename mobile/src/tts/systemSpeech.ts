import * as Speech from 'expo-speech';
import * as SecureStore from 'expo-secure-store';

const VOICE_PREFERENCE_KEY = 'vass_voice_id';

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

export async function listRussianVoices(): Promise<Speech.Voice[]> {
  const voices = await Speech.getAvailableVoicesAsync();
  return voices.filter((v) => v.language.toLowerCase().startsWith('ru'));
}

// Google's Android TTS engine names voices with a `-local`/`-network`
// suffix — the ONLY signal exposed via JS for whether synthesizing with a
// given voice needs a network round-trip. Not a documented public
// contract (expo-speech's Android bridge doesn't surface Android's own
// Voice.isNetworkConnectionRequired(); see
// node_modules/expo-speech/android/.../SpeechModule.kt's getVoices(),
// which aliases Voice.getName() as both `identifier` and `name` — that's
// also why raw identifiers look like "ru-ru-x-rud-network" instead of a
// human name, a real gap in the library, not something fixable from here).
// Empirically consistent enough to act on, and the only way to avoid
// silently defaulting to a voice that defeats the entire point of this
// feature — synthesis without a network hop.
export function isLikelyLocalVoice(identifier: string): boolean {
  return !identifier.endsWith('-network');
}

// The resolved voice actually in effect right now — an explicit preference
// from the settings screen if one was ever set, otherwise the same
// auto-picked default speakToCompletion() would fall back to. Exported so
// the settings screen can highlight "what's currently active" even for
// someone who never explicitly chose anything.
export async function getResolvedVoiceId(): Promise<string | null> {
  return getRussianVoice();
}

export async function setVoicePreference(identifier: string | null): Promise<void> {
  // Update the in-memory cache immediately — otherwise a voice change made
  // from settings mid-session wouldn't take effect until app restart, since
  // getRussianVoice() below only re-reads storage when the cache is unset.
  cachedRussianVoice = identifier;
  try {
    if (identifier) {
      await SecureStore.setItemAsync(VOICE_PREFERENCE_KEY, identifier);
    } else {
      await SecureStore.deleteItemAsync(VOICE_PREFERENCE_KEY);
    }
  } catch {
    // no-op on platforms without a working SecureStore (web preview only) —
    // matches client.ts's setToken
  }
}

// Interrupts whatever's currently playing (a previous preview, if the user
// is tapping through several voices quickly) before starting the new one.
export function previewVoice(identifier: string): void {
  Speech.stop();
  Speech.speak('Привет! Вот как звучит этот голос.', { language: 'ru-RU', voice: identifier });
}

async function getRussianVoice(): Promise<string | null> {
  if (cachedRussianVoice !== undefined) return cachedRussianVoice;

  let stored: string | null = null;
  try {
    stored = await SecureStore.getItemAsync(VOICE_PREFERENCE_KEY);
  } catch {
    // no-op — falls through to auto-pick, same as client.ts's loadStoredToken
  }
  if (stored) {
    cachedRussianVoice = stored;
    return stored;
  }

  const russian = await listRussianVoices();
  const local = russian.filter((v) => isLikelyLocalVoice(v.identifier));
  // Prefer local voices for the auto-picked default — falls back to the
  // full list only if the device genuinely has no local Russian voice at
  // all, rather than leaving the default unset.
  const pool = local.length > 0 ? local : russian;
  const best = pool.find((v) => v.quality === Speech.VoiceQuality.Enhanced) ?? pool[0];
  cachedRussianVoice = best?.identifier ?? null;
  return cachedRussianVoice;
}

// Direct port of frontend/js/voice.js's cleanTextForTts — the LLM reply is
// meant to be spoken, not rendered, but nothing stops it from producing
// markdown (**bold**, bullet lists, headings) or emoji. The system prompt
// already bans emoji for the same reason ("они не озвучиваются и портят
// речь" — CompanionPromptService's Prompts/companion-system.txt) but never
// covered markdown; the web client has carried this defensive strip all
// along, which is exactly why this only showed up as a mobile bug — Speech
// on Android reads "**" and "#" literally instead of silently dropping them
// the way a markdown renderer would.
function stripMarkdownForSpeech(text: string): string {
  return text
    // \u{1F300}-\u{1F5FF} (Misc Symbols and Pictographs — 👍🎉🔥 etc.) added
    // on top of the web version's ranges, which miss that block entirely.
    .replace(
      /[\u{1F300}-\u{1F6FF}\u{1F900}-\u{1FAFF}\u{2600}-\u{27BF}\u{FE00}-\u{FE0F}\u{1F000}-\u{1F02F}\u{200D}\u{20E3}\u{E0020}-\u{E007F}]/gu,
      ''
    )
    .replace(/\[([^\]|]+)\|[^\]]*\]/g, '$1') // [word|translation] → word
    .replace(/\*{1,3}/g, '') // *, **, ***
    .replace(/^-{3,}$/gm, '') // --- horizontal rules
    .replace(/^[\s]*[-•]\s+/gm, '') // bullet points
    .replace(/^[\s]*\d+\.\s+/gm, '') // numbered lists
    .replace(/#+\s*/g, '') // headings
    .replace(/`{1,3}[^`]*`{1,3}/g, '') // inline code
    .replace(/[✓✗✔✘☑☐→←↑↓►▶◀▼▲+]/g, '') // special symbols
    .replace(/\n{2,}/g, '. ') // multiple newlines → pause
    .replace(/\n/g, ' ') // single newlines → space
    .replace(/\s{2,}/g, ' ') // collapse spaces
    .trim();
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

// Speaks one chunk and resolves once it's actually finished via onDone, or
// rejects on onError. speak() itself is fire-and-forget on the JS side: on
// Android it dispatches onto a native background thread (AppContext's own
// HandlerThread) without waiting for the utterance to actually reach the
// native queue. Awaiting THIS promise before speaking the next chunk is
// what keeps chunks strictly sequenced — speak() for chunk N+1 is never
// even called until chunk N's native callback has actually fired, so
// there's nothing left half-submitted for a stray Speech.stop() to race
// against.
//
// onStopped needs isExpectedStop rather than a blanket resolve(): nothing
// in this file calls Speech.stop() while a chunk is actively playing except
// the MAX_SPEECH_MS safety timeout below (there's no barge-in yet — see
// BACKLOG.md — so a user can't trigger a legitimate mid-chunk stop today).
// That means an onStopped NOT caused by that timeout can only be Android's
// own TextToSpeech engine losing audio focus to something external (a
// notification, another app, an OS-level event) — expo-speech's Android
// bridge discards the `interrupted` flag Android's own
// UtteranceProgressListener.onStop provides (see
// node_modules/expo-speech/android/.../SpeechModule.kt's onStop), so this
// is the only signal available to tell the two apart. Treating every
// onStopped as success (the previous behavior) meant a lost-focus interrupt
// silently ended the reply with no error and no fallback — the exact
// "assistant just goes quiet mid-reply" symptom reported from a real
// device. Rejecting instead lets speakToCompletion's existing catch below
// re-throw, which triggers useVoiceChat's existing network-TTS fallback —
// same recovery path as any other on-device TTS failure.
function speakChunk(text: string, voice: string, isExpectedStop: () => boolean): Promise<void> {
  return new Promise((resolve, reject) => {
    Speech.speak(text, {
      language: 'ru-RU',
      voice,
      onDone: () => resolve(),
      onStopped: () => {
        if (isExpectedStop()) {
          resolve();
        } else {
          console.warn('[TTS] onStopped without an expected stop — likely lost audio focus');
          reject(new Error('Speech stopped unexpectedly'));
        }
      },
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

  const clean = stripMarkdownForSpeech(text);
  if (!clean) return;

  const chunks = splitIntoChunks(clean, MAX_CHUNK_LENGTH);
  if (chunks.length === 0) return;

  let timedOut = false;
  // Stops (not just ignores) playback once the cap is hit — resolving
  // without calling stop() would let a long reply keep talking in the
  // background after the UI has already moved on to 'idle'. Also doubles as
  // speakChunk's "this stop was ours" signal — see its comment above.
  const timeoutId = setTimeout(() => {
    timedOut = true;
    Speech.stop();
  }, MAX_SPEECH_MS);

  try {
    for (const chunk of chunks) {
      if (timedOut) break;
      await speakChunk(chunk, voice, () => timedOut);
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
