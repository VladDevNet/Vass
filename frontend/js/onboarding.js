// Onboarding — check if user needs level selection
window.Onboarding = {
    async check() {
        try {
            const status = await API.get('/onboarding/status');
            if (status.needsOnboarding) {
                this.show();
                return true;
            }
        } catch {}
        return false;
    },

    show() {
        document.getElementById('onboarding-overlay').classList.remove('hidden');
    },

    hide() {
        document.getElementById('onboarding-overlay').classList.add('hidden');
    },

    async setLevel(level) {
        try {
            await API.post('/onboarding/set-level', { level });
            document.getElementById('user-level').textContent = level;
            this.hide();
        } catch (err) {
            alert(err.message);
        }
    },

    async startLevelTest() {
        this.hide();
        // Create a level_test session and open it
        const session = await API.post('/chat/sessions', { mode: 'level_test', title: 'Тест рівня' });
        if (window.openSession) {
            currentSessionId = session.id;
            document.getElementById('chat-messages').innerHTML = '';
            await loadSessions();
            // Send initial trigger message
            document.getElementById('message-input').value = 'Cześć! Chcę sprawdzić swój poziom polskiego.';
            document.getElementById('send-btn').click();
        }
    },

    init() {
        // Level select buttons
        document.querySelectorAll('.onboarding-level-btn').forEach(btn => {
            btn.addEventListener('click', () => this.setLevel(btn.dataset.level));
        });

        document.getElementById('onboarding-test-btn')?.addEventListener('click', () => this.startLevelTest());
        document.getElementById('onboarding-skip-btn')?.addEventListener('click', () => {
            const sel = document.getElementById('onboarding-level-select');
            this.setLevel(sel.value);
        });
    }
};
