// Audio Recording (server-side transcription via Whisper)
const voiceBtn = document.getElementById('voice-btn');
let isRecording = false;
let mediaRecorder = null;
let audioChunks = [];
let lastAudioUrl = null;

voiceBtn.addEventListener('click', () => {
    isRecording ? stopRecording() : startRecording();
});

async function startRecording() {
    try {
        const stream = await navigator.mediaDevices.getUserMedia({ audio: true });
        audioChunks = [];
        mediaRecorder = new MediaRecorder(stream);
        mediaRecorder.ondataavailable = (e) => {
            if (e.data.size > 0) audioChunks.push(e.data);
        };
        mediaRecorder.onstop = () => {
            const blob = new Blob(audioChunks, { type: 'audio/webm' });
            if (lastAudioUrl) URL.revokeObjectURL(lastAudioUrl);
            lastAudioUrl = URL.createObjectURL(blob);
            stream.getTracks().forEach(t => t.stop());
        };
        mediaRecorder.start();
        isRecording = true;
        voiceBtn.classList.add('recording');
        voiceBtn.textContent = '\u23F9';
    } catch (err) {
        console.warn('Audio recording not available:', err);
    }
}

function stopRecording() {
    if (mediaRecorder && mediaRecorder.state !== 'inactive') {
        mediaRecorder.stop();
    }
    isRecording = false;
    voiceBtn.classList.remove('recording');
    voiceBtn.textContent = '\uD83C\uDFA4';
}

// Expose for chat.js
window.VoiceRecorder = {
    isRecording() { return isRecording; },
    // Stop recording and return a promise that resolves with the blob
    stopAndGetBlob() {
        return new Promise((resolve) => {
            if (!isRecording || !mediaRecorder || mediaRecorder.state === 'inactive') {
                resolve(audioChunks.length > 0 ? new Blob(audioChunks, { type: 'audio/webm' }) : null);
                audioChunks = [];
                return;
            }
            // Override onstop to resolve the promise
            const origOnstop = mediaRecorder.onstop;
            mediaRecorder.onstop = (e) => {
                origOnstop?.(e);
                const blob = audioChunks.length > 0 ? new Blob(audioChunks, { type: 'audio/webm' }) : null;
                audioChunks = [];
                resolve(blob);
            };
            stopRecording();
        });
    },
    consumeAudioUrl() {
        const url = lastAudioUrl;
        lastAudioUrl = null;
        return url;
    },
    consumeAudioBlob() {
        if (audioChunks.length === 0) return null;
        const blob = new Blob(audioChunks, { type: 'audio/webm' });
        audioChunks = [];
        return blob;
    }
};

// TTS Settings
const ttsVoiceSelect = document.getElementById('tts-voice');
const ttsRateInput = document.getElementById('tts-rate');
const ttsRateVal = document.getElementById('tts-rate-val');
const ttsSettingsBtn = document.getElementById('tts-settings-btn');
const ttsSettingsPanel = document.getElementById('tts-settings');

let ttsRate = parseFloat(localStorage.getItem('tts-rate') || '0.9');
let ttsVoiceName = localStorage.getItem('tts-voice') || '';

ttsRateInput.value = ttsRate;
ttsRateVal.textContent = ttsRate;

ttsRateInput.addEventListener('input', () => {
    ttsRate = parseFloat(ttsRateInput.value);
    ttsRateVal.textContent = ttsRate;
    localStorage.setItem('tts-rate', ttsRate);
});

ttsSettingsBtn.addEventListener('click', () => {
    ttsSettingsPanel.classList.toggle('hidden');
});

function populateVoices() {
    if (!('speechSynthesis' in window)) return;
    const voices = speechSynthesis.getVoices();
    const ruVoices = voices.filter(v => v.lang.startsWith('ru'));

    ttsVoiceSelect.innerHTML = '';
    if (ruVoices.length === 0) {
        ttsVoiceSelect.innerHTML = '<option>Нет русских голосов</option>';
        return;
    }

    ruVoices.forEach(v => {
        const opt = document.createElement('option');
        opt.value = v.name;
        opt.textContent = `${v.name} (${v.lang})`;
        if (v.name === ttsVoiceName) opt.selected = true;
        ttsVoiceSelect.appendChild(opt);
    });

    if (!ttsVoiceName && ruVoices.length > 0) {
        ttsVoiceName = ruVoices[0].name;
    }
}

