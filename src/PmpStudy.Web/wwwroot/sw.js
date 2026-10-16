// Service worker PMP Study : installation en application, mode hors ligne et notifications de rappel.
// La version en ligne (Blazor) n'est jamais mise en cache : seule l'application hors ligne (/hors-ligne/) l'est.
// Les données hors ligne vivent dans IndexedDB ; le PDF du cours et PDF.js dans le cache « pmpstudy-data ».
const SHELL_CACHE = 'pmpstudy-shell-v2';
const DATA_CACHE = 'pmpstudy-data';
const OFFLINE_APP = '/hors-ligne/index.html';
const SHELL = [
  OFFLINE_APP,
  '/hors-ligne/app.js',
  '/hors-ligne/store.js',
  '/hors-ligne/icons.js',
  '/hors-ligne/reader.js',
  '/hors-ligne/hl.css',
  '/fonts/geist-latin-wght-normal.woff2',
  '/fonts/geist-latin-ext-wght-normal.woff2',
  '/fonts/geist-mono-latin-wght-normal.woff2',
  '/icons/icon-192.png',
  '/icons/icon-512.png',
  '/icons/favicon-64.png',
  '/icons/apple-touch-icon.png',
  '/manifest.webmanifest',
];

self.addEventListener('install', (event) => {
  event.waitUntil(caches.open(SHELL_CACHE).then((c) => c.addAll(SHELL.map((u) => new Request(u, { cache: 'reload' })))).then(() => self.skipWaiting()));
});

self.addEventListener('activate', (event) => {
  event.waitUntil(
    caches.keys()
      .then((keys) => Promise.all(keys.filter((k) => k !== SHELL_CACHE && k !== DATA_CACHE).map((k) => caches.delete(k))))
      .then(() => self.clients.claim()));
});

/** Réseau d'abord (les mises à jour arrivent tout de suite), cache si le réseau ne répond pas dans le délai. */
async function networkFirst(request, cacheKey, timeoutMs) {
  const cache = await caches.open(SHELL_CACHE);
  try {
    const response = await Promise.race([
      fetch(request),
      new Promise((_, reject) => setTimeout(() => reject(new Error('timeout')), timeoutMs)),
    ]);
    if (response.ok && response.type === 'basic') cache.put(cacheKey, response.clone());
    return response;
  } catch {
    const cached = await caches.match(cacheKey, { ignoreSearch: true });
    if (cached) return cached;
    throw new Error('Hors ligne et absent du cache.');
  }
}

self.addEventListener('fetch', (event) => {
  const request = event.request;
  if (request.method !== 'GET') return;
  const url = new URL(request.url);
  if (url.origin !== self.location.origin) return;

  if (request.mode === 'navigate') {
    // Application hors ligne : toujours disponible. Version en ligne : réseau, sinon bascule vers le hors ligne.
    if (url.pathname.startsWith('/hors-ligne')) {
      event.respondWith(networkFirst(request, OFFLINE_APP, 3500).catch(() => Response.error()));
    } else {
      // « no-store » : jamais une ancienne page du cache HTTP du navigateur (ses scripts ne seraient pas disponibles).
      event.respondWith(fetch(request.url, { cache: 'no-store', credentials: 'same-origin', redirect: 'manual' })
        .catch(async () => (await caches.match(OFFLINE_APP)) || Response.error()));
    }
    return;
  }

  if (url.pathname.startsWith('/hors-ligne/') || url.pathname.startsWith('/fonts/') || url.pathname.startsWith('/icons/')) {
    event.respondWith(networkFirst(request, url.pathname, 3500));
    return;
  }

  if (url.pathname.startsWith('/lib/pdfjs/')) {
    // PDF.js : gardé avec le PDF du cours quand il a été téléchargé.
    event.respondWith(caches.match(url.pathname, { ignoreSearch: true }).then((cached) => cached || fetch(request)));
  }
});

self.addEventListener('push', (event) => {
  let data = {};
  try { data = event.data ? event.data.json() : {}; } catch { data = { body: event.data && event.data.text() }; }
  const title = data.title || 'PMP Study';
  event.waitUntil(self.registration.showNotification(title, {
    body: data.body || "C'est l'heure de tes révisions.",
    icon: '/icons/icon-192.png',
    badge: '/icons/icon-192.png',
    tag: data.tag || 'pmp-daily',
    renotify: true,
    data: { url: data.url || '/' },
  }));
});

self.addEventListener('notificationclick', (event) => {
  event.notification.close();
  const url = new URL((event.notification.data && event.notification.data.url) || '/', self.location.origin).href;
  event.waitUntil((async () => {
    const all = await self.clients.matchAll({ type: 'window', includeUncontrolled: true });
    for (const client of all) {
      if (client.url.startsWith(self.location.origin) && 'focus' in client) {
        await client.focus();
        if ('navigate' in client) await client.navigate(url);
        return;
      }
    }
    await self.clients.openWindow(url);
  })());
});
