import * as Speech from 'expo-speech';
import * as SecureStore from 'expo-secure-store';
import { log } from '../logging/remoteLogger';

const VOICE_PREFERENCE_KEY = 'vass_voice_id';
const VOICE_GENDER_TAGS_KEY = 'vass_voice_gender_tags';

// Android's TextToSpeech.speak() hard-rejects (throws) text longer than this
// — expo-speech doesn't chunk automatically. A warm, conversational reply
// from the companion can easily run past it, so long replies are split on
// sentence boundaries and spoken as separate, explicitly sequenced chunks
// (see createStreamingSpeech below — NOT fired all at once relying on
// native auto-queueing; see speakChunk's comment for why).
const MAX_CHUNK_LENGTH = Math.min(Speech.maxSpeechInputLength - 100, 3500);

// A stalled/never-firing onDone would otherwise leave the UI stuck in
// 'speaking' forever — same MAX_PLAYBACK_MS safety-net pattern already used
// for network TTS playback in useVoiceChat.ts. Per CHUNK, not per reply —
// a single chunk can run up to MAX_CHUNK_LENGTH (~3400 chars), and a real
// production reply that long legitimately took longer than a flat 60s to
// read aloud and got silently truncated mid-sentence by what was previously
// a single reply-wide timer (found via real-device log analysis:
// "MAX_SPEECH_MS safety timeout hit" firing on a normal, un-stuck
// 1441-char single-chunk reply). 100ms/char is a generous allowance — real
// observed Russian TTS pace is well under that — with a floor so short
// chunks still get a meaningful safety net.
const MAX_CHUNK_SPEECH_MS_PER_CHAR = 100;
const MIN_CHUNK_SPEECH_MS = 30_000;
// Android TTS normally reports onStart within well under a second (confirmed
// by production logs). After returning from YouTube we observed utterances
// that produced neither onStart nor sound and only hit the old 30s duration
// timeout. Fail fast into the network-TTS fallback instead of showing
// `speaking` silently for half a minute.
const MAX_CHUNK_START_MS = 5_000;

// undefined = not looked up yet this session, null = no Russian voice found.
let cachedRussianVoice: string | null | undefined;

export type VoiceGender = 'male' | 'female';

// undefined = not loaded yet this session. Separate from cachedRussianVoice
// — a different SecureStore key, a different shape (map, not a single id).
let cachedGenderTags: Record<string, VoiceGender> | undefined;

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
// auto-picked default createStreamingSpeech() would fall back to. Exported
// so the settings screen can highlight "what's currently active" even for
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

// expo-speech's Voice type has no gender field at all ({identifier, name,
// quality, language} — confirmed by reading Speech.types.d.ts directly, not
// assumed) — Android's TTS engine exposes nothing programmatically usable
// here (see isLikelyLocalVoice's comment for the same class of gap on the
// local/network signal). Manual, user-driven tagging is the only reliable
// option; this is pure local storage for that tagging, no attempt at
// automatic classification.
export async function getVoiceGenderTags(): Promise<Record<string, VoiceGender>> {
  if (cachedGenderTags) return cachedGenderTags;
  try {
    const raw = await SecureStore.getItemAsync(VOICE_GENDER_TAGS_KEY);
    // Cast (not just `JSON.parse(raw)`) on purpose — JSON.parse returns
    // `any`, and assigning an `any`-typed value back to this module-level
    // `let` doesn't narrow away `| undefined` the way assigning a concrete
    // type does (TS resets an `any` assignment to the full declared type,
    // undefined included, since cachedGenderTags is also reassigned from
    // setVoiceGender below) — confirmed by tsc: without this cast the
    // `return cachedGenderTags` below fails to compile.
    cachedGenderTags = raw ? (JSON.parse(raw) as Record<string, VoiceGender>) : {};
  } catch {
    cachedGenderTags = {};
  }
  return cachedGenderTags;
}

