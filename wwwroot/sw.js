// Service worker NutriPlan — installabilité (le gestionnaire fetch est requis
// par Chrome pour proposer l'installation) + page de secours hors-ligne.
// Interception minimale : SEULES les navigations passent par le handler, en
// réseau d'abord ; tout le reste — assets, API, SignalR — n'est jamais
// intercepté (pas de respondWith → comportement navigateur inchangé).
const CACHE = 'nutriplan-sw-v2';
const OFFLINE_URL = '/offline.html';

self.addEventListener('install', (e) => {
    e.waitUntil(
        caches.open(CACHE)
            .then((c) => c.add(OFFLINE_URL))
            .then(() => self.skipWaiting())
    );
});

self.addEventListener('activate', (e) => {
    e.waitUntil(
        caches.keys()
            .then((keys) => Promise.all(keys.filter((k) => k !== CACHE).map((k) => caches.delete(k))))
            .then(() => self.clients.claim())
    );
});

self.addEventListener('fetch', (e) => {
    if (e.request.mode !== 'navigate') return;
    e.respondWith(fetch(e.request).catch(() => caches.match(OFFLINE_URL)));
});
