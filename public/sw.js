const CACHE = 'xp-efb-shell-v6';
const SHELL = ['/', '/styles.css', '/app.js', '/manifest.webmanifest', '/assets/icon.svg', '/vendor/leaflet/leaflet.css', '/vendor/leaflet/leaflet.js', '/vendor/leaflet-rotate/leaflet-rotate.umd.min.js', '/vendor/leaflet/images/marker-icon.png', '/vendor/leaflet/images/marker-shadow.png'];
self.addEventListener('install', (event) => event.waitUntil(caches.open(CACHE).then((cache) => cache.addAll(SHELL)).then(() => self.skipWaiting())));
self.addEventListener('activate', (event) => event.waitUntil(caches.keys().then((keys) => Promise.all(keys.filter((key) => key !== CACHE).map((key) => caches.delete(key)))).then(() => self.clients.claim())));
self.addEventListener('fetch', (event) => {
  if (event.request.method !== 'GET' || new URL(event.request.url).origin !== location.origin || new URL(event.request.url).pathname.startsWith('/api/')) return;
  event.respondWith(caches.match(event.request).then((cached) => cached || fetch(event.request).then((response) => {
    const copy = response.clone();
    caches.open(CACHE).then((cache) => cache.put(event.request, copy));
    return response;
  })));
});
