const API = {
    baseUrl: '/api',

    getToken() {
        return localStorage.getItem('token');
    },

    setToken(token) {
        localStorage.setItem('token', token);
    },

    clearToken() {
        localStorage.removeItem('token');
    },

    async request(path, options = {}) {
        const token = this.getToken();
        const headers = { 'Content-Type': 'application/json', ...options.headers };
        if (token) headers['Authorization'] = `Bearer ${token}`;

        const res = await fetch(this.baseUrl + path, { ...options, headers });

        if (res.status === 401) {
            this.clearToken();
            window.location.href = 'index.html';
            throw new Error('Unauthorized');
        }

        if (!res.ok) {
            const data = await res.json().catch(() => ({}));
            throw new Error(data.error || data.errors?.join(', ') || `HTTP ${res.status}`);
        }

        if (res.status === 204) return null;
        return res.json();
    },

    get(path) {
        return this.request(path);
    },

    post(path, body) {
        return this.request(path, { method: 'POST', body: JSON.stringify(body) });
    },

    put(path, body) {
        return this.request(path, { method: 'PUT', body: JSON.stringify(body) });
    },

    patch(path, body) {
        return this.request(path, { method: 'PATCH', body: JSON.stringify(body) });
    },

    delete(path) {
        return this.request(path, { method: 'DELETE' });
    },

    async uploadAudio(blob) {
        const token = this.getToken();
        const form = new FormData();
        form.append('file', blob, 'voice.webm');
        const res = await fetch(this.baseUrl + '/chat/upload-audio', {
            method: 'POST',
            headers: { 'Authorization': `Bearer ${token}` },
            body: form
        });
        if (!res.ok) throw new Error(`Upload failed: ${res.status}`);
        return res.json();
    },

    async ocrImage(file) {
        const token = this.getToken();
        const form = new FormData();
        form.append('file', file);
        const res = await fetch(this.baseUrl + '/chat/ocr-image', {
            method: 'POST',
            headers: { 'Authorization': `Bearer ${token}` },
            body: form
        });
        if (!res.ok) throw new Error(`OCR failed: ${res.status}`);
        return res.json();
    },

    // SSE streaming for chat
    streamChat(sessionId, message, onChunk, onDone, audioFileName, onTranscription, onPronunciation, onTranslation, onTranslationDone, signal) {
        const token = this.getToken();
        let mainDone = false;

        fetch(this.baseUrl + '/chat/send', {
            method: 'POST',
            headers: {
                'Content-Type': 'application/json',
                'Authorization': `Bearer ${token}`
            },
            body: JSON.stringify({ sessionId, message, audioFileName }),
            signal: signal
        }).then(response => {
            if (!response.ok) { onDone(new Error(`HTTP ${response.status}`)); return; }
            const reader = response.body.getReader();
            const decoder = new TextDecoder();
            let buffer = '';

            function read() {
                reader.read().then(({ done, value }) => {
                    if (done) { if (!mainDone) onDone(); if (onTranslationDone) onTranslationDone(); return; }

                    buffer += decoder.decode(value, { stream: true });
                    const lines = buffer.split('\n');
                    buffer = lines.pop();

                    for (const line of lines) {
                        if (line.startsWith('data: ')) {
                            const data = line.slice(6).trim();
                            if (data === '[DONE]') { mainDone = true; onDone(); continue; }
                            if (data === '[TR_DONE]') { if (onTranslationDone) onTranslationDone(); continue; }
                            try {
                                const parsed = JSON.parse(data);
                                if (parsed.stats) {
                                    console.log("Performance Stats:", parsed.stats);
                                    if (window.onYoloStats) window.onYoloStats(parsed.stats);
                                } else if (parsed.transcription && onTranscription) {
                                    onTranscription(parsed.transcription);
                                } else if (parsed.pronunciation && onPronunciation) {
                                    onPronunciation(parsed.pronunciation);
                                } else if (parsed.translation && onTranslation) {
                                    onTranslation(parsed.translation);
                                } else if (parsed.text) {
                                    onChunk(parsed.text);
                                }
                            } catch {}
                        }
                    }
                    read();
                }).catch((err) => onDone(err));
            }
            read();
        }).catch((err) => onDone(err));
    }
};