export async function setVoiceGender(identifier: string, gender: VoiceGender): Promise<void> {
  const tags = await getVoiceGenderTags();
  const updated = { ...tags, [identifier]: gender };
  cachedGenderTags = updated;
  try {
    await SecureStore.setItemAsync(VOICE_GENDER_TAGS_KEY, JSON.stringify(updated));
  } catch {
    // no-op on platforms without a working SecureStore (web preview only) —
    // matches setVoicePreference's own catch above.
  }
}

// Interrupts whatever's currently playing (a previous preview, if the user
// is tapping through several voices quickly) before starting the new one.
export function previewVoice(identifier: string): void {
  markExpectedStop();
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
//
// Split into a non-trimming core (exported as stripMarkdownForSpeechChunk)
// and this trimming wrapper on purpose: useVoiceChat.ts's sendSegment
// applies the CORE to its streaming sentence buffer before extracting
// sentences from it, not just here per already-extracted sentence (see
// stripMarkdownForSpeechChunk's own comment for why trimming there would
// be actively wrong, not just redundant). Everything else in this file
// still wants the trimmed version — a complete, already-extracted sentence
// or full reply has no "more text might still be coming" concern.
export function stripMarkdownForSpeech(text: string): string {
  return stripMarkdownForSpeechChunk(text).trim();
}

// The core replacements only, no trim — see stripMarkdownForSpeech above
// for the trimming wrapper most callers want instead. Exists as its own
// export specifically for useVoiceChat.ts's GROWING streaming buffer: that
// buffer can legitimately end in a single meaningful space mid-stream (a
// chunk boundary landing right after a word, with the next word arriving
// in a LATER chunk) — trimming it away would glue the two words together
// once the next chunk is concatenated on. Idempotent like the wrapper
// above (every replacement here either removes its target outright or only
// fires once per occurrence), so re-running it on the whole accumulated
// buffer on every new chunk — not just the newly-arrived piece — is safe,
// and is what correctly handles a marker split across a chunk boundary
// (e.g. "**bold" | "text**"): a chunk-local strip would only see and
// remove the half that's arrived so far, but re-stripping the full buffer
// catches the complete marker once both halves are in it. Also why
// numbered-list markers need this applied BEFORE sentence extraction, not
// just after (see extractCompleteSentences' caller in useVoiceChat.ts): a
// marker like "1." gets extracted as if it were its own complete sentence
// (its period looks like a terminator), and by then the numbered-list
// regex below (which needs trailing whitespace after the marker) can no
// longer match on the isolated, trimmed "1." alone. Found by independent
// review.
//
// Residual, accepted gap (same review pass, empirically measured — not
// closed by the fix above): "re-stripping the whole buffer equals
// stripping once" is FALSE for two narrow chunk-boundary shapes — a
// marker whose period arrives with no trailing whitespace yet (buffer is
// literally "1." when extractCompleteSentences grabs it as a complete
// sentence, before this function ever sees the space that would let the
// numbered-list regex match) and a later list item whose leading "\n"
// anchor was already flattened to a space by an EARLIER pass over an
// earlier chunk (the ^-anchored bullet/numbered regexes below can't
// recognize a line start that no longer has a real newline before it).
// Measured against real split positions: ~2 of 32-35 possible chunk
// boundaries for a 2-item list, both immediately at the marker/newline
// itself. Failure mode is a mispronounced marker read aloud, not
// silence/crash/stuck-state — same tolerance this file already extends to
// "3.14"/"т.е." elsewhere. Not worth the structural fix (holding back a
// suspected-marker prefix across a chunk boundary before ever extracting)
// for a cosmetic, narrow-window gap.
export function stripMarkdownForSpeechChunk(text: string): string {
  return text
    // \u{1F300}-\u{1F5FF} (Misc Symbols and Pictographs — 👍🎉🔥 etc.) added
    // on top of the web version's ranges, which miss that block entirely.
    .replace(
      /[\u{1F300}-\u{1F6FF}\u{1F900}-\u{1FAFF}\u{2600}-\u{27BF}\u{FE00}-\u{FE0F}\u{1F000}-\u{1F02F}\u{200D}\u{20E3}\u{E0020}-\u{E007F}]/gu,
      ''
    )
    .replace(/\[([^\]]+)\]\([^\s)]+\)/g, '$1') // markdown link -> label
    .replace(/\[(?:\d+(?:\s*[,;–-]\s*\d+)*)\]/g, '') // grounding citations: [6], [2-4]
    .replace(/\[([^\]|]+)\|[^\]]*\]/g, '$1') // [word|translation] -> word
    .replace(/https?:\/\/[^\s`]+/gi, '') // URLs are useful in chat, never in speech
    .replace(/\*{1,3}/g, '') // *, **, ***
    .replace(/^-{3,}$/gm, '') // --- horizontal rules
    .replace(/^[\s]*[-•]\s+/gm, '') // bullet points
    .replace(/^[\s]*\d+\.\s+/gm, '') // numbered lists
    .replace(/#+\s*/g, '') // headings
    .replace(/`{1,3}[^`]*`{1,3}/g, '') // inline code
    .replace(/[✓✗✔✘☑☐→←↑↓►▶◀▼▲+]/g, '') // special symbols
    .replace(/\n{2,}/g, '. ') // multiple newlines → pause
    .replace(/\n/g, ' ') // single newlines → space
    .replace(/\s{2,}/g, ' '); // collapse spaces
}

// ConversationPeek is plain React Native Text, not a markdown renderer.
// Keep links visible for reference/context, but remove formatting syntax and
// Google grounding citation artifacts so the chat does not show raw ** or [6].
export function stripMarkupForDisplay(text: string): string {
  return text
    .replace(/\[([^\]]+)\]\((https?:\/\/[^\s)]+)\)/g, '$1 ($2)')
    .replace(/\[(?:\d+(?:\s*[,;–-]\s*\d+)*)\]/g, '')
    .replace(/\*{1,3}/g, '')
    .replace(/`{1,3}/g, '')
    .replace(/^\s*#+\s*/gm, '')
    .replace(/^\s*[-•]\s+/gm, '')
    .replace(/^\s*\d+\.\s+/gm, '')
    .replace(/[ \t]{2,}/g, ' ');
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

// Set right before ANY call to Speech.stop() that this file (or an external
// caller via stopSpeaking()) initiates on purpose, and consumed by the very
// next onStopped — see speakChunk's comment for why onStopped needs this at
// all. Module-level rather than per-call because stopSpeaking() is a public
// API called from outside this file (useVoiceChat.ts's unmount cleanup)
// with no visibility into whichever speakChunk promise happens to be in
// flight at the time.
let expectedStop = false;

function markExpectedStop(): void {
  expectedStop = true;
}

// Read-and-clear so a later, genuinely unexpected onStopped isn't
// mistakenly waved through by a flag some earlier stop left set.
function consumeExpectedStop(): boolean {
  const was = expectedStop;
  expectedStop = false;
  return was;
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
// onStopped needs consumeExpectedStop() rather than a blanket resolve():
// every Speech.stop() call this app can currently make is marked via
// markExpectedStop() first (the per-chunk safety timeout below,
// interruptSpeaking()'s barge-in, and stopSpeaking()'s external callers like
// useVoiceChat's unmount cleanup) — nothing else legitimately stops a chunk
// mid-playback. That means an onStopped with the flag NOT set
// can only be Android's own TextToSpeech engine losing audio focus to
// something external (a notification, another app, an OS-level event) —
// expo-speech's Android bridge discards the `interrupted` flag Android's
// own UtteranceProgressListener.onStop provides (see
// node_modules/expo-speech/android/.../SpeechModule.kt's onStop), so this
// is the only signal available to tell the two apart. Treating every
// onStopped as success (the previous behavior) meant a lost-focus interrupt
// silently ended the reply with no error and no fallback — the exact
// "assistant just goes quiet mid-reply" symptom reported from a real
// device. Rejecting instead lets speakChunkSafely's caller re-throw, which
// triggers useVoiceChat's existing network-TTS fallback — same recovery
// path as any other on-device TTS failure.
function speakChunk(
  text: string,
  voice: string,
  chunkIndex: number,
  totalChunks: number,
  onPlaybackStart: () => void
): Promise<void> {
  return new Promise((resolve, reject) => {
    Speech.speak(text, {
      language: 'ru-RU',
      voice,
      // Fires from Android's own UtteranceProgressListener.onStart (see
      // expo-speech's SpeechModule.kt) — the actual moment audio begins,
      // not just when this JS call was dispatched. Diagnostic only: added
      // to pin down a real-device report of a multi-second gap between the
      // reply appearing on screen and being audibly spoken, which the
      // existing 'speaking reply' log (fired BEFORE Speech.speak() is even
      // called) can't distinguish from native TTS engine startup latency.
      onStart: () => {
        onPlaybackStart();
        log('debug', 'tts', 'chunk playback started', { chunkIndex, totalChunks });
      },
      onDone: () => resolve(),
      onStopped: () => {
        if (consumeExpectedStop()) {
          resolve();
        } else {
          log('warn', 'tts', 'onStopped without an expected stop — likely lost audio focus', {
            chunkIndex,
            totalChunks,
          });
          reject(new Error('Speech stopped unexpectedly'));
        }
      },
      onError: (err) => {
        log('error', 'tts', 'chunk error', {
          chunkIndex,
          totalChunks,
          error: err instanceof Error ? err.message : String(err),
        });
        reject(err);
      },
    });
  });
}

// Wraps speakChunk with two independent watchdogs: one for an utterance that
// never starts at all, and one for playback that starts but never finishes.
// Both reject so useVoiceChat's existing network-TTS fallback actually runs.
async function speakChunkSafely(
  text: string,
  voice: string,
  chunkIndex: number,
  totalChunks: number
): Promise<void> {
  const timeoutMs = Math.max(MIN_CHUNK_SPEECH_MS, text.length * MAX_CHUNK_SPEECH_MS_PER_CHAR);
  let startTimeoutId: ReturnType<typeof setTimeout> | undefined;
  let finishTimeoutId: ReturnType<typeof setTimeout> | undefined;
  const stopForTimeout = (message: string, deadlineMs: number, reject: (reason: Error) => void) => {
    log('warn', 'tts', message, { chunkIndex, totalChunks, timeoutMs: deadlineMs });
    markExpectedStop();
    Speech.stop();
    reject(new Error(message));
  };

  const startDeadline = new Promise<never>((_, reject) => {
    startTimeoutId = setTimeout(
      () => stopForTimeout('chunk playback did not start', MAX_CHUNK_START_MS, reject),
      MAX_CHUNK_START_MS
    );
  });
  const finishDeadline = new Promise<never>((_, reject) => {
    finishTimeoutId = setTimeout(
      () => stopForTimeout('chunk speech safety timeout hit', timeoutMs, reject),
      timeoutMs
    );
  });
  const playback = speakChunk(text, voice, chunkIndex, totalChunks, () => {
    if (startTimeoutId) clearTimeout(startTimeoutId);
  });
  try {
    await Promise.race([playback, startDeadline, finishDeadline]);
  } finally {
    if (startTimeoutId) clearTimeout(startTimeoutId);
    if (finishTimeoutId) clearTimeout(finishTimeoutId);
  }
}

export interface StreamingSpeech {
  // Queues a sentence to be spoken once any prior ones finish. Safe to call
  // repeatedly while a previous sentence is still playing — that's the
  // whole point: an SSE onChunk callback firing faster than speech can keep
  // up just grows the queue, it doesn't block.
  push(text: string): void;
  // Signals no more sentences are coming. Resolves once the queue fully
  // drains and everything's been spoken. Rejects only on a genuine,
  // unrecovered TTS engine failure (see speakChunk's onError/unexpected-
  // onStopped) — an abort() resolves this normally, matching speakChunk's
  // own "an expected stop is success, not failure" convention.
  finish(): Promise<void>;
  // Stops whatever's currently playing and discards anything still queued
  // — for barge-in. Safe to call even if nothing has started speaking yet
  // (e.g. interrupted during onBeforeFirstSpeech's own setup).
  abort(): void;
  // Pauses playback WITHOUT discarding the queue (unlike abort) — for the
  // avatar's pause gesture (useVoiceChat.ts's pauseConversation), not
  // barge-in. Whatever sentence was actually playing when this fires is
  // repeated in full on resume(), not resumed mid-word — no per-word
  // position is ever tracked, a deliberate "resume from the nearest point,
  // a little repetition is fine" product decision. Safe to call before
  // anything has started speaking, or while the queue is simply empty and
  // the pump is waiting for more text.
  pause(): void;
  // Resumes a paused instance. No-op if not currently paused.
  resume(): void;
}

// Currently-active StreamingSpeech instance, if any — lets
// interruptSpeaking()/stopSpeaking() (called from outside this file, with
// no reference to whichever instance is live) reach in and abort it. At
// most one is ever active at a time — the same invariant expectedStop
// above already relies on (only one real reply is ever being spoken at
// once; backchannel fillers deliberately never touch this, see
// speakBackchannelPhrase's comment).
let activeStreaming: StreamingSpeech | null = null;

// Speaks sentences as they arrive, instead of waiting for a complete reply
// to be known first — the on-device counterpart to letting playback start
// while generation is still in progress. Previously the reply had to
// finish streaming ENTIRELY (2-15s+ observed in production, scaling with
// reply length) before a single word was spoken; now the first sentence
// can be heard as soon as IT alone is ready, while the rest keeps
// generating in the background — the "streaming TTS" follow-up explicitly
// deferred when the optimistic turn-taking redesign shipped (PR #49).
//
// onBeforeFirstSpeech lets the caller do async setup (arm the recorder for
// barge-in, flip UI state to 'speaking') that must complete before the
// FIRST utterance actually starts, without blocking push() itself — push()
// only enqueues text, it can't await (SendMessageCallbacks.onChunk isn't
// async) — and without racing it: the pump loop below awaits this before
// ever calling Speech.speak(), regardless of how many sentences are
// already queued by the time it gets there.
//
// startPaused lets the caller create an instance that won't speak (or run
// onBeforeFirstSpeech) until resume() is called — for a pause that lands
// during 'thinking', before the reply's first sentence has even arrived
// yet (see useVoiceChat.ts's handleChunk, which lazily creates the
// instance on the FIRST chunk regardless of pause state, passing this).
export function createStreamingSpeech(
  onBeforeFirstSpeech?: () => Promise<void>,
  startPaused = false
): StreamingSpeech {
  if (activeStreaming) {
    log('warn', 'tts', 'replacing a still-active streaming speech pipeline');
    markExpectedStop();
    activeStreaming.abort();
    Speech.stop();
    activeStreaming = null;
  }
  const queue: string[] = [];
  let finished = false;
  let aborted = false;
  let paused = startPaused;
  let wake: (() => void) | null = null;
  let pauseWake: (() => void) | null = null;
  // The sentence currently being processed/spoken, if any — held OUTSIDE
  // the queue (not just queue[0]) so pause() can hand it back to the pump
  // loop on the very next resume without disturbing whatever's still
  // queued behind it.
  let currentSentence: string | null = null;
  let armed = false;
  let voice: string | null = null;
  let spokenCount = 0;

  const notify = () => {
    const w = wake;
    wake = null;
    w?.();
  };
  const notifyPause = () => {
    const w = pauseWake;
    pauseWake = null;
    w?.();
  };

  async function pump(): Promise<void> {
    while (true) {
      if (aborted) return;
      if (paused) {
        await new Promise<void>((resolve) => {
          pauseWake = resolve;
        });
        continue;
      }

      if (!armed) {
        // onBeforeFirstSpeech runs FIRST, before even checking whether a
        // voice exists — arming the recorder and entering 'speaking' has
        // to happen regardless of whether on-device TTS ends up working at
        // all, since the caller falls back to network TTS on failure
        // (including "no voice") and still needs barge-in live for THAT
        // playback. Getting this order backwards was a real regression
        // found by independent review: with the voice check first, a
        // voice-less device never left 'thinking' during the entire
        // fallback playback, silently disabling both voice and tap
        // barge-in for it — the old (pre-streaming) design set 'speaking'
        // unconditionally before ever attempting on-device speech, and
        // this restores that guarantee. Guarded by `armed` so a pause/
        // resume cycle doesn't repeat these side effects on every loop
        // — except when paused DURING setup itself (below), where
        // repeating them on the next resume is correct, not accidental.
        armed = true;
        await onBeforeFirstSpeech?.();
        if (aborted) return;
        if (paused) {
          armed = false;
          continue;
        }
        voice = await getRussianVoice();
        if (aborted) return;
        if (!voice) throw new Error('На устройстве нет русского голоса');
      }

      if (currentSentence === null) {
        if (queue.length === 0) {
          if (finished) return;
          await new Promise<void>((resolve) => {
            wake = resolve;
          });
          continue;
        }
        currentSentence = queue.shift()!;
      }

      const clean = stripMarkdownForSpeech(currentSentence);
      // Neither this strip nor splitIntoChunks below treats bare
      // punctuation as empty — a "sentence" that's nothing but leftover
      // terminators/whitespace (e.g. a stray "." — see
      // extractCompleteSentences' own comment in useVoiceChat.ts for how
      // one can reach this queue) would otherwise sail through both and
      // reach Speech.speak() as literal input, which Android's TTS reads
      // aloud as its own word ("точка") rather than staying silent. This
      // is the second, independent layer catching that class of input —
      // the first (useVoiceChat.ts's stricter extraction regex) narrows
      // how OFTEN one is ever queued at all, this one guarantees none of
      // them ever reach actual playback regardless of source.
      if (!clean || !/[^.!?…\s]/.test(clean)) {
        currentSentence = null;
        continue;
      }

      let interrupted = false;
      for (const chunk of splitIntoChunks(clean, MAX_CHUNK_LENGTH)) {
        if (aborted) return;
        // -1: no fixed total in streaming mode, unlike the array-based loop
        // this replaces — more sentences can always still be coming.
        await speakChunkSafely(chunk, voice!, spokenCount++, -1);
        if (paused) {
          interrupted = true;
          break;
        }
      }
      // Only clear currentSentence once every chunk of it actually
      // finished — pausing mid-sentence leaves it set, so resume repeats
      // the SAME sentence from its first chunk (not wherever playback was
      // cut off — no per-chunk position is tracked). A sentence rarely
      // spans more than one chunk (MAX_CHUNK_LENGTH is ~3400 characters),
      // so in practice this means repeating one short sentence — the
      // deliberate "resume from the nearest point, a little repetition is
      // fine" product decision pause() exists for.
      if (!interrupted) currentSentence = null;
    }
  }

  const pumpPromise = pump();
  // Swallow here so an aborted stream (finish() never called/awaited) never
  // surfaces as an unhandled rejection — finish() below is the one place
  // that awaits (and re-throws) this SAME promise; a .catch() here doesn't
  // stop that from happening, it only adds a second, independent handler.
  pumpPromise.catch(() => {});

  const handle: StreamingSpeech = {
    push(text: string) {
      queue.push(text);
      notify();
    },
    async finish(): Promise<void> {
      finished = true;
      notify();
      await pumpPromise;
    },
    abort(): void {
      aborted = true;
      queue.length = 0;
      currentSentence = null;
      notify();
      notifyPause();
    },
    pause(): void {
      if (paused || aborted) return;
      paused = true;
    },
    resume(): void {
      if (!paused) return;
      paused = false;
      notifyPause();
    },
  };

  activeStreaming = handle;
  void pumpPromise.finally(() => {
    if (activeStreaming === handle) activeStreaming = null;
  });

  return handle;
}

// Public API — call to cut a reply short on purpose mid-playback (barge-in)
// and prevent it from continuing to any remaining sentences. Distinct from
// stopSpeaking() (unmount cleanup, where nothing continues afterward
// regardless so the "keep going vs. stop" distinction doesn't matter).
export function interruptSpeaking(): void {
  markExpectedStop();
  activeStreaming?.abort();
  Speech.stop();
}

// Public API — pauses whatever's currently playing WITHOUT discarding the
// queue (unlike interruptSpeaking, which is for barge-in and throws
// everything away). See StreamingSpeech.pause's own comment for the
// resume-with-repetition contract.
//
// Speech.stop() here is UNCONDITIONAL, same shape as interruptSpeaking/
// stopSpeaking, and for the same reason: it's the only thing that also
// interrupts a currently-playing BACKCHANNEL filler (speakBackchannel,
// below), which plays outside activeStreaming's tracking entirely.
// activeStreaming?.pause() alone doesn't touch native playback at all
// (StreamingSpeech.pause() only flips the internal flag — matching abort(),
// which likewise never calls Speech.stop() itself); an earlier version of
// this function dropped the Speech.stop() call as "redundant" with pause()'s
// own, which briefly regressed exactly this — a long-press landing during a
// filler phrase let it keep playing out loud instead of cutting off.
// Independent review caught it.
export function pauseSpeaking(): void {
  markExpectedStop();
  activeStreaming?.pause();
  Speech.stop();
}

// Public API — resumes a paused instance. No-op if nothing is paused.
export function resumeSpeaking(): void {
  activeStreaming?.resume();
}

// Whether there's a live (possibly paused) StreamingSpeech instance to
// resume — lets useVoiceChat.ts's resumeConversation distinguish "resume
// actual paused speech" from "nothing was ever speaking, just go back to
// listening" (e.g. paused while idle/recording, or a turn that failed/
// produced no speakable content while paused).
export function hasSpeechToResume(): boolean {
  return activeStreaming !== null;
}

// Short, warm acknowledgment phrases ("still listening") played during a
// pause while turn-taking decides whether the speaker is done — mirrors
// yolo.js's BACKCHANNEL_FILLERS, matches companion-system.txt's "тёплый
// голосовой собеседник" tone ("не подгоняй собеседника... терпение важнее
// скорости ответа" — avoided anything that could read as rushing/curt on
// flat TTS intonation, e.g. a repeated "Да, да." or an interrogative-
// sounding "Так-так.").
const BACKCHANNEL_PHRASES = ['Хорошо.', 'Минутку.', 'Понятно.', 'Секунду.', 'Дайте подумать.'];

// Warm opening phrases for useGreeting.ts — spoken once when the app first
// becomes ready to listen, and again on returning to the foreground after
// being backgrounded. Deliberately generic enough to fit either moment,
// matching frontend/js/yolo.js's own GREETING_FILLERS, which shared one
// pool for both "YOLO start" and "focus-return" (commit 2b28a10) — unlike
// BACKCHANNEL_PHRASES above, which are specifically about turn-taking, not
// a fresh conversation.
//
// Deliberately gender-neutral (no adjective agreeing with the speaker,
// e.g. NOT "Рада"/"Готова", which are grammatically feminine-only) — this
// assistant supports a male voice/persona (LayeredAvatar's "male" avatar,
// AssistantName "Максим" — see HomeScreen.tsx and the VoiceGender tagging
// above) as well as a female one, and a mis-gendered greeting would be
// wrong every time that voice is active. Same constraint BACKCHANNEL_PHRASES
// already satisfies by accident; here it's deliberate.
const GREETING_PHRASES = [
  'Здравствуйте! Я на связи, слушаю вас.',
  'Добрый день! Можно начинать — я слушаю.',
  'Привет! Всё готово, говорите.',
];

// Speaks one short phrase, resolving on ANY stop — done, engine-initiated,
// or explicitly stopped — never rejecting. Deliberately separate from
// speakChunk above despite the near-identical Speech.speak() call:
// speakChunk's onStopped consumes the module-level expectedStop flag, which
// works ONLY because at most one speakChunk call has ever been in flight at
// a time — true for createStreamingSpeech's own strictly-sequenced pump
// loop, but NOT once a backchannel filler can be queued (Android's
// TextToSpeech defaults to QUEUE_ADD, confirmed in expo-speech's own
// SpeechModule.kt) alongside a real reply that starts moments later. If a
// barge-in's Speech.stop() then stops BOTH utterances in one native call,
// only ONE of their onStopped handlers can actually consume the shared
// flag — the other would wrongly see it already cleared and reject as
// "unexpected," sending a real, cleanly-interrupted reply into the
// network-TTS fallback path instead of just stopping. A filler doesn't
// need that fallback machinery at all — it's a non-critical nicety — so it
// simply never participates in expectedStop tracking. Found by independent
// review.
function speakBackchannelPhrase(text: string, voice: string): Promise<void> {
  return new Promise((resolve) => {
    Speech.speak(text, {
      language: 'ru-RU',
      voice,
      // Same diagnostic as speakChunk's onStart — the filler's whole point
      // is filling dead air IMMEDIATELY, so if native TTS startup latency
      // is real, it undermines this path just as much as the real reply.
      onStart: () => log('debug', 'tts', 'backchannel playback started'),
      onDone: () => resolve(),
      onStopped: () => resolve(),
      onError: (err) => {
        log('debug', 'tts', 'backchannel chunk error (non-critical)', {
          error: err instanceof Error ? err.message : String(err),
        });
        resolve();
      },
    });
  });
}

// Fire-and-forget: the turn-taking loop calling this needs to keep listening
// immediately, not wait for the filler to finish playing. Spoken with the
// same resolved voice as real replies (getRussianVoice) — this used to be
// five pre-recorded WAV files (frontend/audio/fillers/back-*.wav, ported
// as-is from the web client) that didn't match whichever voice the user
// actually has selected, an inconsistency real-device feedback described as
// sounding "like some kind of horror movie."
export function speakBackchannel(): void {
  void (async () => {
    try {
      const voice = await getRussianVoice();
      if (!voice) return; // no Russian voice on this device — silently skip, same as the WAV version's implicit behavior
      const phrase = BACKCHANNEL_PHRASES[Math.floor(Math.random() * BACKCHANNEL_PHRASES.length)];
      await speakBackchannelPhrase(phrase, voice);
    } catch (err) {
      log('debug', 'tts', 'backchannel filler failed (non-critical)', {
        error: err instanceof Error ? err.message : String(err),
      });
    }
  })();
}

// Fire-and-forget, same shape as speakBackchannel above (and the same
// reason it doesn't participate in expectedStop tracking — see
// speakBackchannelPhrase's own comment) — called by useGreeting.ts on app
// open and on returning from the background.
export function speakGreeting(): void {
  void (async () => {
    try {
      const voice = await getRussianVoice();
      if (!voice) return; // no Russian voice on this device — silently skip
      const phrase = GREETING_PHRASES[Math.floor(Math.random() * GREETING_PHRASES.length)];
      await speakBackchannelPhrase(phrase, voice);
    } catch (err) {
      log('debug', 'tts', 'greeting failed (non-critical)', {
        error: err instanceof Error ? err.message : String(err),
      });
    }
  })();
}

// A short, awaited system notice for failures that happen on the device
// after the server has already emitted an action (for example no handler for
// a YouTube URL). The caller stops live recorders before awaiting this so the
// assistant never captures its own failure message as user speech.
export async function speakSystemNotice(text: string): Promise<void> {
  try {
    const voice = await getRussianVoice();
    if (!voice) return;
    await speakBackchannelPhrase(text, voice);
  } catch (err) {
    log('debug', 'tts', 'system notice failed (non-critical)', {
      error: err instanceof Error ? err.message : String(err),
    });
  }
}

// Public API — called from outside this file whenever something else needs
// to cut speech short (currently just useVoiceChat's unmount cleanup).
// Marks the stop as expected first so speakChunk's onStopped handler
// doesn't mistake this for a lost-focus interruption — see its comment.
export function stopSpeaking(): void {
  markExpectedStop();
  activeStreaming?.abort();
  Speech.stop();
}
