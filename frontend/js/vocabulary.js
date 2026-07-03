// Vocabulary panel logic
window.Vocabulary = {
    currentFilter: 'all',

    async load() {
        const search = document.getElementById('vocab-search')?.value || '';
        const status = this.currentFilter;
        try {
            const params = new URLSearchParams({ status, search });
            const data = await API.get(`/vocabulary?${params}`);
            this.renderList(data.words);
            this.loadStats();
        } catch {}
    },

    async loadStats() {
        try {
            const stats = await API.get('/vocabulary/stats');
            const el = document.getElementById('vocab-stats');
            if (el) {
                el.textContent = `Всього: ${stats.total} | Нові: ${stats.new} | Вчу: ${stats.learning} | Знаю: ${stats.known}`;
            }
        } catch {}
    },

    renderList(words) {
        const list = document.getElementById('vocab-list');
        if (!list) return;

        if (!words.length) {
            list.innerHTML = '<div class="vocab-empty">Словник порожній</div>';
            return;
        }

        list.innerHTML = words.map(w => {
            const grammarHtml = w.grammarInfo
                ? `<div class="vocab-grammar-toggle" data-id="${w.id}">граматика ▾</div>
                   <div class="vocab-grammar hidden" id="grammar-${w.id}">${this.formatGrammar(w.grammarInfo)}</div>`
                : '';
            return `
            <div class="vocab-item" data-id="${w.id}">
                <div class="vocab-item-main">
                    <span class="vocab-word-text" title="Натисни щоб прослухати">${w.word}</span>
                    <span class="vocab-translation">${w.translation}</span>
                    ${grammarHtml}
                </div>
                <div class="vocab-item-actions">
                    <select class="vocab-status-select" data-id="${w.id}">
                        <option value="new" ${w.status === 'new' ? 'selected' : ''}>Нове</option>
                        <option value="learning" ${w.status === 'learning' ? 'selected' : ''}>Вчу</option>
                        <option value="known" ${w.status === 'known' ? 'selected' : ''}>Знаю</option>
                    </select>
                </div>
            </div>`;
        }).join('');
    },

    formatGrammar(grammarJson) {
        try {
            const data = typeof grammarJson === 'string' ? JSON.parse(grammarJson) : grammarJson;
            const g = data.grammar;
            if (!g || Object.keys(g).length === 0) return '';

            let html = `<div class="grammar-part">${data.partOfSpeech}</div>`;

            if (g.przypadki) {
                html += '<table class="grammar-table">';
                for (const [caso, form] of Object.entries(g.przypadki)) {
                    html += `<tr><td>${caso}</td><td>${form}</td></tr>`;
                }
                html += '</table>';
            }

            if (g.czas_teraźniejszy) {
                html += '<div class="grammar-label">Czas teraźniejszy:</div><table class="grammar-table">';
                for (const [p, f] of Object.entries(g.czas_teraźniejszy)) {
                    html += `<tr><td>${p}</td><td>${f}</td></tr>`;
                }
                html += '</table>';
            }
            if (g.czas_przeszły) {
                html += '<div class="grammar-label">Czas przeszły:</div><table class="grammar-table">';
                for (const [p, f] of Object.entries(g.czas_przeszły)) {
                    html += `<tr><td>${p}</td><td>${f}</td></tr>`;
                }
                html += '</table>';
            }

            if (g.stopniowanie) {
                html += '<table class="grammar-table">';
                for (const [s, f] of Object.entries(g.stopniowanie)) {
                    html += `<tr><td>${s}</td><td>${f}</td></tr>`;
                }
                html += '</table>';
            }

            return html;
        } catch {
            return '';
        }
    },

    async addWord(word, translation) {
        try {
            await API.post('/vocabulary/add', { word, translation });
        } catch {}
    },

    async addWordWithAnalysis(word) {
        try {
            const analysis = await API.post('/vocabulary/analyze', { word });
            return analysis;
        } catch (e) {
            console.error('Analyze failed:', e);
            return null;
        }
    },

    async saveAnalyzedWord(word, translation, grammarInfo) {
        try {
            const result = await API.post('/vocabulary/add', {
                word, translation, grammarInfo: JSON.stringify(grammarInfo)
            });
            return result;
        } catch (e) {
            return { error: e.message };
        }
    },

    async updateStatus(id, status) {
        try {
            await API.request(`/vocabulary/${id}/status`, {
                method: 'PUT',
                body: JSON.stringify({ status })
            });
            this.loadStats();
        } catch {}
    },

    init() {
        const panel = document.getElementById('vocabulary-panel');
        if (!panel) return;

        // Open/close
        document.getElementById('vocab-btn')?.addEventListener('click', () => {
            panel.classList.toggle('hidden');
            if (!panel.classList.contains('hidden')) this.load();
        });

        document.getElementById('vocab-close-btn')?.addEventListener('click', () => {
            panel.classList.add('hidden');
        });

        // Filters
        panel.querySelectorAll('.vocab-filter').forEach(btn => {
            btn.addEventListener('click', () => {
                panel.querySelectorAll('.vocab-filter').forEach(b => b.classList.remove('active'));
                btn.classList.add('active');
                this.currentFilter = btn.dataset.status;
                this.load();
            });
        });

        // Search
        let searchTimeout;
        document.getElementById('vocab-search')?.addEventListener('input', () => {
            clearTimeout(searchTimeout);
            searchTimeout = setTimeout(() => this.load(), 300);
        });

        // Status change (delegated)
        document.getElementById('vocab-list')?.addEventListener('change', (e) => {
            const select = e.target.closest('.vocab-status-select');
            if (select) this.updateStatus(select.dataset.id, select.value);
        });

        // Grammar toggle (delegated)
        document.getElementById('vocab-list')?.addEventListener('click', (e) => {
            const toggle = e.target.closest('.vocab-grammar-toggle');
            if (toggle) {
                const grammarEl = document.getElementById(`grammar-${toggle.dataset.id}`);
                if (grammarEl) grammarEl.classList.toggle('hidden');
                return;
            }

            // TTS on word click
            const wordEl = e.target.closest('.vocab-word-text');
            if (wordEl && window.speakText) {
                window.speakText(wordEl.textContent);
            }
        });

        // Add word from clipboard button
        document.getElementById('vocab-add-clipboard-btn')?.addEventListener('click', () => {
            this.showAddWordDialog();
        });

        // Selection-based add in chat
        this.initSelectionPopup();
    },

    showAddWordDialog(prefill = '') {
        // Remove existing dialog
        document.getElementById('vocab-add-dialog')?.remove();

        const dialog = document.createElement('div');
        dialog.id = 'vocab-add-dialog';
        dialog.className = 'vocab-add-dialog';
        dialog.innerHTML = `
            <div class="vocab-dialog-content">
                <h4>Додати слово</h4>
                <input type="text" id="vocab-dialog-word" placeholder="Слово польською" value="${prefill}">
                <div id="vocab-dialog-result" class="vocab-dialog-result hidden"></div>
                <div class="vocab-dialog-actions">
                    <button id="vocab-dialog-analyze" class="btn-primary">Аналізувати</button>
                    <button id="vocab-dialog-cancel" class="btn-small">Скасувати</button>
                </div>
            </div>
        `;
        document.body.appendChild(dialog);

        const wordInput = document.getElementById('vocab-dialog-word');
        const resultDiv = document.getElementById('vocab-dialog-result');

        document.getElementById('vocab-dialog-analyze').addEventListener('click', async () => {
            const w = wordInput.value.trim();
            if (!w) return;
            resultDiv.classList.remove('hidden');
            resultDiv.innerHTML = '<em>Аналізую...</em>';

            const analysis = await this.addWordWithAnalysis(w);
            if (!analysis) {
                resultDiv.innerHTML = '<span class="error">Помилка аналізу</span>';
                return;
            }

            const saveBtn = analysis.alreadyExists
                ? '<div style="color:var(--text-light);margin-top:8px">Це слово вже є у словнику</div>'
                : '<button id="vocab-dialog-save" class="btn-primary" style="margin-top:8px">Зберегти</button>';
            resultDiv.innerHTML = `
                <div><strong>${analysis.word}</strong> — ${analysis.translation}</div>
                <div class="grammar-part">${analysis.partOfSpeech}</div>
                ${this.formatGrammar(analysis)}
                ${saveBtn}
            `;

            document.getElementById('vocab-dialog-save')?.addEventListener('click', async () => {
                const saved = await this.saveAnalyzedWord(analysis.word, analysis.translation, analysis);
                if (saved?.error) {
                    resultDiv.innerHTML += `<div class="error">${saved.error}</div>`;
                } else {
                    dialog.remove();
                    this.load();
                }
            });
        });

        document.getElementById('vocab-dialog-cancel').addEventListener('click', () => dialog.remove());
        dialog.addEventListener('click', (e) => { if (e.target === dialog) dialog.remove(); });

        if (prefill) {
            document.getElementById('vocab-dialog-analyze').click();
        } else {
            wordInput.focus();
        }
    },

    initSelectionPopup() {
        // Floating popup with two buttons when text is selected in chat
        const popup = document.createElement('div');
        popup.id = 'vocab-selection-popup';
        popup.className = 'vocab-selection-popup hidden';
        popup.innerHTML = '<button class="popup-vocab-btn">+ Словник</button><button class="popup-speak-btn">\u{1F50A} Озвучити</button>';
        document.body.appendChild(popup);

        let selectedText = '';
        const chatMessages = document.getElementById('chat-messages');
        if (!chatMessages) return;

        function showPopupForSelection() {
            const sel = window.getSelection();
            const text = sel?.toString().trim();
            if (!text || text.length === 0 || text.length >= 100) {
                popup.classList.add('hidden');
                return;
            }
            // Check selection is inside chat
            if (!sel.rangeCount) return;
            const range = sel.getRangeAt(0);
            if (!chatMessages.contains(range.commonAncestorContainer)) {
                popup.classList.add('hidden');
                return;
            }
            selectedText = text;
            const rect = range.getBoundingClientRect();
            popup.style.left = (rect.left + rect.width / 2 - 80) + 'px';
            popup.style.top = (rect.bottom + window.scrollY + 6) + 'px';
            popup.classList.remove('hidden');
        }

        // Desktop: mouseup
        chatMessages.addEventListener('mouseup', () => {
            setTimeout(showPopupForSelection, 10);
        });

        // Mobile: selectionchange (fires when user adjusts selection handles)
        let selTimer = null;
        document.addEventListener('selectionchange', () => {
            clearTimeout(selTimer);
            selTimer = setTimeout(showPopupForSelection, 300);
        });

        document.addEventListener('mousedown', (e) => {
            if (!popup.contains(e.target)) popup.classList.add('hidden');
        });
        document.addEventListener('touchstart', (e) => {
            if (!popup.contains(e.target)) popup.classList.add('hidden');
        });

        popup.querySelector('.popup-vocab-btn').addEventListener('click', () => {
            popup.classList.add('hidden');
            window.getSelection()?.removeAllRanges();
            if (selectedText) this.showAddWordDialog(selectedText);
        });

        popup.querySelector('.popup-speak-btn').addEventListener('click', () => {
            popup.classList.add('hidden');
            window.getSelection()?.removeAllRanges();
            if (selectedText && window.speakText) window.speakText(selectedText);
        });
    }
};

// Init on load
Vocabulary.init();
