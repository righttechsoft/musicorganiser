// ponytail: shell-only cache. Makes the localhost/HTTPS case installable; API
// calls always hit the network (never cached). Bump CACHE to invalidate the shell.
const CACHE = 'mo-shell-v1';
const SHELL = ['/', '/index.html', '/manifest.webmanifest', '/icon.png'];

self.addEventListener('install', e => {
  e.waitUntil(caches.open(CACHE).then(c => c.addAll(SHELL)).then(() => self.skipWaiting()));
});

self.addEventListener('activate', e => {
  e.waitUntil(
    caches.keys()
      .then(keys => Promise.all(keys.filter(k => k !== CACHE).map(k => caches.delete(k))))
      .then(() => self.clients.claim())
  );
});

self.addEventListener('fetch', e => {
  const url = new URL(e.request.url);
  // Only the app shell is cache-first. Everything else (the API) goes straight to network.
  if (e.request.method !== 'GET' || !SHELL.includes(url.pathname)) return;
  e.respondWith(caches.match(e.request).then(r => r || fetch(e.request)));
});
