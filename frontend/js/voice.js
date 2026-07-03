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
    const plVoices = voices.filter(v => v.lang.startsWith('pl'));

    ttsVoiceSelect.innerHTML = '';
    if (plVoices.length === 0) {
        ttsVoiceSelect.innerHTML = '<option>Немає польських голосів</option>';
        return;
    }

    plVoices.forEach(v => {
        const opt = document.createElement('option');
        opt.value = v.name;
        opt.textContent = `${v.name} (${v.lang})`;
        if (v.name === ttsVoiceName) opt.selected = true;
        ttsVoiceSelect.appendChild(opt);
    });

    if (!ttsVoiceName && plVoices.length > 0) {
        ttsVoiceName = plVoices[0].name;
    }
}

ttsVoiceSelect.addEventListener('change', () => {
    ttsVoiceName = ttsVoiceSelect.value;
    localStorage.setItem('tts-voice', ttsVoiceName);
});

// Text-to-Speech (TTS)
window.speakText = function(text, onEnd) {
    if (!('speechSynthesis' in window)) {
        if (onEnd) onEnd();
        return;
    }

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

    speechSynthesis.cancel();

    // iOS workaround: after cancel(), synthesis can get stuck in a paused state
    if (speechSynthesis.paused) speechSynthesis.resume();

    const utterance = new SpeechSynthesisUtterance(clean);
    utterance.lang = 'pl-PL';
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
};

// Preload voices
if ('speechSynthesis' in window) {
    speechSynthesis.getVoices();
    speechSynthesis.onvoiceschanged = () => populateVoices();
    populateVoices();

    // iOS/iPadOS requires speechSynthesis.speak() to be triggered from a user gesture
    // to "unlock" audio. Once unlocked with a silent utterance, subsequent programmatic
    // calls (e.g. after SSE response) will work.
    let ttsUnlocked = false;
    function unlockTTS() {
        if (ttsUnlocked) return;
        ttsUnlocked = true;
        const silent = new SpeechSynthesisUtterance('');
        silent.volume = 0;
        speechSynthesis.speak(silent);
    }
    document.addEventListener('click', unlockTTS, { once: true });
    document.addEventListener('touchstart', unlockTTS, { once: true });
}
