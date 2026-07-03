// Redirect if not logged in
if (!API.getToken()) {
    window.location.href = 'index.html';
}

let currentSessionId = null;
let isStreaming = false;

// Expose currentSessionId to other scripts (e.g. yolo.js)
Object.defineProperty(window, 'currentSessionId', {
    get: () => currentSessionId,
    set: (val) => { currentSessionId = val; },
    configurable: true
});

// Load user info
async function loadUser() {
    try {
        const user = await API.get('/auth/me');
        document.getElementById('user-email').textContent = user.email;
        document.getElementById('user-level').textContent = user.level;
    } catch {}
}

// Load sessions list
async function loadSessions(autoOpen = false) {
    const sessions = await API.get('/chat/sessions');
    const list = document.getElementById('sessions-list');
    list.innerHTML = sessions.map(s => `
        <div class="session-item ${s.id === currentSessionId ? 'active' : ''}"
             data-id="${s.id}">
            <span class="session-title">${escapeHtml(s.title || s.mode)} — ${new Date(s.createdAt).toLocaleDateString()}</span>
            <button class="session-menu-btn" data-id="${s.id}" title="Menu">&#8942;</button>
            <div class="session-menu hidden" data-id="${s.id}">
                <div class="session-menu-item rename" data-id="${s.id}">Перейменувати</div>
                <div class="session-menu-item delete" data-id="${s.id}">Видалити</div>
            </div>
        </div>
    `).join('');

    list.querySelectorAll('.session-item').forEach(el => {
        el.querySelector('.session-title').addEventListener('click', () => openSession(parseInt(el.dataset.id)));
    });

    // Menu button toggle
    list.querySelectorAll('.session-menu-btn').forEach(btn => {
        btn.addEventListener('click', (e) => {
            e.stopPropagation();
            closeAllSessionMenus();
            btn.nextElementSibling.classList.toggle('hidden');
        });
    });

    // Rename handler
    list.querySelectorAll('.session-menu-item.rename').forEach(item => {
        item.addEventListener('click', async (e) => {
            e.stopPropagation();
            closeAllSessionMenus();
            const id = parseInt(item.dataset.id);
            const titleEl = item.closest('.session-item').querySelector('.session-title');
            const currentTitle = titleEl.textContent.split(' — ')[0];
            const newTitle = prompt('Нова назва:', currentTitle);
            if (!newTitle || newTitle.trim() === currentTitle) return;
            try {
                await API.patch(`/chat/sessions/${id}`, { title: newTitle.trim() });
                await loadSessions();
            } catch (err) { console.warn('Rename failed:', err); }
        });
    });

    // Delete handler
    list.querySelectorAll('.session-menu-item.delete').forEach(item => {
        item.addEventListener('click', async (e) => {
            e.stopPropagation();
            const id = parseInt(item.dataset.id);
            try {
                await API.delete(`/chat/sessions/${id}`);
            } catch {}
            if (id === currentSessionId) {
                currentSessionId = null;
                document.getElementById('chat-messages').innerHTML = '';
            }
            await loadSessions();
        });
    });

    if (autoOpen && !currentSessionId && sessions.length > 0) {
        await openSession(sessions[0].id);
    }
}

// Open session
async function openSession(id) {
    currentSessionId = id;
    const data = await API.get(`/chat/sessions/${id}`);
    const container = document.getElementById('chat-messages');

    container.innerHTML = data.messages.map(m => {
        const audioHtml = m.audioFileName
            ? `<div class="voice-playback"><button class="btn-play-audio" data-audio="${escapeHtml(m.audioFileName)}">&#9654; Audio</button></div>`
            : '';
        const replayHtml = m.role === 'assistant'
            ? `<div><button class="btn-replay-tts">&#128264; Replay</button></div>`
            : '';
        return `<div class="message ${m.role}">${audioHtml}${formatMessage(m.content, m.role)}${replayHtml}</div>`;
    }).join('');

    container.scrollTop = container.scrollHeight;
    loadSessions();
}

// Format message — simple escape for Voice Assistant
function formatMessage(text, role) {
    return escapeHtml(text);
}

// Create new session
document.getElementById('new-session-btn').addEventListener('click', async () => {
    const session = await API.post('/chat/sessions', { mode: 'dialog' });
    currentSessionId = session.id;
    document.getElementById('chat-messages').innerHTML = '';
    await loadSessions();
});

