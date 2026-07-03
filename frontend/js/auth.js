// Redirect if already logged in
if (API.getToken()) {
    window.location.href = 'app.html';
}

// Tabs
document.querySelectorAll('.tab').forEach(tab => {
    tab.addEventListener('click', () => {
        document.querySelectorAll('.tab').forEach(t => t.classList.remove('active'));
        tab.classList.add('active');

        const isLogin = tab.dataset.tab === 'login';
        document.getElementById('login-form').classList.toggle('hidden', !isLogin);
        document.getElementById('register-form').classList.toggle('hidden', isLogin);
    });
});

// Login
document.getElementById('login-form').addEventListener('submit', async (e) => {
    e.preventDefault();
    const errorEl = document.getElementById('login-error');
    errorEl.textContent = '';

    const form = new FormData(e.target);
    try {
        const data = await API.post('/auth/login', {
            email: form.get('email'),
            password: form.get('password')
        });
        API.setToken(data.token);
        window.location.href = 'app.html';
    } catch (err) {
        errorEl.textContent = err.message;
    }
});

// Register
document.getElementById('register-form').addEventListener('submit', async (e) => {
    e.preventDefault();
    const errorEl = document.getElementById('register-error');
    errorEl.textContent = '';

    const form = new FormData(e.target);
    try {
        const data = await API.post('/auth/register', {
            email: form.get('email'),
            password: form.get('password'),
            nativeLang: form.get('nativeLang')
        });
        API.setToken(data.token);
        window.location.href = 'app.html';
    } catch (err) {
        errorEl.textContent = err.message;
    }
});