ttsVoiceSelect.addEventListener('change', () => {
    ttsVoiceName = ttsVoiceSelect.value;
    localStorage.setItem('tts-voice', ttsVoiceName);
});

// Temporary TTS diagnostics (visible inside YOLO overlay on devices without devtools)
function ttsDbg(msg) {
    console.log('[TTS-DBG]', msg);
    if (window.yoloDebugLine) window.yoloDebugLine(msg);
}

// Text-to-Speech (TTS)
const globalAudioElement = new Audio();
window.activeAudioElement = globalAudioElement;
window.activeStreamSources = []; // AudioBufferSourceNodes scheduled by the PCM streaming player
window.activeStreamAbort = null; // AbortController for the in-flight streaming fetch

const PIPER_SAMPLE_RATE = 22050; // must match ru_RU-irina-medium.onnx.json audio.sample_rate

window.stopAudioPlayback = function() {
    if ('speechSynthesis' in window) {
        speechSynthesis.cancel();
    }
    if (window.activeStreamAbort) {
        window.activeStreamAbort.abort();
        window.activeStreamAbort = null;
    }
    if (window.activeStreamSources.length) {
        window.activeStreamSources.forEach(node => { try { node.stop(); } catch(e){} });
        window.activeStreamSources = [];
    }
    if (globalAudioElement) {
        try { globalAudioElement.pause(); } catch(e){}
    }
};

// Plays a small complete static audio file (e.g. a filler phrase) on the shared
// AudioContext, so it participates in the same stop/interrupt tracking as streamed
// TTS. Fine to use decodeAudioData here since these clips are short and pre-fetched
// from static hosting, not streamed from the network.
window.playStaticClip = async function(url, onEnd) {
    try {
        let ctx = window.activeAudioContext;
        if (!ctx) {
            if (!window.voiceLocalAudioContext) {
                window.voiceLocalAudioContext = new (window.AudioContext || window.webkitAudioContext)();
            }
            ctx = window.voiceLocalAudioContext;
        }
        if (ctx.state !== 'running') await ctx.resume();

        const res = await fetch(url);
        const arrayBuffer = await res.arrayBuffer();
        const audioBuffer = await ctx.decodeAudioData(arrayBuffer);

        const source = ctx.createBufferSource();
        source.buffer = audioBuffer;
        source.connect(ctx.destination);
        window.activeStreamSources.push(source);
        source.onended = () => { if (onEnd) onEnd(); };
        source.start(0);
    } catch (err) {
        console.warn('playStaticClip failed:', err);
        if (onEnd) onEnd();
    }
};

// Plays a stream of raw 16-bit mono PCM chunks back-to-back with no gaps, by
// manually building an AudioBuffer per chunk instead of decodeAudioData (which
// needs a complete, self-contained file and can't work off partial data).
async function playPcmStream(response, ctx, onEnd) {
    const reader = response.body.getReader();
    let leftover = null; // one dangling byte when a chunk splits a 16-bit sample
    let nextStartTime = null;
    let lastSource = null;
    let streamDone = false;
    const sources = window.activeStreamSources;

    const finishIfDone = () => {
        if (streamDone && (lastSource === null || nextStartTime === null || ctx.currentTime >= nextStartTime)) {
            if (onEnd) onEnd();
        }
    };

    function scheduleChunk(bytes) {
        const sampleCount = bytes.length / 2;
        const int16 = new Int16Array(bytes.buffer, bytes.byteOffset, sampleCount);
        const audioBuffer = ctx.createBuffer(1, sampleCount, PIPER_SAMPLE_RATE);
        const channel = audioBuffer.getChannelData(0);
        for (let i = 0; i < sampleCount; i++) {
            channel[i] = int16[i] / 32768;
        }

        const source = ctx.createBufferSource();
        source.buffer = audioBuffer;
        source.connect(ctx.destination);

        const startAt = nextStartTime === null ? ctx.currentTime + 0.08 : Math.max(nextStartTime, ctx.currentTime);
        source.start(startAt);
        nextStartTime = startAt + audioBuffer.duration;

        if (lastSource) lastSource.onended = null;
        lastSource = source;
        source.onended = () => {
            if (lastSource === source) finishIfDone();
        };

        sources.push(source);
    }

    while (true) {
        let result;
        try {
            result = await reader.read();
        } catch (err) {
            if (err.name === 'AbortError') return;
            throw err;
        }
        if (result.done) break;

        let bytes = new Uint8Array(result.value);
        if (leftover) {
            const merged = new Uint8Array(leftover.length + bytes.length);
            merged.set(leftover, 0);
            merged.set(bytes, leftover.length);
            bytes = merged;
            leftover = null;
        }
        if (bytes.length % 2 !== 0) {
            leftover = bytes.slice(bytes.length - 1);
            bytes = bytes.slice(0, bytes.length - 1);
        }
        if (bytes.length > 0) {
            scheduleChunk(bytes);
        }
    }

    streamDone = true;
    finishIfDone();
}