// Send message
async function sendMessage() {
    const input = document.getElementById('message-input');
    const text = input.value.trim();
    const container = document.getElementById('chat-messages');

    // Stop recording if active and wait for blob
    let audioBlob = null;
    if (window.VoiceRecorder?.isRecording()) {
        audioBlob = await window.VoiceRecorder.stopAndGetBlob();
    } else if (window.VoiceRecorder) {
        audioBlob = window.VoiceRecorder.consumeAudioBlob();
    }
    if (window.VoiceRecorder) window.VoiceRecorder.consumeAudioUrl();

    const hasAudio = !!audioBlob;
    if (!text && !hasAudio) return;
    if (isStreaming) return;

    // Auto-create session if none selected
    if (!currentSessionId) {
        const session = await API.post('/chat/sessions', { mode: 'dialog' });
        currentSessionId = session.id;
        await loadSessions();
    }

    input.value = '';
    input.style.height = 'auto';
    isStreaming = true;
    document.getElementById('send-btn').disabled = true;

    let audioFileName = null;
    if (audioBlob) {
        try {
            const result = await API.uploadAudio(audioBlob);
            audioFileName = result.fileName;
        } catch (e) {
            console.warn('Audio upload failed:', e);
        }
    }

    const audioHtml = audioFileName
        ? `<div class="voice-playback"><button class="btn-play-audio" data-audio="${escapeHtml(audioFileName)}">&#9654; Audio</button></div>`
        : '';

    // Show user message — placeholder if audio-only (will be replaced by transcription)
    const userMsg = document.createElement('div');
    userMsg.className = 'message user';
    const displayText = text || 'Розпізнавання...';
    userMsg.innerHTML = `${audioHtml}<span class="user-text">${escapeHtml(displayText)}</span>`;
    container.appendChild(userMsg);

    // Add streaming assistant message with thinking indicator
    const assistantMsg = document.createElement('div');
    assistantMsg.className = 'message assistant streaming';
    assistantMsg.innerHTML = '<em class="thinking">Ольга думает...</em>';
    container.appendChild(assistantMsg);
    container.scrollTop = container.scrollHeight;

    // For audio-only: send empty message, server will transcribe
    const messageToSend = hasAudio && !text ? '' : text;

    let fullText = '';
    let transMsg = null;
    API.streamChat(currentSessionId, messageToSend,
        (chunk) => {
            if (!fullText) assistantMsg.innerHTML = '';
            fullText += chunk;
            assistantMsg.textContent += chunk;
            container.scrollTop = container.scrollHeight;
        },
        (err) => {
            assistantMsg.classList.remove('streaming');
            if (err) {
                assistantMsg.innerHTML = '<em class="error">Помилка з\'єднання. Спробуй ще раз.</em>';
                isStreaming = false;
                document.getElementById('send-btn').disabled = false;
            } else {
                assistantMsg.innerHTML = formatMessage(fullText, 'assistant')
                    + '<div><button class="btn-replay-tts">&#128264; Replay</button></div>';
                if (hasAudio && window.speakText) window.speakText(fullText);
                container.scrollTop = container.scrollHeight;
                // If no translation follows, re-enable immediately
                // (will be overridden by onTranslationDone if translation streams)
                setTimeout(() => { if (!transMsg) { isStreaming = false; document.getElementById('send-btn').disabled = false; } }, 500);
            }
        },
        audioFileName,
        (transcription) => {
            const textSpan = userMsg.querySelector('.user-text');
            if (textSpan) textSpan.textContent = transcription;
        },
        (pronunciation) => {
            showPronunciationBlock(userMsg, pronunciation);
        },
        // onTranslation chunk
        (chunk) => {
            if (!transMsg) {
                transMsg = document.createElement('div');
                transMsg.className = 'message assistant translation-msg streaming';
                container.appendChild(transMsg);
            }
            transMsg.textContent += chunk;
            container.scrollTop = container.scrollHeight;
        },
        // onTranslationDone
        () => {
            if (transMsg) transMsg.classList.remove('streaming');
            isStreaming = false;
            document.getElementById('send-btn').disabled = false;
        }
    );
}

document.getElementById('send-btn').addEventListener('click', sendMessage);

document.getElementById('message-input').addEventListener('keydown', (e) => {
    if (e.key === 'Enter' && !e.shiftKey) {
        e.preventDefault();
        sendMessage();
    }
});

// Auto-resize textarea
document.getElementById('message-input').addEventListener('input', function() {
    this.style.height = 'auto';
    this.style.height = Math.min(this.scrollHeight, 150) + 'px';
});

// Camera OCR
document.getElementById('camera-btn').addEventListener('click', () => {
    document.getElementById('camera-input').click();
});

document.getElementById('camera-input').addEventListener('change', async (e) => {
    const file = e.target.files[0];
    if (!file) return;
    e.target.value = '';

    // Show attachment preview
    const preview = document.getElementById('attachment-preview');
    const thumbUrl = URL.createObjectURL(file);
    preview.innerHTML = `<img src="${thumbUrl}" class="attachment-thumb"><span class="attachment-label">Розпізнаю текст...</span><button class="attachment-remove">&times;</button>`;
    preview.classList.remove('hidden');
    preview.querySelector('.attachment-remove').addEventListener('click', () => {
        preview.classList.add('hidden');
        preview.innerHTML = '';
        URL.revokeObjectURL(thumbUrl);
    });

    const cameraBtn = document.getElementById('camera-btn');
    cameraBtn.classList.add('ocr-loading');
    cameraBtn.disabled = true;

    try {
        const result = await API.ocrImage(file);
        if (result.text) {
            const input = document.getElementById('message-input');
            input.value = input.value ? input.value + '\n' + result.text : result.text;
            input.style.height = 'auto';
            input.style.height = Math.min(input.scrollHeight, 150) + 'px';
            input.focus();
            preview.querySelector('.attachment-label').textContent = 'Текст розпізнано';
            setTimeout(() => { preview.classList.add('hidden'); preview.innerHTML = ''; }, 2000);
        } else {
            preview.querySelector('.attachment-label').textContent = 'Текст не знайдено';
        }
    } catch (err) {
        console.warn('OCR failed:', err);
        preview.querySelector('.attachment-label').textContent = 'Помилка розпізнавання';
    } finally {
        cameraBtn.classList.remove('ocr-loading');
        cameraBtn.disabled = false;
        URL.revokeObjectURL(thumbUrl);
    }
});

