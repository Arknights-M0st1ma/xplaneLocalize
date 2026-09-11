// Application shell cache. Only registered on a secure context (HTTPS or
// localhost), so a plain http://iPad-IP connection keeps working without it.
const CACHE = 'xp-efb-shell-v21';
const SHELL = [
  '/', '/styles.css?v=22', '/app.js?v=22', '/manifest.webmanifest', '/assets/icon.svg', '/data/airlines.json',
  '/vendor/leaflet/leaflet.css', '/vendor/leaflet/leaflet.js',
  '/vendor/leaflet/images/marker-icon.png', '/vendor/leaflet/images/marker-shadow.png'
];

self.addEventListener('install', (event) => {
  event.waitUntil(caches.open(CACHE).then((cache) => cache.addAll(SHELL)).then(() => self.skipWaiting()));
});

self.addEventListener('activate', (event) => {
  event.waitUntil(caches.keys()
    .then((keys) => Promise.all(keys.filter((key) => key !== CACHE).map((key) => caches.delete(key))))
    .then(() => self.clients.claim()));
});

self.addEventListener('fetch', (event) => {
  const request = event.request;
  if (request.method !== 'GET') return;
  const url = new URL(request.url);
  if (url.origin !== location.origin || url.pathname.startsWith('/api/') || url.pathname === '/ws') return;
  event.respondWith((async () => {
    // ignoreSearch keeps asset versions (app.js?v=9) working from the cache.
    const cached = await caches.match(request, { ignoreSearch: true });
    if (cached) return cached;
    try {
      const response = await fetch(request);
      if (response.ok) {
        const copy = response.clone();
        caches.open(CACHE).then((cache) => cache.put(request, copy));
      }
      return response;
    } catch (error) {
      const fallback = await caches.match('/', { ignoreSearch: true });
      if (fallback) return fallback;
      throw error;
    }
  })());
});
