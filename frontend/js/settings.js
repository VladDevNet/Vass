if (!API.getToken()) {
    window.location.href = 'index.html';
}

async function loadSettings() {
    try {
        const s = await API.get('/settings');
        document.getElementById('display-name').value = s.displayName || '';
        document.getElementById('interface-language').value = s.interfaceLanguage || 'uk';
        document.getElementById('openai-key').value = s.openAiApiKey || '';
        document.getElementById('anthropic-key').value = s.anthropicApiKey || '';
        document.getElementById('gemini-key').value = s.geminiApiKey || '';
        document.getElementById('custom-prompt').value = s.customSystemPrompt || '';
        document.getElementById('full-translation').checked = s.fullTranslation || false;
    } catch (e) {
        showError('Не вдалося завантажити налаштування');
    }
}

document.getElementById('save-btn').addEventListener('click', async () => {
    const btn = document.getElementById('save-btn');
    btn.disabled = true;
    hideMessages();

    try {
        const openAiApiKey = document.getElementById('openai-key').value.trim();
        const anthropicApiKey = document.getElementById('anthropic-key').value.trim();
        const geminiApiKey = document.getElementById('gemini-key').value.trim();

        await API.put('/settings', {
            displayName: document.getElementById('display-name').value.trim() || null,
            interfaceLanguage: document.getElementById('interface-language').value,
            openAiApiKey,
            anthropicApiKey,
            geminiApiKey,
            customSystemPrompt: document.getElementById('custom-prompt').value.trim() || null,
            fullTranslation: document.getElementById('full-translation').checked
        });
        showSuccess();
    } catch (e) {
        showError('Помилка збереження: ' + e.message);
    } finally {
        btn.disabled = false;
    }
});

document.getElementById('show-default-btn').addEventListener('click', async () => {
    const block = document.getElementById('default-prompt-block');
    if (!block.classList.contains('hidden')) {
        block.classList.add('hidden');
        return;
    }
    try {
        const data = await API.get('/settings/default-prompt');
        block.textContent = data.prompt;
        block.classList.remove('hidden');
    } catch {}
});

function showError(msg) {
    const el = document.getElementById('settings-error');
    el.textContent = msg;
    el.classList.remove('hidden');
}

function showSuccess() {
    const el = document.getElementById('settings-success');
    el.classList.remove('hidden');
    setTimeout(() => el.classList.add('hidden'), 2000);
}

function hideMessages() {
    document.getElementById('settings-error').classList.add('hidden');
    document.getElementById('settings-success').classList.add('hidden');
}

loadSettings();