// Logout
document.getElementById('logout-btn').addEventListener('click', () => {
    API.clearToken();
    window.location.href = 'index.html';
});

// Play audio button handler
document.getElementById('chat-messages').addEventListener('click', async (e) => {
    const playBtn = e.target.closest('.btn-play-audio');
    if (!playBtn) return;
    const fileName = playBtn.dataset.audio;
    if (!fileName) return;
    playBtn.disabled = true;
    playBtn.textContent = '...';
    try {
        const token = API.getToken();
        const res = await fetch(`/api/chat/audio/${fileName}`, {
            headers: { 'Authorization': `Bearer ${token}` }
        });
        if (!res.ok) throw new Error('Failed to load audio');
        const blob = await res.blob();
        const url = URL.createObjectURL(blob);
        const audio = new Audio(url);
        audio.onended = () => URL.revokeObjectURL(url);
        audio.play();
        playBtn.textContent = '\u25B6 Audio';
        playBtn.disabled = false;
    } catch (err) {
        console.warn('Audio playback failed:', err);
        playBtn.textContent = '\u25B6 Audio';
        playBtn.disabled = false;
    }
});

// Replay TTS button handler
document.getElementById('chat-messages').addEventListener('click', (e) => {
    const btn = e.target.closest('.btn-replay-tts');
    if (!btn) return;
    const msg = btn.closest('.message.assistant');
    if (!msg || !window.speakText) return;
    // Extract text content, skipping the button itself
    const clone = msg.cloneNode(true);
    clone.querySelectorAll('.btn-replay-tts, .voice-playback').forEach(el => el.remove());
    window.speakText(clone.textContent);
});

// Delegate click on vocab add buttons
document.getElementById('chat-messages').addEventListener('click', (e) => {
    const btn = e.target.closest('.vocab-add-btn');
    if (!btn) return;
    const span = btn.closest('.vocab-word');
    if (!span) return;
    const word = span.dataset.word;
    const translation = span.dataset.translation;
    if (window.Vocabulary) {
        window.Vocabulary.showAddWordDialog(word);
    }
});

// Close all session context menus
function closeAllSessionMenus() {
    document.querySelectorAll('.session-menu').forEach(m => m.classList.add('hidden'));
}

// Close menus on click outside
document.addEventListener('click', () => closeAllSessionMenus());

function showPronunciationBlock(userMsg, pron) {
    const block = document.createElement('div');
    block.className = 'pronunciation-block';
    const stars = pron.accuracy >= 8 ? '&#11088;' : pron.accuracy >= 5 ? '&#128993;' : '&#128308;';
    let html = `<div class="pron-header">${stars} Вимова: <strong>${pron.accuracy}/10</strong></div>`;
    html += `<div class="pron-feedback">${escapeHtml(pron.feedback)}</div>`;
    if (pron.problemWords && pron.problemWords.length > 0) {
        html += '<div class="pron-problems">';
        for (const pw of pron.problemWords) {
            html += `<span class="pron-problem"><strong>${escapeHtml(pw.word)}</strong>: ${escapeHtml(pw.issue)}</span>`;
        }
        html += '</div>';
    }
    block.innerHTML = html;
    userMsg.appendChild(block);
}

function escapeHtml(text) {
    const div = document.createElement('div');
    div.textContent = text;
    return div.innerHTML;
}

// Sidebar toggle (mobile drawer)
(function() {
    const toggle = document.getElementById('sidebar-toggle');
    const sidebar = document.getElementById('sidebar');
    const backdrop = document.getElementById('sidebar-backdrop');
    if (!toggle || !sidebar || !backdrop) return;
    function closeSidebar() { sidebar.classList.remove('open'); backdrop.classList.remove('open'); }
    toggle.addEventListener('click', () => { sidebar.classList.toggle('open'); backdrop.classList.toggle('open'); });
    backdrop.addEventListener('click', closeSidebar);
    sidebar.querySelectorAll('.session-title, .btn-primary, .btn-sidebar, a.btn-sidebar').forEach(el =>
        el.addEventListener('click', closeSidebar)
    );
})();

// Init
async function init() {
    await loadUser();
    await loadSessions(true);

    // Auto-open YOLO mode on startup
    setTimeout(() => {
        const yoloBtn = document.getElementById('yolo-btn');
        if (yoloBtn && currentSessionId) {
            yoloBtn.click();
        }
    }, 500);
}
init();
