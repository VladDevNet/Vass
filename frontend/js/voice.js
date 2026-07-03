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
window.activeAudioSourceNode = null;

window.stopAudioPlayback = function() {
    if ('speechSynthesis' in window) {
        speechSynthesis.cancel();
    }
    if (window.activeAudioSourceNode) {
        try { window.activeAudioSourceNode.stop(); } catch(e){}
        window.activeAudioSourceNode = null;
    }
    if (globalAudioElement) {
        try { globalAudioElement.pause(); } catch(e){}
    }
};

window.speakText = async function(text, onEnd) {
    window.stopAudioPlayback();

    const clean = text
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
    if (!clean) {
        if (onEnd) onEnd();
        return;
    }

    // Try OpenAI neural TTS via API
    try {
        const token = API.getToken();
        if (token) {
            const res = await fetch('/api/chat/tts', {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/json',
                    'Authorization': `Bearer ${token}`
                },
                body: JSON.stringify({ text: clean, voice: 'nova' })
            });

            ttsDbg(`fetch: ${res.status}`);
            if (res.ok) {
                const arrayBuffer = await res.arrayBuffer();
                ttsDbg(`bytes: ${arrayBuffer.byteLength}`);

                let ctx = window.activeAudioContext;
                if (!ctx) {
                    if (!window.voiceLocalAudioContext) {
                        window.voiceLocalAudioContext = new (window.AudioContext || window.webkitAudioContext)();
                    }
                    ctx = window.voiceLocalAudioContext;
                }
                ttsDbg(`ctx: ${window.activeAudioContext ? 'shared' : 'local'}, state=${ctx.state}, sr=${ctx.sampleRate}`);
                ctx.onstatechange = () => ttsDbg(`ctx statechange: ${ctx.state}`);

                if (ctx.state !== 'running') {
                    await ctx.resume();
                    ttsDbg(`after resume: ${ctx.state}`);
                }

                // Decode the MP3 audio data with fallback for older WebKit versions
                let audioBuffer;
                try {
                    audioBuffer = await ctx.decodeAudioData(arrayBuffer);
                } catch (decodeErr) {
                    ttsDbg(`decode(promise) failed: ${decodeErr && decodeErr.message}`);
                    audioBuffer = await new Promise((resolveBuffer, rejectBuffer) => {
                        ctx.decodeAudioData(arrayBuffer, resolveBuffer, rejectBuffer);
                    });
                }
                ttsDbg(`decoded: ${audioBuffer.duration.toFixed(1)}s`);

                const sourceNode = ctx.createBufferSource();
                sourceNode.buffer = audioBuffer;
                sourceNode.connect(ctx.destination);
                window.activeAudioSourceNode = sourceNode;

                const startedAt = Date.now();
                sourceNode.onended = () => {
                    ttsDbg(`ended after ${((Date.now() - startedAt) / 1000).toFixed(1)}s (ctx=${ctx.state})`);
                    if (window.activeAudioSourceNode === sourceNode) {
                        window.activeAudioSourceNode = null;
                    }
                    if (onEnd) onEnd();
                };

                sourceNode.start(0);
                ttsDbg('source started');
                return;
            }
        }
    } catch (err) {
        console.warn("Neural TTS request failed, falling back:", err);
        ttsDbg(`neural TTS error: ${err && err.message}`);
    }

    ttsDbg('using WebSpeech fallback');
    fallbackToWebSpeech(clean, onEnd);
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