function cleanTextForTts(text) {
    return text
        .replace(/[\u{1F600}-\u{1F6FF}\u{1F900}-\u{1FAFF}\u{2600}-\u{27BF}\u{FE00}-\u{FE0F}\u{1F000}-\u{1F02F}\u{200D}\u{20E3}\u{E0020}-\u{E007F}]/gu, '')
        .replace(/\[([^\]|]+)\|[^\]]*\]/g, '$1')  // [word|translation] → word
        .replace(/\*{1,3}/g, '')                   // *, **, ***
        .replace(/^-{3,}$/gm, '')                  // --- horizontal rules
        .replace(/^[\s]*[-•]\s+/gm, '')            // bullet points
        .replace(/^[\s]*\d+\.\s+/gm, '')           // numbered lists
        .replace(/#+\s*/g, '')                      // headings
        .replace(/`{1,3}[^`]*`{1,3}/g, '')         // inline code
        .replace(/[✓✗✔✘☑☐→←↑↓►▶◀▼▲+]/g, '')      // special symbols
        .replace(/\n{2,}/g, '. ')                   // multiple newlines → pause
        .replace(/\n/g, ' ')                        // single newlines → space
        .replace(/\s{2,}/g, ' ')                    // collapse spaces
        .trim();
}

// Plays a single utterance (local neural TTS, streamed, with WebSpeech fallback).
// Unlike window.speakText, it does NOT stop whatever's currently playing first —
// used by the TTS queue below to play consecutive sentences back-to-back.
async function playOneUtterance(text, onEnd) {
    const clean = cleanTextForTts(text);
    if (!clean) {
        if (onEnd) onEnd();
        return;
    }

    try {
        const token = API.getToken();
        if (token) {
            const abort = new AbortController();
            window.activeStreamAbort = abort;

            const res = await fetch('/api/v1/chat/tts_stream', {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/json',
                    'Authorization': `Bearer ${token}`
                },
                body: JSON.stringify({ text: clean }),
                signal: abort.signal
            });

            ttsDbg(`fetch: ${res.status}`);
            if (res.ok) {
                let ctx = window.activeAudioContext;
                if (!ctx) {
                    if (!window.voiceLocalAudioContext) {
                        window.voiceLocalAudioContext = new (window.AudioContext || window.webkitAudioContext)();
                    }
                    ctx = window.voiceLocalAudioContext;
                }
                ttsDbg(`ctx: ${window.activeAudioContext ? 'shared' : 'local'}, state=${ctx.state}, sr=${ctx.sampleRate}`);

                if (ctx.state !== 'running') {
                    await ctx.resume();
                    ttsDbg(`after resume: ${ctx.state}`);
                }

                const startedAt = Date.now();
                await playPcmStream(res, ctx, () => {
                    ttsDbg(`stream ended after ${((Date.now() - startedAt) / 1000).toFixed(1)}s`);
                    window.activeStreamAbort = null;
                    if (onEnd) onEnd();
                });
                return;
            }
        }
    } catch (err) {
        if (err.name === 'AbortError') return; // interrupted deliberately, don't fall back
        console.warn("Neural TTS request failed, falling back:", err);
        ttsDbg(`neural TTS error: ${err && err.message}`);
    }

    ttsDbg('using WebSpeech fallback');
    fallbackToWebSpeech(clean, onEnd);
}

window.speakText = async function(text, onEnd) {
    window.stopAudioPlayback();
    await playOneUtterance(text, onEnd);
};

// A queue of utterances that play back-to-back in arrival order, without
// interrupting each other — lets a caller feed it sentences as they stream in
// from an LLM instead of waiting for the whole reply before speaking anything.
window.createTtsQueue = function() {
    const queue = [];
    let playing = false;
    let stopped = false;
    let finishRequested = false;
    let onAllDone = null;

    function playNext() {
        if (stopped) return;
        if (queue.length === 0) {
            playing = false;
            if (finishRequested && onAllDone) {
                const cb = onAllDone;
                onAllDone = null;
                cb();
            }
            return;
        }
        playing = true;
        const text = queue.shift();
        playOneUtterance(text, playNext);
    }

    return {
        push(text) {
            if (stopped || !text || !text.trim()) return;
            queue.push(text);
            if (!playing) playNext();
        },
        // Call once no more sentences are coming; onDone fires once the queue drains.
        finish(onDone) {
            finishRequested = true;
            if (!playing && queue.length === 0) {
                onDone();
            } else {
                onAllDone = onDone;
            }
        },
        // Cancels any not-yet-played queued sentences. Does not stop audio already
        // playing — pair with window.stopAudioPlayback() for a full interrupt.
        stop() {
            stopped = true;
            queue.length = 0;
        }
    };
};

function fallbackToWebSpeech(clean, onEnd) {
    if (!('speechSynthesis' in window)) {
        if (onEnd) onEnd();
        return;
    }

    speechSynthesis.cancel();
    if (speechSynthesis.paused) speechSynthesis.resume();

    const utterance = new SpeechSynthesisUtterance(clean);
    utterance.lang = 'ru-RU';
    utterance.rate = ttsRate;

    const voices = speechSynthesis.getVoices();
    const selected = voices.find(v => v.name === ttsVoiceName);
    if (selected) utterance.voice = selected;

    // Workaround for garbage collection in Chrome/iOS
    if (!window.activeUtterances) window.activeUtterances = [];
    window.activeUtterances.push(utterance);

    utterance.onend = () => {
        const idx = window.activeUtterances.indexOf(utterance);
        if (idx > -1) window.activeUtterances.splice(idx, 1);
        if (onEnd) onEnd();
    };
    utterance.onerror = (e) => {
        const idx = window.activeUtterances.indexOf(utterance);
        if (idx > -1) window.activeUtterances.splice(idx, 1);
        console.warn('TTS speak error:', e);
        if (onEnd) onEnd();
    };

    speechSynthesis.speak(utterance);
}

// Preload voices
if ('speechSynthesis' in window) {
    speechSynthesis.getVoices();
    speechSynthesis.onvoiceschanged = () => populateVoices();
    populateVoices();

    // iOS/iPadOS requires user gestures to "unlock" both SpeechSynthesis and HTML5 Audio
    let ttsUnlocked = false;
    function unlockTTS() {
        if (ttsUnlocked) return;
        ttsUnlocked = true;
        
        // Unlock SpeechSynthesis
        const silent = new SpeechSynthesisUtterance('');
        silent.volume = 0;
        speechSynthesis.speak(silent);

        // Unlock HTML5 Audio by playing a brief silent WAV
        globalAudioElement.src = 'data:audio/wav;base64,UklGRigAAABXQVZFZm10IBIAAAABAAEARKwAAIhYAQACABAAAABkYXRhAgAAAAEA';
        globalAudioElement.play().catch(e => {
            console.warn("Failed to pre-unlock HTML5 Audio on user gesture:", e);
        });
    }
    document.addEventListener('click', unlockTTS, { once: true });
    document.addEventListener('touchstart', unlockTTS, { once: true });
}
