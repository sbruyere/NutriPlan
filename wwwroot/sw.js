// Service worker minimal : rend l'application installable (PWA) sans intercepter
// le réseau — Blazor Server (SignalR) passe en direct, aucun cache offline.
self.addEventListener('install', () => self.skipWaiting());
self.addEventListener('activate', (e) => e.waitUntil(self.clients.claim()));
