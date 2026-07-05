// YOLO Mode - Voice-only Hands-free Conversational Voice Assistant
(function() {
    const yoloBtn = document.getElementById('yolo-btn');
    const yoloOverlay = document.getElementById('yolo-overlay');
    const yoloCloseBtn = document.getElementById('yolo-close-btn');
    const yoloToggleTextBtn = document.getElementById('yolo-toggle-text-btn');
    const yoloTextPreview = document.getElementById('yolo-text-preview');
    const yoloStatus = document.getElementById('yolo-status');
    const yoloOrb = document.getElementById('yolo-orb');
    const yoloSensitivity = document.getElementById('yolo-sensitivity');
    const yoloSensitivityVal = document.getElementById('yolo-sensitivity-val');
    const yoloMuteBtn = document.getElementById('yolo-mute-btn');
    const yoloSpeakNowBtn = document.getElementById('yolo-speak-now-btn');

    // States
    const STATES = {
        DISABLED: 'disabled',
        IDLE: 'idle',           // Calm, waiting for speech
        LISTENING: 'listening', // Actively recording user speech
        THINKING: 'thinking',   // Server transcribing/streaming
        SPEAKING: 'speaking'    // Playing back TTS audio
    };

    let currentState = STATES.DISABLED;
    let audioContext = null;
    let analyser = null;
    let micStream = null;
    let mediaRecorder = null;
    let currentRecordingChunks = null; // Tracks chunks of the active recording session to avoid cross-session contamination
    let wakeLock = null; // Keeps the screen from dimming/sleeping in YOLO mode
    let vadInterval = null;
    let activeAbortController = null;
    let isMuted = false;
    let showText = true;
    const activeUtterances = [];
    let userExitedYolo = false;
    let isInitializing = false;

    // VAD Configuration
    let sensitivityThreshold = parseFloat(localStorage.getItem('yolo-sensitivity') || '0.02');
    yoloSensitivity.value = sensitivityThreshold;
    yoloSensitivityVal.textContent = sensitivityThreshold.toFixed(3);

    const SILENCE_TIMEOUT = 1000; // ms of silence to trigger end of speech
    const INTERRUPTION_FRAMES = 5; // ~250ms of consecutive loud speech to interrupt AI
    const START_SPEECH_FRAMES = 5;  // ~250ms of consecutive speech to trigger listening state (filters out short coughs/clicks)

    let lastSpeechTime = 0;
    let speechStartTime = 0; // Timestamp of when the user started speaking this turn
    let consecutiveSpeechFrames = 0;
    let consecutiveSilenceFrames = 0;
    let hasSpoken = false;
    let smoothedRms = 0;

    let activeTtsQueue = null; // window.createTtsQueue() instance for the in-flight response

    // Pre-synthesized filler clips: played immediately (no synthesis wait) to bridge
    // the silence while transcription/LLM/TTS are still working, and as a greeting
    // when YOLO mode starts or regains focus.
    const THINKING_FILLERS = [
        '/audio/fillers/thinking-1.wav',
        '/audio/fillers/thinking-2.wav',
        '/audio/fillers/thinking-3.wav',
        '/audio/fillers/thinking-4.wav'
    ];
    const GREETING_FILLERS = [
        '/audio/fillers/greeting-1.wav',
        '/audio/fillers/greeting-2.wav'
    ];
    function pickRandom(arr) {
        return arr[Math.floor(Math.random() * arr.length)];
    }

    // Splits off any newly-completed sentences from the tail of `text` (starting at
    // `fromIndex`) so they can be sent to TTS as soon as they're ready, instead of
    // waiting for the whole LLM response to finish streaming.
    const SENTENCE_END_RE = /[.!?…]+(?=\s|$)/;
    function extractCompleteSentences(text, fromIndex) {
        const sentences = [];
        let cursor = fromIndex;
        while (true) {
            const match = SENTENCE_END_RE.exec(text.slice(cursor));
            if (!match) break;
            const cutAt = cursor + match.index + match[0].length;
            sentences.push(text.slice(fromIndex, cutAt));
            fromIndex = cutAt;
            cursor = cutAt;
        }
        return { sentences, nextIndex: fromIndex };
    }

    // Clean text for TTS (strip formatting & translation details)
    function cleanTextForTTS(text) {
        return text
            .replace(/[\u{1F600}-\u{1F6FF}\u{1F900}-\u{1FAFF}\u{2600}-\u{27BF}\u{FE00}-\u{FE0F}\u{1F000}-\u{1F02F}\u{200D}\u{20E3}\u{E0020}-\u{E007F}]/gu, '')
            .replace(/\[([^\]|]+)\|[^\]]*\]/g, '$1')  // [word|translation] → word
            .replace(/\*{1,3}/g, '')
            .replace(/^-{3,}$/gm, '')
            .replace(/^[\s]*[-•]\s+/gm, '')
            .replace(/^[\s]*\d+\.\s+/gm, '')
            .replace(/#+\s*/g, '')
            .replace(/`{1,3}[^`]*`{1,3}/g, '')
            .replace(/[✓✗✔✘☑☐→←↑↓►▶◀▼▲+]/g, '')
            .replace(/\n{2,}/g, '. ')
            .replace(/\n/g, ' ')
            .replace(/\s{2,}/g, ' ')
            .trim();
    }

    // Initialize/Toggle YOLO Mode
    const yoloFloatingBtn = document.getElementById('yolo-floating-btn');

    yoloBtn.addEventListener('click', () => {
        userExitedYolo = false;
        if (currentState === STATES.DISABLED) {
            enterYoloMode();
        }
    });

    if (yoloFloatingBtn) {
        yoloFloatingBtn.addEventListener('click', () => {
            userExitedYolo = false;
            if (currentState === STATES.DISABLED) {
                enterYoloMode();
            }
        });
    }

    window.addEventListener('focus', () => {
        // Automatically enter YOLO mode when tab gains focus, but only if user didn't explicitly close it
        if (currentState === STATES.DISABLED && window.currentSessionId && !userExitedYolo) {
            enterYoloMode();
        }
    });

    yoloCloseBtn.addEventListener('click', () => {
        userExitedYolo = true;
        exitYoloMode();
    });

    yoloToggleTextBtn.addEventListener('click', () => {
        showText = !showText;
        if (showText) {
            yoloTextPreview.classList.remove('hidden');
            yoloToggleTextBtn.textContent = 'Приховати текст';
        } else {
            yoloTextPreview.classList.add('hidden');
            yoloToggleTextBtn.textContent = 'Показати текст';
        }
    });

    yoloSensitivity.addEventListener('input', () => {
        sensitivityThreshold = parseFloat(yoloSensitivity.value);
        yoloSensitivityVal.textContent = sensitivityThreshold.toFixed(3);
        localStorage.setItem('yolo-sensitivity', sensitivityThreshold);
    });

    yoloMuteBtn.addEventListener('click', () => {
        if (audioContext && audioContext.state === 'suspended') {
            audioContext.resume();
        }
        isMuted = !isMuted;
        yoloMuteBtn.classList.toggle('muted', isMuted);
        if (isMuted) {
            yoloStatus.textContent = 'Мікрофон вимкнено';
            yoloOrb.className = 'yolo-orb state-idle';
            if (mediaRecorder && mediaRecorder.state === 'recording') {
                mediaRecorder.pause();
            }
        } else {
            yoloStatus.textContent = 'Слухаю...';
            yoloOrb.className = 'yolo-orb state-listening';
            if (mediaRecorder && mediaRecorder.state === 'paused') {
                mediaRecorder.resume();
            }
        }
    });

    yoloSpeakNowBtn.addEventListener('click', () => {
        if (audioContext && audioContext.state === 'suspended') {
            audioContext.resume();
        }
        if (currentState === STATES.LISTENING && hasSpoken) {
            // Force submit the current utterance
            submitSpeech();
        } else if (currentState === STATES.SPEAKING || currentState === STATES.THINKING) {
            // Manual interruption
            triggerInterruption();
        } else if (currentState === STATES.IDLE || currentState === STATES.LISTENING) {
            // Force start speaking (mocking voice activity)
            hasSpoken = true;
            lastSpeechTime = Date.now();
            updateState(STATES.LISTENING);
            yoloStatus.textContent = 'Говоріть, я слухаю...';
        }
    });

    yoloOverlay.addEventListener('click', () => {
        if (audioContext && audioContext.state === 'suspended') {
            audioContext.resume();
        }
    });

    // State machine updates
    function updateState(newState) {
        currentState = newState;
        yoloOrb.className = `yolo-orb state-${newState}`;

        if (newState === STATES.IDLE) {
            yoloStatus.textContent = 'Готова к разговору...';
            yoloSpeakNowBtn.textContent = 'Начать говорить';
            yoloSpeakNowBtn.disabled = false;
        } else if (newState === STATES.LISTENING) {
            yoloStatus.textContent = 'Слушаю вас...';
            yoloSpeakNowBtn.textContent = 'Отправить';
            yoloSpeakNowBtn.disabled = false;
        } else if (newState === STATES.THINKING) {
            yoloStatus.textContent = 'Ольга думает...';
            yoloSpeakNowBtn.textContent = 'Перебить';
            yoloSpeakNowBtn.disabled = false;
        } else if (newState === STATES.SPEAKING) {
            yoloStatus.textContent = 'Ольга говорит...';
            yoloSpeakNowBtn.textContent = 'Перебить';
            yoloSpeakNowBtn.disabled = false;
        }
    }

    async function acquireMicrophone() {
        if (micStream) return true;
        try {
            const stream = await navigator.mediaDevices.getUserMedia({
                audio: {
                    echoCancellation: true,
                    noiseSuppression: true,
                    autoGainControl: true
                }
            });
            micStream = stream;

            if (!audioContext) {
                audioContext = new (window.AudioContext || window.webkitAudioContext)();
                window.activeAudioContext = audioContext;
            }
            if (audioContext.state === 'suspended') {
                await audioContext.resume();
            }

            const source = audioContext.createMediaStreamSource(stream);
            if (analyser) {
                try { analyser.disconnect(); } catch(e){}
            } else {
                analyser = audioContext.createAnalyser();
                analyser.fftSize = 512;
            }
            source.connect(analyser);
            return true;
        } catch (err) {
            console.error('Failed to acquire microphone:', err);
            return false;
        }
    }

    function releaseMicrophone() {
        if (micStream) {
            micStream.getTracks().forEach(track => track.stop());
            micStream = null;
        }
    }

    async function enterYoloMode() {
        if (isInitializing || currentState !== STATES.DISABLED) return;
        isInitializing = true;
        try {
            const ok = await acquireMicrophone();
            if (!ok) {
                alert('Помилка: не вдалося отримати доступ до мікрофона.');
                isInitializing = false;
                return;
            }

            yoloOverlay.classList.remove('hidden');
            if (yoloFloatingBtn) yoloFloatingBtn.classList.add('hidden');
            updateState(STATES.IDLE);
            
            yoloTextPreview.innerHTML = '<div class="yolo-line assistant"><em>Ольга готова слушать вас. Начните говорить...</em></div>';
            if (window.playStaticClip) window.playStaticClip(pickRandom(GREETING_FILLERS));

            startUserListening();
            startVadLoop();
            requestWakeLock(); // Request wake lock to prevent screen sleep

            if (!window.currentSessionId) {
                const session = await API.post('/chat/sessions', { mode: 'dialog' });
                window.currentSessionId = session.id;
                if (window.loadSessions) window.loadSessions();
            }
            isInitializing = false;
        } catch (err) {
            isInitializing = false;
            console.error('Failed to initialize YOLO mode:', err);
        }
    }

    function exitYoloMode() {
        updateState(STATES.DISABLED);
        yoloOverlay.classList.add('hidden');
        if (yoloFloatingBtn) yoloFloatingBtn.classList.remove('hidden');

        if (vadInterval) clearInterval(vadInterval);
        vadInterval = null;

        releaseMicrophone();

        if (activeTtsQueue) { activeTtsQueue.stop(); activeTtsQueue = null; }
        if (window.stopAudioPlayback) window.stopAudioPlayback(); else {
            speechSynthesis.cancel();
        }
        if (window.activeUtterances) window.activeUtterances.length = 0;

        releaseWakeLock();

        if (activeAbortController) {
            activeAbortController.abort();
            activeAbortController = null;
        }

        if (audioContext && audioContext.state !== 'closed') {
            audioContext.close();
            audioContext = null;
            window.activeAudioContext = null;
            analyser = null;
        }

        if (window.currentSessionId && window.openSession) {
            window.openSession(window.currentSessionId);
        }
    }

    // Start waiting / recording user input
    function startUserListening() {
        const chunks = [];
        currentRecordingChunks = chunks;
        hasSpoken = false;
        consecutiveSpeechFrames = 0;
        consecutiveSilenceFrames = 0;

        if (mediaRecorder && mediaRecorder.state !== 'inactive') {
            try { mediaRecorder.stop(); } catch(e){}
        }

        mediaRecorder = new MediaRecorder(micStream);
        mediaRecorder.ondataavailable = (e) => {
            if (e.data.size > 0) chunks.push(e.data);
        };
        mediaRecorder.onstop = () => {
            // Audio recording finalized, ready to upload if user spoke
        };

        mediaRecorder.start(250); // Deliver chunks every 250ms
        updateState(STATES.LISTENING);
    }

    function stopAndGetBlob() {
        const chunks = currentRecordingChunks;
        return new Promise((resolve) => {
            if (!mediaRecorder || mediaRecorder.state === 'inactive') {
                resolve(chunks && chunks.length > 0 ? new Blob(chunks, { type: 'audio/webm' }) : null);
                return;
            }
            mediaRecorder.onstop = () => {
                const blob = chunks && chunks.length > 0 ? new Blob(chunks, { type: 'audio/webm' }) : null;
                resolve(blob);
            };
            mediaRecorder.stop();
        });
    }

    // Check VAD volumes
    function startVadLoop() {
        const dataArray = new Float32Array(analyser.fftSize);
        smoothedRms = 0;
        
        vadInterval = setInterval(() => {
            if (isMuted || currentState === STATES.DISABLED) return;

            analyser.getFloatTimeDomainData(dataArray);
            
            // Calculate RMS (Root Mean Square) volume
            let sum = 0;
            for (let i = 0; i < dataArray.length; i++) {
                sum += dataArray[i] * dataArray[i];
            }
            const rms = Math.sqrt(sum / dataArray.length);

            // Smooth the RMS with a rolling average filter to suppress transient clicks/noises
            smoothedRms = (smoothedRms * 0.75) + (rms * 0.25);

            // Dynamically scale the orb if user is talking
            if (currentState === STATES.LISTENING) {
                const scale = 1 + (smoothedRms * 5);
                yoloOrb.style.transform = `scale(${Math.min(scale, 1.7)})`;
            } else {
                yoloOrb.style.transform = '';
            }

            // VAD State transitions
            // Prevent AI's own voice from interrupting itself by raising threshold and frames during playback
            const activeThreshold = (currentState === STATES.SPEAKING)
                ? (sensitivityThreshold * 2.5)
                : sensitivityThreshold;

            if (smoothedRms > activeThreshold) {
                consecutiveSilenceFrames = 0;
                
                if (currentState === STATES.IDLE) {
                    consecutiveSpeechFrames++;
                    if (consecutiveSpeechFrames >= START_SPEECH_FRAMES) {
                        hasSpoken = true;
                        lastSpeechTime = Date.now();
                        speechStartTime = Date.now();
                        updateState(STATES.LISTENING);
                    }
                } else if (currentState === STATES.LISTENING) {
                    if (!hasSpoken) {
                        hasSpoken = true;
                        speechStartTime = Date.now();
                    }
                    
                    // Speech debounce: Only push back silence timer if sound is sustained for >= 2 frames (100ms)
                    consecutiveSpeechFrames++;
                    if (consecutiveSpeechFrames >= 2) {
                        lastSpeechTime = Date.now();
                    }
                } else if (currentState === STATES.THINKING) {
                    // Still transcribing/generating, nothing has been said out loud yet —
                    // treat renewed speech as a continuation of the same thought, not an
                    // interruption. Abandon the in-flight turn and start capturing the rest.
                    consecutiveSpeechFrames++;
                    if (consecutiveSpeechFrames >= 5) { // ~250ms of sustained sound
                        captureContinuation();
                    }
                } else if (currentState === STATES.SPEAKING) {
                    // Speech interruption detection — only once the assistant is actually
                    // talking out loud.
                    consecutiveSpeechFrames++;
                    if (consecutiveSpeechFrames >= 10) { // ~500ms of sustained sound
                        // User spoke over the AI!
                        triggerInterruption();
                    }
                }
            } else {
                consecutiveSpeechFrames = 0;
                
                if (currentState === STATES.LISTENING && hasSpoken) {
                    consecutiveSilenceFrames++;
                    const silenceDuration = Date.now() - lastSpeechTime;
                    
                    if (silenceDuration >= SILENCE_TIMEOUT) {
                        submitSpeech();
                    }
                }
            }
        }, 50);
    }

    function triggerInterruption() {
        console.log("YOLO: User interrupted the response.");

        // Stop any not-yet-played queued sentences and cancel speech synthesis
        if (activeTtsQueue) { activeTtsQueue.stop(); activeTtsQueue = null; }
        if (window.stopAudioPlayback) window.stopAudioPlayback(); else {
            speechSynthesis.cancel();
        }
        if (window.activeUtterances) window.activeUtterances.length = 0;

        // Abort fetch stream
        if (activeAbortController) {
            activeAbortController.abort();
            activeAbortController = null;
        }

        // Add visual indicator of interruption
        appendPreviewLine('user', '... (Перебито)');
        
        // Start listening to the new speech
        startUserListening();
        yoloStatus.textContent = 'Слухаю вас (перебито)...';
    }

    // User kept talking while we were still transcribing/generating (nothing spoken
    // out loud yet) — abandon this in-flight turn and start recording the rest. The
    // continuation gets submitted as a normal follow-up message in the same session,
    // so the model sees both fragments as consecutive turns and answers the combined
    // thought — no manual text-splicing needed.
    function captureContinuation() {
        console.log("YOLO: User continued speaking before the response started — merging.");

        if (activeTtsQueue) { activeTtsQueue.stop(); activeTtsQueue = null; }
        if (activeAbortController) {
            activeAbortController.abort();
            activeAbortController = null;
        }

        startUserListening();
    }

    async function submitSpeech() {
        if (!hasSpoken) {
            startUserListening();
            return;
        }

        // Filter out short throat clearing, coughs, snaps (under 450ms of active speech)
        const activeSpeechDuration = lastSpeechTime - speechStartTime;
        if (activeSpeechDuration < 450) {
            console.log(`YOLO VAD: Discarded short sound/cough of ${activeSpeechDuration}ms.`);
            yoloStatus.textContent = 'Игнорирую короткий шум...';
            hasSpoken = false;
            currentRecordingChunks = null;
            setTimeout(() => {
                if (currentState === STATES.LISTENING) {
                    startUserListening();
                }
            }, 750);
            return;
        }

        updateState(STATES.THINKING);

        // Record blob by safely stopping and waiting for recorder events
        const audioBlob = await stopAndGetBlob();
        currentRecordingChunks = null;

        if (!audioBlob) {
            console.warn("YOLO VAD: Failed to capture audio.");
            startUserListening();
            return;
        }

        try {
            // Upload audio to the API
            const result = await API.uploadAudio(audioBlob);
            const audioFileName = result.fileName;

            // Generate transcription display placeholder
            const previewMsgEl = appendPreviewLine('user', 'Распознавание...');

            // Call streaming chat with support for abortion
            activeAbortController = new AbortController();
            const signal = activeAbortController.signal;

            let assistantPreviewEl = null;
            let fullResponseText = "";
            let dispatchedLen = 0;

            const ttsQueue = window.createTtsQueue();
            activeTtsQueue = ttsQueue;

            // Play a filler phrase immediately so the user hears something right away
            // instead of silence while transcription/LLM/TTS are still working. Real
            // sentences are buffered until the filler finishes, then queued right after
            // it, so they always play in order with no overlap.
            let fillerDone = false;
            const bufferedSentences = [];
            let pendingFinish = null;
            function flushBuffered() {
                if (bufferedSentences.length > 0) {
                    if (currentState !== STATES.SPEAKING) updateState(STATES.SPEAKING);
                    bufferedSentences.forEach(s => ttsQueue.push(s));
                    bufferedSentences.length = 0;
                }
                if (pendingFinish) {
                    const fn = pendingFinish;
                    pendingFinish = null;
                    fn();
                }
            }
            if (window.playStaticClip) {
                window.playStaticClip(pickRandom(THINKING_FILLERS), () => {
                    fillerDone = true;
                    flushBuffered();
                });
            } else {
                fillerDone = true;
            }

            API.streamChat(window.currentSessionId, '',
                // onChunk
                (chunk) => {
                    if (!assistantPreviewEl) {
                        assistantPreviewEl = appendPreviewLine('assistant', '');
                    }
                    assistantPreviewEl.textContent += chunk;
                    yoloTextPreview.scrollTop = yoloTextPreview.scrollHeight;
                    fullResponseText += chunk;

                    const { sentences, nextIndex } = extractCompleteSentences(fullResponseText, dispatchedLen);
                    if (sentences.length > 0) {
                        dispatchedLen = nextIndex;
                        if (fillerDone) {
                            if (currentState !== STATES.SPEAKING) updateState(STATES.SPEAKING);
                            sentences.forEach(s => ttsQueue.push(s));
                        } else {
                            bufferedSentences.push(...sentences);
                        }
                    }
                },
                // onDone
                (err) => {
                    activeAbortController = null;
                    if (err) {
                        if (err.name === 'AbortError') {
                            console.log("YOLO stream aborted.");
                        } else {
                            console.warn("YOLO stream error:", err);
                            appendPreviewLine('assistant', 'Ошибка соединения. Попробуйте еще раз.');
                            ttsQueue.stop();
                            activeTtsQueue = null;
                            if (window.stopAudioPlayback) window.stopAudioPlayback();
                            updateState(STATES.IDLE);
                            startUserListening();
                        }
                    } else {
                        const doFinish = () => {
                            const remainder = fullResponseText.slice(dispatchedLen);
                            if (remainder.trim()) ttsQueue.push(remainder);

                            if (fullResponseText.trim()) {
                                if (currentState !== STATES.SPEAKING) updateState(STATES.SPEAKING);
                                ttsQueue.finish(() => {
                                    activeTtsQueue = null;
                                    // Transition back to listening ONLY if we were not interrupted
                                    if (currentState === STATES.SPEAKING) {
                                        startUserListening();
                                    }
                                });
                            } else {
                                activeTtsQueue = null;
                                startUserListening();
                            }
                        };
                        if (fillerDone) doFinish(); else pendingFinish = doFinish;
                    }
                },
                // audioFileName
                audioFileName,
                // onTranscription
                (transcription) => {
                    if (previewMsgEl) {
                        previewMsgEl.textContent = transcription;
                    }
                },
                // onPronunciation
                (pron) => {
                    // Optionally display pronunciation feedback in the preview
                    if (previewMsgEl) {
                        const stars = pron.accuracy >= 8 ? '⭐' : pron.accuracy >= 5 ? '🟡' : '🔴';
                        const scoreEl = document.createElement('div');
                        scoreEl.className = 'yolo-pron-score';
                        scoreEl.innerHTML = `<small style="color: #94a3b8">${stars} Вимова: ${pron.accuracy}/10. ${pron.feedback}</small>`;
                        previewMsgEl.appendChild(scoreEl);
                    }
                },
                // onTranslation (ignored in YOLO voice overlay to keep UI simple)
                null,
                // onTranslationDone (ignored)
                null,
                // signal
                signal
            );

        } catch (err) {
            console.error('YOLO sending failed:', err);
            appendPreviewLine('assistant', 'Ошибка отправки аудио.');
            updateState(STATES.IDLE);
        }
    }

    window.onYoloStats = (stats) => {
        const statsParts = [];
        if (stats.convertMs > 0) statsParts.push(`Conv: ${stats.convertMs}ms`);
        if (stats.transcribeMs > 0) statsParts.push(`Trans: ${stats.transcribeMs}ms`);
        if (stats.llmFirstTokenMs > 0) statsParts.push(`TTFT: ${stats.llmFirstTokenMs}ms`);
        if (stats.llmTotalMs > 0) statsParts.push(`LLM: ${stats.llmTotalMs}ms`);
        if (stats.translationMs > 0) statsParts.push(`Tr: ${stats.translationMs}ms`);
        
        if (statsParts.length > 0) {
            const statsEl = document.createElement('div');
            statsEl.className = 'yolo-line stats-info';
            statsEl.style.fontSize = '0.7rem';
            statsEl.style.color = 'var(--text-secondary)';
            statsEl.style.opacity = '0.7';
            statsEl.style.textAlign = 'right';
            statsEl.style.marginTop = '2px';
            statsEl.style.marginBottom = '6px';
            statsEl.textContent = '⏱️ ' + statsParts.join(' | ');
            yoloTextPreview.appendChild(statsEl);
            yoloTextPreview.scrollTop = yoloTextPreview.scrollHeight;
        }
    };

    async function requestWakeLock() {
        try {
            if ('wakeLock' in navigator) {
                wakeLock = await navigator.wakeLock.request('screen');
                console.log('YOLO: Screen Wake Lock activated.');
            }
        } catch (err) {
            console.warn('YOLO: Wake Lock request failed:', err);
        }
    }

    function releaseWakeLock() {
        if (wakeLock !== null) {
            wakeLock.release().then(() => {
                wakeLock = null;
                console.log('YOLO: Screen Wake Lock released.');
            });
        }
    }

    // Re-request wake lock when user switches back to the visible tab
    document.addEventListener('visibilitychange', async () => {
        if (document.visibilityState === 'visible' && currentState !== STATES.DISABLED) {
            await requestWakeLock();
        }
    });

    // Temporary hook for TTS diagnostics from voice.js (visible on iPad without devtools)
    window.yoloDebugLine = (msg) => {
        const line = appendPreviewLine('assistant', '🔧 ' + msg);
        line.style.fontSize = '0.7rem';
        line.style.opacity = '0.6';
        return line;
    };

    function appendPreviewLine(role, text) {
        // Ensure preview stays neat
        const line = document.createElement('div');
        line.className = `yolo-line ${role}`;
        line.textContent = text;
        yoloTextPreview.appendChild(line);
        yoloTextPreview.scrollTop = yoloTextPreview.scrollHeight;
        return line;
    }
})();
