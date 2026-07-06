const CACHE_NAME = 'vass-v2';
const STATIC_ASSETS = [
  '/',
  '/app.html',
  '/index.html',
  '/settings.html',
  '/css/styles.css',
  '/js/api.js',
  '/js/auth.js',
  '/js/chat.js',
  '/js/voice.js',
  '/js/yolo.js',
  '/js/settings.js',
  '/manifest.json'
];

// Install — cache static assets
self.addEventListener('install', (e) => {
  e.waitUntil(
    caches.open(CACHE_NAME).then(cache => cache.addAll(STATIC_ASSETS))
  );
  self.skipWaiting();
});

// Activate — clean old caches
self.addEventListener('activate', (e) => {
  e.waitUntil(
    caches.keys().then(keys =>
      Promise.all(keys.filter(k => k !== CACHE_NAME).map(k => caches.delete(k)))
    )
  );
  self.clients.claim();
});

// Fetch — network first for API, cache first for static
self.addEventListener('fetch', (e) => {
  const url = new URL(e.request.url);

  // Never cache API calls or SSE streams
  if (url.pathname.startsWith('/api/')) return;

  e.respondWith(
    fetch(e.request)
      .then(res => {
        const clone = res.clone();
        caches.open(CACHE_NAME).then(cache => cache.put(e.request, clone));
        return res;
      })
      .catch(() => caches.match(e.request))
  );
});
